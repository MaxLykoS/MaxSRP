using System;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxTerrain
{
    // Owns PS request compaction and readback. The cache decodes the returned
    // local keys against its allocation table and decides which pages to load.
    internal sealed class MeshWorldVtFeedback : IDisposable
    {
        static readonly ProfilerMarker feedbackMarker = new ProfilerMarker("MaxTerrain.VT.Feedback.Poll");
        static readonly ProfilerMarker feedbackBeginMarker = new ProfilerMarker("MaxTerrain.VT.Feedback.Begin");
        static readonly ProfilerMarker feedbackSubmitMarker = new ProfilerMarker("MaxTerrain.VT.Feedback.Submit");
        static readonly ProfilerMarker feedbackRecordMarker = new ProfilerMarker("MaxTerrain.VT.Feedback.RecordCompaction");
        static readonly ProfilerMarker feedbackReadbackMarker = new ProfilerMarker("MaxTerrain.VT.Feedback.ReadbackCallback");

        const int FeedbackSlotCount = 3;
        const int FeedbackHashCapacity = 8192;
        public const int PackedCapacity = 512; // uint2 表头 + 至多 511 条请求。
        const int FeedbackRecoveryStride = 16;
        const int MainFeedbackRawCapacity = 65536; // 计数头 + 最多 65535 个屏幕请求。

        sealed class FeedbackSlot
        {
            readonly MeshWorldVtFeedback owner;
            public readonly Action<AsyncGPUReadbackRequest> ReadbackCallback;
            public NativeArray<uint> Records;
            public bool Pending, ReadbackReady, ReadbackError;
            public int Epoch, Frame;
            public long Sequence;

            public FeedbackSlot(MeshWorldVtFeedback owner)
            {
                this.owner = owner;
                ReadbackCallback = CompleteReadback;
            }

            void CompleteReadback(AsyncGPUReadbackRequest request)
            {
                using var readbackProfile = feedbackReadbackMarker.Auto();
                if (owner.disposed || !Records.IsCreated) return;
                ReadbackError = request.hasError;
                if (!request.hasError) request.GetData<uint>().CopyTo(Records);
                ReadbackReady = true;
            }

            public void Release()
            {
                // GPU readback targets Unity-owned staging memory. The callback
                // copies into Records only while this slot is alive.
                ReadbackReady = false;
                Pending = false;
                if (Records.IsCreated) Records.Dispose();
                Records = default;
            }
        }

        readonly FeedbackSlot[] feedback = new FeedbackSlot[FeedbackSlotCount];
        readonly ComputeShader feedbackCompactor;
        readonly ComputeBuffer feedbackHash, packedFeedback;
        readonly CommandBuffer compactFeedbackCommands;
        readonly int clearFeedbackKernel, gatherBufferFeedbackKernel, packFeedbackKernel;
        readonly uint[] emptyMainFeedbackHeader = { 0 };
        ComputeBuffer mainFeedbackRaw;
        FeedbackSlot mainFeedbackSlot;
        bool disposed;
        int feedbackEpoch, feedbackHashSeed, feedbackSweepLeft;
        long feedbackSequence;

        public int FeedbackSubmitted { get; private set; }
        public int CompletedFeedback { get; private set; }
        public int FeedbackErrors { get; private set; }
        public int FeedbackDropped { get; private set; }
        public int FeedbackTruncatedFrames { get; private set; }
        public int FeedbackOverflowFrames { get; private set; }
        public int LatestFeedbackUniqueKeys { get; private set; }
        public int LatestFeedbackOverflowKeys { get; private set; }
        public int LatestFeedbackFailedSamples { get; private set; }
        public long LatestFeedbackSequence { get; private set; }
        public int LatestFeedbackFrame { get; private set; } = -1;

        public MeshWorldVtFeedback()
        {
            try
            {
                feedbackCompactor = Resources.Load<ComputeShader>("WorldVtCompactFeedback");
                if (!feedbackCompactor)
                    throw new InvalidOperationException("The world VT feedback compaction shader is unavailable.");
                clearFeedbackKernel = feedbackCompactor.FindKernel("ClearHash");
                gatherBufferFeedbackKernel = feedbackCompactor.FindKernel("GatherBuffer");
                packFeedbackKernel = feedbackCompactor.FindKernel("Pack");
                feedbackHash = new ComputeBuffer(FeedbackHashCapacity, 2 * sizeof(uint)) { name = "Delta VT feedback hash" };
                packedFeedback = new ComputeBuffer(PackedCapacity, 2 * sizeof(uint)) { name = "Delta VT packed feedback page IDs and counts" };
                compactFeedbackCommands = new CommandBuffer { name = "Delta VT: compact feedback requests" };
                for (int i = 0; i < feedback.Length; i++) feedback[i] = new FeedbackSlot(this);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public bool Begin(CommandBuffer commands, Material material, int frame, int divisor, int sampleX, int sampleY)
        {
            using var profile = feedbackBeginMarker.Auto();
            if (mainFeedbackSlot != null)
            {
                if (mainFeedbackSlot.Frame == frame) return false;
                // 相机在上一帧 Begin 后中止且没有 PostRender 时，取消未提交预约。
                mainFeedbackSlot = null;
                FeedbackDropped++;
            }
            foreach (FeedbackSlot slot in feedback)
            {
                if (slot.Pending) continue;
                if (!slot.Records.IsCreated)
                    slot.Records = new NativeArray<uint>(PackedCapacity * 2, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                if (mainFeedbackRaw == null)
                    mainFeedbackRaw = new ComputeBuffer(MainFeedbackRawCapacity, sizeof(uint)) { name = "Delta VT main pass raw page requests" };

                slot.Epoch = feedbackEpoch;
                slot.Frame = frame;
                slot.Sequence = ++feedbackSequence;
                mainFeedbackSlot = slot;
                // 与随后 draw 在同一个 CommandBuffer 中清计数头并绑定 UAV。
                commands.SetBufferData(mainFeedbackRaw, emptyMainFeedbackHeader, 0, 0, 1);
                commands.SetRandomWriteTarget(4, mainFeedbackRaw);
                material.SetBuffer("_DDVtFeedbackRaw", mainFeedbackRaw);
                material.SetVector("_DDVtFeedbackSampling", new Vector4(divisor, sampleX, sampleY, MainFeedbackRawCapacity));
                return true;
            }
            FeedbackDropped++;
            return false;
        }

        public void Submit(ScriptableRenderContext context)
        {
            using var profile = feedbackSubmitMarker.Auto();
            FeedbackSlot slot = mainFeedbackSlot;
            mainFeedbackSlot = null;
            if (disposed || slot == null || slot.Epoch != feedbackEpoch) return;
            // Queue the dispatch AND readback after this camera's MRT draw through
            // the same SRP context. An immediate Graphics readback here could run
            // before the context's deferred draw commands have been submitted.
            CompactFeedback();
            slot.Pending = true;
            slot.ReadbackReady = slot.ReadbackError = false;
            compactFeedbackCommands.RequestAsyncReadback(packedFeedback, slot.ReadbackCallback);
            context.ExecuteCommandBuffer(compactFeedbackCommands);
            FeedbackSubmitted++;
        }

        void CompactFeedback()
        {
            using var profile = feedbackRecordMarker.Auto();
            CommandBuffer commands = compactFeedbackCommands;
            commands.Clear();
            commands.SetComputeIntParam(feedbackCompactor, "_HashCapacity", FeedbackHashCapacity);
            commands.SetComputeIntParam(feedbackCompactor, "_PackedCapacity", PackedCapacity);
            // 按环形顺序轮换列表起点，列表溢出时不会每次只搬同一批页。
            commands.SetComputeIntParam(feedbackCompactor, "_HashStart",
                (int)(feedbackSequence * (PackedCapacity - 1) & (FeedbackHashCapacity - 1)));
            commands.SetComputeIntParam(feedbackCompactor, "_HashSeed", feedbackHashSeed);
            // 哈希插入失败后，把原始PS请求列表分散到16个互斥相位。
            // 每相位最多4096个样本，回读大小不变，并给其它样本补采机会。
            commands.SetComputeIntParam(feedbackCompactor, "_GatherStride", feedbackSweepLeft > 0 ? FeedbackRecoveryStride : 1);
            commands.SetComputeIntParam(feedbackCompactor, "_GatherPhase", feedbackSweepLeft > 0 ? FeedbackRecoveryStride - feedbackSweepLeft : 0);
            commands.SetComputeBufferParam(feedbackCompactor, clearFeedbackKernel, "_Hash", feedbackHash);
            commands.SetComputeBufferParam(feedbackCompactor, clearFeedbackKernel, "_Packed", packedFeedback);
            commands.DispatchCompute(feedbackCompactor, clearFeedbackKernel, FeedbackHashCapacity / 256, 1, 1);
            commands.SetComputeBufferParam(feedbackCompactor, gatherBufferFeedbackKernel, "_Hash", feedbackHash);
            commands.SetComputeBufferParam(feedbackCompactor, gatherBufferFeedbackKernel, "_Packed", packedFeedback);
            commands.SetComputeBufferParam(feedbackCompactor, gatherBufferFeedbackKernel, "_RawFeedback", mainFeedbackRaw);
            commands.SetComputeIntParam(feedbackCompactor, "_RawCapacity", MainFeedbackRawCapacity);
            commands.DispatchCompute(feedbackCompactor, gatherBufferFeedbackKernel, (MainFeedbackRawCapacity - 1 + 63) / 64, 1, 1);
            commands.SetComputeBufferParam(feedbackCompactor, packFeedbackKernel, "_Hash", feedbackHash);
            commands.SetComputeBufferParam(feedbackCompactor, packFeedbackKernel, "_Packed", packedFeedback);
            commands.DispatchCompute(feedbackCompactor, packFeedbackKernel, 1, 1, 1);
            if (feedbackSweepLeft > 0) feedbackSweepLeft--;
        }

        public void Invalidate()
        {
            feedbackEpoch++;
            mainFeedbackSlot = null;
            feedbackSweepLeft = LatestFeedbackUniqueKeys = LatestFeedbackOverflowKeys = LatestFeedbackFailedSamples = 0;
            LatestFeedbackFrame = -1;
        }

        // The caller consumes the borrowed records before scheduling another readback.
        public bool TryReadLatest(bool recipeMatches, out NativeArray<uint> records, out int frame)
        {
            using var profile = feedbackMarker.Auto();
            // GPU 可以在同一帧交回多张反馈图，只用最新一张有效图建立需求。
            FeedbackSlot newest = null;
            foreach (FeedbackSlot slot in feedback)
            {
                if (!slot.Pending || !slot.ReadbackReady) continue;
                slot.Pending = false;
                if (slot.Epoch != feedbackEpoch || !recipeMatches || slot.Sequence <= LatestFeedbackSequence) continue;
                if (slot.ReadbackError)
                {
                    FeedbackErrors++;
                    continue;
                }
                CompletedFeedback++;
                if (slot.Records[0] >= PackedCapacity) FeedbackTruncatedFrames++;
                if (slot.Records[1] != 0) FeedbackOverflowFrames++;
                if (slot.Records[1] != 0 && feedbackSweepLeft == 0)
                {
                    feedbackHashSeed++;
                    feedbackSweepLeft = FeedbackRecoveryStride;
                }
                if (newest == null || slot.Sequence > newest.Sequence) newest = slot;
            }
            records = newest == null ? default : newest.Records;
            frame = newest == null ? -1 : newest.Frame;
            if (newest == null) return false;

            LatestFeedbackSequence = newest.Sequence;
            LatestFeedbackFrame = frame;
            LatestFeedbackUniqueKeys = (int)records[0];
            LatestFeedbackOverflowKeys = Math.Max(0, LatestFeedbackUniqueKeys - (PackedCapacity - 1));
            LatestFeedbackFailedSamples = (int)records[1];
            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            mainFeedbackSlot = null;
            foreach (FeedbackSlot slot in feedback) slot?.Release();
            mainFeedbackRaw?.Release();
            compactFeedbackCommands?.Release();
            packedFeedback?.Release();
            feedbackHash?.Release();
        }
    }
}
