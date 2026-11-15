using System;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxTerrain
{
    /// <summary>
    /// One persistent, explicitly placed surface page for the Delta comparison.
    /// Both source maps are local snapshots of the repaired one-metre data.
    /// The page pass reconstructs its height and normal from that snapshot,
    /// independently of the observer's CDLOD morph and moving Clipmap windows.
    /// This source reconstruction is our adaptation of a page-space producer.
    /// </summary>
    public sealed class MeshSinglePageRvt : IDisposable
    {
        public const int Border = 8;
        public const int DefaultResolution = 2048;
        public const float DefaultWorldSize = 32;
        public const string ProducerShaderName = "DirectDeltaMesh/SinglePageProducer";

        readonly MeshHeightSource heightSource;
        readonly string controlDirectory;
        readonly MeshMaterialResident materials;
        readonly Vector4[] materialUvs = new Vector4[MeshMaterialSet.Capacity];
        readonly Vector4[] materialParameters = new Vector4[MeshMaterialSet.Capacity];
        Material producer;
        RenderTexture depth;
        bool generated, disposed;
        float hexTiling, cliffProjection;
        Vector4 heightBlend, hexParameters;
        string materialIdentity;
        Texture sourceAlbedoHeight, sourceNormalRoughAo;

        public RenderTexture AlbedoAo { get; private set; }
        public RenderTexture NormalRoughnessHeight { get; private set; }
        public Texture2D HeightTexture { get; private set; }
        public Texture2D ControlTexture { get; private set; }
        public Vector2 Origin { get; private set; }
        public float WorldSize { get; private set; }
        public int Resolution { get; private set; }
        public int PhysicalSide => Resolution + 2 * Border;
        public Vector2 SourceOrigin { get; private set; }
        public int SourceSide { get; private set; }
        public int GenerationCount { get; private set; }
        public long BytesRead { get; private set; }
        public string SourceFingerprint { get; private set; }
        public string Error { get; private set; }
        public bool Ready => !disposed && generated && AlbedoAo && AlbedoAo.IsCreated() &&
            NormalRoughnessHeight && NormalRoughnessHeight.IsCreated() && depth && depth.IsCreated() &&
            HeightTexture && ControlTexture;

        // Logical payloads only. Drivers and rebuild CPU buffers are additional.
        public long SourceBytes => HeightTexture && ControlTexture ? (long)SourceSide * SourceSide * 4 : 0;
        public long CacheBytes => AlbedoAo && NormalRoughnessHeight ? (long)PhysicalSide * PhysicalSide * 16 : 0;
        public long DepthBytes => depth ? (long)PhysicalSide * PhysicalSide * 4 : 0;
        public long GpuBytes => SourceBytes + CacheBytes + DepthBytes;

        public MeshSinglePageRvt(MeshHeightSource heightSource, string controlDirectory,
            MeshMaterialResident materials, Material sourceSettings)
        {
            this.heightSource = heightSource ?? throw new ArgumentNullException(nameof(heightSource));
            this.materials = materials ?? throw new ArgumentNullException(nameof(materials));
            this.controlDirectory = Path.GetFullPath(controlDirectory ?? throw new ArgumentNullException(nameof(controlDirectory)));
            if (!sourceSettings) throw new ArgumentNullException(nameof(sourceSettings));
            MeshControlBake.ValidateMaterialContract(this.controlDirectory, materials.Set);
            if (SystemInfo.supportedRenderTargetCount < 2 ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ||
                !SystemInfo.SupportsTextureFormat(TextureFormat.R16))
                throw new NotSupportedException("Single-page RVT requires two ARGBHalf render targets and native R16 source textures.");
            Shader shader = Shader.Find(ProducerShaderName);
            if (!shader || !shader.isSupported)
                throw new NotSupportedException("The single-page RVT producer shader is missing or unsupported.");
            producer = new Material(shader)
            {
                name = "Delta single-page surface producer",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        public bool NeedsRebuild(Vector2 origin, float worldSize, int resolution, Material settings)
        {
            RequireLive();
            if (!settings) throw new ArgumentNullException(nameof(settings));
            if (!Ready || !Origin.Equals(origin) || WorldSize != worldSize || Resolution != resolution ||
                hexTiling != settings.GetFloat("_DDHexTiling") ||
                cliffProjection != settings.GetFloat("_DDCliffProjection") ||
                !heightBlend.Equals(settings.GetVector("_DDHeightBlend")) ||
                !hexParameters.Equals(settings.GetVector("_DDHexParams")) ||
                materialIdentity != materials.Set.sourceFingerprint ||
                sourceAlbedoHeight != materials.AlbedoHeight || sourceNormalRoughAo != materials.NormalRoughAo)
                return true;
            for (int id = 0; id < MeshMaterialSet.Capacity; id++)
                if (!materialUvs[id].Equals(materials.Set.uvPerMetre[id]) ||
                    !materialParameters[id].Equals(materials.Set.parameters[id])) return true;
            return false;
        }

        /// <summary>Regenerate from the loaded sources. Reinitialize the scene after editing source files; camera movement does not invalidate pages.</summary>
        public void Invalidate() { RequireLive(); generated = false; }

        public void Rebuild(Vector2 origin, float worldSize, int resolution, Material sourceSettings)
        {
            RequireLive();
            ValidateRequest(origin, worldSize, resolution);
            if (!sourceSettings) throw new ArgumentNullException(nameof(sourceSettings));
            generated = false;
            Error = null;
            ReleasePage();
            Origin = origin;
            WorldSize = worldSize;
            Resolution = resolution;
            try
            {
                MeshControlBake.ValidateMaterialContract(controlDirectory, materials.Set);
                MeshControlMetadata metadata = MeshControlBake.ReadMetadata(controlDirectory);
                PrepareSourceSnapshot(metadata);
                AlbedoAo = CreateColorTarget("Delta single-page Albedo AO");
                NormalRoughnessHeight = CreateColorTarget("Delta single-page normal roughness material height");
                depth = new RenderTexture(PhysicalSide, PhysicalSide, 24, RenderTextureFormat.Depth, RenderTextureReadWrite.Linear)
                {
                    name = "Delta single-page producer depth",
                    hideFlags = HideFlags.HideAndDontSave,
                    useMipMap = false,
                    autoGenerateMips = false,
                    antiAliasing = 1
                };
                if (!depth.Create()) throw new InvalidOperationException("Could not allocate single-page producer depth.");
                ApplyProducerSettings(sourceSettings);
                RenderPage();
                CaptureSettings(sourceSettings);
                generated = true;
                GenerationCount++;
            }
            catch (Exception exception)
            {
                Error = exception.Message;
                ReleasePage();
                throw;
            }
        }

        static void ValidateRequest(Vector2 origin, float worldSize, int resolution)
        {
            if (!Finite(origin.x) || !Finite(origin.y) || !Finite(worldSize) || worldSize < 1 || worldSize > 512 ||
                origin.x < 0 || origin.y < 0 || origin.x + worldSize > MeshHeightLayout.WorldSize ||
                origin.y + worldSize > MeshHeightLayout.WorldSize)
                throw new ArgumentOutOfRangeException(nameof(origin), "A single page must cover 1..512 metres inside the terrain.");
            if (resolution < 128 || resolution > 2048 || (resolution & (resolution - 1)) != 0 ||
                resolution + 2 * Border > SystemInfo.maxTextureSize)
                throw new ArgumentOutOfRangeException(nameof(resolution), "Single-page interior resolution must be a supported power of two from 128 to 2048.");
        }

        void PrepareSourceSnapshot(MeshControlMetadata metadata)
        {
            float borderMetres = Border * WorldSize / Resolution;
            int sourceX = Mathf.FloorToInt(Origin.x - borderMetres) - 2;
            int sourceZ = Mathf.FloorToInt(Origin.y - borderMetres) - 2;
            int lastX = Mathf.CeilToInt(Origin.x + WorldSize + borderMetres) + 2;
            int lastZ = Mathf.CeilToInt(Origin.y + WorldSize + borderMetres) + 2;
            SourceSide = Math.Max(lastX - sourceX, lastZ - sourceZ) + 1;
            SourceOrigin = new Vector2(sourceX, sourceZ);
            var heights = new ushort[SourceSide * SourceSide];
            long heightBytesBefore = heightSource.BytesRead;
            for (int z = 0; z < SourceSide; z++)
                for (int x = 0; x < SourceSide; x++)
                    heights[z * SourceSide + x] = heightSource.SampleGrid(sourceX + x, sourceZ + z, 0);
            BytesRead += heightSource.BytesRead - heightBytesBefore;

            ushort[] controls = ReadControlWindow(metadata.levels[0], sourceX, sourceZ);
            if (MeshControlContract.CountInvalidTriangles(controls, SourceSide, SourceSide) != 0)
                throw new InvalidDataException("Single-page source snapshot contains an unrepaired material neighborhood.");
            HeightTexture = CreateSourceTexture("Delta page fixed 1m height source", heights, FilterMode.Bilinear);
            ControlTexture = CreateSourceTexture("Delta page fixed 1m control source", controls, FilterMode.Point);
            SourceFingerprint = HashValues(heights) + ":" + HashValues(controls) + ":" + materials.Set.sourceFingerprint;
        }

        ushort[] ReadControlWindow(MeshControlLevel entry, int originX, int originZ)
        {
            var result = new ushort[SourceSide * SourceSide];
            int left = Mathf.Clamp(originX, 0, MeshControlContract.WorldSize);
            int right = Mathf.Clamp(originX + SourceSide - 1, 0, MeshControlContract.WorldSize);
            var row = new byte[(right - left + 1) * 2];
            using (var stream = new FileStream(Path.Combine(controlDirectory, entry.fileName), FileMode.Open,
                FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess))
            {
                if (stream.Length != entry.bytes) throw new InvalidDataException("Single-page control source length changed.");
                for (int z = 0; z < SourceSide; z++)
                {
                    int worldZ = Mathf.Clamp(originZ + z, 0, MeshControlContract.WorldSize);
                    stream.Position = ((long)worldZ * entry.width + left) * 2;
                    int read = 0;
                    while (read < row.Length)
                    {
                        int count = stream.Read(row, read, row.Length - read);
                        if (count == 0) throw new EndOfStreamException("Single-page control source row is incomplete.");
                        read += count;
                    }
                    BytesRead += read;
                    for (int x = 0; x < SourceSide; x++)
                    {
                        int worldX = Mathf.Clamp(originX + x, 0, MeshControlContract.WorldSize);
                        int offset = (worldX - left) * 2;
                        result[z * SourceSide + x] = (ushort)(row[offset] | row[offset + 1] << 8);
                    }
                }
            }
            return result;
        }

        Texture2D CreateSourceTexture(string name, ushort[] values, FilterMode filter)
        {
            var texture = new Texture2D(SourceSide, SourceSide, TextureFormat.R16, false, true)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 1
            };
            try
            {
                if (texture.graphicsFormat != GraphicsFormat.R16_UNorm || texture.mipmapCount != 1)
                    throw new NotSupportedException("Page sources require native R16_UNorm without generated mips.");
                texture.SetPixelData(values, 0);
                texture.Apply(false, true);
                return texture;
            }
            catch { MeshResidentHeight.Destroy(texture); throw; }
        }

        RenderTexture CreateColorTarget(string name)
        {
            var texture = new RenderTexture(PhysicalSide, PhysicalSide, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 1,
                useMipMap = false,
                autoGenerateMips = false,
                antiAliasing = 1
            };
            if (texture.Create() && texture.graphicsFormat == GraphicsFormat.R16G16B16A16_SFloat) return texture;
            texture.Release();
            MeshResidentHeight.Destroy(texture);
            throw new InvalidOperationException("Could not allocate native RGBA16F single-page surface output.");
        }

        void ApplyProducerSettings(Material settings)
        {
            materials.Bind(producer);
            producer.SetVector("_DDControlInfo", new Vector4(MeshControlContract.WorldSize, SourceSide, 1, 1));
            producer.SetFloat("_DDHexTiling", settings.GetFloat("_DDHexTiling"));
            producer.SetFloat("_DDCliffProjection", settings.GetFloat("_DDCliffProjection"));
            producer.SetVector("_DDHeightBlend", settings.GetVector("_DDHeightBlend"));
            producer.SetVector("_DDHexParams", settings.GetVector("_DDHexParams"));
            BindPageCoordinates(producer, true);
            producer.SetTexture("_DDRvtHeight", HeightTexture);
            producer.SetVector("_DDRvtHeightInfo", new Vector4(SourceOrigin.x, SourceOrigin.y, SourceSide, heightSource.Metadata.heightScale));
            producer.SetTexture("_DDRvtControls", ControlTexture);
            producer.SetVector("_DDRvtControlInfo", new Vector4(SourceOrigin.x, SourceOrigin.y, SourceSide, 1));
        }

        void RenderPage()
        {
#if UNITY_EDITOR
            // A persistent page must never publish the Editor's asynchronous
            // compilation placeholder on its first draw.
            bool previousAsyncCompilation = UnityEditor.ShaderUtil.allowAsyncCompilation;
            UnityEditor.ShaderUtil.allowAsyncCompilation = false;
#endif
            try
            {
                using (var commands = new CommandBuffer { name = "Delta generate fixed single-page RVT" })
                {
                    var outputs = new[] { new RenderTargetIdentifier(AlbedoAo), new RenderTargetIdentifier(NormalRoughnessHeight) };
                    commands.SetRenderTarget(outputs, new RenderTargetIdentifier(depth));
                    commands.SetViewport(new Rect(0, 0, PhysicalSide, PhysicalSide));
                    commands.ClearRenderTarget(true, true, new Color(0, 0, 0, -1));
                    commands.DrawProcedural(Matrix4x4.identity, producer, 0, MeshTopology.Triangles, 3, 1);
                    Graphics.ExecuteCommandBuffer(commands);
                }
#if UNITY_EDITOR
                if (UnityEditor.ShaderUtil.ShaderHasError(producer.shader))
                    throw new InvalidOperationException("The single-page RVT producer failed shader compilation.");
#endif
            }
            finally
            {
#if UNITY_EDITOR
                UnityEditor.ShaderUtil.allowAsyncCompilation = previousAsyncCompilation;
#endif
            }
            // The draw is submitted before binding this page for later consumer draws.
            // This relies on graphics queue ordering, not a synchronous CPU readback.
        }

        void CaptureSettings(Material settings)
        {
            hexTiling = settings.GetFloat("_DDHexTiling");
            cliffProjection = settings.GetFloat("_DDCliffProjection");
            heightBlend = settings.GetVector("_DDHeightBlend");
            hexParameters = settings.GetVector("_DDHexParams");
            materialIdentity = materials.Set.sourceFingerprint;
            sourceAlbedoHeight = materials.AlbedoHeight;
            sourceNormalRoughAo = materials.NormalRoughAo;
            Array.Copy(materials.Set.uvPerMetre, materialUvs, materialUvs.Length);
            Array.Copy(materials.Set.parameters, materialParameters, materialParameters.Length);
        }

        public void Bind(Material consumer, int mode)
        {
            RequireLive();
            if (!consumer) throw new ArgumentNullException(nameof(consumer));
            if (mode < 0 || mode > 2) throw new ArgumentOutOfRangeException(nameof(mode));
            BindPageCoordinates(consumer, Ready);
            consumer.SetInt("_DDRvtMode", mode);
            consumer.SetTexture("_DDRvtAlbedoAo", AlbedoAo);
            consumer.SetTexture("_DDRvtNormalRoughnessHeight", NormalRoughnessHeight);
        }

        void BindPageCoordinates(Material material, bool ready)
        {
            material.SetVector("_DDRvtRect", new Vector4(Origin.x, Origin.y, WorldSize, WorldSize));
            material.SetVector("_DDRvtInfo", new Vector4(Resolution, Border, PhysicalSide, ready ? 1 : 0));
        }

        static string HashValues(ushort[] values)
        {
            var bytes = new byte[values.Length * 2];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            if (!BitConverter.IsLittleEndian) MeshControlBake.SwapPairs(bytes);
            return MeshMaterialResident.Sha256(bytes);
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        void RequireLive() { if (disposed) throw new ObjectDisposedException(nameof(MeshSinglePageRvt)); }

        void ReleasePage()
        {
            generated = false;
            ReleaseTarget(AlbedoAo);
            ReleaseTarget(NormalRoughnessHeight);
            ReleaseTarget(depth);
            AlbedoAo = null;
            NormalRoughnessHeight = null;
            depth = null;
            MeshResidentHeight.Destroy(HeightTexture);
            MeshResidentHeight.Destroy(ControlTexture);
            HeightTexture = null;
            ControlTexture = null;
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
            ReleasePage();
            MeshResidentHeight.Destroy(producer);
            producer = null;
        }
    }
}
