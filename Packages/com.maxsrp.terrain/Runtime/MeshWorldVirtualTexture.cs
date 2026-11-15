using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxTerrain
{
    public enum MeshVtMaxAnisotropy
    {
        [InspectorName("1")] X1 = 1,
        [InspectorName("2")] X2 = 2,
        [InspectorName("4")] X4 = 4
    }

    /// <summary>
    /// Delta 相机的世界 VT 缓存。SVT 粗页与 RVT 细页共用物理页池。
    /// 主地形PS写入请求缓冲，本类负责压实回读、排队、填页和发布页表。
    /// </summary>
    public sealed class MeshWorldVirtualTexture : IDisposable
    {
        public const int PageInterior = 128;
        public const int Border = 8;
        public const int PageSide = PageInterior + Border * 2;
        public const int Slots = 1543; // 双 BC3 144² 页共 63,991,296 B，所有 mip 共享。
        public const int Mip0PagesPerAxis = 32768;
        public const int RootMip = 15;
        public const int FirstSvtMip = 8;
        public const int PinnedMip = 13;
        public const GraphicsFormat AlbedoFormat = GraphicsFormat.R8G8B8A8_SRGB;
        public const GraphicsFormat DataFormat = GraphicsFormat.R8G8B8A8_UNorm;
        public const int BytesPerTexel = 4; // Each output has four 8-bit channels.
        public const int ChannelBytes = PageSide * PageSide * BytesPerTexel;
        public const int PageBytes = ChannelBytes * 2;
        public const int PinnedPageCount = 17; // 4 x 4 mip13 plus the root.
        const int PendingReadLimit = 12;
        const int FeedbackSlotCount = 3;
        const int FeedbackHashCapacity = 8192;
        const int FeedbackPackedCapacity = 512; // uint2 表头 + 至多 511 条请求。
        const int FeedbackRecoveryStride = 16;
        const int MainFeedbackRawCapacity = 65536; // 计数头 + 最多 65535 个屏幕请求。
        public const int RecentVisibilityFrames = 15; // UE r.VT.PageFreeThreshold的默认窗口。
        public const int FeedbackReadbackBytes = FeedbackPackedCapacity * 2 * sizeof(uint);

        sealed class FeedbackSlot
        {
            public NativeArray<uint> Records;
            public bool Pending, ReadbackReady, ReadbackError;
            public int Epoch, Frame;
            public long Sequence;

            public void Release()
            {
                // 回读完成前 GPU 仍可能写入这块原生内存。
                // SRP readback targets Unity-owned staging memory. Its callback copies
                // only while this slot is alive, so Dispose never races a GPU write.
                ReadbackReady = false;
                Pending = false;
                if (Records.IsCreated) Records.Dispose();
                Records = default;
            }
        }

        struct ResidentPage
        {
            public uint Key;
            public int LastVisibleFrame;
            public int LastFeedbackFrame; // 只由真实反馈命中的驻留页刷新，不由计划父页链刷新。
            public bool Streamed;
        }

        struct Candidate
        {
            public uint Key;
            public int Score;
        }

        readonly MeshWorldVtProducer producer;
        readonly MeshWorldVtArchive archive;
        readonly MeshWorldVtBc3 bc3;
        readonly IntPtr colorPoolPtr, dataPoolPtr;
        readonly Texture2D[] upload = new Texture2D[2];
        readonly ResidentPage[] slots = new ResidentPage[Slots];
        readonly FeedbackSlot[] feedback = new FeedbackSlot[FeedbackSlotCount];
        readonly ComputeShader feedbackCompactor;
        readonly ComputeBuffer feedbackHash, packedFeedback;
        readonly CommandBuffer compactFeedbackCommands;
        readonly int clearFeedbackKernel, gatherBufferFeedbackKernel, packFeedbackKernel;
        readonly uint[] emptyMainFeedbackHeader = { 0 };
        ComputeBuffer mainFeedbackRaw;
        FeedbackSlot mainFeedbackSlot;
        readonly MeshAdaptiveVtTable adaptive;
        readonly uint[] pinned = new uint[PinnedPageCount];
        readonly CancellationTokenSource ioCancellation = new CancellationTokenSource();
        readonly Dictionary<uint, int> resident = new Dictionary<uint, int>(Slots);
        readonly Dictionary<uint, Task<byte[]>> pendingReads = new Dictionary<uint, Task<byte[]>>(PendingReadLimit);
        readonly HashSet<uint> failedPages = new HashSet<uint>();
        readonly Dictionary<uint, int> requests = new Dictionary<uint, int>();
        readonly Dictionary<uint, int> incoming = new Dictionary<uint, int>();
        readonly Dictionary<uint, int> scores = new Dictionary<uint, int>();
        readonly List<Candidate> candidates = new List<Candidate>();
        readonly List<uint> work = new List<uint>(Slots);
        readonly List<uint> sourceWork = new List<uint>(Slots);
        readonly HashSet<uint> wanted = new HashSet<uint>();
        readonly HashSet<uint> locked = new HashSet<uint>();
        readonly List<uint> obsoleteReads = new List<uint>(PendingReadLimit);
        static readonly Comparison<Candidate> compareCandidates = CompareCandidates;

        struct PendingCompressedPage { public uint Key; public int Slot; }
        readonly List<PendingCompressedPage> compressionPending = new List<PendingCompressedPage>(4);
        readonly HashSet<int> compressionReservedSlots = new HashSet<int>();
        bool disposed, recipeMatches = true, planDirty;
        int frame = int.MinValue, feedbackEpoch, failedPinnedPages;
        int feedbackHashSeed, feedbackSweepLeft;
        long feedbackSequence;
        string pageError, recipeError;

        public Texture2DArray AlbedoAo { get; private set; }
        public Texture2DArray NormalRoughnessHeight { get; private set; }
        public ComputeBuffer PageTable { get; private set; }
        public MeshAdaptiveVtTable AdaptiveTable => adaptive;
        public bool Ready => !disposed && recipeMatches && resident.ContainsKey(pinned[0]);
        public bool PinnedReady => Ready && PinnedPagesPublished == PinnedPageCount;
        bool CoverageSettled => Ready && PinnedPagesPublished + failedPinnedPages == PinnedPageCount;
        public int PinnedPagesPublished { get; private set; }
        public int ResidentCount => resident.Count;
        public int ResidentSvtCount { get; private set; }
        public int ResidentRvtCount => ResidentCount - ResidentSvtCount;
        public int RequestedCount => requests.Count;
        public int RequestsDropped { get; private set; }
        public int PendingReads => pendingReads.Count;
        public int FeedbackSubmitted { get; private set; }
        public int MainFeedbackSubmitted { get; private set; }
        public int CompletedFeedback { get; private set; }
        public int FeedbackErrors { get; private set; }
        public int FeedbackDropped { get; private set; }
        // Fixed 4 KiB feedback deliberately defers unique keys beyond 511.
        // Keep this normal budget truncation separate from failed GPU inserts.
        public int FeedbackTruncatedFrames { get; private set; }
        public int FeedbackOverflowFrames { get; private set; }
        public int LatestFeedbackUniqueKeys { get; private set; }
        public int LatestFeedbackOverflowKeys { get; private set; }
        public int LatestFeedbackFailedSamples { get; private set; }
        public long FeedbackBytesSubmitted => (long)FeedbackSubmitted * FeedbackReadbackBytes;
        public int InvalidFeedbackKeys { get; private set; }
        public long LatestFeedbackSequence { get; private set; }
        public int LatestFeedbackFrame { get; private set; } = -1;
        public int RvtPagesRendered { get; private set; }
        public int SvtPagesUploaded { get; private set; }
        public int Evictions { get; private set; }
        public int AdaptivePagesReclaimed { get; private set; }
        public int RecentVisiblePages { get; private set; }
        public int TableUploads { get; private set; }
        public int PagesPublishedThisFrame { get; private set; }
        public int PageInputValidationFailures => producer.PageInputValidationFailures;
        public int ValidatedPages => producer.ValidatedPages;
        public int PageInputNotReadyRetries => producer.PageInputNotReadyRetries;
        public int PendingSourcePages => producer.PendingSourcePages;
        public int PreparedSourcePages => producer.PreparedSourcePages;
        public int PeakPendingSourcePages => producer.PeakPendingSourcePages;
        public int CompletedSourcePages => producer.CompletedSourcePages;
        public int ConsumedSourcePages => producer.ConsumedSourcePages;
        public int CancelledSourcePages => producer.CancelledSourcePages;
        public int FailedSourcePages => producer.FailedSourcePages;
        public int SourceWorkerThreadId => producer.SourceWorkerThreadId;
        public long PreparedSourceBytes => producer.PreparedSourceBytes;
        public long SourceHeightCacheBytes => producer.SourceHeightCacheBytes;
        public int OverlayPagesRendered => producer.OverlayPagesRendered;
        public int OverlayDraws => producer.OverlayDraws;
        public int OverlayRepackDraws => producer.OverlayRepackDraws;
        public int Bc3CompressDispatches => bc3?.CompressDispatches ?? 0;
        public int Bc3CompressedPages => bc3?.CompressedPages ?? 0;
        public int Bc3CompressionBatches => bc3?.CompressionBatchCount ?? 0;
        public long Bc3CompressionTemporaryBytes => bc3?.CompressionTemporaryBytes ?? 0;
        public int Bc3CapturedPageCopies => bc3?.CapturedPageCopies ?? 0;
        public int Bc3DirectRenderedPages => bc3?.DirectRenderedPages ?? 0;
        public long OverlayTemporaryBytes => producer.OverlayTemporaryBytes;
        public string Error => recipeError ?? pageError;
        public bool UsesBc3 => bc3 != null;
        public long GpuBytes => disposed ? 0 : (long)Slots * archive.BytesPerPage; // Physical cache payload only.

        public MeshWorldVirtualTexture(MeshHeightSource source, string controlDirectory,
            MeshMaterialResident materials, Material settings, string archiveDirectory, Shader producerShader = null,
            MeshWorldVtOverlaySet overlays = null, Func<MeshWorldVtDecal[]> sceneDecals = null,
            Func<MeshWorldVtRoad[]> sceneRoads = null, bool immutableOverlayInputs = false)
        {
            try
            {
                CheckGraphicsSupport();
                producer = new MeshWorldVtProducer(source, controlDirectory, materials, producerShader, overlays, sceneDecals, sceneRoads, immutableOverlayInputs);
                string bc3Directory = Path.Combine(archiveDirectory, MeshWorldVtArchive.Bc3Subdirectory);
                bool useBc3 = MeshWorldVtBc3.CanEncode && File.Exists(Path.Combine(bc3Directory, MeshWorldVtArchive.ManifestName)) &&
                    SystemInfo.IsFormatSupported(MeshWorldVtBc3.ColorFormat, FormatUsage.Sample) &&
                    SystemInfo.IsFormatSupported(MeshWorldVtBc3.DataFormat, FormatUsage.Sample) && Bc3NativeCopy.Available;
                if (!useBc3)
                    throw new NotSupportedException("The 64 MB AVT pool requires the desktop D3D11 BC3 backend and its matching SVT archive. Direct shading remains available.");
                archive = new MeshWorldVtArchive(bc3Directory, producer, settings, true);
                bc3 = new MeshWorldVtBc3();
                feedbackCompactor = Resources.Load<ComputeShader>("WorldVtCompactFeedback");
                if (!feedbackCompactor)
                    throw new InvalidOperationException("The world VT feedback compaction shader is unavailable.");
                clearFeedbackKernel = feedbackCompactor.FindKernel("ClearHash");
                gatherBufferFeedbackKernel = feedbackCompactor.FindKernel("GatherBuffer");
                packFeedbackKernel = feedbackCompactor.FindKernel("Pack");
                feedbackHash = new ComputeBuffer(FeedbackHashCapacity, 2 * sizeof(uint)) { name = "Delta VT feedback hash" };
                packedFeedback = new ComputeBuffer(FeedbackPackedCapacity, 2 * sizeof(uint)) { name = "Delta VT packed feedback page IDs and counts" };
                compactFeedbackCommands = new CommandBuffer { name = "Delta VT: compact feedback requests" };
                pinned[0] = EncodeKey(RootMip, 0, 0);
                int write = 1;
                for (int z = 0; z < PagesPerAxis(PinnedMip); z++)
                for (int x = 0; x < PagesPerAxis(PinnedMip); x++)
                    pinned[write++] = EncodeKey(PinnedMip, x, z);
                foreach (uint key in pinned)
                    if (!archive.Contains(key))
                        throw new InvalidDataException("World SVT archive is missing a pinned coverage page: " + key);

                var colorFormat = MeshWorldVtBc3.ColorFormat;
                var dataFormat = MeshWorldVtBc3.DataFormat;
                AlbedoAo = CreatePool("Delta AVT shared BC3 color and normal 1", colorFormat);
                NormalRoughnessHeight = CreatePool("Delta AVT shared BC3 surface data and normal 2", dataFormat);
                colorPoolPtr = AlbedoAo.GetNativeTexturePtr();
                dataPoolPtr = NormalRoughnessHeight.GetNativeTexturePtr();
                // 目录与带 mip 的页表共享一个 uint Buffer，保持一个地址 SRV。
                // CPU 管逻辑分配与父页映射；GPU 先查目录，再直接定位表项。
                adaptive = new MeshAdaptiveVtTable(resident);
                PageTable = new ComputeBuffer(MeshAdaptiveVtTable.WordCount, sizeof(uint), ComputeBufferType.Structured)
                {
                    name = "Delta AVT directory and mip page tables"
                };
                for (int i = 0; i < upload.Length; i++)
                {
                    GraphicsFormat format = i == 0 ? colorFormat : dataFormat;
                    upload[i] = new Texture2D(PageSide, PageSide, format, TextureCreationFlags.None)
                    {
                        name = "Delta world VT SVT upload " + i,
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    if (upload[i].graphicsFormat != format)
                        throw new NotSupportedException("World VT SVT upload requires native " + format + ".");
                }
                for (int i = 0; i < feedback.Length; i++) feedback[i] = new FeedbackSlot();
                UploadTable();
                BuildPlan();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        static void CheckGraphicsSupport()
        {
            const CopyTextureSupport copy = CopyTextureSupport.Basic | CopyTextureSupport.DifferentTypes | CopyTextureSupport.RTToTexture;
            if (!SystemInfo.supportsAsyncGPUReadback || !SystemInfo.supportsComputeShaders || !SystemInfo.supports2DArrayTextures ||
                SystemInfo.maxTextureArraySlices < Slots || (SystemInfo.copyTextureSupport & copy) != copy ||
                !SystemInfo.IsFormatSupported(AlbedoFormat, FormatUsage.Sample) ||
                !SystemInfo.IsFormatSupported(DataFormat, FormatUsage.Sample) ||
                SystemInfo.supportedRandomWriteTargetCount < 2)
                throw new NotSupportedException("World VT graphics requirements not met. Required copies: " + copy +
                    ", required array slices: " + Slots + ". " + DescribeGraphicsSupport());
        }

        public static string DescribeGraphicsSupport() =>
            "Device=" + SystemInfo.graphicsDeviceType + " (" + SystemInfo.graphicsDeviceName + ")" +
            ", asyncReadback=" + SystemInfo.supportsAsyncGPUReadback +
            ", compute=" + SystemInfo.supportsComputeShaders +
            ", textureArrays=" + SystemInfo.supports2DArrayTextures +
            ", maxArraySlices=" + SystemInfo.maxTextureArraySlices +
            ", copyTexture=" + SystemInfo.copyTextureSupport +
            ", rgba8SrgbSample=" + SystemInfo.IsFormatSupported(AlbedoFormat, FormatUsage.Sample) +
            ", rgba8LinearSample=" + SystemInfo.IsFormatSupported(DataFormat, FormatUsage.Sample) +
            ", pixelUavSlots=" + SystemInfo.supportedRandomWriteTargetCount +
            ", tableRG32UIntSample=" + SystemInfo.IsFormatSupported(GraphicsFormat.R32G32_UInt, FormatUsage.Sample) +
            ", tableR32UIntSample=" + SystemInfo.IsFormatSupported(GraphicsFormat.R32_UInt, FormatUsage.Sample);

        static Texture2DArray CreatePool(string name, GraphicsFormat format)
        {
            var texture = new Texture2DArray(PageSide, PageSide, Slots, format, TextureCreationFlags.None)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            try
            {
                if (texture.graphicsFormat != format)
                    throw new NotSupportedException("World VT physical cache requires native " + format + ".");
                texture.Apply(false, true);
                return texture;
            }
            catch
            {
                MeshResidentHeight.Destroy(texture);
                throw;
            }
        }

        public static float PageWorldSize(int mip)
        {
            if ((uint)mip > RootMip) throw new ArgumentOutOfRangeException(nameof(mip));
            return MeshHeightLayout.WorldSize / (float)(Mip0PagesPerAxis >> mip);
        }

        public static int PagesPerAxis(int mip)
        {
            if ((uint)mip > RootMip) throw new ArgumentOutOfRangeException(nameof(mip));
            return Mip0PagesPerAxis >> mip;
        }

        public static uint EncodeKey(int mip, int x, int z)
        {
            int count = PagesPerAxis(mip);
            if ((uint)x >= count || (uint)z >= count) throw new ArgumentOutOfRangeException(nameof(x), "Page lies outside the terrain.");
            // 按完整 mip 金字塔连续编号。1,431,655,765 张逻辑页可放入 uint；
            // 这是稳定的世界页键，与 AVT 会迁移的页表空间反馈地址不同。
            uint offset = 0;
            for (int level = 0; level < mip; level++)
            {
                uint side = (uint)(Mip0PagesPerAxis >> level);
                offset += side * side;
            }
            return offset + (uint)(z * count + x) + 1;
        }

        public static bool DecodeKey(uint key, out int mip, out int x, out int z)
        {
            uint remaining = key - 1;
            for (mip = 0; mip <= RootMip; mip++)
            {
                uint side = (uint)(Mip0PagesPerAxis >> mip);
                uint count = side * side;
                if (remaining < count)
                {
                    x = (int)(remaining % side);
                    z = (int)(remaining / side);
                    return true;
                }
                remaining -= count;
            }
            mip = x = z = 0;
            return false;
        }

        internal static int NormalizeMaxAnisotropy(int value) => value == 1 || value == 2 ? value : 4;

        public void Bind(Material material, int mode, float mipBias, int maxAnisotropy = 4)
        {
            maxAnisotropy = NormalizeMaxAnisotropy(maxAnisotropy);
            if (maxAnisotropy > 1) material.EnableKeyword("DD_WORLD_VT_ANISOTROPIC");
            else material.DisableKeyword("DD_WORLD_VT_ANISOTROPIC");
            if (UsesBc3) material.EnableKeyword("DD_WORLD_VT_BC3");
            else material.DisableKeyword("DD_WORLD_VT_BC3");
            material.SetBuffer("_DDVtTable", PageTable);
            material.SetTexture("_DDVtAlbedoAo", AlbedoAo);
            material.SetTexture("_DDVtNormalRoughnessHeight", NormalRoughnessHeight);
            material.SetFloat("_DDVtReady", Ready ? 1 : 0);
            material.SetInt("_DDVtMode", mode);
            material.SetFloat("_DDVtMipBias", mipBias);
            material.SetFloat("_DDVtMaxAnisotropy", maxAnisotropy);
        }

        public bool BeginMainFeedback(CommandBuffer commands, Material material, int divisor, int sampleX, int sampleY)
        {
            if (disposed || !recipeMatches) return false;
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
                    slot.Records = new NativeArray<uint>(FeedbackPackedCapacity * 2, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
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

        public void SubmitMainFeedback(ScriptableRenderContext context)
        {
            FeedbackSlot slot = mainFeedbackSlot;
            mainFeedbackSlot = null;
            if (disposed || slot == null || slot.Epoch != feedbackEpoch) return;
            // Queue the dispatch AND readback after this camera's MRT draw through
            // the same SRP context. An immediate Graphics readback here could run
            // before the context's deferred draw commands have been submitted.
            CompactFeedback();
            slot.Pending = true;
            slot.ReadbackReady = slot.ReadbackError = false;
            compactFeedbackCommands.RequestAsyncReadback(packedFeedback, request =>
            {
                if (disposed || !slot.Records.IsCreated) return;
                slot.ReadbackError = request.hasError;
                if (!request.hasError) request.GetData<uint>().CopyTo(slot.Records);
                slot.ReadbackReady = true;
            });
            context.ExecuteCommandBuffer(compactFeedbackCommands);
            FeedbackSubmitted++;
            MainFeedbackSubmitted++;
        }

        void CompactFeedback()
        {
            CommandBuffer commands = compactFeedbackCommands;
            commands.Clear();
            commands.SetComputeIntParam(feedbackCompactor, "_HashCapacity", FeedbackHashCapacity);
            commands.SetComputeIntParam(feedbackCompactor, "_PackedCapacity", FeedbackPackedCapacity);
            // 按环形顺序轮换列表起点，列表溢出时不会每次只搬同一批页。
            commands.SetComputeIntParam(feedbackCompactor, "_HashStart",
                (int)(feedbackSequence * (FeedbackPackedCapacity - 1) & (FeedbackHashCapacity - 1)));
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

        /// <summary>切换视角后丢弃旧反馈，保留已驻留的有效页面。</summary>
        public void InvalidateFeedback()
        {
            feedbackEpoch++;
            mainFeedbackSlot = null;
            feedbackSweepLeft = LatestFeedbackUniqueKeys = LatestFeedbackOverflowKeys = LatestFeedbackFailedSamples = 0;
            LatestFeedbackFrame = -1;
            requests.Clear();
            adaptive?.ClearRequests();
            producer?.CancelRuntimeSources();
            BuildPlan();
        }

        public void Advance(Material settings, int pageBudget, int nextFrame)
        {
            if (disposed || frame == nextFrame) return;
            frame = nextFrame;
            PagesPublishedThisFrame = 0;

            RefreshArchiveRecipe(settings);
            PollFeedback();
            if (!recipeMatches || pageBudget <= 0)
            {
                producer.CancelRuntimeSources();
                return;
            }

            // 逻辑区域扩张/缩减和材质写页分别限额。新区域先等待它的 root 材质页，
            // 下一帧再由分配器发布目录；不会先公开一块没有保底映射的精细区域。
            RecentVisiblePages = CountRecentVisiblePages();
            if (adaptive.UpdateAllocations(frame, RecentVisiblePages))
            {
                BuildPlan();
                ReclaimUnaddressablePages();
                RecentVisiblePages = CountRecentVisiblePages();
            }
            DiscardObsoleteReads();
            QueueSvtReads();
            PrepareRvtSources();
            ProcessPageBudget(settings, Mathf.Min(pageBudget, 4));
            UploadTable();
            if (planDirty) BuildPlan();
        }

        void RefreshArchiveRecipe(Material settings)
        {
            // 同一个页表可能指向 SVT 或 RVT，两种页必须采用同一套材质参数。
            bool matches = archive.MatchesRecipe(settings);
            if (recipeMatches != matches)
            {
                recipeMatches = matches;
                InvalidateFeedback();
            }
            recipeError = recipeMatches ? null :
                "World VT material recipe differs from the SVT archive. Restore the baked settings or rebuild the archive.";
        }

        void PrepareRvtSources()
        {
            // 与GPU页生产预算解耦；等待磁盘的页不预留物理槽，也不消耗四页预算。
            sourceWork.Clear();
            if (CoverageSettled)
                foreach (uint key in work)
                    if (!resident.ContainsKey(key) && !failedPages.Contains(key) && !archive.Contains(key) &&
                        DecodeKey(key, out int mip, out _, out _) && mip < FirstSvtMip)
                        sourceWork.Add(key);
            producer.PrepareRuntimeSources(sourceWork);
        }

        void ProcessPageBudget(Material settings, int remaining)
        {
            compressionPending.Clear();
            compressionReservedSlots.Clear();
            bool batchStarted = false;
            try
            {
                foreach (uint key in work)
                {
                    if (remaining == 0) break;
                    if (resident.ContainsKey(key) || failedPages.Contains(key)) continue;
                    // Coarse pinned SVT coverage still comes first. Streaming pages
                    // already contain BC3 and are copied directly, never recompressed.
                    if (!CoverageSettled && !IsPinned(key)) break;
                    bool streamed = archive.Contains(key);
                    if (!streamed && (!DecodeKey(key, out int mip, out _, out _) || mip >= FirstSvtMip))
                        continue;
                    // 不因为高优先级源尚未就绪而挡住其它ready页。
                    if (!streamed && !producer.HasPreparedRuntimeSource(key)) continue;
                    int slot = SelectSlot();
                    if (slot < 0) break;
                    byte[] bytes = null;
                    if (streamed && !TryTakeSvtPayload(key, out bytes)) continue;
                    try
                    {
                        if (streamed)
                        {
                            remaining--;
                            PopulateSvtPage(key, slot, bytes);
                        }
                        else
                        {
                            if (!batchStarted) { bc3.BeginBatch(); batchStarted = true; }
                            if (!producer.TryRenderPreparedIntoCompressionBatch(key, settings, bc3)) continue;
                            remaining--;
                            // Each page is drawn straight into its private batch slice.
                            // Only the small R16 fallback window needs capturing before
                            // the source-window texture is reused by the next page.
                            bc3.QueueRenderedPage(producer.CompressionUsesOverlayMrt,
                                producer.CompressionSourceHeight, producer.CompressionHeightInfo,
                                producer.CompressionTexelGrid, colorPoolPtr, dataPoolPtr, slot);
                            RvtPagesRendered++;
                            compressionPending.Add(new PendingCompressedPage { Key = key, Slot = slot });
                            compressionReservedSlots.Add(slot);
                        }
                    }
                    catch (Exception exception) { RecordFailure(key, exception); }
                }

                if (compressionPending.Count > 0)
                {
                    // Retire old mappings before a flush can overwrite their slots.
                    // Publish all new mappings only after encoding and both pool copies
                    // have been submitted, ahead of UploadTable on the same GPU queue.
                    foreach (PendingCompressedPage page in compressionPending) RetireResidentPage(page.Slot);
                    try
                    {
                        bc3.FlushBatch();
                        foreach (PendingCompressedPage page in compressionPending)
                            RegisterResidentPage(page.Key, page.Slot, false);
                    }
                    catch (Exception exception)
                    {
                        foreach (PendingCompressedPage page in compressionPending) RecordFailure(page.Key, exception);
                    }
                }
            }
            finally
            {
                if (batchStarted) bc3.CancelBatch();
                compressionPending.Clear();
                compressionReservedSlots.Clear();
            }
        }

        bool TryTakeSvtPayload(uint key, out byte[] bytes)
        {
            // 只取已完成并通过校验的数据，磁盘慢时跳过这页，避免阻塞相机。
            bytes = null;
            if (!pendingReads.TryGetValue(key, out Task<byte[]> read) || !read.IsCompleted) return false;
            pendingReads.Remove(key);
            try
            {
                bytes = read.GetAwaiter().GetResult();
                return true;
            }
            catch (Exception exception)
            {
                RecordFailure(key, exception);
                return false;
            }
        }

        void PollFeedback()
        {
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
                if (slot.Records[0] >= FeedbackPackedCapacity) FeedbackTruncatedFrames++;
                if (slot.Records[1] != 0) FeedbackOverflowFrames++;
                if (slot.Records[1] != 0 && feedbackSweepLeft == 0)
                {
                    feedbackHashSeed++;
                    feedbackSweepLeft = FeedbackRecoveryStride;
                }
                if (newest == null || slot.Sequence > newest.Sequence) newest = slot;
            }
            if (newest == null) return;
            LatestFeedbackSequence = newest.Sequence;
            LatestFeedbackFrame = newest.Frame;
            incoming.Clear();
            LatestFeedbackUniqueKeys = (int)newest.Records[0];
            LatestFeedbackOverflowKeys = Math.Max(0, LatestFeedbackUniqueKeys - (FeedbackPackedCapacity - 1));
            LatestFeedbackFailedSamples = (int)newest.Records[1];
            int recordCount = Math.Min(LatestFeedbackUniqueKeys, FeedbackPackedCapacity - 1);
            adaptive.BeginFeedback();
            for (int i = 1; i <= recordCount; i++)
            {
                // GPU 已将相同的局部页键合并，count 仍保留屏幕覆盖权重。
                // 页键指向生成反馈时的局部页表区域；
                // 分配器检查产生帧，防止旧反馈把已复用的地址当成另一块世界地形。
                int invalidBefore = adaptive.InvalidFeedback;
                if (!adaptive.DecodeFeedback(newest.Records[i * 2], newest.Frame, frame, out uint key))
                {
                    InvalidFeedbackKeys += adaptive.InvalidFeedback - invalidBefore;
                    continue;
                }
                MarkFeedbackUsage(key);
                AddCount(incoming, key, (int)newest.Records[i * 2 + 1]);
            }
            if (SameRequests()) return;
            requests.Clear();
            foreach (var pair in incoming) requests.Add(pair.Key, pair.Value);
            BuildPlan();
        }

        bool SameRequests()
        {
            if (incoming.Count != requests.Count) return false;
            foreach (var pair in incoming)
                if (!requests.TryGetValue(pair.Key, out int count) || count != pair.Value) return false;
            return true;
        }

        void MarkFeedbackUsage(uint key)
        {
            if (!DecodeKey(key, out int mip, out int x, out int z)) return;
            // 与UE FindNearestPageAddress/UpdateUsage一致：缺页时，实际可见的是
            // 最近的驻留父页。不要把预取的整条父链都算成被GPU看到的物理页。
            for (int parent = mip; parent <= RootMip; parent++)
            {
                int shift = parent - mip;
                if (!resident.TryGetValue(EncodeKey(parent, x >> shift, z >> shift), out int slot)) continue;
                slots[slot].LastFeedbackFrame = frame;
                slots[slot].LastVisibleFrame = frame;
                return;
            }
        }

        int CountRecentVisiblePages()
        {
            int threshold = frame > RecentVisibilityFrames ? frame - RecentVisibilityFrames : 0, count = 0;
            foreach (ResidentPage page in slots)
                // UE把锁定页计为可见；普通缓存页则须在最近15帧由反馈命中过。
                if (page.Key != 0 && (locked.Contains(page.Key) || page.LastFeedbackFrame > threshold)) count++;
            return count;
        }

        void ReclaimUnaddressablePages()
        {
            // 缩容后过细的页不再有任何逻辑地址能引用。先撤销其映射与CPU槽占用，
            // 槽内旧纹理字节可留着，下一次填页会完整覆盖两个通道，无需GPU清图。
            for (int slot = 0; slot < slots.Length; slot++)
            {
                uint key = slots[slot].Key;
                if (key == 0 || adaptive.CanRepresent(key) || wanted.Contains(key) ||
                    locked.Contains(key) || pendingReads.ContainsKey(key)) continue;
                RetireResidentPage(slot);
                AdaptivePagesReclaimed++;
            }
        }

        static void AddCount(Dictionary<uint, int> map, uint key, int count)
        {
            map.TryGetValue(key, out int previous);
            map[key] = previous + count;
        }

        void BuildPlan()
        {
            planDirty = false;
            work.Clear();
            wanted.Clear();
            locked.Clear();
            scores.Clear();
            candidates.Clear();
            foreach (uint key in pinned)
            {
                work.Add(key);
                wanted.Add(key);
                locked.Add(key);
            }
            // 每个活跃 allocation 的 root 页必须保留；发布目录之前也优先补齐它。
            // 它们与 RVT/SVT 细页共用同一物理池，没有专属 mip0 池。
            foreach (uint key in adaptive.LockedRoots)
            {
                locked.Add(key);
                if (!wanted.Add(key)) continue;
                work.Add(key);
            }
            foreach (var pair in requests)
            {
                uint key = pair.Key;
                if (!DecodeKey(key, out int mip, out int x, out int z)) continue;
                // 异步旧请求可能仍停留在刚缩容区域的精度。不可表达的细页不再
                // 进入wanted，也不会在归还槽后立刻被旧计划重新生成。
                if (!adaptive.CanRepresent(key)) continue;
                // 直接请求优先。父页只加 SVT 粗页，让多处请求可以共用同一张保底页。
                // RVT 细页只根据实际反馈生成。
                AddCount(scores, key, pair.Value * 8);
                for (int parentMip = Math.Max(mip + 1, FirstSvtMip); parentMip < PinnedMip; parentMip++)
                {
                    int steps = parentMip - mip;
                    AddCount(scores, EncodeKey(parentMip, x >> steps, z >> steps), pair.Value);
                }
            }
            foreach (var pair in scores)
            {
                if (wanted.Contains(pair.Key) || failedPages.Contains(pair.Key)) continue;
                candidates.Add(new Candidate { Key = pair.Key, Score = pair.Value });
            }
            candidates.Sort(compareCandidates);
            foreach (Candidate candidate in candidates)
            {
                if (work.Count >= Slots) break;
                work.Add(candidate.Key);
                wanted.Add(candidate.Key);
            }
            RequestsDropped = 0;
            foreach (uint key in requests.Keys)
                if (!wanted.Contains(key)) RequestsDropped++;
            foreach (uint key in work)
                if (resident.TryGetValue(key, out int slot)) slots[slot].LastVisibleFrame = frame;
        }

        static int CompareCandidates(Candidate a, Candidate b)
        {
            int priority = b.Score.CompareTo(a.Score);
            // 分数相同时优先较粗的页，覆盖范围更大。
            return priority != 0 ? priority : b.Key.CompareTo(a.Key);
        }

        void DiscardObsoleteReads()
        {
            obsoleteReads.Clear();
            foreach (var pair in pendingReads)
            {
                if (!pair.Value.IsCompleted || wanted.Contains(pair.Key)) continue;
                if (pair.Value.IsFaulted) _ = pair.Value.Exception;
                obsoleteReads.Add(pair.Key);
            }
            foreach (uint key in obsoleteReads) pendingReads.Remove(key);
        }

        void QueueSvtReads()
        {
            foreach (uint key in work)
            {
                if (pendingReads.Count >= PendingReadLimit) break;
                if (!CoverageSettled && !IsPinned(key)) break;
                if (resident.ContainsKey(key) || pendingReads.ContainsKey(key) || failedPages.Contains(key) || !archive.Contains(key))
                    continue;
                try { pendingReads.Add(key, archive.ReadAsync(key, ioCancellation.Token)); }
                catch (Exception exception) { RecordFailure(key, exception); }
            }
        }

        int SelectSlot()
        {
            int best = -1;
            for (int i = 0; i < slots.Length; i++)
            {
                if (compressionReservedSlots.Contains(i)) continue;
                uint key = slots[i].Key;
                if (key == 0) return i;
                // 本轮计划里的目标页、父页和保底页都不能被同轮更新淘汰。
                if (wanted.Contains(key)) continue;
                if (best < 0 || slots[i].LastVisibleFrame < slots[best].LastVisibleFrame) best = i;
            }
            return best;
        }

        static bool IsPinned(uint key) => DecodeKey(key, out int mip, out _, out _) && (mip == RootMip || mip == PinnedMip);

        void StageSvtPage(byte[] bytes)
        {
            // 预烘焙页的两个通道依次排列，先分别上传到可复用的临时纹理。
            if (bytes == null || bytes.Length != archive.BytesPerPage) throw new InvalidDataException("Wrong SVT page payload size.");
            int channelBytes = archive.BytesPerPage / upload.Length;
            for (int channel = 0; channel < upload.Length; channel++)
            {
                upload[channel].SetPixelData(bytes, 0, channel * channelBytes);
                upload[channel].Apply(false, false);
            }
        }

        void PopulateSvtPage(uint key, int slot, byte[] bytes)
        {
            // SVT already contains the final compressed channels. Preserve the
            // direct upload/copy path and publish only after both copies are queued.
            StageSvtPage(bytes);
            RetireResidentPage(slot);
            Graphics.CopyTexture(upload[0], 0, 0, AlbedoAo, slot, 0);
            Graphics.CopyTexture(upload[1], 0, 0, NormalRoughnessHeight, slot, 0);
            RegisterResidentPage(key, slot, true);
        }

        void RetireResidentPage(int slot)
        {
            // 先移除旧页。拷贝任一通道失败时，页表更新会撤下旧映射，避免采到半张新页。
            ResidentPage previous = slots[slot];
            if (previous.Key == 0) return;
            resident.Remove(previous.Key);
            adaptive.PageRemoved(previous.Key, slot);
            slots[slot] = default;
            if (previous.Streamed) ResidentSvtCount--;
            Evictions++;
        }

        void RegisterResidentPage(uint key, int slot, bool streamed)
        {
            // 通道拷贝和页表上传共用图形队列，地形绘制看到新映射时，两次拷贝已排在前面。
            slots[slot] = new ResidentPage { Key = key, LastVisibleFrame = frame,
                LastFeedbackFrame = int.MinValue, Streamed = streamed };
            resident.Add(key, slot);
            adaptive.PageAdded(key, slot);
            if (streamed)
            {
                SvtPagesUploaded++;
                ResidentSvtCount++;
            }
            if (IsPinned(key)) PinnedPagesPublished++;
            PagesPublishedThisFrame++;
        }

        void UploadTable()
        {
            // 只传 dirty chunk；静止且页面/目录均不变时，分配器不提交上传。
            if (adaptive.Upload(PageTable)) TableUploads++;
        }

        void RecordFailure(uint key, Exception exception)
        {
            if (failedPages.Add(key) && IsPinned(key)) failedPinnedPages++;
            planDirty = true;
            pageError = "World VT page " + key + ": " + exception.Message;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ioCancellation.Cancel();
            // 磁盘任务只持有文件和字节数组，销毁时异步处理它们的异常，避免等磁盘。
            foreach (Task<byte[]> read in pendingReads.Values)
                _ = read.ContinueWith(task => { _ = task.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            pendingReads.Clear();
            ioCancellation.Dispose();
            foreach (FeedbackSlot slot in feedback) slot?.Release();
            mainFeedbackRaw?.Release();
            compactFeedbackCommands?.Release();
            packedFeedback?.Release();
            feedbackHash?.Release();
            foreach (Texture2D texture in upload) MeshResidentHeight.Destroy(texture);
            PageTable?.Release();
            MeshResidentHeight.Destroy(AlbedoAo);
            MeshResidentHeight.Destroy(NormalRoughnessHeight);
            PageTable = null;
            AlbedoAo = NormalRoughnessHeight = null;
            bc3?.Dispose();
            producer?.Dispose();
            resident.Clear();
            requests.Clear();
            ResidentSvtCount = PinnedPagesPublished = 0;
        }
    }
}
