using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MaxTerrain
{
    /// <summary>固定世界范围的纯CPU页输入；尚未创建或访问任何Unity资源。</summary>
    public sealed class MeshWorldVtSourcePage
    {
        public readonly uint Key;
        public readonly int SourceX, SourceZ, Side;
        public readonly ushort[] Heights, Controls;

        public MeshWorldVtSourcePage(uint key, int sourceX, int sourceZ, int side,
            ushort[] heights, ushort[] controls)
        {
            Key = key; SourceX = sourceX; SourceZ = sourceZ; Side = side;
            Heights = heights; Controls = controls;
        }

        public long Bytes => (long)(Heights.Length + Controls.Length) * sizeof(ushort);
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
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            public MeshWorldVtSourcePage Page;
            public Exception Error;
            public bool Cancelled;
        }

        readonly object gate = new object();
        readonly Dictionary<uint, Entry> entries = new Dictionary<uint, Entry>();
        readonly List<Entry> queue = new List<Entry>();
        readonly HashSet<uint> wanted = new HashSet<uint>();
        readonly List<uint> obsolete = new List<uint>();
        readonly Func<uint, CancellationToken, MeshWorldVtSourcePage> readPage;
        readonly Action disposeReader;
        readonly int capacity;
        Entry active;
        Task workerTask = Task.CompletedTask;
        bool workerRunning, disposed, readerReleased;
        int peakPending, completed, cancelled, consumed, failed, workerThreadId;

        public MeshWorldVtSourceQueue(Func<uint, CancellationToken, MeshWorldVtSourcePage> readPage,
            Action disposeReader = null, int capacity = 12)
        {
            this.readPage = readPage ?? throw new ArgumentNullException(nameof(readPage));
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.disposeReader = disposeReader;
            this.capacity = capacity;
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
                    entry.Cancellation.Dispose();
                }
                queue.Clear();
                for (int i = 0; i < priorityKeys.Count; i++)
                {
                    uint key = priorityKeys[i];
                    if (!entries.TryGetValue(key, out Entry entry))
                    {
                        if (entries.Count >= capacity) continue;
                        entry = new Entry { Key = key };
                        entries.Add(key, entry);
                    }
                    if (entry == active || entry.Cancelled || entry.Page != null || entry.Error != null || queue.Contains(entry)) continue;
                    queue.Add(entry);
                }
                peakPending = Math.Max(peakPending, entries.Count);
                if (!workerRunning && queue.Count > 0)
                {
                    workerRunning = true;
                    workerTask = Task.Run((Action)Run);
                }
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
                entry.Cancellation.Dispose();
                if (entry.Error != null) throw new InvalidOperationException("RVT source preparation failed for page " + key + ".", entry.Error);
                page = entry.Page;
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
                    entry.Cancellation.Token.ThrowIfCancellationRequested();
                    page = readPage(entry.Key, entry.Cancellation.Token);
                    if (page == null || page.Key != entry.Key) throw new InvalidOperationException("RVT reader returned a different page.");
                }
                catch (Exception exception) { error = exception; page = null; }
                lock (gate)
                {
                    active = null;
                    if (entry.Cancelled || disposed)
                    {
                        entries.Remove(entry.Key);
                        entry.Cancellation.Dispose();
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
            entry.Cancellation.Cancel(); // reader仅轮询token，没有依赖主线程的回调。
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
                foreach (Entry entry in entries.Values)
                {
                    Cancel(entry);
                    if (entry != active) entry.Cancellation.Dispose();
                }
                entries.Clear();
                queue.Clear();
                // 不Join/Wait，也不在活动读者读文件时关闭它的stream/cache。
                if (!workerRunning && !readerReleased) { readerReleased = true; release = disposeReader; }
            }
            release?.Invoke();
        }
    }
}
