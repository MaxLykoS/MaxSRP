using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxTerrain
{
    /// <summary>
    /// BC3 页面编码。离线烘焙保留单页 Pixel Shader 入口；运行时可把至多四张页
    /// 的输入先保存到独立 slice，再用一次 Compute dispatch 压缩整批。
    /// </summary>
    public sealed class MeshWorldVtBc3 : IDisposable
    {
        public const GraphicsFormat ColorFormat = GraphicsFormat.RGBA_DXT5_SRGB;
        public const GraphicsFormat DataFormat = GraphicsFormat.RGBA_DXT5_UNorm;
        public const GraphicsFormat BlockFormat = GraphicsFormat.R32G32B32A32_UInt;
        public const int BlockSide = MeshWorldVirtualTexture.PageSide / 4;
        public const int ChannelBytes = BlockSide * BlockSide * 16;
        public const int PageBytes = ChannelBytes * 2;
        public const int MaximumBatchPages = 4;
        // 当前生产器只绘制到 mip8，其带边框、差分邻域的 1m 高度窗口小于128。
        // 固定上限避免每张页重新申请资源；未来扩大页世界范围时必须显式修改契约。
        public const int BatchHeightSide = 128;

        static readonly int albedoInput = Shader.PropertyToID("_DDVtBc3AlbedoAo");
        static readonly int dataInput = Shader.PropertyToID("_DDVtBc3NormalData");
        readonly Material material;
        readonly CommandBuffer commands;
        readonly RenderTargetIdentifier[] outputs = new RenderTargetIdentifier[2];
        RenderTexture depth;
        IntPtr colorBlockPtr, dataBlockPtr;

        static readonly int batchSource0 = Shader.PropertyToID("_DDBc3Source0");
        static readonly int batchSource1 = Shader.PropertyToID("_DDBc3Source1");
        static readonly int batchSource2 = Shader.PropertyToID("_DDBc3Source2");
        static readonly int batchSourceHeight = Shader.PropertyToID("_DDBc3SourceHeight");
        static readonly int batchPageKinds = Shader.PropertyToID("_DDBc3PageKinds");
        static readonly int batchPageCount = Shader.PropertyToID("_DDBc3PageCount");
        static readonly int batchHeightInfo = Shader.PropertyToID("_DDBc3HeightInfo");
        static readonly int batchTexelGrid = Shader.PropertyToID("_DDBc3TexelGrid");
        static readonly int batchWorldInfo = Shader.PropertyToID("_DDBc3WorldInfo");
        static readonly int batchColorBlocks = Shader.PropertyToID("_DDBc3ColorBlocks");
        static readonly int batchDataBlocks = Shader.PropertyToID("_DDBc3DataBlocks");
        readonly CommandBuffer captureCommands;
        readonly CommandBuffer batchCommands;
        readonly int[] pageKinds = new int[MaximumBatchPages];
        readonly Vector4[] heightInfos = new Vector4[MaximumBatchPages];
        readonly Vector4[] texelGrids = new Vector4[MaximumBatchPages];
        readonly IntPtr[] colorPools = new IntPtr[MaximumBatchPages], dataPools = new IntPtr[MaximumBatchPages];
        readonly int[] destinationSlots = new int[MaximumBatchPages];
        readonly IntPtr[] preparedCopies = new IntPtr[MaximumBatchPages];
        readonly RenderTargetIdentifier[][] stagePageTargets = new RenderTargetIdentifier[MaximumBatchPages][];
        readonly RenderTargetIdentifier[][] overlayPageTargets = new RenderTargetIdentifier[MaximumBatchPages][];
        ComputeShader batchShader;
        int batchKernel;
        RenderTexture source0, source1, source2, sourceHeight, compressedColor, compressedData;
        bool batchOpen, disposed;
        int renderedTargetKind = -1;

        public RenderTexture ColorBlocks { get; private set; }
        public RenderTexture DataBlocks { get; private set; }
        // 只用于诊断/Editor逐slice验证，不作为地形consumer的新SRV。
        public RenderTexture BatchColorBlocks => compressedColor;
        public RenderTexture BatchDataBlocks => compressedData;
        public int QueuedPageCount { get; private set; }
        /// <summary>以下计数只包含批量Compute路径，不把离线单页PS算作dispatch。</summary>
        public int CompressDispatches { get; private set; }
        public int CompressedPages { get; private set; }
        public int CompressionBatchCount { get; private set; }
        public int LastBatchPages { get; private set; }
        /// <summary>复制参考入口的全页纹理CopyTexture次数：双stage计2，三MRT计3，不含小高度窗口。</summary>
        public int CapturedPageCopies { get; private set; }
        /// <summary>直接绘制到batch slice并成功排队的页数，不产生上述全页输入复制。</summary>
        public int DirectRenderedPages { get; private set; }
        public int HeightWindowCopies { get; private set; }
        public long CompressionTemporaryBytes =>
            (ColorBlocks && ColorBlocks.IsCreated() ? 2L * ChannelBytes + (long)BlockSide * BlockSide * 4 : 0) +
            (source0 && source0.IsCreated() ? MaximumBatchPages *
                (3L * MeshWorldVirtualTexture.PageSide * MeshWorldVirtualTexture.PageSide * 4 +
                 (long)BatchHeightSide * BatchHeightSide * 2 + 2L * ChannelBytes) : 0);

        public static bool CanEncode => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11 &&
            SystemInfo.supportedRenderTargetCount >= 2 && SystemInfo.IsFormatSupported(BlockFormat, FormatUsage.Render);
        public static bool CanEncodeBatch => CanEncode && SystemInfo.supportsComputeShaders &&
            SystemInfo.supports2DArrayTextures && SystemInfo.IsFormatSupported(BlockFormat, FormatUsage.LoadStore) &&
            SystemInfo.IsFormatSupported(GraphicsFormat.R16_UNorm, FormatUsage.Sample);

        public MeshWorldVtBc3()
        {
            var shader = Resources.Load<Shader>("WorldVtCompressBc3");
            if (!CanEncode || !shader || !shader.isSupported)
                throw new NotSupportedException("World VT BC3 requires the D3D11 integer MRT encoder.");
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            commands = new CommandBuffer { name = "Delta World VT: encode and commit BC3 page" };
            captureCommands = new CommandBuffer { name = "Delta World VT: capture page for BC3 batch" };
            batchCommands = new CommandBuffer { name = "Delta World VT: compress and commit BC3 batch" };
            try { EnsureTargets(); }
            catch { Dispose(); throw; }
        }

        public void Encode(Texture albedo, Texture data)
        {
            RequireSinglePageAccess();
            RecordEncoding(albedo, data);
            Graphics.ExecuteCommandBuffer(commands);
        }

        public void EncodeAndCopy(Texture albedo, Texture data, IntPtr colorPool, IntPtr dataPool, int slot)
        {
            RequireSinglePageAccess();
            RecordEncoding(albedo, data);
            if (colorBlockPtr == IntPtr.Zero)
            {
                colorBlockPtr = ColorBlocks.GetNativeTexturePtr();
                dataBlockPtr = DataBlocks.GetNativeTexturePtr();
            }
            // 片元写块、原生复制及后续页表上传排在同一图形命令流。
            Bc3NativeCopy.Record(commands, colorBlockPtr, dataBlockPtr, colorPool, dataPool, slot);
            Graphics.ExecuteCommandBuffer(commands);
        }

        /// <summary>
        /// 开始一批至多四张页。这里不改物理池；QueuePage保存每页的独立输入，
        /// FlushBatch才编码并提交。两次Begin之间必须Flush或Cancel，禁止覆盖未提交的页。
        /// </summary>
        public void BeginBatch()
        {
            RequireLive();
            if (batchOpen) throw new InvalidOperationException("Flush or cancel the current BC3 batch before beginning another.");
            EnsureBatchTargets();
            batchOpen = true;
            QueuedPageCount = 0;
            LastBatchPages = 0;
            renderedTargetKind = -1;
        }

        /// <summary>
        /// 取得当前待排队页的RTV。生产器直接向这一slice绘制基底和覆盖物，
        /// 两张或三张合成结果已经是Compute的输入，无须再复制完整页面。
        /// 本方法不推进页号；成功绘制后调用QueueRenderedPage，失败可在同slice重试。
        /// Unity的MRT简化重载会忽略RTI内部的slice。调用方必须显式使用
        /// SetRenderTarget(targets,None,0,CubemapFace.Unknown,QueuedPageCount)。
        /// 页面shader不写深度且ZTest Always，因此该数组路径不绑定2D depth。
        /// </summary>
        public RenderTargetIdentifier[] GetPageRenderTargets(bool overlay)
        {
            RequireQueueSpace();
            int slice = QueuedPageCount;
            renderedTargetKind = overlay ? 1 : 0;
            RenderTargetIdentifier[][] cache = overlay ? overlayPageTargets : stagePageTargets;
            if (cache[slice] == null)
            {
                cache[slice] = overlay
                    ? new[] { PageTarget(source0, slice), PageTarget(source1, slice), PageTarget(source2, slice) }
                    : new[] { PageTarget(source0, slice), PageTarget(source1, slice) };
            }
            return cache[slice];
        }

        static RenderTargetIdentifier PageTarget(RenderTexture texture, int slice) =>
            new RenderTargetIdentifier(texture, 0, CubemapFace.Unknown, slice);

        /// <summary>
        /// 完成直接绘制的当前页。颜色/法线/属性已经位于batch slice中，不能再复制它们；
        /// 三MRT模式只捕获较小的R16源高度窗口，用于极少数近相消法线的固定源回退。
        /// 源高度纹理会被下一页上传复用，因此其复制仍需立即排入同一图形队列。
        /// </summary>
        public void QueueRenderedPage(bool overlay, Texture pageSourceHeight, Vector4 heightInfo, Vector4 texelGrid,
            IntPtr colorPool, IntPtr dataPool, int slot)
        {
            RequireQueueSpace();
            if (renderedTargetKind != (overlay ? 1 : 0))
                throw new InvalidOperationException("Render the current BC3 slice with matching MRT targets before queueing it.");
            ValidateDestination(colorPool, dataPool, slot);
            if (overlay)
            {
                ValidateHeightSource(pageSourceHeight, heightInfo, texelGrid);
                captureCommands.Clear();
                CopyHeightWindow(pageSourceHeight, QueuedPageCount);
                Graphics.ExecuteCommandBuffer(captureCommands);
                HeightWindowCopies++;
            }
            StorePage(overlay, heightInfo, texelGrid, colorPool, dataPool, slot);
            DirectRenderedPages++;
        }

        /// <summary>保存原有两张stage；用于没有静态覆盖的页，编码规则与单页PS一致。</summary>
        public void QueuePage(Texture albedoAo, Texture normalData, IntPtr colorPool, IntPtr dataPool, int slot)
        {
            QueuePage(albedoAo, normalData, null, null, Vector4.zero, Vector4.zero, colorPool, dataPool, slot);
        }

        /// <summary>
        /// source2为空：source0/1是原有双stage。source2非空：source0/1/2是覆盖合成后的
        /// Color/XYZNormal/Attributes三MRT，Compute直接重排并压缩，不经过双stage。
        /// 三MRT还必须提供当页R16高度窗口及原生产器的HeightInfo、TexelGrid，以精确复现
        /// 近相消法线的源高度回退。Queue会立即复制输入，因为生产器下一页会复用这些纹理。
        /// </summary>
        public void QueuePage(Texture pageSource0, Texture pageSource1, Texture pageSource2,
            Texture pageSourceHeight, Vector4 heightInfo, Vector4 texelGrid,
            IntPtr colorPool, IntPtr dataPool, int slot)
        {
            RequireQueueSpace();
            ValidatePageTexture(pageSource0, MeshWorldVirtualTexture.AlbedoFormat, nameof(pageSource0));
            ValidatePageTexture(pageSource1, MeshWorldVirtualTexture.DataFormat, nameof(pageSource1));
            ValidateDestination(colorPool, dataPool, slot);
            bool fused = pageSource2;
            if (fused)
            {
                ValidatePageTexture(pageSource2, MeshWorldVirtualTexture.DataFormat, nameof(pageSource2));
                ValidateHeightSource(pageSourceHeight, heightInfo, texelGrid);
            }

            int slice = QueuedPageCount;
            captureCommands.Clear();
            captureCommands.CopyTexture(pageSource0, 0, 0, source0, slice, 0);
            captureCommands.CopyTexture(pageSource1, 0, 0, source1, slice, 0);
            if (fused)
            {
                captureCommands.CopyTexture(pageSource2, 0, 0, source2, slice, 0);
                CopyHeightWindow(pageSourceHeight, slice);
            }
            // 先捕获再允许下一页覆盖原MRT。顺序在同一图形队列中，不作CPU/GPU等待或回读。
            Graphics.ExecuteCommandBuffer(captureCommands);
            CapturedPageCopies += fused ? 3 : 2;
            if (fused) HeightWindowCopies++;
            StorePage(fused, heightInfo, texelGrid, colorPool, dataPool, slot);
        }

        void CopyHeightWindow(Texture texture, int slice)
        {
            captureCommands.CopyTexture(texture, 0, 0, 0, 0, texture.width, texture.height,
                sourceHeight, slice, 0, 0, 0);
        }

        void StorePage(bool overlay, Vector4 heightInfo, Vector4 texelGrid, IntPtr colorPool, IntPtr dataPool, int slot)
        {
            int slice = QueuedPageCount;
            pageKinds[slice] = overlay ? 1 : 0;
            heightInfos[slice] = heightInfo;
            texelGrids[slice] = texelGrid;
            colorPools[slice] = colorPool;
            dataPools[slice] = dataPool;
            destinationSlots[slice] = slot;
            QueuedPageCount++;
            renderedTargetKind = -1;
        }

        /// <summary>
        /// 一次dispatch处理所有已排队页。每页仍写自己的物理槽；旧D3D11插件只接受2D块图，
        /// 因此每个输出slice先复制到已有2D scratch，再顺序调用原插件，全部在同一命令流。
        /// </summary>
        public int FlushBatch()
        {
            RequireLive();
            if (!batchOpen) throw new InvalidOperationException("There is no open BC3 batch to flush.");
            int count = QueuedPageCount;
            if (count == 0) { CancelBatch(); return 0; }
            bool submitted = false;
            int preparedCount = 0;
            try
            {
                EnsureTargets();
                if (colorBlockPtr == IntPtr.Zero)
                {
                    colorBlockPtr = ColorBlocks.GetNativeTexturePtr();
                    dataBlockPtr = DataBlocks.GetNativeTexturePtr();
                }
                batchCommands.Clear();
                // 最后一页的RTV可能仍绑定source数组；先解除写绑定，再让Compute作为SRV读取。
                batchCommands.SetRenderTarget(BuiltinRenderTextureType.None);
                batchCommands.BeginSample("Delta World VT: compress BC3 page batch");
                batchCommands.SetComputeTextureParam(batchShader, batchKernel, batchSource0, source0);
                batchCommands.SetComputeTextureParam(batchShader, batchKernel, batchSource1, source1);
                batchCommands.SetComputeTextureParam(batchShader, batchKernel, batchSource2, source2);
                batchCommands.SetComputeTextureParam(batchShader, batchKernel, batchSourceHeight, sourceHeight);
                batchCommands.SetComputeIntParams(batchShader, batchPageKinds, pageKinds);
                batchCommands.SetComputeIntParam(batchShader, batchPageCount, count);
                batchCommands.SetComputeVectorArrayParam(batchShader, batchHeightInfo, heightInfos);
                batchCommands.SetComputeVectorArrayParam(batchShader, batchTexelGrid, texelGrids);
                batchCommands.SetComputeVectorParam(batchShader, batchWorldInfo,
                    new Vector4(MeshControlContract.WorldSize, MeshWorldVirtualTexture.Border, 0, 0));
                batchCommands.SetComputeTextureParam(batchShader, batchKernel, batchColorBlocks, compressedColor);
                batchCommands.SetComputeTextureParam(batchShader, batchKernel, batchDataBlocks, compressedData);
                batchCommands.DispatchCompute(batchShader, batchKernel, (BlockSide + 7) / 8, (BlockSide + 7) / 8, count);
                batchCommands.EndSample("Delta World VT: compress BC3 page batch");
                batchCommands.BeginSample("Delta World VT: commit BC3 page batch");
                for (int i = 0; i < count; i++)
                {
                    batchCommands.CopyTexture(compressedColor, i, 0, ColorBlocks, 0, 0);
                    batchCommands.CopyTexture(compressedData, i, 0, DataBlocks, 0, 0);
                    preparedCopies[preparedCount++] = Bc3NativeCopy.Record(batchCommands, colorBlockPtr, dataBlockPtr,
                        colorPools[i], dataPools[i], destinationSlots[i]);
                }
                batchCommands.EndSample("Delta World VT: commit BC3 page batch");
                submitted = true;
                Graphics.ExecuteCommandBuffer(batchCommands);
                CompressDispatches++;
                CompressedPages += count;
                CompressionBatchCount++;
                LastBatchPages = count;
                return count;
            }
            finally
            {
                // 尚未提交的原生请求由CPU取消；提交后的请求由render callback释放。
                if (!submitted) for (int i = 0; i < preparedCount; i++) Bc3NativeCopy.Cancel(preparedCopies[i]);
                Array.Clear(preparedCopies, 0, preparedCopies.Length);
                CancelBatch();
            }
        }

        /// <summary>丢弃尚未压缩的私有输入，不修改物理池。可用于生产器中途失败后的清理。</summary>
        public void CancelBatch()
        {
            QueuedPageCount = 0;
            batchOpen = false;
            renderedTargetKind = -1;
            Array.Clear(colorPools, 0, colorPools.Length);
            Array.Clear(dataPools, 0, dataPools.Length);
        }

        void EnsureBatchTargets()
        {
            if (source0 && source0.IsCreated() && source1 && source1.IsCreated() && source2 && source2.IsCreated() &&
                sourceHeight && sourceHeight.IsCreated() && compressedColor && compressedColor.IsCreated() &&
                compressedData && compressedData.IsCreated()) return;
            ReleaseBatchTargets();
            if (!CanEncodeBatch) throw new NotSupportedException("World VT batched BC3 requires D3D11 compute and integer UAV texture arrays.");
            if (!batchShader)
            {
                batchShader = Resources.Load<ComputeShader>("WorldVtCompressBc3Batch");
                if (!batchShader) throw new NotSupportedException("The WorldVtCompressBc3Batch compute shader is unavailable.");
                batchKernel = batchShader.FindKernel("EncodeBc3Batch");
            }
            try
            {
                source0 = CreateBatchTarget("World VT BC3 batch input color", MeshWorldVirtualTexture.PageSide, MeshWorldVirtualTexture.AlbedoFormat, false);
                source1 = CreateBatchTarget("World VT BC3 batch input normal", MeshWorldVirtualTexture.PageSide, MeshWorldVirtualTexture.DataFormat, false);
                source2 = CreateBatchTarget("World VT BC3 batch input attributes", MeshWorldVirtualTexture.PageSide, MeshWorldVirtualTexture.DataFormat, false);
                sourceHeight = CreateBatchTarget("World VT BC3 batch source heights", BatchHeightSide, GraphicsFormat.R16_UNorm, false);
                compressedColor = CreateBatchTarget("World VT BC3 batch color blocks", BlockSide, BlockFormat, true);
                compressedData = CreateBatchTarget("World VT BC3 batch data blocks", BlockSide, BlockFormat, true);
            }
            catch { ReleaseBatchTargets(); throw; }
        }

        static RenderTexture CreateBatchTarget(string name, int side, GraphicsFormat format, bool randomWrite)
        {
            var descriptor = new RenderTextureDescriptor(side, side, format, 0)
            {
                dimension = TextureDimension.Tex2DArray, volumeDepth = MaximumBatchPages,
                enableRandomWrite = randomWrite, msaaSamples = 1, mipCount = 1,
                useMipMap = false, autoGenerateMips = false
            };
            var target = new RenderTexture(descriptor)
            {
                name = name, hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            if (target.Create() && target.graphicsFormat == format && target.volumeDepth == MaximumBatchPages) return target;
            ReleaseTarget(target);
            throw new NotSupportedException("Could not allocate " + name);
        }

        static void ValidatePageTexture(Texture texture, GraphicsFormat format, string name)
        {
            if (!texture || texture.dimension != TextureDimension.Tex2D || texture.width != MeshWorldVirtualTexture.PageSide ||
                texture.height != MeshWorldVirtualTexture.PageSide || texture.graphicsFormat != format)
                throw new ArgumentException("BC3 page input must retain its exact 144x144 RGBA8 stage format.", name);
        }

        void RequireQueueSpace()
        {
            RequireLive();
            if (!batchOpen) throw new InvalidOperationException("Begin a BC3 batch before queueing pages.");
            if (QueuedPageCount >= MaximumBatchPages) throw new InvalidOperationException("A BC3 batch holds at most four pages.");
        }

        static void ValidateDestination(IntPtr colorPool, IntPtr dataPool, int slot)
        {
            if (colorPool == IntPtr.Zero || dataPool == IntPtr.Zero || slot < 0)
                throw new ArgumentException("A queued BC3 page needs valid physical pools and a nonnegative slot.");
        }

        static void ValidateHeightSource(Texture texture, Vector4 heightInfo, Vector4 texelGrid)
        {
            if (!texture || texture.dimension != TextureDimension.Tex2D || texture.graphicsFormat != GraphicsFormat.R16_UNorm ||
                texture.width != texture.height || texture.width < 2 || texture.width > BatchHeightSide ||
                heightInfo.z != texture.width || !Finite(heightInfo) || heightInfo.w <= 0 ||
                !Finite(texelGrid) || texelGrid.z <= 0 || texelGrid.w <= 0)
                throw new ArgumentException("Fused BC3 needs the matching R16 height window, HeightInfo and TexelGrid.");
        }

        static bool Finite(Vector4 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z) && !float.IsNaN(value.w) && !float.IsInfinity(value.w);

        void RequireLive()
        {
            if (disposed) throw new ObjectDisposedException(nameof(MeshWorldVtBc3));
        }

        void RequireSinglePageAccess()
        {
            RequireLive();
            if (batchOpen) throw new InvalidOperationException("Finish the pending BC3 batch before using the single-page encoder.");
        }

        void RecordEncoding(Texture albedo, Texture data)
        {
            EnsureTargets();
            commands.Clear();
            commands.SetRenderTarget(outputs, new RenderTargetIdentifier(depth));
            commands.SetViewport(new Rect(0, 0, BlockSide, BlockSide));
            commands.SetGlobalTexture(albedoInput, albedo);
            commands.SetGlobalTexture(dataInput, data);
            commands.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1);
        }

        void EnsureTargets()
        {
            if (ColorBlocks && ColorBlocks.IsCreated() && DataBlocks && DataBlocks.IsCreated() && depth && depth.IsCreated()) return;
            ReleaseTargets();
            ColorBlocks = CreateTarget("World VT BC3 color blocks", BlockFormat, 0);
            DataBlocks = CreateTarget("World VT BC3 data blocks", BlockFormat, 0);
            depth = CreateTarget("World VT BC3 depth", GraphicsFormat.None, 24);
            outputs[0] = new RenderTargetIdentifier(ColorBlocks);
            outputs[1] = new RenderTargetIdentifier(DataBlocks);
        }

        static RenderTexture CreateTarget(string name, GraphicsFormat format, int depthBits)
        {
            var target = new RenderTexture(new RenderTextureDescriptor(BlockSide, BlockSide, format, depthBits))
            {
                name = name, hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point,
                useMipMap = false, autoGenerateMips = false, antiAliasing = 1
            };
            if (target.Create() && target.graphicsFormat == format) return target;
            target.Release();
            MeshResidentHeight.Destroy(target);
            throw new NotSupportedException("Could not allocate " + name);
        }

        void ReleaseTargets()
        {
            ReleaseTarget(ColorBlocks);
            ReleaseTarget(DataBlocks);
            ReleaseTarget(depth);
            ColorBlocks = DataBlocks = depth = null;
            colorBlockPtr = dataBlockPtr = IntPtr.Zero;
        }

        void ReleaseBatchTargets()
        {
            ReleaseTarget(source0); ReleaseTarget(source1); ReleaseTarget(source2); ReleaseTarget(sourceHeight);
            ReleaseTarget(compressedColor); ReleaseTarget(compressedData);
            source0 = source1 = source2 = sourceHeight = compressedColor = compressedData = null;
            for (int i = 0; i < MaximumBatchPages; i++) stagePageTargets[i] = overlayPageTargets[i] = null;
        }

        static void ReleaseTarget(RenderTexture target)
        {
            if (!target) return;
            target.Release();
            MeshResidentHeight.Destroy(target);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CancelBatch();
            commands?.Release();
            captureCommands?.Release();
            batchCommands?.Release();
            ReleaseTargets();
            ReleaseBatchTargets();
            MeshResidentHeight.Destroy(material);
        }
    }

    internal static class Bc3NativeCopy
    {
        const string Library = "DirectDeltaBc3D3D11";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr GetBc3CopyRenderEvent();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr PrepareBc3Copy(IntPtr colorBlocks, IntPtr dataBlocks, IntPtr colorPool, IntPtr dataPool, int slot);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr GetBc3CopyError();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        static extern void CancelBc3Copy(IntPtr request);

        static IntPtr renderEvent;

        public static bool Available
        {
            get
            {
                if (renderEvent != IntPtr.Zero) return true;
                try { renderEvent = GetBc3CopyRenderEvent(); }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
                return renderEvent != IntPtr.Zero;
            }
        }

        public static IntPtr Record(CommandBuffer commands, IntPtr colorBlocks, IntPtr dataBlocks,
            IntPtr colorPool, IntPtr dataPool, int slot)
        {
            if (!Available) throw new NotSupportedException("World VT D3D11 BC3 copy plugin is unavailable.");
            var request = PrepareBc3Copy(colorBlocks, dataBlocks, colorPool, dataPool, slot);
            if (request == IntPtr.Zero)
                throw new InvalidOperationException("Could not prepare World VT D3D11 BC3 copy to slot " + slot +
                    ": " + Marshal.PtrToStringAnsi(GetBc3CopyError()));
            try { commands.IssuePluginEventAndData(renderEvent, slot, request); }
            catch { CancelBc3Copy(request); throw; }
            return request;
        }

        // Only cancel requests whose command buffer was never submitted to the graphics queue.
        public static void Cancel(IntPtr request) { if (request != IntPtr.Zero) CancelBc3Copy(request); }
    }
}
