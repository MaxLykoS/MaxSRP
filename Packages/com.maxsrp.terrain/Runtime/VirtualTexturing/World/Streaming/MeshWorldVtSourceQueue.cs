using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MaxTerrain
{
    /// <summary>固定世界范围的纯CPU页输入；尚未创建或访问任何Unity资源。</summary>
    public sealed class MeshWorldVtSourcePage
    {
        public uint Key { get; private set; }
        public int SourceX { get; private set; }
        public int SourceZ { get; private set; }
        public readonly int Side;
        public readonly ushort[] Heights;
        public readonly uint[] Controls;
        internal bool InPool;

        public MeshWorldVtSourcePage(uint key, int sourceX, int sourceZ, int side,
            ushort[] heights, uint[] controls)
        {
            Key = key; SourceX = sourceX; SourceZ = sourceZ; Side = side;
            Heights = heights; Controls = controls;
        }

        public long Bytes => (long)Heights.Length * sizeof(ushort) + (long)Controls.Length * sizeof(uint);

        internal void Reset(uint key, int sourceX, int sourceZ)
        { Key = key; SourceX = sourceX; SourceZ = sourceZ; }
    }

    /// <summary>
    /// 单读者、有界的RVT源准备队列。容量同时包含正在读、排队及已就绪的数据；
    /// 取消正在读的页不会提前腾出容量，也不会另外启动并发读者。
    /// </summary>
    public sealed class MeshWorldVtSourceQueue : IDisposable
    {
        sealed class Entry
        {
            public uint Key;
            public CancellationTokenSource Cancellation;
            public MeshWorldVtSourcePage Page;
            public Exception Error;
            public bool Cancelled;
        }

        readonly object gate = new object();
        readonly Dictionary<uint, Entry> entries;
        readonly List<Entry> queue;
        readonly HashSet<uint> wanted;
        readonly List<uint> obsolete;
        readonly Func<uint, CancellationToken, MeshWorldVtSourcePage> readPage;
        readonly Action disposeReader;
        readonly Action<MeshWorldVtSourcePage> releasePage;
        readonly int capacity;
        readonly bool persistentWorker;
        readonly Stack<Entry> freeEntries;
        readonly CancellationTokenSource lifetimeCancellation;
        readonly Action run;
        Thread workerThread;
        TaskCompletionSource<bool> workerStopped;
        Entry active;
        Task workerTask = Task.CompletedTask;
        bool workerRunning, disposed, readerReleased;
        int peakPending, completed, cancelled, consumed, failed, workerThreadId;

        public MeshWorldVtSourceQueue(Func<uint, CancellationToken, MeshWorldVtSourcePage> readPage,
            Action disposeReader = null, int capacity = 12, bool persistentWorker = false,
            Action<MeshWorldVtSourcePage> releasePage = null, int requestCapacity = 0)
        {
            this.readPage = readPage ?? throw new ArgumentNullException(nameof(readPage));
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.disposeReader = disposeReader;
            this.capacity = capacity;
            this.persistentWorker = persistentWorker;
            this.releasePage = releasePage;
            entries = new Dictionary<uint, Entry>(capacity);
            queue = new List<Entry>(capacity);
            obsolete = new List<uint>(capacity);
            wanted = new HashSet<uint>(Math.Max(capacity, requestCapacity));
            run = Run;
            if (persistentWorker)
            {
                lifetimeCancellation = new CancellationTokenSource();
                freeEntries = new Stack<Entry>(capacity);
                for (int i = 0; i < capacity; i++) freeEntries.Push(new Entry());
            }
        }

        public int Capacity => capacity;
        public int PendingCount { get { lock (gate) return entries.Count; } }
        public int ReadyCount { get { lock (gate) { int n = 0; foreach (Entry e in entries.Values) if (e.Page != null && e.Error == null) n++; return n; } } }
        public long ReadyBytes { get { lock (gate) { long n = 0; foreach (Entry e in entries.Values) if (e.Page != null && e.Error == null) n += e.Page.Bytes; return n; } } }
        public int PeakPendingCount { get { lock (gate) return peakPending; } }
        public int CompletedCount { get { lock (gate) return completed; } }
        public int CancelledCount { get { lock (gate) return cancelled; } }
        public int ConsumedCount { get { lock (gate) return consumed; } }
        public int FailedCount { get { lock (gate) return failed; } }
        public int WorkerThreadId { get { lock (gate) return workerThreadId; } }
        /// <summary>Legacy: current burst. Persistent runtime worker: completes after Dispose.</summary>
        public Task Completion { get { lock (gate) return workerTask; } }
        public bool IsReady(uint key)
        {
            lock (gate)
                return !disposed && entries.TryGetValue(key, out Entry entry) && !entry.Cancelled &&
                    entry != active && (entry.Page != null || entry.Error != null);
        }

        /// <summary>按当前生产优先级更新需求；只处理元数据，不读取文件、不等待任务。</summary>
        public void SetRequests(IList<uint> priorityKeys)
        {
            if (priorityKeys == null) throw new ArgumentNullException(nameof(priorityKeys));
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(MeshWorldVtSourceQueue));
                wanted.Clear();
                for (int i = 0; i < priorityKeys.Count; i++) wanted.Add(priorityKeys[i]);
                obsolete.Clear();
                foreach (var pair in entries)
                {
                    Entry entry = pair.Value;
                    if (wanted.Contains(pair.Key)) continue;
                    Cancel(entry);
                    // 活动读者可能仍在OS读调用中。保留它的容量，结束后才移除。
                    if (entry != active) obsolete.Add(pair.Key);
                }
                foreach (uint key in obsolete)
                {
                    Entry entry = entries[key];
                    entries.Remove(key);
                    Recycle(entry);
                }
                queue.Clear();
                for (int i = 0; i < priorityKeys.Count; i++)
                {
                    uint key = priorityKeys[i];
                    if (!entries.TryGetValue(key, out Entry entry))
                    {
                        if (entries.Count >= capacity) continue;
                        entry = persistentWorker ? freeEntries.Pop() :
                            new Entry { Cancellation = new CancellationTokenSource() };
                        entry.Key = key;
                        entries.Add(key, entry);
                    }
                    if (entry == active || entry.Cancelled || entry.Page != null || entry.Error != null || queue.Contains(entry)) continue;
                    queue.Add(entry);
                }
                peakPending = Math.Max(peakPending, entries.Count);
                if (!workerRunning && queue.Count > 0)
                {
                    StartWorker();
                }
                else if (persistentWorker && queue.Count > 0) Monitor.Pulse(gate);
            }
        }

        /// <summary>只取已完成的页。后台错误在取该页时上报，坏数据不会进入GPU。</summary>
        public bool TryTake(uint key, out MeshWorldVtSourcePage page)
        {
            lock (gate)
            {
                page = null;
                if (disposed) throw new ObjectDisposedException(nameof(MeshWorldVtSourceQueue));
                if (!entries.TryGetValue(key, out Entry entry) || entry.Cancelled || entry == active ||
                    (entry.Page == null && entry.Error == null)) return false;
                entries.Remove(key);
                Exception error = entry.Error;
                page = entry.Page;
                entry.Page = null; // The consumer returns it after copying to its textures.
                Recycle(entry);
                if (error != null) throw new InvalidOperationException("RVT source preparation failed for page " + key + ".", error);
                consumed++;
                return true;
            }
        }

        void Run()
        {
            while (true)
            {
                Entry entry;
                Action release = null;
                lock (gate)
                {
                    workerThreadId = Thread.CurrentThread.ManagedThreadId;
                    while (persistentWorker && !disposed && queue.Count == 0) Monitor.Wait(gate);
                    if (disposed || queue.Count == 0)
                    {
                        workerRunning = false;
                        if (disposed && !readerReleased) { readerReleased = true; release = disposeReader; }
                        entry = null;
                    }
                    else
                    {
                        entry = active = queue[0];
                        queue.RemoveAt(0);
                    }
                }
                if (entry == null) { release?.Invoke(); return; }
                MeshWorldVtSourcePage page = null;
                Exception error = null;
                try
                {
                    CancellationToken token = persistentWorker ? lifetimeCancellation.Token : entry.Cancellation.Token;
                    token.ThrowIfCancellationRequested();
                    page = readPage(entry.Key, token);
                    if (page == null || page.Key != entry.Key) throw new InvalidOperationException("RVT reader returned a different page.");
                }
                catch (Exception exception)
                {
                    if (page != null) releasePage?.Invoke(page);
                    error = exception;
                    page = null;
                }
                lock (gate)
                {
                    active = null;
                    if (entry.Cancelled || disposed)
                    {
                        entries.Remove(entry.Key);
                        if (page != null) releasePage?.Invoke(page);
                        Recycle(entry);
                    }
                    else
                    {
                        entry.Page = page;
                        entry.Error = error;
                        if (error == null) completed++; else failed++;
                    }
                }
            }
        }

        void Cancel(Entry entry)
        {
            if (entry.Cancelled) return;
            entry.Cancelled = true;
            // Runtime reads retain their bounded slot until completion; obsolete
            // data is discarded. Only disposal interrupts the persistent reader.
            entry.Cancellation?.Cancel();
            if (entry.Page != null) releasePage?.Invoke(entry.Page);
            entry.Page = null;
            cancelled++;
        }

        public void Dispose()
        {
            Action release = null;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                lifetimeCancellation?.Cancel();
                foreach (Entry entry in entries.Values)
                {
                    Cancel(entry);
                    if (entry != active) Recycle(entry);
                }
                entries.Clear();
                queue.Clear();
                if (persistentWorker) Monitor.Pulse(gate);
                // 不Join/Wait，也不在活动读者读文件时关闭它的stream/cache。
                if (!workerRunning && !readerReleased) { readerReleased = true; release = disposeReader; }
            }
            release?.Invoke();
            if (persistentWorker && workerThread == null) lifetimeCancellation.Dispose();
        }

        /// <summary>Return a consumed runtime page after its CPU data was copied.</summary>
        public void ReturnPage(MeshWorldVtSourcePage page)
        { if (page != null) releasePage?.Invoke(page); }

        void Recycle(Entry entry)
        {
            entry.Cancellation?.Dispose();
            entry.Cancellation = null;
            if (entry.Page != null) releasePage?.Invoke(entry.Page);
            entry.Page = null;
            entry.Error = null;
            entry.Cancelled = false;
            if (persistentWorker) freeEntries.Push(entry);
        }

        void StartWorker()
        {
            workerRunning = true;
            if (!persistentWorker) { workerTask = Task.Run(run); return; }
            if (workerThread == null)
            {
                workerStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                workerTask = workerStopped.Task;
                workerThread = new Thread(RunPersistent) { IsBackground = true, Name = "MaxTerrain RVT source" };
                workerThread.Start();
            }
            Monitor.Pulse(gate);
        }

        void RunPersistent()
        {
            try { Run(); workerStopped.TrySetResult(true); }
            catch (Exception error) { workerStopped.TrySetException(error); }
            finally { lifetimeCancellation.Dispose(); }
        }
    }
}
