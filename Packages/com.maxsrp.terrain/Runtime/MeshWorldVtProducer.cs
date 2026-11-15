using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxTerrain
{
    /// <summary>
    /// 根据世界页地址生成材质缓存。运行时直接绘制到压缩批次的独立 slice；
    /// 离线烘焙与参考对照保留可复用的 2D MRT stage。
    /// </summary>
    public sealed class MeshWorldVtProducer : IDisposable
    {
        // 页着色、边界采样、通道打包或 SVT 滤波改动时，升级版本以拒绝旧归档。
        public const string RecipeVersion = "Surface16-WorldVt-AVT419-v4-basecolorclamp-stochastic1";

        sealed class SourceWindow : IDisposable
        {
            public readonly ushort[] Heights, Controls;
            public readonly byte[] ControlRow;
            public readonly Texture2D HeightTexture, ControlTexture;

            public SourceWindow(int side)
            {
                Heights = new ushort[side * side];
                Controls = new ushort[side * side];
                ControlRow = new byte[side * sizeof(ushort)];
                HeightTexture = MakeSourceTexture(side, "World VT source heights", FilterMode.Bilinear);
                try { ControlTexture = MakeSourceTexture(side, "World VT source controls", FilterMode.Point); }
                catch { MeshResidentHeight.Destroy(HeightTexture); throw; }
            }

            public void Dispose()
            {
                MeshResidentHeight.Destroy(HeightTexture);
                MeshResidentHeight.Destroy(ControlTexture);
            }
        }

        readonly MeshHeightSource source;
        readonly MeshMaterialResident materials;
        readonly MeshControlLevel controls;
        readonly string sourceIdentity;
        readonly FileStream controlFile;
        readonly Material producer;
        readonly MeshWorldVtOverlaySet.Snapshot overlays;
        readonly Material overlayWriter, overlayRepack;
        readonly bool immutableOverlayInputs;
        readonly Dictionary<int, SourceWindow> windows = new Dictionary<int, SourceWindow>();
        readonly string controlPath;
        MeshWorldVtSourceReader runtimeSourceReader;
        MeshWorldVtSourceQueue runtimeSources;
        static readonly uint[] noSourceRequests = new uint[0];
        readonly Vector4[] recipeUv = new Vector4[MeshMaterialSet.Capacity];
        readonly Vector4[] recipeParameters = new Vector4[MeshMaterialSet.Capacity];
        readonly CommandBuffer commands;
        RenderTexture depth;
        RenderTargetIdentifier[] outputs;
        readonly RenderTexture[] overlayTargets = new RenderTexture[3];
        RenderTargetIdentifier[] overlayOutputs;
        bool disposed, compressionUsesBatchTargets;
        string recipeIdentity;
        float recipeHex, recipeCliff;
        Vector4 recipeBlend, recipeHexParameters;

        public RenderTexture AlbedoAo { get; private set; }
        public RenderTexture NormalRoughnessHeight { get; private set; }
        public int PageInputValidationFailures { get; private set; }
        public int ValidatedPages { get; private set; }
        public int PageInputNotReadyRetries { get; private set; }
        public int OverlayPagesRendered { get; private set; }
        public int OverlayDraws { get; private set; }
        public int OverlayRepackDraws { get; private set; }
        public int DirectBatchPagesRendered { get; private set; }
        public int PendingSourcePages => runtimeSources?.PendingCount ?? 0; // 含running/queued/ready。
        public int PreparedSourcePages => runtimeSources?.ReadyCount ?? 0;
        public int PeakPendingSourcePages => runtimeSources?.PeakPendingCount ?? 0;
        public int CompletedSourcePages => runtimeSources?.CompletedCount ?? 0;
        public int ConsumedSourcePages => runtimeSources?.ConsumedCount ?? 0;
        public int CancelledSourcePages => runtimeSources?.CancelledCount ?? 0;
        public int FailedSourcePages => runtimeSources?.FailedCount ?? 0;
        public int SourceWorkerThreadId => runtimeSources?.WorkerThreadId ?? 0;
        public long PreparedSourceBytes => runtimeSources?.ReadyBytes ?? 0;
        public long SourceHeightCacheBytes => runtimeSourceReader?.HeightCacheBytes ?? 0;
        // Valid only after a successful TryRenderForCompression. QueuePage captures
        // these reusable textures before the next page overwrites them. Direct
        // batch rendering exposes no 2D source textures: its MRTs already belong
        // to the batch, and QueueRenderedPage records only height/metadata.
        public bool CompressionUsesOverlayMrt { get; private set; }
        public Texture CompressionSource0 => compressionUsesBatchTargets ? null : CompressionUsesOverlayMrt ? overlayTargets[0] : AlbedoAo;
        public Texture CompressionSource1 => compressionUsesBatchTargets ? null : CompressionUsesOverlayMrt ? overlayTargets[1] : NormalRoughnessHeight;
        public Texture CompressionSource2 => compressionUsesBatchTargets ? null : CompressionUsesOverlayMrt ? overlayTargets[2] : null;
        public Texture CompressionSourceHeight { get; private set; }
        public Vector4 CompressionHeightInfo { get; private set; }
        public Vector4 CompressionTexelGrid { get; private set; }
        public string OverlayContentHash => overlays?.ContentHash;
        public long OverlayTemporaryBytes => overlayTargets[0] && overlayTargets[0].IsCreated()
            ? 3L * MeshWorldVirtualTexture.PageSide * MeshWorldVirtualTexture.PageSide * 4 : 0;

        public MeshWorldVtProducer(MeshHeightSource source, string controlDirectory,
            MeshMaterialResident materials, Shader shader = null, MeshWorldVtOverlaySet overlaySet = null,
            Func<MeshWorldVtDecal[]> sceneDecals = null, Func<MeshWorldVtRoad[]> sceneRoads = null,
            bool immutableOverlayInputs = false)
        {
            this.immutableOverlayInputs = immutableOverlayInputs;
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.materials = materials ?? throw new ArgumentNullException(nameof(materials));
            materials.Set.Validate(); // 所有16个ID都必须合法，不能依赖页内像素的有效性标记。
            MaterialSourcesReady();
            if (!BitConverter.IsLittleEndian || SystemInfo.supportedRenderTargetCount < 2 ||
                !SystemInfo.IsFormatSupported(MeshWorldVirtualTexture.AlbedoFormat, FormatUsage.Render) ||
                !SystemInfo.IsFormatSupported(MeshWorldVirtualTexture.DataFormat, FormatUsage.Render) ||
                !SystemInfo.SupportsTextureFormat(TextureFormat.R16))
                throw new NotSupportedException("World VT requires little-endian R16 sources and sRGB/linear RGBA8 render targets.");
            if (!shader) shader = Shader.Find(MeshSinglePageRvt.ProducerShaderName);
            if (!shader || !shader.isSupported) throw new NotSupportedException("World VT page shader is unavailable.");

            MeshControlMetadata metadata = MeshControlBake.ReadMetadata(controlDirectory);
            if ((metadata.materialLayerOrder ?? "") != (materials.Set.layerOrder ?? "") ||
                (!string.IsNullOrEmpty(metadata.heightRecipe) && metadata.heightRecipe != source.Metadata.recipe))
                throw new InvalidDataException("World VT height, control and material sources must describe the same terrain.");
            controls = metadata.levels[0];
            controlPath = Path.Combine(controlDirectory, controls.fileName);
            MeshMaterialSet.Variant variant = materials.Set.GetVariant(materials.Format);
            sourceIdentity = string.Join("|", RecipeVersion, JsonUtility.ToJson(source.Metadata), metadata.heightMetadataSha256,
                controls.sha256, materials.Set.sourceFingerprint, materials.Format.ToString(), variant.albedoHeightSha256,
                variant.normalRoughAoSha256, shader.name);

            try
            {
                // 空集合不改变旧配方。覆盖输入冻结为独立快照，不能在旧SVT实例中热换内容。
                if (overlaySet || sceneDecals != null || sceneRoads != null)
                {
                    overlays = MeshWorldVtOverlaySet.CreateSnapshot(overlaySet, sceneDecals, sceneRoads);
                    if (overlays.Count > 0)
                    {
                        if (SystemInfo.supportedRenderTargetCount < 3)
                            throw new NotSupportedException("World VT overlays require three temporary render targets.");
                        sourceIdentity += "|Surface16-StaticOverlays-v1|" + overlays.ContentHash;
                        Shader writer = overlaySet && overlaySet.writerShader ? overlaySet.writerShader : Shader.Find("Hidden/DirectDeltaMesh/WorldVtOverlay");
                        Shader repack = overlaySet && overlaySet.repackShader ? overlaySet.repackShader : Shader.Find("Hidden/DirectDeltaMesh/WorldVtOverlayRepack");
                        if (!writer || !writer.isSupported || !repack || !repack.isSupported)
                            throw new NotSupportedException("The world VT overlay writer or repack shader is unavailable.");
                        overlayWriter = new Material(writer) { name = "World VT static overlay writer", hideFlags = HideFlags.HideAndDontSave };
                        overlayRepack = new Material(repack) { name = "World VT overlay repack", hideFlags = HideFlags.HideAndDontSave };
                        overlayWriter.EnableKeyword("DD_WORLD_VT_RGBA8");
                        overlayRepack.EnableKeyword("DD_WORLD_VT_RGBA8");
                    }
                }
                controlFile = new FileStream(controlPath, FileMode.Open,
                    FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
                producer = new Material(shader) { name = "World VT page producer", hideFlags = HideFlags.HideAndDontSave };
                producer.EnableKeyword("DD_WORLD_VT_RGBA8");
                commands = new CommandBuffer { name = "Delta World VT: generate material page" };
                EnsureDepth();
            }
            catch { Dispose(); throw; }
        }

        // 只记录会改变页内容的参数。相机位置、屏幕光照和 Clipmap 窗口都由消费侧处理。
        public string GetRecipeIdentity(Material settings)
        {
            if (disposed) throw new ObjectDisposedException(nameof(MeshWorldVtProducer));
            if (!settings) throw new ArgumentNullException(nameof(settings));
            // Published content has already been validated and frozen. Editable
            // comparison inputs retain the source-change rejection contract.
            if (!immutableOverlayInputs) overlays?.EnsureSourceUnchanged();
            float hex = settings.GetFloat("_DDHexTiling");
            float cliff = settings.GetFloat("_DDCliffProjection");
            Vector4 blend = settings.GetVector("_DDHeightBlend");
            Vector4 hexParameters = settings.GetVector("_DDHexParams");
            bool same = recipeIdentity != null && recipeHex.Equals(hex) && recipeCliff.Equals(cliff) &&
                recipeBlend.Equals(blend) && recipeHexParameters.Equals(hexParameters);
            if (materials.Set.uvPerMetre == null || materials.Set.uvPerMetre.Length != MeshMaterialSet.Capacity ||
                materials.Set.parameters == null || materials.Set.parameters.Length != MeshMaterialSet.Capacity)
                materials.Set.Validate();
            for (int i = 0; same && i < MeshMaterialSet.Capacity; i++)
                same = recipeUv[i].Equals(materials.Set.uvPerMetre[i]) && recipeParameters[i].Equals(materials.Set.parameters[i]);
            if (same) return recipeIdentity;

            // 编辑器可能在构造后修改材质参数。只在生成新身份时重新验证元数据，
            // 不重新读取大文件或计算源贴图SHA。
            materials.Set.Validate();

            recipeHex = hex;
            recipeCliff = cliff;
            recipeBlend = blend;
            recipeHexParameters = hexParameters;
            Array.Copy(materials.Set.uvPerMetre, recipeUv, recipeUv.Length);
            Array.Copy(materials.Set.parameters, recipeParameters, recipeParameters.Length);
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
            {
                writer.Write(sourceIdentity);
                writer.Write(hex);
                writer.Write(cliff);
                // v4归档在此处保存float投影鉴别值。正式随机单投影固定为1f，
                // 保留位置/类型可复用已验收的原B页；这不是材质参数或运行切换入口。
                // 若今后更换算法，仍须升级RecipeVersion并重烘，不能只改这个常量。
                writer.Write(1f);
                WriteVector(writer, blend);
                WriteVector(writer, hexParameters);
                for (int i = 0; i < MeshMaterialSet.Capacity; i++)
                {
                    WriteVector(writer, recipeUv[i]);
                    WriteVector(writer, recipeParameters[i]);
                }
                writer.Flush();
                recipeIdentity = MeshMaterialResident.Sha256(bytes.ToArray());
            }
            return recipeIdentity;
        }

        static void WriteVector(BinaryWriter writer, Vector4 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
            writer.Write(value.w);
        }

        public void Render(uint key, Material settings)
        {
            // 离线烘焙不能把上一次stage的内容当成新页继续保存。
            if (!TryRender(key, settings))
                throw new InvalidOperationException("World VT material sources are not ready; no page was produced.");
        }

        public bool TryRender(uint key, Material settings) => TryRenderPage(key, settings, false);

        /// <summary>2D capture/reference BC3 path: preserve three blended MRTs for fused packing/compression.
        /// Runtime batches use TryRenderIntoCompressionBatch; raw SVT baking keeps TryRender/Render.</summary>
        public bool TryRenderForCompression(uint key, Material settings) => TryRenderPage(key, settings, true);

        /// <summary>
        /// 绘制到 batch 当前待提交 slice。覆盖页输出三 MRT，普通页输出两 MRT；
        /// 不经过生产器的 2D 颜色 stage，也不执行重排或颜色 CopyTexture。
        /// 成功后由调用方 QueueRenderedPage 保存高度窗口及元数据，再推进 slice。
        /// </summary>
        public bool TryRenderIntoCompressionBatch(uint key, Material settings, MeshWorldVtBc3 batch)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            return TryRenderPage(key, settings, true, batch);
        }

        /// <summary>
        /// 运行期只领取后台已校验的源窗口；未就绪不上传、不绘制、不覆写物理槽。
        /// 同步接口保留给离线烘焙和逐页参考，防止离线工具保存上一次stage。
        /// </summary>
        public bool TryRenderPreparedIntoCompressionBatch(uint key, Material settings, MeshWorldVtBc3 batch)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            return TryRenderPage(key, settings, true, batch, true);
        }

        public void PrepareRuntimeSources(IList<uint> priorityKeys)
        {
            if (disposed) throw new ObjectDisposedException(nameof(MeshWorldVtProducer));
            if (priorityKeys == null) throw new ArgumentNullException(nameof(priorityKeys));
            if (runtimeSources == null && priorityKeys.Count != 0)
            {
                // 只快照路径/数值，不在此构造第二个Unity高度源或执行文件扫描。
                runtimeSourceReader = new MeshWorldVtSourceReader(source.DirectoryPath, controlPath,
                    controls.width, source.Metadata.heightScale);
                runtimeSources = new MeshWorldVtSourceQueue(runtimeSourceReader.Read, runtimeSourceReader.Dispose);
            }
            runtimeSources?.SetRequests(priorityKeys);
        }

        public void CancelRuntimeSources() => runtimeSources?.SetRequests(noSourceRequests);
        public bool HasPreparedRuntimeSource(uint key) => runtimeSources?.IsReady(key) ?? false;

        bool TryRenderPage(uint key, Material settings, bool fuseOverlayCompression, MeshWorldVtBc3 batch = null, bool preparedOnly = false)
        {
            CompressionUsesOverlayMrt = false;
            CompressionSourceHeight = null;
            compressionUsesBatchTargets = batch != null;
            if (disposed) throw new ObjectDisposedException(nameof(MeshWorldVtProducer));
            if (!MeshWorldVirtualTexture.DecodeKey(key, out int mip, out int x, out int z) ||
                mip > MeshWorldVirtualTexture.FirstSvtMip)
                throw new ArgumentOutOfRangeException(nameof(key), "Draw pages through mip 8. Filter larger SVT pages offline.");
            GetRecipeIdentity(settings);
            if (!MaterialSourcesReady())
            {
                PageInputNotReadyRetries++;
                return false;
            }
            if (batch != null) EnsureDepth();
            else EnsureStaging();

            float size = MeshWorldVirtualTexture.PageWorldSize(mip);
            float originX = x * size, originZ = z * size;
            // 两条路径使用同一窗口公式。异步队列只含固定世界数据，不依赖显示Clipmap。
            MeshWorldVtSourceReader.DescribePage(key, out int sourceX, out int sourceZ, out int side);
            SourceWindow window;
            if (preparedOnly)
            {
                MeshWorldVtSourcePage ready;
                try
                {
                    if (runtimeSources == null || !runtimeSources.TryTake(key, out ready))
                    {
                        PageInputNotReadyRetries++;
                        return false;
                    }
                }
                catch { PageInputValidationFailures++; throw; }
                window = GetSourceWindow(side);
                ValidatedPages++; // 整页校验已在worker完成；此处只向Unity提交上传。
                Upload(window.HeightTexture, ready.Heights);
                Upload(window.ControlTexture, ready.Controls);
            }
            else window = StagePageSources(key, sourceX, sourceZ, side);

            // 页矩形定义本次输出的世界范围，源窗口提供这个范围内固定的 1m 高度与控制点。
            materials.Bind(producer);
            producer.SetVector("_DDControlInfo", new Vector4(MeshControlContract.WorldSize, side, 1, 1));
            producer.SetFloat("_DDHexTiling", recipeHex);
            producer.SetFloat("_DDCliffProjection", recipeCliff);
            producer.SetVector("_DDHeightBlend", recipeBlend);
            producer.SetVector("_DDHexParams", recipeHexParameters);
            producer.SetVector("_DDRvtRect", new Vector4(originX, originZ, size, size));
            // 使用全局纹素坐标再乘世界步长，让相邻页重叠区得到完全相同的浮点世界位置。
            float texelWidth = size / MeshWorldVirtualTexture.PageInterior;
            producer.SetVector("_DDRvtTexelGrid", new Vector4(x * MeshWorldVirtualTexture.PageInterior,
                z * MeshWorldVirtualTexture.PageInterior, texelWidth, texelWidth));
            producer.SetVector("_DDRvtInfo", new Vector4(MeshWorldVirtualTexture.PageInterior, MeshWorldVirtualTexture.Border,
                MeshWorldVirtualTexture.PageSide, 1));
            producer.SetTexture("_DDRvtHeight", window.HeightTexture);
            producer.SetVector("_DDRvtHeightInfo", new Vector4(sourceX, sourceZ, side, source.Metadata.heightScale));
            producer.SetTexture("_DDRvtControls", window.ControlTexture);
            producer.SetVector("_DDRvtControlInfo", new Vector4(sourceX, sourceZ, side, 1));
            CompressionSourceHeight = window.HeightTexture;
            CompressionHeightInfo = new Vector4(sourceX, sourceZ, side, source.Metadata.heightScale);
            CompressionTexelGrid = new Vector4(x * MeshWorldVirtualTexture.PageInterior,
                z * MeshWorldVirtualTexture.PageInterior, texelWidth, texelWidth);
            var pageRect = new Rect(originX, originZ, size, size);
            bool hasCoverage = overlays != null && overlays.Count > 0 && overlays.HasCoverage(pageRect);
            RenderTargetIdentifier[] batchTargets = batch != null ? batch.GetPageRenderTargets(hasCoverage) : null;
            if (hasCoverage)
            {
                BindOverlayPageInputs(overlayWriter, window, sourceX, sourceZ, side, originX, originZ, size, x, z);
                if (!fuseOverlayCompression)
                    BindOverlayPageInputs(overlayRepack, window, sourceX, sourceZ, side, originX, originZ, size, x, z);
                materials.Bind(overlayWriter);
                DrawOverlayPage(pageRect, !fuseOverlayCompression, batchTargets, batch?.QueuedPageCount ?? -1);
                CompressionUsesOverlayMrt = fuseOverlayCompression;
            }
            else DrawPage(batchTargets, batch?.QueuedPageCount ?? -1);
            if (batch != null) DirectBatchPagesRendered++;
            return true;
        }

        void BindOverlayPageInputs(Material target, SourceWindow window, int sourceX, int sourceZ,
            int side, float originX, float originZ, float size, int pageX, int pageZ)
        {
            // 所有writer使用完全相同的固定源和全局纹素网格，和相机/显示morph无关。
            target.SetVector("_DDControlInfo", new Vector4(MeshControlContract.WorldSize, side, 1, 1));
            target.SetTexture("_DDRvtHeight", window.HeightTexture);
            target.SetVector("_DDRvtHeightInfo", new Vector4(sourceX, sourceZ, side, source.Metadata.heightScale));
            target.SetVector("_DDRvtRect", new Vector4(originX, originZ, size, size));
            float step = size / MeshWorldVirtualTexture.PageInterior;
            target.SetVector("_DDRvtTexelGrid", new Vector4(pageX * MeshWorldVirtualTexture.PageInterior,
                pageZ * MeshWorldVirtualTexture.PageInterior, step, step));
            target.SetVector("_DDRvtInfo", new Vector4(MeshWorldVirtualTexture.PageInterior,
                MeshWorldVirtualTexture.Border, MeshWorldVirtualTexture.PageSide, 1));
        }

        void DrawOverlayPage(Rect pageRect, bool repack, RenderTargetIdentifier[] batchTargets = null, int batchSlice = -1)
        {
            // batchTargets 已经是当前页的数组 slice 视图。仅离线/参考路径需要
            // 额外分配三张 2D 覆盖 MRT；运行时直接将合成结果留给压缩批次。
            if (batchTargets == null) EnsureOverlayStaging();
            else if (repack) throw new InvalidOperationException("Direct BC3 batch rendering must not repack through 2D stages.");
            commands.Clear();
            commands.BeginSample("Delta World VT: compose static road and decal");
            if (batchTargets == null) commands.SetRenderTarget(overlayOutputs, new RenderTargetIdentifier(depth));
            else commands.SetRenderTarget(batchTargets, BuiltinRenderTextureType.None, 0, CubemapFace.Unknown, batchSlice);
            commands.SetViewport(new Rect(0, 0, MeshWorldVirtualTexture.PageSide, MeshWorldVirtualTexture.PageSide));
            commands.ClearRenderTarget(batchTargets == null, true, Color.clear);
            commands.DrawProcedural(Matrix4x4.identity, producer, 1, MeshTopology.Triangles, 3, 1);
            int draws = overlays.Draw(commands, overlayWriter, pageRect);
            commands.EndSample("Delta World VT: compose static road and decal");

            // The raw/reference path still needs two RGBA8 stages. Runtime BC3
            // consumes the three MRTs directly and performs packing in compression.
            if (repack)
            {
                commands.BeginSample("Delta World VT: repack overlay MRT");
                commands.SetRenderTarget(outputs, new RenderTargetIdentifier(depth));
                overlayRepack.SetTexture("_DDOverlayTempColor", overlayTargets[0]);
                overlayRepack.SetTexture("_DDOverlayTempNormal", overlayTargets[1]);
                overlayRepack.SetTexture("_DDOverlayTempData", overlayTargets[2]);
                commands.DrawProcedural(Matrix4x4.identity, overlayRepack, 0, MeshTopology.Triangles, 3, 1);
                commands.EndSample("Delta World VT: repack overlay MRT");
            }
#if UNITY_EDITOR
            bool previousAsync = UnityEditor.ShaderUtil.allowAsyncCompilation;
            UnityEditor.ShaderUtil.allowAsyncCompilation = false;
#endif
            try
            {
                Graphics.ExecuteCommandBuffer(commands);
#if UNITY_EDITOR
                if (UnityEditor.ShaderUtil.ShaderHasError(producer.shader) ||
                    UnityEditor.ShaderUtil.ShaderHasError(overlayWriter.shader) ||
                    (repack && UnityEditor.ShaderUtil.ShaderHasError(overlayRepack.shader)))
                    throw new InvalidOperationException("World VT static overlay shader compilation failed.");
#endif
                OverlayPagesRendered++;
                OverlayDraws += draws;
                if (repack) OverlayRepackDraws++;
            }
            finally
            {
#if UNITY_EDITOR
                UnityEditor.ShaderUtil.allowAsyncCompilation = previousAsync;
#endif
            }
        }

        void EnsureOverlayStaging()
        {
            if (overlayTargets[0] && overlayTargets[0].IsCreated() && overlayTargets[1] &&
                overlayTargets[1].IsCreated() && overlayTargets[2] && overlayTargets[2].IsCreated()) return;
            ReleaseOverlayStaging();
            try
            {
                overlayTargets[0] = MakeTarget("World VT overlay MRT albedo opacity", MeshWorldVirtualTexture.AlbedoFormat, 0);
                overlayTargets[1] = MakeTarget("World VT overlay MRT world normal opacity", MeshWorldVirtualTexture.DataFormat, 0);
                overlayTargets[2] = MakeTarget("World VT overlay MRT AO roughness height opacity", MeshWorldVirtualTexture.DataFormat, 0);
                overlayOutputs = new[] { new RenderTargetIdentifier(overlayTargets[0]),
                    new RenderTargetIdentifier(overlayTargets[1]), new RenderTargetIdentifier(overlayTargets[2]) };
            }
            catch { ReleaseOverlayStaging(); throw; }
        }

        void ReleaseOverlayStaging()
        {
            for (int i = 0; i < overlayTargets.Length; i++) { ReleaseTarget(overlayTargets[i]); overlayTargets[i] = null; }
            overlayOutputs = null;
        }

        bool MaterialSourcesReady()
        {
            if (!materials.AlbedoHeight || !materials.NormalRoughAo) return false;
            ValidateMaterialArray(materials.AlbedoHeight, true);
            ValidateMaterialArray(materials.NormalRoughAo, false);
            return true;
        }

        void ValidateMaterialArray(Texture2DArray texture, bool srgb)
        {
            if (texture.width != materials.Set.resolution || texture.height != materials.Set.resolution ||
                texture.depth != MeshMaterialSet.Capacity || texture.mipmapCount != materials.Set.MipCount ||
                texture.format != materials.Format || !GraphicsFormatUtility.IsCompressedFormat(texture.graphicsFormat) ||
                GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat) != srgb)
                throw new InvalidDataException("World VT requires all sixteen compressed material layers and their complete mip chains.");
        }

        SourceWindow StagePageSources(uint key, int sourceX, int sourceZ, int side)
        {
            // 离线与参考路径可同步读取，运行时改走上方ready-only分支。
            SourceWindow window = GetSourceWindow(side);
            try
            {
                source.CopyGridRectangle(0, sourceX, sourceZ, side, side, window.Heights);
                ReadControls(sourceX, sourceZ, side, window);
                ValidatePageSources(key, sourceX, sourceZ, side, window.Heights, window.Controls, source.Metadata.heightScale);
            }
            catch
            {
                PageInputValidationFailures++;
                throw;
            }
            ValidatedPages++;
            // CPU完整检查通过之后才上传；坏输入不会覆盖stage，更不会发布物理槽。
            Upload(window.HeightTexture, window.Heights);
            Upload(window.ControlTexture, window.Controls);
            return window;
        }

        SourceWindow GetSourceWindow(int side)
        {
            if (!windows.TryGetValue(side, out SourceWindow window))
            {
                window = new SourceWindow(side);
                windows.Add(side, window);
            }
            return window;
        }

        internal static void ValidatePageSources(uint key, int sourceX, int sourceZ, int side,
            ushort[] heights, ushort[] controlValues, float heightScale) =>
            MeshWorldVtSourceReader.ValidatePageSources(key, sourceX, sourceZ, side, heights, controlValues, heightScale);

        void DrawPage(RenderTargetIdentifier[] batchTargets = null, int batchSlice = -1)
        {
            commands.Clear();
            if (batchTargets == null) commands.SetRenderTarget(outputs, new RenderTargetIdentifier(depth));
            else commands.SetRenderTarget(batchTargets, BuiltinRenderTextureType.None, 0, CubemapFace.Unknown, batchSlice);
            commands.SetViewport(new Rect(0, 0, MeshWorldVirtualTexture.PageSide, MeshWorldVirtualTexture.PageSide));
            // 全屏覆盖两张stage；输入已在CPU整页验证，正式页池不存逐纹素有效性。
            commands.ClearRenderTarget(batchTargets == null, true, Color.clear);
            commands.DrawProcedural(Matrix4x4.identity, producer, 0, MeshTopology.Triangles, 3, 1);
#if UNITY_EDITOR
            bool previousAsync = UnityEditor.ShaderUtil.allowAsyncCompilation;
            UnityEditor.ShaderUtil.allowAsyncCompilation = false;
#endif
            try
            {
                Graphics.ExecuteCommandBuffer(commands);
#if UNITY_EDITOR
                if (UnityEditor.ShaderUtil.ShaderHasError(producer.shader))
                    throw new InvalidOperationException("World VT producer shader compilation failed.");
#endif
            }
            finally
            {
#if UNITY_EDITOR
                UnityEditor.ShaderUtil.allowAsyncCompilation = previousAsync;
#endif
            }
        }

        void ReadControls(int sourceX, int sourceZ, int side, SourceWindow window)
        {
            // 世界边缘的页边框沿用最近的源控制点，避免跨出原始 R16 数据。
            int left = Mathf.Clamp(sourceX, 0, MeshControlContract.WorldSize);
            int right = Mathf.Clamp(sourceX + side - 1, 0, MeshControlContract.WorldSize);
            int column = Math.Min(side - 1, Math.Max(0, left - sourceX));
            int count = right - left + 1;
            int previousZ = -1;
            for (int z = 0; z < side; z++)
            {
                int worldZ = Mathf.Clamp(sourceZ + z, 0, MeshControlContract.WorldSize);
                int rowStart = z * side;
                if (worldZ == previousZ)
                {
                    Array.Copy(window.Controls, rowStart - side, window.Controls, rowStart, side);
                    continue;
                }
                previousZ = worldZ;
                controlFile.Position = ((long)worldZ * controls.width + left) * sizeof(ushort);
                ReadExactly(controlFile, window.ControlRow, count * sizeof(ushort));
                Buffer.BlockCopy(window.ControlRow, 0, window.Controls, (rowStart + column) * sizeof(ushort), count * sizeof(ushort));
                ushort first = window.Controls[rowStart + column], last = window.Controls[rowStart + column + count - 1];
                for (int i = 0; i < column; i++) window.Controls[rowStart + i] = first;
                for (int i = column + count; i < side; i++) window.Controls[rowStart + i] = last;
            }
        }

        internal static void ReadExactly(Stream stream, byte[] bytes, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int received = stream.Read(bytes, offset, count - offset);
                if (received <= 0) throw new EndOfStreamException("World VT data ended inside a page or row.");
                offset += received;
            }
        }

        static Texture2D MakeSourceTexture(int side, string name, FilterMode filter)
        {
            var result = new Texture2D(side, side, TextureFormat.R16, false, true)
            {
                name = name, hideFlags = HideFlags.HideAndDontSave, filterMode = filter,
                wrapMode = TextureWrapMode.Clamp, anisoLevel = 1
            };
            if (result.graphicsFormat == GraphicsFormat.R16_UNorm) return result;
            MeshResidentHeight.Destroy(result);
            throw new NotSupportedException("World VT requires native R16 source textures.");
        }

        static void Upload(Texture2D texture, ushort[] values)
        {
            texture.SetPixelData(values, 0);
            texture.Apply(false, false);
        }

        void EnsureDepth()
        {
            if (depth && depth.IsCreated()) return;
            ReleaseTarget(depth);
            depth = null;
            depth = MakeTarget("World VT stage depth", GraphicsFormat.None, 24);
        }

        void EnsureStaging()
        {
            if (AlbedoAo && AlbedoAo.IsCreated() && NormalRoughnessHeight && NormalRoughnessHeight.IsCreated() &&
                depth && depth.IsCreated()) return;
            ReleaseStaging();
            try
            {
                AlbedoAo = MakeTarget("World VT stage albedo AO", MeshWorldVirtualTexture.AlbedoFormat, 0);
                NormalRoughnessHeight = MakeTarget("World VT stage normal roughness height", MeshWorldVirtualTexture.DataFormat, 0);
                depth = MakeTarget("World VT stage depth", GraphicsFormat.None, 24);
                outputs = new[] { new RenderTargetIdentifier(AlbedoAo), new RenderTargetIdentifier(NormalRoughnessHeight) };
            }
            catch { ReleaseStaging(); throw; }
        }

        static RenderTexture MakeTarget(string name, GraphicsFormat format, int depthBits)
        {
            int side = MeshWorldVirtualTexture.PageSide;
            var result = new RenderTexture(new RenderTextureDescriptor(side, side, format, depthBits))
            {
                name = name, hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp, useMipMap = false, autoGenerateMips = false, antiAliasing = 1
            };
            if (result.Create() && result.graphicsFormat == format)
                return result;
            ReleaseTarget(result);
            throw new NotSupportedException("Could not allocate World VT staging: " + name);
        }

        static void ReleaseTarget(RenderTexture target)
        {
            if (!target) return;
            target.Release();
            MeshResidentHeight.Destroy(target);
        }

        void ReleaseStaging()
        {
            ReleaseTarget(AlbedoAo);
            ReleaseTarget(NormalRoughnessHeight);
            ReleaseTarget(depth);
            AlbedoAo = null;
            NormalRoughnessHeight = null;
            depth = null;
            outputs = null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            runtimeSources?.Dispose(); // 不等待磁盘；后台读者在结束后自行释放文件/cache。
            commands?.Dispose();
            controlFile?.Dispose();
            foreach (SourceWindow window in windows.Values) window.Dispose();
            windows.Clear();
            overlays?.Dispose();
            ReleaseOverlayStaging();
            ReleaseStaging();
            MeshResidentHeight.Destroy(producer);
            MeshResidentHeight.Destroy(overlayWriter);
            MeshResidentHeight.Destroy(overlayRepack);
        }
    }
}
