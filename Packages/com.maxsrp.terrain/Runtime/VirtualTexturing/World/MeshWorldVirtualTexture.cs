using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Profiling;
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
        // CPU preparation/submission scopes; command-buffer samples report GPU work separately.
        static readonly ProfilerMarker advanceMarker = new ProfilerMarker("MaxTerrain.VT.Advance");
        static readonly ProfilerMarker prefetchMarker = new ProfilerMarker("MaxTerrain.VT.Prefetch.Update");
        static readonly ProfilerMarker recipeMarker = new ProfilerMarker("MaxTerrain.VT.RecipeValidation");
        static readonly ProfilerMarker planMarker = new ProfilerMarker("MaxTerrain.VT.BuildPlan");
        static readonly ProfilerMarker allocationsMarker = new ProfilerMarker("MaxTerrain.VT.AVT.UpdateAllocations");
        static readonly ProfilerMarker reclaimMarker = new ProfilerMarker("MaxTerrain.VT.AVT.ReclaimPages");
        static readonly ProfilerMarker queueReadsMarker = new ProfilerMarker("MaxTerrain.VT.SVT.QueueReads");
        static readonly ProfilerMarker prepareSourcesMarker = new ProfilerMarker("MaxTerrain.VT.RVT.PrepareSources");
        static readonly ProfilerMarker productionMarker = new ProfilerMarker("MaxTerrain.VT.ProducePages");
        static readonly ProfilerMarker batchMarker = new ProfilerMarker("MaxTerrain.VT.ProduceBatch");
        static readonly ProfilerMarker selectSlotMarker = new ProfilerMarker("MaxTerrain.VT.SelectSlot");
        static readonly ProfilerMarker slotCandidatesMarker = new ProfilerMarker("MaxTerrain.VT.BuildSlotCandidates");
        static readonly ProfilerMarker svtUploadMarker = new ProfilerMarker("MaxTerrain.VT.SVT.UploadTextures");
        static readonly ProfilerMarker svtPopulateMarker = new ProfilerMarker("MaxTerrain.VT.SVT.PublishPage");
        static readonly ProfilerMarker svtCopyMarker = new ProfilerMarker("MaxTerrain.VT.SVT.CopyToPool");
        static readonly ProfilerMarker retirePageMarker = new ProfilerMarker("MaxTerrain.VT.PageTable.RemovePage");
        static readonly ProfilerMarker registerPageMarker = new ProfilerMarker("MaxTerrain.VT.PageTable.AddPage");
        static readonly ProfilerMarker tableUploadMarker = new ProfilerMarker("MaxTerrain.VT.PageTable.Upload");

        public const int PageInterior = 128;
        public const int Border = 8;
        public const int PageSide = PageInterior + Border * 2;
        // Each array slice holds 4x4 unchanged BC3 pages. A 360 MB pool no longer
        // requires 8680 array slices (above D3D11's limit), or additional SRVs.
        public const int PoolPageAxis = 4;
        public const int PagesPerPoolSlice = PoolPageAxis * PoolPageAxis;
        public const int PoolSide = PageSide * PoolPageAxis;
        public const int MaximumSlots = 65520; // slot+1 occupies the low 16 bits.
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
        const int PendingReadLimit = MaxTerrainConfig.MaximumPagesPerFrame * 2;
        public const int RecentVisibilityFrames = 15; // UE r.VT.PageFreeThreshold的默认窗口。
        const int FeedbackPackedCapacity = MeshWorldVtFeedback.PackedCapacity;
        public const int FeedbackReadbackBytes = FeedbackPackedCapacity * 2 * sizeof(uint);
        const int MaximumPlanCandidates = FeedbackPackedCapacity * (PinnedMip - FirstSvtMip + 1);

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
        readonly MeshWorldVtSvtQueue svtReads;
        readonly MeshWorldVtBc3 bc3;
        readonly IntPtr colorStagingPtr, dataStagingPtr;
        Texture2DArray colorStaging, dataStaging;
        readonly Texture2D[] upload = new Texture2D[2];
        readonly ResidentPage[] slots;
        readonly MeshWorldVtFeedback feedback;
        readonly MeshAdaptiveVtTable adaptive;
        readonly uint[] pinned = new uint[PinnedPageCount];
        readonly Dictionary<uint, int> resident;
        readonly HashSet<uint> failedPages = new HashSet<uint>();
        readonly Dictionary<uint, int> requests = new Dictionary<uint, int>(FeedbackPackedCapacity);
        readonly Dictionary<uint, int> incoming = new Dictionary<uint, int>(FeedbackPackedCapacity);
        readonly Dictionary<uint, int> scores = new Dictionary<uint, int>(MaximumPlanCandidates);
        readonly List<Candidate> candidates = new List<Candidate>(MaximumPlanCandidates);
        readonly List<uint> work;
        readonly List<uint> sourceWork;
        readonly List<uint> svtReadWork;
        readonly HashSet<uint> wanted;
        readonly HashSet<uint> locked = new HashSet<uint>(PinnedPageCount + MeshAdaptiveVtTable.MaxAllocations);
        readonly MeshWorldVtPrefetch prefetch = new MeshWorldVtPrefetch();
        readonly HashSet<uint> prefetchWanted = new HashSet<uint>(MeshWorldVtPrefetch.MaximumPages);
        readonly MeshWorldVtSlotCandidates slotCandidates;
        static readonly Comparison<Candidate> compareCandidates = CompareCandidates;

        struct PendingCompressedPage { public uint Key; public int Slot; }
        readonly List<PendingCompressedPage> compressionPending = new List<PendingCompressedPage>(4);
        readonly HashSet<int> compressionReservedSlots = new HashSet<int>(MeshWorldVtBc3.MaximumBatchPages);
        bool disposed, recipeMatches = true, planDirty, slotCandidatesDirty = true;
        int frame = int.MinValue, failedPinnedPages;
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
        public int PrefetchRequestedCount => prefetchWanted.Count;
        public int PrefetchResidentCount
        {
            get
            {
                int count = 0;
                var keys = prefetch.Keys;
                for (int i = 0; i < keys.Count; i++) if (resident.ContainsKey(keys[i])) count++;
                return count;
            }
        }
        public int PrefetchCoverageCount => prefetch.Keys.Count;
        public int RequestsDropped { get; private set; }
        public int PendingReads => svtReads?.PendingCount ?? 0;
        public int FeedbackSubmitted => feedback.FeedbackSubmitted;
        public int MainFeedbackSubmitted => FeedbackSubmitted;
        public int CompletedFeedback => feedback.CompletedFeedback;
        public int FeedbackErrors => feedback.FeedbackErrors;
        public int FeedbackDropped => feedback.FeedbackDropped;
        // Fixed 4 KiB feedback deliberately defers unique keys beyond 511.
        // Keep this normal budget truncation separate from failed GPU inserts.
        public int FeedbackTruncatedFrames => feedback.FeedbackTruncatedFrames;
        public int FeedbackOverflowFrames => feedback.FeedbackOverflowFrames;
        public int LatestFeedbackUniqueKeys => feedback.LatestFeedbackUniqueKeys;
        public int LatestFeedbackOverflowKeys => feedback.LatestFeedbackOverflowKeys;
        public int LatestFeedbackFailedSamples => feedback.LatestFeedbackFailedSamples;
        public long FeedbackBytesSubmitted => (long)FeedbackSubmitted * FeedbackReadbackBytes;
        public int InvalidFeedbackKeys { get; private set; }
        public long LatestFeedbackSequence => feedback.LatestFeedbackSequence;
        public int LatestFeedbackFrame => feedback.LatestFeedbackFrame;
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
        public int Slots { get; }
        public int PhysicalCacheMB { get; }
        public int PoolSlices => Slots / PagesPerPoolSlice;
        public long GpuBytes => disposed ? 0 : (long)Slots * archive.BytesPerPage; // Physical cache payload only.
        public long PoolStagingBytes => disposed || !colorStaging ? 0 : (long)MeshWorldVtBc3.MaximumBatchPages * MeshWorldVtBc3.PageBytes;

        public static int SlotsForBudget(int physicalCacheMB)
        {
            int mb = Mathf.Clamp(physicalCacheMB, MaxTerrainConfig.MinimumPhysicalCacheMB, MaxTerrainConfig.MaximumPhysicalCacheMB);
            long sliceBytes = (long)PagesPerPoolSlice * MeshWorldVtBc3.PageBytes;
            return (int)Math.Min(MaximumSlots / PagesPerPoolSlice, mb * 1000000L / sliceBytes) * PagesPerPoolSlice;
        }

        public MeshWorldVirtualTexture(MeshHeightSource source, string controlDirectory,
            MeshMaterialResident materials, Material settings, string archiveDirectory, Shader producerShader = null,
            MeshWorldVtOverlaySet overlays = null, Func<MeshWorldVtDecal[]> sceneDecals = null,
            Func<MeshWorldVtRoad[]> sceneRoads = null, bool immutableOverlayInputs = false,
            int physicalCacheMB = MaxTerrainConfig.DefaultPcPhysicalCacheMB)
        {
            PhysicalCacheMB = Mathf.Clamp(physicalCacheMB, MaxTerrainConfig.MinimumPhysicalCacheMB, MaxTerrainConfig.MaximumPhysicalCacheMB);
            Slots = SlotsForBudget(PhysicalCacheMB);
            slots = new ResidentPage[Slots];
            resident = new Dictionary<uint, int>(Slots);
            work = new List<uint>(Slots);
            sourceWork = new List<uint>(Slots);
            svtReadWork = new List<uint>(Slots);
            wanted = new HashSet<uint>(Slots);
            slotCandidates = new MeshWorldVtSlotCandidates(Slots);
            try
            {
                CheckGraphicsSupport(PoolSlices);
                producer = new MeshWorldVtProducer(source, controlDirectory, materials, producerShader, overlays, sceneDecals, sceneRoads, immutableOverlayInputs);
                string bc3Directory = Path.Combine(archiveDirectory, MeshWorldVtArchive.Bc3Subdirectory);
                bool useBc3 = MeshWorldVtBc3.CanEncode && File.Exists(Path.Combine(bc3Directory, MeshWorldVtArchive.ManifestName)) &&
                    SystemInfo.IsFormatSupported(MeshWorldVtBc3.ColorFormat, FormatUsage.Sample) &&
                    SystemInfo.IsFormatSupported(MeshWorldVtBc3.DataFormat, FormatUsage.Sample) && Bc3NativeCopy.Available;
                if (!useBc3)
                    throw new NotSupportedException("The AVT pool requires the desktop D3D11 BC3 backend and its matching SVT archive. Direct shading remains available.");
                archive = new MeshWorldVtArchive(bc3Directory, producer, settings, true);
                svtReads = archive.CreateRuntimeQueue(PendingReadLimit, Slots);
                bc3 = new MeshWorldVtBc3();
                feedback = new MeshWorldVtFeedback();
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
                AlbedoAo = CreatePool("Delta AVT shared BC3 color and normal 1", colorFormat, PoolSide, PoolSlices);
                NormalRoughnessHeight = CreatePool("Delta AVT shared BC3 surface data and normal 2", dataFormat, PoolSide, PoolSlices);
                // The existing native bridge writes one page per slice. Keep four
                // scratch pages, then copy unchanged compressed blocks into the pool.
                colorStaging = CreatePool("Delta AVT BC3 commit color staging", colorFormat, PageSide, MeshWorldVtBc3.MaximumBatchPages);
                dataStaging = CreatePool("Delta AVT BC3 commit data staging", dataFormat, PageSide, MeshWorldVtBc3.MaximumBatchPages);
                colorStagingPtr = colorStaging.GetNativeTexturePtr();
                dataStagingPtr = dataStaging.GetNativeTexturePtr();
                // 目录与带 mip 的页表共享一个 uint Buffer，保持一个地址 SRV。
                // CPU 管逻辑分配与父页映射；GPU 先查目录，再直接定位表项。
                adaptive = new MeshAdaptiveVtTable(resident, Slots);
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
                UploadTable();
                BuildPlan();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        static void CheckGraphicsSupport(int poolSlices)
        {
            const CopyTextureSupport copy = CopyTextureSupport.Basic | CopyTextureSupport.DifferentTypes | CopyTextureSupport.RTToTexture;
            if (!SystemInfo.supportsAsyncGPUReadback || !SystemInfo.supportsComputeShaders || !SystemInfo.supports2DArrayTextures ||
                SystemInfo.maxTextureArraySlices < poolSlices || SystemInfo.maxTextureSize < PoolSide ||
                (SystemInfo.copyTextureSupport & copy) != copy ||
                !SystemInfo.IsFormatSupported(AlbedoFormat, FormatUsage.Sample) ||
                !SystemInfo.IsFormatSupported(DataFormat, FormatUsage.Sample) ||
                SystemInfo.supportedRandomWriteTargetCount < 2)
                throw new NotSupportedException("World VT graphics requirements not met. Required copies: " + copy +
                    ", required array slices: " + poolSlices + ", pool side: " + PoolSide + ". " + DescribeGraphicsSupport());
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

        static Texture2DArray CreatePool(string name, GraphicsFormat format, int side, int slices)
        {
            var texture = new Texture2DArray(side, side, slices, format, TextureCreationFlags.None)
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
            material.SetInt("_DDVtSlotCount", Slots);
            material.SetFloat("_DDVtReady", Ready ? 1 : 0);
            material.SetInt("_DDVtMode", mode);
            material.SetFloat("_DDVtMipBias", mipBias);
            material.SetFloat("_DDVtMaxAnisotropy", maxAnisotropy);
        }

        public bool BeginMainFeedback(CommandBuffer commands, Material material, int divisor, int sampleX, int sampleY)
            => !disposed && recipeMatches && feedback.Begin(commands, material, frame, divisor, sampleX, sampleY);

        public void SubmitMainFeedback(ScriptableRenderContext context) => feedback.Submit(context);

        /// <summary>切换视角后丢弃旧反馈，保留已驻留的有效页面。</summary>
        public void InvalidateFeedback()
        {
            feedback?.Invalidate();
            requests.Clear();
            adaptive?.ClearRequests();
            producer?.CancelRuntimeSources();
            BuildPlan();
        }

        public void Advance(Material settings, int pageBudget, int nextFrame)
            => Advance(settings, pageBudget, nextFrame, new Vector2(float.NaN, float.NaN));

        public void Advance(Material settings, int pageBudget, int nextFrame, Vector2 prefetchPosition)
        {
            using var profile = advanceMarker.Auto();
            if (disposed || frame == nextFrame) return;
            frame = nextFrame;
            PagesPublishedThisFrame = 0;

            bool prefetchChanged;
            using (prefetchMarker.Auto()) prefetchChanged = prefetch.Update(prefetchPosition);
            RefreshArchiveRecipe(settings);
            PollFeedback();
            if (prefetchChanged) BuildPlan();
            if (!recipeMatches || pageBudget <= 0)
            {
                producer.CancelRuntimeSources();
                return;
            }

            // 逻辑区域扩张/缩减和材质写页分别限额。新区域先等待它的 root 材质页，
            // 下一帧再由分配器发布目录；不会先公开一块没有保底映射的精细区域。
            using (allocationsMarker.Auto())
            {
                RecentVisiblePages = CountRecentVisiblePages();
                if (adaptive.UpdateAllocations(frame, RecentVisiblePages))
                {
                    BuildPlan();
                    ReclaimUnaddressablePages();
                    RecentVisiblePages = CountRecentVisiblePages();
                }
            }
            QueueSvtReads();
            PrepareRvtSources();
            ProcessPageBudget(settings, Mathf.Clamp(pageBudget,
                MaxTerrainConfig.MinimumPagesPerFrame, MaxTerrainConfig.MaximumPagesPerFrame));
            UploadTable();
            if (planDirty) BuildPlan();
        }

        void RefreshArchiveRecipe(Material settings)
        {
            using var profile = recipeMarker.Auto();
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
            using var profile = prepareSourcesMarker.Auto();
            // 与GPU页生产预算解耦；等待磁盘的页不预留物理槽，也不消耗生产预算。
            sourceWork.Clear();
            if (CoverageSettled)
                foreach (uint key in work)
                    if (!resident.ContainsKey(key) && !failedPages.Contains(key) && !archive.Contains(key) &&
                        DecodeKey(key, out int mip, out _, out _) && mip < FirstSvtMip)
                        sourceWork.Add(key);
            producer.PrepareRuntimeSources(sourceWork, Slots);
        }

        void ProcessPageBudget(Material settings, int remaining)
        {
            using var profile = productionMarker.Auto();
            slotCandidatesDirty = true;
            // The encoder keeps its four-slice resources and unchanged BC3 math.
            // Submit several complete batches on the same graphics queue for PC.
            while (remaining > 0)
            {
                int consumed = ProcessPageBatch(settings, Math.Min(remaining, MeshWorldVtBc3.MaximumBatchPages));
                if (consumed == 0) break; // Never wait or spin on unfinished source I/O.
                remaining -= consumed;
            }
        }

        int ProcessPageBatch(Material settings, int remaining)
        {
            using var profile = batchMarker.Auto();
            int budget = remaining;
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
                    MeshWorldVtSvtQueue.Payload payload = default;
                    if (streamed && !TryTakeSvtPayload(key, out payload)) continue;
                    try
                    {
                        if (streamed)
                        {
                            remaining--;
                            PopulateSvtPage(key, slot, payload.Bytes);
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
                                producer.CompressionTexelGrid, colorStagingPtr, dataStagingPtr, compressionPending.Count);
                            RvtPagesRendered++;
                            compressionPending.Add(new PendingCompressedPage { Key = key, Slot = slot });
                            compressionReservedSlots.Add(slot);
                            slotCandidates.Remove(slot);
                        }
                    }
                    catch (Exception exception) { RecordFailure(key, exception); }
                    finally { payload.Dispose(); }
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
                        // Flush queued the native writes into staging. These copies
                        // follow them on the same graphics queue; publish only after
                        // both compressed channels reach their packed destination.
                        for (int i = 0; i < compressionPending.Count; i++)
                            CopyIntoPool(colorStaging, dataStaging, i, compressionPending[i].Slot);
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
            return budget - remaining;
        }

        bool TryTakeSvtPayload(uint key, out MeshWorldVtSvtQueue.Payload payload)
        {
            // 只取已完成并通过校验的数据，磁盘慢时跳过这页，避免阻塞相机。
            payload = default;
            try
            {
                return svtReads.TryTake(key, out payload);
            }
            catch (Exception exception)
            {
                RecordFailure(key, exception);
                return false;
            }
        }

        void PollFeedback()
        {
            if (!feedback.TryReadLatest(recipeMatches, out NativeArray<uint> records, out int feedbackFrame))
                return;

            incoming.Clear();
            int recordCount = Math.Min(LatestFeedbackUniqueKeys, FeedbackPackedCapacity - 1);
            adaptive.BeginFeedback();
            for (int i = 1; i <= recordCount; i++)
            {
                // GPU 已将相同的局部页键合并，count 仍保留屏幕覆盖权重。
                // 页键指向生成反馈时的局部页表区域；
                // 分配器检查产生帧，防止旧反馈把已复用的地址当成另一块世界地形。
                int invalidBefore = adaptive.InvalidFeedback;
                if (!adaptive.DecodeFeedback(records[i * 2], feedbackFrame, frame, out uint key))
                {
                    InvalidFeedbackKeys += adaptive.InvalidFeedback - invalidBefore;
                    continue;
                }
                MarkFeedbackUsage(key);
                AddCount(incoming, key, (int)records[i * 2 + 1]);
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
            using var profile = reclaimMarker.Auto();
            // 缩容后过细的页不再有任何逻辑地址能引用。先撤销其映射与CPU槽占用，
            // 槽内旧纹理字节可留着，下一次填页会完整覆盖两个通道，无需GPU清图。
            for (int slot = 0; slot < slots.Length; slot++)
            {
                uint key = slots[slot].Key;
                if (key == 0 || adaptive.CanRepresent(key) || wanted.Contains(key) ||
                    locked.Contains(key) || svtReads.Contains(key)) continue;
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
            using var profile = planMarker.Auto();
            planDirty = false;
            work.Clear();
            wanted.Clear();
            locked.Clear();
            prefetchWanted.Clear();
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
            foreach (MeshAdaptiveVtTable.Allocation allocation in adaptive.PlanRegions)
            {
                uint key = allocation.RootKey;
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
            // Reserve only this bounded, medium-resolution ring. It needs no fine
            // AVT allocations and survives replacement of the visible GPU requests.
            // Work remains visible-first; hidden pages consume the remaining budget.
            var prefetchKeys = prefetch.Keys;
            for (int i = 0; i < prefetchKeys.Count; i++)
            {
                uint key = prefetchKeys[i];
                if (!wanted.Contains(key) && !failedPages.Contains(key) && archive.Contains(key))
                    prefetchWanted.Add(key);
            }
            int remainingPrefetch = prefetchWanted.Count;
            foreach (Candidate candidate in candidates)
            {
                bool isPrefetch = prefetchWanted.Contains(candidate.Key);
                if (!isPrefetch && work.Count >= Slots - remainingPrefetch) continue;
                if (isPrefetch) remainingPrefetch--;
                work.Add(candidate.Key);
                wanted.Add(candidate.Key);
            }
            for (int i = 0; i < prefetchKeys.Count; i++)
            {
                uint key = prefetchKeys[i];
                if (prefetchWanted.Contains(key) && wanted.Add(key)) work.Add(key);
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

        void QueueSvtReads()
        {
            using var profile = queueReadsMarker.Auto();
            svtReadWork.Clear();
            foreach (uint key in work)
            {
                if (!CoverageSettled && !IsPinned(key)) break;
                if (resident.ContainsKey(key) || failedPages.Contains(key) || !archive.Contains(key))
                    continue;
                svtReadWork.Add(key);
            }
            // Only metadata is submitted here. The bounded worker owns its
            // persistent file stream, checksum and reusable page buffers.
            svtReads.SetRequests(svtReadWork);
        }

        int SelectSlot()
        {
            using var profile = selectSlotMarker.Auto();
            if (slotCandidatesDirty) BuildSlotCandidates();
            return slotCandidates.Peek(out int slot) ? slot : -1;
        }

        void BuildSlotCandidates()
        {
            using var profile = slotCandidatesMarker.Auto();
            slotCandidates.Reset();
            for (int i = 0; i < slots.Length; i++)
            {
                if (compressionReservedSlots.Contains(i)) continue;
                uint key = slots[i].Key;
                if (key != 0 && wanted.Contains(key)) continue;
                slotCandidates.Add(i, key == 0, slots[i].LastVisibleFrame);
            }
            slotCandidates.FinishBuild();
            slotCandidatesDirty = false;
        }

        static bool IsPinned(uint key) => DecodeKey(key, out int mip, out _, out _) && (mip == RootMip || mip == PinnedMip);

        void StageSvtPage(byte[] bytes)
        {
            using var profile = svtUploadMarker.Auto();
            // 预烘焙页的两个通道依次排列，先分别上传到可复用的临时纹理。
            if (bytes == null || bytes.Length != archive.BytesPerPage) throw new InvalidDataException("Wrong SVT page payload size.");
            int channelBytes = archive.BytesPerPage / upload.Length;
            for (int channel = 0; channel < upload.Length; channel++)
            {
                NativeArray<byte>.Copy(bytes, channel * channelBytes,
                    upload[channel].GetRawTextureData<byte>(), 0, channelBytes);
                upload[channel].Apply(false, false);
            }
        }

        void PopulateSvtPage(uint key, int slot, byte[] bytes)
        {
            using var profile = svtPopulateMarker.Auto();
            // SVT already contains the final compressed channels. Preserve the
            // direct upload/copy path and publish only after both copies are queued.
            StageSvtPage(bytes);
            RetireResidentPage(slot);
            using (svtCopyMarker.Auto())
            {
                CopyIntoPool(upload[0], upload[1], 0, slot);
            }
            RegisterResidentPage(key, slot, true);
        }

        void CopyIntoPool(Texture color, Texture data, int sourceSlice, int slot)
        {
            int slice = slot / PagesPerPoolSlice;
            int cell = slot % PagesPerPoolSlice;
            int x = cell % PoolPageAxis * PageSide, y = cell / PoolPageAxis * PageSide;
            Graphics.CopyTexture(color, sourceSlice, 0, 0, 0, PageSide, PageSide, AlbedoAo, slice, 0, x, y);
            Graphics.CopyTexture(data, sourceSlice, 0, 0, 0, PageSide, PageSide, NormalRoughnessHeight, slice, 0, x, y);
        }

        void RetireResidentPage(int slot)
        {
            using var profile = retirePageMarker.Auto();
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
            using var profile = registerPageMarker.Auto();
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
            slotCandidates.Remove(slot);
        }

        void UploadTable()
        {
            using var profile = tableUploadMarker.Auto();
            // 只传 dirty chunk；静止且页面/目录均不变时，分配器不提交上传。
            if (adaptive.Upload(PageTable)) TableUploads++;
        }

        void RecordFailure(uint key, Exception exception)
        {
            // A failed replacement can leave its retired slot empty. Rebuild to
            // preserve the original empty-first and slot-index ordering exactly.
            slotCandidatesDirty = true;
            if (failedPages.Add(key) && IsPinned(key)) failedPinnedPages++;
            planDirty = true;
            pageError = "World VT page " + key + ": " + exception.Message;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // The reader releases its stream on the worker; never wait for disk
            // I/O in a render-loop or scene-disposal callback.
            svtReads?.Dispose();
            feedback?.Dispose();
            foreach (Texture2D texture in upload) MeshResidentHeight.Destroy(texture);
            PageTable?.Release();
            MeshResidentHeight.Destroy(AlbedoAo);
            MeshResidentHeight.Destroy(NormalRoughnessHeight);
            MeshResidentHeight.Destroy(colorStaging);
            MeshResidentHeight.Destroy(dataStaging);
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
