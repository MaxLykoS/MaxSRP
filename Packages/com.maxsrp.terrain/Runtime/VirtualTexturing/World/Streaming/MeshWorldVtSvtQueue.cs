using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MaxTerrain
{
    /// <summary>
    /// Bounded SVT input queue. Only the worker allocates page buffers and invokes
    /// the reader; ready/reading/leased pages all retain their capacity slot.
    /// </summary>
    public sealed class MeshWorldVtSvtQueue : IDisposable
    {
        enum State { Free, Queued, Reading, Ready, Failed, Leased }

        sealed class Entry
        {
            public readonly int Slot;
            public uint Key;
            public int Generation;
            public State State;
            public bool Cancelled;
            public byte[] Bytes;
            public Exception Error;

            public Entry(int slot) { Slot = slot; }
        }

        /// <summary>The consumer owns the exact-sized buffer until Dispose.</summary>
        public readonly struct Payload : IDisposable
        {
            readonly MeshWorldVtSvtQueue owner;
            readonly int slot, generation;
            public readonly byte[] Bytes;

            internal Payload(MeshWorldVtSvtQueue owner, int slot, int generation, byte[] bytes)
            { this.owner = owner; this.slot = slot; this.generation = generation; Bytes = bytes; }

            // Generation checking makes copied, double-disposed or stale leases harmless.
            public void Dispose() => owner?.Return(slot, generation);
        }

        readonly object gate = new object();
        readonly Entry[] slots;
        readonly Dictionary<uint, Entry> entries;
        readonly List<Entry> queue, obsolete;
        readonly HashSet<uint> wanted;
        readonly Action<uint, byte[], CancellationToken> readPage;
        readonly Action disposeReader, run;
        readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        readonly int pageBytes;
        readonly bool persistentWorker;
        Thread workerThread;
        TaskCompletionSource<bool> workerStopped;
        Task workerTask = Task.CompletedTask;
        bool disposed, workerRunning, readerReleased;
        int peakPending, workerThreadId, completed, failed, consumed, cancelled;

        public MeshWorldVtSvtQueue(int pageBytes,
            Action<uint, byte[], CancellationToken> readPage, Action disposeReader, int capacity,
            bool persistentWorker = false, int requestCapacity = 0)
        {
            if (pageBytes < 1) throw new ArgumentOutOfRangeException(nameof(pageBytes));
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.pageBytes = pageBytes;
            this.readPage = readPage ?? throw new ArgumentNullException(nameof(readPage));
            this.disposeReader = disposeReader;
            this.persistentWorker = persistentWorker;
            wanted = new HashSet<uint>(Math.Max(capacity, requestCapacity));
            slots = new Entry[capacity];
            for (int i = 0; i < capacity; i++) slots[i] = new Entry(i);
            entries = new Dictionary<uint, Entry>(capacity);
            queue = new List<Entry>(capacity);
            obsolete = new List<Entry>(capacity);
            run = Run;
        }

        public int Capacity => slots.Length;
        public int PendingCount { get { lock (gate) return entries.Count; } }
        public int PeakPendingCount { get { lock (gate) return peakPending; } }
        public int WorkerThreadId { get { lock (gate) return workerThreadId; } }
        public int CompletedCount { get { lock (gate) return completed; } }
        public int FailedCount { get { lock (gate) return failed; } }
        public int ConsumedCount { get { lock (gate) return consumed; } }
        public int CancelledCount { get { lock (gate) return cancelled; } }
        /// <summary>Legacy: current burst. Persistent runtime worker: completes after Dispose.</summary>
        public Task Completion { get { lock (gate) return workerTask; } }

        public bool Contains(uint key)
        {
            lock (gate) return !disposed && entries.ContainsKey(key);
        }

        /// <summary>Update priority and cancellation metadata without file or payload work.</summary>
        public void SetRequests(IList<uint> priorityKeys)
        {
            if (priorityKeys == null) throw new ArgumentNullException(nameof(priorityKeys));
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(MeshWorldVtSvtQueue));
                wanted.Clear();
                for (int i = 0; i < priorityKeys.Count; i++) wanted.Add(priorityKeys[i]);
                obsolete.Clear();
                foreach (Entry entry in entries.Values)
                {
                    if (wanted.Contains(entry.Key)) { entry.Cancelled = false; continue; }
                    if (!entry.Cancelled) { entry.Cancelled = true; cancelled++; }
                    // An in-flight read keeps its buffer/capacity until the worker returns.
                    if (entry.State != State.Reading) obsolete.Add(entry);
                }
                foreach (Entry entry in obsolete) Free(entry);

                queue.Clear();
                int freeCount = 0;
                foreach (Entry entry in slots) if (entry.State == State.Free) freeCount++;
                for (int i = 0; i < priorityKeys.Count; i++)
                {
                    uint key = priorityKeys[i];
                    if (!entries.TryGetValue(key, out Entry entry))
                    {
                        // A full queue still scans existing keys for priority changes,
                        // but need not rescan all capacity slots for every new key.
                        if (freeCount == 0) continue;
                        entry = FindFree();
                        freeCount--;
                        entry.Key = key;
                        unchecked { entry.Generation++; }
                        entry.State = State.Queued;
                        entry.Cancelled = false;
                        entries.Add(key, entry);
                    }
                    if (entry.State == State.Queued && !queue.Contains(entry)) queue.Add(entry);
                }
                peakPending = Math.Max(peakPending, entries.Count);
                if (!workerRunning && queue.Count != 0) StartWorker();
                else if (persistentWorker && queue.Count != 0) Monitor.Pulse(gate);
            }
        }

        public bool TryTake(uint key, out Payload payload)
        {
            lock (gate)
            {
                payload = default;
                if (disposed) throw new ObjectDisposedException(nameof(MeshWorldVtSvtQueue));
                if (!entries.TryGetValue(key, out Entry entry) || entry.Cancelled ||
                    (entry.State != State.Ready && entry.State != State.Failed)) return false;
                if (entry.Error != null)
                {
                    Exception error = entry.Error;
                    Free(entry);
                    throw new InvalidOperationException("SVT page preparation failed for page " + key + ".", error);
                }
                entries.Remove(key);
                entry.State = State.Leased;
                payload = new Payload(this, entry.Slot, entry.Generation, entry.Bytes);
                consumed++;
                return true;
            }
        }

        void Return(int slot, int generation)
        {
            lock (gate)
            {
                Entry entry = slots[slot];
                if (entry.State != State.Leased || entry.Generation != generation) return;
                Free(entry);
            }
        }

        Entry FindFree()
        {
            foreach (Entry entry in slots) if (entry.State == State.Free) return entry;
            return null;
        }

        // All callers hold gate. Keep the buffer available for the next page.
        void Free(Entry entry)
        {
            if (entries.TryGetValue(entry.Key, out Entry current) && current == entry) entries.Remove(entry.Key);
            entry.State = State.Free;
            entry.Key = 0;
            entry.Cancelled = false;
            entry.Error = null;
            if (disposed) entry.Bytes = null;
        }

        void StartWorker()
        {
            workerRunning = true;
            if (!persistentWorker) { workerTask = Task.Run(run); return; }
            if (workerThread == null)
            {
                workerStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                workerTask = workerStopped.Task;
                workerThread = new Thread(RunPersistent) { IsBackground = true, Name = "MaxTerrain SVT source" };
                workerThread.Start();
            }
            Monitor.Pulse(gate);
        }

        void RunPersistent()
        {
            try { Run(); workerStopped.TrySetResult(true); }
            catch (Exception error) { workerStopped.TrySetException(error); }
        }

        void Run()
        {
            while (true)
            {
                Entry entry;
                bool release = false;
                lock (gate)
                {
                    workerThreadId = Thread.CurrentThread.ManagedThreadId;
                    while (persistentWorker && !disposed && queue.Count == 0) Monitor.Wait(gate);
                    if (disposed || queue.Count == 0)
                    {
                        workerRunning = false;
                        if (disposed && !readerReleased) { readerReleased = true; release = true; }
                        entry = null;
                    }
                    else
                    {
                        entry = queue[0];
                        queue.RemoveAt(0);
                        entry.State = State.Reading;
                    }
                }
                if (entry == null)
                {
                    if (release)
                    {
                        try { disposeReader?.Invoke(); }
                        finally { cancellation.Dispose(); }
                    }
                    return;
                }

                Exception error = null;
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (entry.Bytes == null) entry.Bytes = new byte[pageBytes];
                    readPage(entry.Key, entry.Bytes, cancellation.Token);
                }
                catch (Exception exception) { error = exception; }
                lock (gate)
                {
                    if (disposed || entry.Cancelled || !wanted.Contains(entry.Key)) Free(entry);
                    else
                    {
                        entry.Error = error;
                        entry.State = error == null ? State.Ready : State.Failed;
                        if (error == null) completed++; else failed++;
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                cancellation.Cancel(); // Reader only polls this token; no main-thread callbacks.
                foreach (Entry entry in slots)
                {
                    if (entry.State == State.Reading) entry.Cancelled = true;
                    else if (entry.State != State.Leased) Free(entry);
                }
                entries.Clear();
                queue.Clear();
                // Closing the persistent file is worker work too. Never Join/Wait here.
                if (!workerRunning && !readerReleased) StartWorker();
                else if (persistentWorker) Monitor.Pulse(gate);
            }
        }
    }

    /// <summary>Single-worker fixed-page reader; opens and retains one payload handle lazily.</summary>
    internal sealed class MeshWorldVtSvtReader : IDisposable
    {
        readonly string path;
        readonly int pageBytes;
        readonly Func<uint, int> indexForKey;
        readonly Func<int, string> checksumForIndex;
        FileStream file;
        SHA256 checksum;

        public MeshWorldVtSvtReader(string path, int pageBytes,
            Func<uint, int> indexForKey, Func<int, string> checksumForIndex)
        {
            this.path = path ?? throw new ArgumentNullException(nameof(path));
            this.pageBytes = pageBytes;
            this.indexForKey = indexForKey ?? throw new ArgumentNullException(nameof(indexForKey));
            this.checksumForIndex = checksumForIndex ?? throw new ArgumentNullException(nameof(checksumForIndex));
        }

        public void Read(uint key, byte[] bytes, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (bytes == null || bytes.Length != pageBytes) throw new ArgumentException("Wrong SVT page buffer size.", nameof(bytes));
            int index = indexForKey(key);
            if (file == null) file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            file.Position = checked((long)index * pageBytes);
            int offset = 0;
            while (offset < bytes.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                int received = file.Read(bytes, offset, bytes.Length - offset);
                if (received <= 0) throw new EndOfStreamException("World SVT archive ended inside a page.");
                offset += received;
            }
            cancellation.ThrowIfCancellationRequested();
            if (checksum == null) checksum = SHA256.Create();
            byte[] hash = checksum.ComputeHash(bytes);
            string expected = checksumForIndex(index);
            if (expected == null || expected.Length != hash.Length * 2) throw new InvalidDataException("Wrong SVT page checksum length.");
            for (int i = 0; i < hash.Length; i++)
                if (hash[i] != (Nibble(expected[i * 2]) << 4 | Nibble(expected[i * 2 + 1])))
                    throw new InvalidDataException("World SVT page checksum failed: key " + key);
        }

        static int Nibble(char c) => c >= '0' && c <= '9' ? c - '0' :
            c >= 'a' && c <= 'f' ? c - 'a' + 10 : throw new InvalidDataException("Invalid SVT page checksum.");

        public void Dispose()
        {
            file?.Dispose();
            checksum?.Dispose();
            file = null;
            checksum = null;
        }
    }
}
