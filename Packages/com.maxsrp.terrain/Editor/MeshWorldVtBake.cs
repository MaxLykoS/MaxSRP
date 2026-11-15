using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace MaxTerrain.Editor
{
    public static class MeshWorldVtBake
    {
        const string SourceScene = "Assets/Samples/MaxTerrain/Scenes/DirectDeltaSinglePageRvt10km.unity";
        const int Side = MeshWorldVirtualTexture.PageSide;
        const int Interior = MeshWorldVirtualTexture.PageInterior;
        const int Border = MeshWorldVirtualTexture.Border;
        const int BytesPerTexel = MeshWorldVirtualTexture.BytesPerTexel;
        const int ChannelBytes = MeshWorldVirtualTexture.ChannelBytes;
        const int PageBytes = MeshWorldVtArchive.PageBytes;
        const int FirstMip = MeshWorldVirtualTexture.FirstSvtMip;
        const int LastMip = MeshWorldVirtualTexture.RootMip;

        [Serializable]
        sealed class BakeReport
        {
            public bool passed;
            public string error, gpu, api, recipe, payloadSha256;
            public int renderedPages, filteredPages, validatedPages, seamComparisons;
            public long payloadBytes;
            public double elapsedSeconds;
        }

        // Execute in a D3D11 batch Editor. DIRECT_DELTA_SVT_OUTPUT can stage an
        // archive elsewhere before copying the manifest and payload as a pair.
        public static void Run() => Run(false);

        public static void RunBc3() => Run(true);

        public sealed class LocalBakeResult
        {
            public string recipe, rawPayloadSha256, bc3PayloadSha256;
            public int rawPages, compressedPages;
        }

        /// <summary>
        /// 普通 Editor 的本地发布入口。调用者先冻结道路/贴花及遮罩，指定一个新的
        /// 版本目录；本函数完整烘焙 RGBA8 与 BC3，验证成功后才返回给发布工具切换引用。
        /// 不打开别的场景，不退出 Editor，也不覆盖已被运行场景使用的归档。
        /// </summary>
        public static LocalBakeResult BakeLocal(MeshComparison rig, MeshWorldVtOverlaySet frozenOverlays,
            string outputDirectory, bool showProgress = true, MeshMaterialSet frozenMaterials = null,
            bool? frozenHexTiling = null)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || !rig || !frozenOverlays || !rig.surfaceMaterials || !rig.geometryShader)
                throw new InvalidOperationException("Bake a fixed terrain snapshot outside Play in its authoring scene.");
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11 || PlayerSettings.colorSpace != ColorSpace.Linear)
                throw new NotSupportedException("The current local BC3 bake requires Editor D3D11 and Linear colour space.");
            string directory = Path.GetFullPath(outputDirectory);
            string root = Path.GetFullPath(Application.streamingAssetsPath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The local terrain bake must stay inside this project's StreamingAssets.");
            string compressed = Path.Combine(directory, MeshWorldVtArchive.Bc3Subdirectory);
            if (File.Exists(Path.Combine(directory, MeshWorldVtArchive.ManifestName)) ||
                File.Exists(Path.Combine(compressed, MeshWorldVtArchive.ManifestName)))
                throw new InvalidOperationException("Bake to a new version directory so a failed build cannot replace the previous publication.");
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(compressed);
            var raw = new BakeReport { gpu = SystemInfo.graphicsDeviceName, api = SystemInfo.graphicsDeviceType.ToString() };
            var bc3 = new BakeReport { gpu = raw.gpu, api = raw.api };
            var watch = Stopwatch.StartNew();
            Action<string, float> progress = showProgress ? (Action<string, float>)((message, value) =>
            {
                if (EditorUtility.DisplayCancelableProgressBar("Bake local terrain", message, value))
                    throw new OperationCanceledException("Terrain bake cancelled. The previous publication remains active.");
            }) : null;
            var settings = new Material(rig.geometryShader) { hideFlags = HideFlags.HideAndDontSave };
            settings.SetFloat("_DDHexTiling", (frozenHexTiling ?? rig.hexTiling) ? 1 : 0);
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                using (var source = new MeshHeightSource(rig.DatasetDirectory))
                using (var materials = new MeshMaterialResident(frozenMaterials ? frozenMaterials : rig.surfaceMaterials))
                using (var producer = new MeshWorldVtProducer(source,
                    Path.Combine(Application.streamingAssetsPath, rig.controlRelativeDirectory), materials,
                    rig.singlePageProducerShader, frozenOverlays))
                {
                    Bake(directory, producer, settings, raw, progress == null ? null :
                        (Action<string, float>)((message, value) => progress(message, value * .65f)));
                    raw.passed = true;
                    raw.elapsedSeconds = watch.Elapsed.TotalSeconds;
                    BakeBc3(directory, compressed, producer, settings, bc3, progress == null ? null :
                        (Action<string, float>)((message, value) => progress(message, .65f + value * .35f)));
                    bc3.passed = true;
                    bc3.elapsedSeconds = watch.Elapsed.TotalSeconds - raw.elapsedSeconds;
                    if (raw.recipe != bc3.recipe)
                        throw new InvalidDataException("Raw and BC3 pages must describe the same fixed terrain inputs.");
                    return new LocalBakeResult { recipe = raw.recipe, rawPayloadSha256 = raw.payloadSha256,
                        bc3PayloadSha256 = bc3.payloadSha256, rawPages = raw.validatedPages, compressedPages = bc3.validatedPages };
                }
            }
            catch (Exception exception)
            {
                if (!raw.passed) raw.error = exception.ToString();
                else bc3.error = exception.ToString();
                throw;
            }
            finally
            {
                File.WriteAllText(Path.Combine(directory, "bake-report.json"), JsonUtility.ToJson(raw, true));
                File.WriteAllText(Path.Combine(compressed, "bake-report.json"), JsonUtility.ToJson(bc3, true));
                Object.DestroyImmediate(settings);
                if (showProgress) EditorUtility.ClearProgressBar();
                AssetDatabase.AllowAutoRefresh();
            }
        }

        static void Run(bool useBc3)
        {
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');
            if (!Application.isBatchMode || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11 ||
                !project.StartsWith("D:/UnityProject/TerrainValidation/Temp/MaxTerrain10km", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Bake World SVT in an isolated MaxTerrain10km D3D11 batch Editor.");
            PlayerSettings.colorSpace = ColorSpace.Linear;
            string directory = Environment.GetEnvironmentVariable("DIRECT_DELTA_SVT_OUTPUT");
            string rawDirectory = Environment.GetEnvironmentVariable("DIRECT_DELTA_SVT_RAW_INPUT");
            if (string.IsNullOrWhiteSpace(rawDirectory))
                rawDirectory = Path.Combine(Application.streamingAssetsPath, MeshWorldVtArchive.DefaultRelativeDirectory);
            rawDirectory = Path.GetFullPath(rawDirectory);
            string sourceScene = Environment.GetEnvironmentVariable("DIRECT_DELTA_SVT_SOURCE_SCENE");
            if (string.IsNullOrWhiteSpace(sourceScene)) sourceScene = SourceScene;
            if (string.IsNullOrWhiteSpace(directory))
                directory = useBc3 ? Path.Combine(rawDirectory, MeshWorldVtArchive.Bc3Subdirectory) : rawDirectory;
            directory = Path.GetFullPath(directory);
            if (useBc3 && string.Equals(directory.TrimEnd('\\', '/'), Path.GetFullPath(rawDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("BC3 output must not replace the source RGBA8 archive.");
            Directory.CreateDirectory(directory);
            var report = new BakeReport { gpu = SystemInfo.graphicsDeviceName, api = SystemInfo.graphicsDeviceType.ToString() };
            var watch = Stopwatch.StartNew();
            int exit = 1;
            try
            {
                if (!File.Exists(sourceScene)) MeshSceneArchive.Restore(sourceScene);
                EditorSceneManager.OpenScene(sourceScene);
                MeshComparison rig = Object.FindObjectOfType<MeshComparison>();
                if (!rig || !rig.surfaceMaterials || !rig.geometryShader)
                    throw new InvalidDataException("The Surface16 source scene is missing its terrain recipe.");
                var settings = new Material(rig.geometryShader) { hideFlags = HideFlags.HideAndDontSave };
                settings.SetFloat("_DDHexTiling", rig.hexTiling ? 1 : 0);
                try
                {
                    string controls = Path.Combine(Application.streamingAssetsPath, rig.controlRelativeDirectory);
                    using (var source = new MeshHeightSource(rig.DatasetDirectory))
                    using (var materials = new MeshMaterialResident(rig.surfaceMaterials))
                    using (var producer = new MeshWorldVtProducer(source, controls, materials, rig.singlePageProducerShader,
                        rig.worldVtOverlays, rig.CollectWorldVtDecals, rig.CollectWorldVtRoads))
                    {
                        if (useBc3) BakeBc3(rawDirectory, directory, producer, settings, report);
                        else Bake(directory, producer, settings, report);
                    }
                }
                finally { Object.DestroyImmediate(settings); }
                report.passed = true;
                exit = 0;
            }
            catch (Exception exception) { report.error = exception.ToString(); Debug.LogException(exception); }
            finally
            {
                report.elapsedSeconds = watch.Elapsed.TotalSeconds;
                File.WriteAllText(Path.Combine(directory, "bake-report.json"), JsonUtility.ToJson(report, true));
                EditorApplication.Exit(exit);
            }
        }

        static void Bake(string directory, MeshWorldVtProducer producer, Material settings, BakeReport report,
            Action<string, float> progress = null)
        {
            string buildId = Guid.NewGuid().ToString("N");
            string staging = Path.Combine(directory, "pages-" + buildId + ".building");
            string indexStaging = Path.Combine(directory, "manifest-" + buildId + ".building");
            var entries = new List<MeshWorldVtArchive.PageEntry>();
            var indexes = new Dictionary<uint, int>();
            var bytes = new byte[PageBytes];
            report.recipe = producer.GetRecipeIdentity(settings);
            try
            {
                using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536))
                {
                    var filter = new ParentFilter();
                    for (int mip = FirstMip; mip <= LastMip; mip++)
                    {
                        int pages = MeshWorldVirtualTexture.PagesPerAxis(mip);
                        for (int z = 0; z < pages; z++)
                            for (int x = 0; x < pages; x++)
                            {
                                uint key = MeshWorldVirtualTexture.EncodeKey(mip, x, z);
                                if (mip == FirstMip)
                                {
                                    producer.Render(key, settings);
                                    ReadChannel(producer.AlbedoAo, bytes, 0);
                                    ReadChannel(producer.NormalRoughnessHeight, bytes, ChannelBytes);
                                    report.renderedPages++;
                                }
                                else
                                {
                                    filter.Downsample(stream, indexes, mip, x, z, bytes);
                                    report.filteredPages++;
                                }
                                ValidatePage(bytes, key);
                                report.validatedPages++;
                                if ((report.validatedPages & 127) == 1)
                                    progress?.Invoke($"RVT source bake: {report.validatedPages}/21845 pages (mip {mip})", report.validatedPages / 21845f);
                                if ((report.validatedPages & 511) == 0) Debug.Log($"World SVT progress: {report.validatedPages} pages, {report.renderedPages} rendered.");
                                indexes.Add(key, entries.Count);
                                entries.Add(new MeshWorldVtArchive.PageEntry
                                    { key = checked((int)key), sha256 = MeshMaterialResident.Sha256(bytes) });
                                stream.Position = (long)(entries.Count - 1) * PageBytes;
                                stream.Write(bytes, 0, bytes.Length);
                            }
                        VerifyRepresentativeBorders(stream, indexes, mip, pages, report);
                        Debug.Log($"World SVT baked mip {mip}, {pages * pages} pages.");
                    }
                    stream.Flush();
                    report.payloadBytes = stream.Length;
                    if (report.payloadBytes != (long)entries.Count * PageBytes)
                        throw new InvalidDataException("World SVT page payload length differs from the index.");
                    stream.Position = 0;
                    using (var sha = SHA256.Create()) report.payloadSha256 = Hex(sha.ComputeHash(stream));
                }

                Publish(directory, staging, indexStaging, producer, settings, entries, report, false);
            }
            finally
            {
                if (File.Exists(staging)) File.Delete(staging);
                if (File.Exists(indexStaging)) File.Delete(indexStaging);
            }
        }

        static void BakeBc3(string rawDirectory, string directory, MeshWorldVtProducer producer, Material settings, BakeReport report,
            Action<string, float> progress = null)
        {
            // 父页已在原归档以线性底色和解码后的法线过滤完毕。逐页压缩，避免在 mip 链上累积 BC3 误差。
            var raw = new MeshWorldVtArchive(rawDirectory, producer, settings);
            report.recipe = raw.RecipeIdentity;
            string id = Guid.NewGuid().ToString("N");
            string staging = Path.Combine(directory, "pages-" + id + ".building");
            string indexStaging = Path.Combine(directory, "manifest-" + id + ".building");
            var entries = new List<MeshWorldVtArchive.PageEntry>(raw.PageCount);
            var bytes = new byte[MeshWorldVtBc3.PageBytes];
            Texture2D color = null, data = null;
            RenderTexture readback = null;
            try
            {
                color = new Texture2D(Side, Side, MeshWorldVirtualTexture.AlbedoFormat, TextureCreationFlags.None);
                data = new Texture2D(Side, Side, MeshWorldVirtualTexture.DataFormat, TextureCreationFlags.None);
                int blocks = MeshWorldVtBc3.BlockSide;
                readback = new RenderTexture(new RenderTextureDescriptor(blocks, blocks, GraphicsFormat.R32G32B32A32_SFloat, 0))
                    { name = "Offline BC3 raw block readback" };
                if (!readback.Create() || readback.graphicsFormat != GraphicsFormat.R32G32B32A32_SFloat)
                    throw new NotSupportedException("Could not allocate BC3 offline readback target.");
                using (var encoder = new MeshWorldVtBc3())
                using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536))
                {
                    for (int mip = FirstMip; mip <= LastMip; mip++)
                    {
                        int side = MeshWorldVirtualTexture.PagesPerAxis(mip);
                        for (int z = 0; z < side; z++)
                        for (int x = 0; x < side; x++)
                        {
                            uint key = MeshWorldVirtualTexture.EncodeKey(mip, x, z);
                            byte[] input = raw.Read(key);
                            ValidatePage(input, key);
                            color.SetPixelData(input, 0, 0);
                            data.SetPixelData(input, 0, ChannelBytes);
                            color.Apply(false, false);
                            data.Apply(false, false);
                            encoder.Encode(color, data);
                            ReadBc3Blocks(encoder.ColorBlocks, readback, bytes, 0);
                            ReadBc3Blocks(encoder.DataBlocks, readback, bytes, MeshWorldVtBc3.ChannelBytes);
                            // BC3 的 RGB565 端点从块内第 8 字节起。SVT 全有效，两端点的 B 都必须是 31。
                            for (int i = MeshWorldVtBc3.ChannelBytes + 8; i < bytes.Length; i += 16)
                                if ((bytes[i] & 31) != 31 || (bytes[i + 2] & 31) != 31)
                                    throw new InvalidDataException($"BC3 SVT page {key} has an invalid B endpoint at byte {i}.");
                            entries.Add(new MeshWorldVtArchive.PageEntry
                                { key = checked((int)key), sha256 = MeshMaterialResident.Sha256(bytes) });
                            stream.Write(bytes, 0, bytes.Length);
                            report.validatedPages++;
                            if ((report.validatedPages & 127) == 1)
                                progress?.Invoke($"BC3 encode: {report.validatedPages}/{raw.PageCount} pages", report.validatedPages / (float)raw.PageCount);
                            if ((report.validatedPages & 1023) == 0) Debug.Log($"World SVT BC3 progress: {report.validatedPages}/{raw.PageCount} pages.");
                        }
                    }
                    stream.Flush();
                    report.payloadBytes = stream.Length;
                    if (entries.Count != raw.PageCount || report.payloadBytes != (long)entries.Count * bytes.Length)
                        throw new InvalidDataException("BC3 SVT payload length differs from the index.");
                    stream.Position = 0;
                    using (var sha = SHA256.Create()) report.payloadSha256 = Hex(sha.ComputeHash(stream));
                }
                Publish(directory, staging, indexStaging, producer, settings, entries, report, true);
            }
            finally
            {
                Object.DestroyImmediate(color);
                Object.DestroyImmediate(data);
                Object.DestroyImmediate(readback);
                if (File.Exists(staging)) File.Delete(staging);
                if (File.Exists(indexStaging)) File.Delete(indexStaging);
            }
        }

        static void ReadBc3Blocks(RenderTexture source, RenderTexture readback, byte[] bytes, int offset)
        {
            // Unity 2022.3 不接受 RGBA32 UInt 的异步回读。D3D11 的同位宽 Copy 保留原始 128 位，
            // 仅离线把这些位当作 RGBA32 Float 资源回读，期间不做浮点运算或颜色转换。
            Graphics.CopyTexture(source, readback);
            ReadChannel(readback, bytes, offset, MeshWorldVtBc3.ChannelBytes);
        }

        static void Publish(string directory, string staging, string indexStaging, MeshWorldVtProducer producer,
            Material settings, List<MeshWorldVtArchive.PageEntry> entries, BakeReport report, bool useBc3)
        {
            // 冻结后的覆盖作者数据若发生变化，不能发布一份混合不同内容版本的归档。
            if (producer.GetRecipeIdentity(settings) != report.recipe)
                throw new InvalidDataException("World SVT inputs changed while baking. Keep the previous archive and rebuild.");
            // 先发布内容寻址的不可变数据文件，再替换索引。
            string payload = Path.Combine(directory, MeshWorldVtArchive.PayloadName(report.payloadSha256, useBc3));
            if (File.Exists(payload))
            {
                using (var existing = File.OpenRead(payload))
                using (var sha = SHA256.Create())
                    if (Hex(sha.ComputeHash(existing)) != report.payloadSha256)
                        throw new InvalidDataException("An existing content-addressed SVT payload is corrupt: " + payload);
                File.Delete(staging);
            }
            else File.Move(staging, payload);

            var manifest = new MeshWorldVtArchive.Manifest
            {
                version = useBc3 ? MeshWorldVtArchive.Bc3FormatVersion : MeshWorldVtArchive.FormatVersion,
                interior = Interior, border = Border, firstMip = FirstMip, rootMip = LastMip,
                worldSize = MeshHeightLayout.WorldSize, mip0PagesPerAxis = MeshWorldVirtualTexture.PagesPerAxis(0),
                bytesPerPage = useBc3 ? MeshWorldVtBc3.PageBytes : PageBytes,
                albedoFormat = (useBc3 ? MeshWorldVtBc3.ColorFormat : MeshWorldVirtualTexture.AlbedoFormat).ToString(),
                dataFormat = (useBc3 ? MeshWorldVtBc3.DataFormat : MeshWorldVirtualTexture.DataFormat).ToString(),
                recipe = report.recipe, payloadSha256 = report.payloadSha256, pages = entries.ToArray()
            };
            File.WriteAllText(indexStaging, JsonUtility.ToJson(manifest, true));
            string index = Path.Combine(directory, MeshWorldVtArchive.ManifestName);
            if (File.Exists(index)) File.Replace(indexStaging, index, null);
            else File.Move(indexStaging, index);

            var archive = new MeshWorldVtArchive(directory, producer, settings, useBc3);
            archive.Read(MeshWorldVirtualTexture.EncodeKey(FirstMip, 0, 0));
            archive.Read(MeshWorldVirtualTexture.EncodeKey(LastMip, 0, 0));
        }

        static string Hex(byte[] hash) => BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

        static void ReadChannel(RenderTexture target, byte[] bytes, int offset, int length = ChannelBytes)
        {
            // Read back the RT's native bytes, including sRGB storage values for albedo RGB.
            // This offline bake waits before reusing the same render targets for the next page.
            var request = AsyncGPUReadback.Request(target, 0);
            request.WaitForCompletion();
            if (request.hasError || request.layerDataSize != length)
                throw new InvalidDataException("Could not read native World SVT page bytes from " + target.name);
            var native = request.GetData<byte>();
            Unity.Collections.NativeArray<byte>.Copy(native, 0, bytes, offset, length);
        }

        static void ValidatePage(byte[] bytes, uint key)
        {
            // The data texture's alpha reserves byte 0 for an invalid texel.
            // A baked SVT page must provide complete coverage, including its border.
            for (int i = ChannelBytes + 3; i < bytes.Length; i += BytesPerTexel)
                if (bytes[i] == 0)
                    throw new InvalidDataException($"SVT page {key} has an invalid texel at byte {i}.");
        }

        static void ReadPage(FileStream stream, Dictionary<uint, int> indexes, uint key, byte[] output)
        {
            stream.Position = (long)indexes[key] * PageBytes;
            int offset = 0;
            while (offset < output.Length)
            {
                int count = stream.Read(output, offset, output.Length - offset);
                if (count <= 0) throw new EndOfStreamException("SVT bake staging ended inside a page.");
                offset += count;
            }
        }

        static void VerifyRepresentativeBorders(FileStream stream, Dictionary<uint, int> indexes,
            int mip, int pages, BakeReport report)
        {
            if (pages < 2) return;
            var first = new byte[PageBytes];
            var second = new byte[PageBytes];
            for (int axis = 0; axis < 2; axis++)
            {
                int x = axis == 0 ? pages - 2 : pages - 1;
                int z = axis == 0 ? pages - 1 : pages - 2;
                ReadPage(stream, indexes, MeshWorldVirtualTexture.EncodeKey(mip, x, z), first);
                ReadPage(stream, indexes, MeshWorldVirtualTexture.EncodeKey(mip, x + (axis == 0 ? 1 : 0), z + (axis == 1 ? 1 : 0)), second);
                for (int along = 0; along < Side; along++)
                    for (int cross = 0; cross < 2 * Border; cross++)
                    {
                        int a = axis == 0 ? along * Side + Interior + cross : (Interior + cross) * Side + along;
                        int b = axis == 0 ? along * Side + cross : cross * Side + along;
                        for (int channel = 0; channel < 2; channel++)
                            for (int byteIndex = 0; byteIndex < BytesPerTexel; byteIndex++)
                                if (first[channel * ChannelBytes + a * BytesPerTexel + byteIndex] !=
                                    second[channel * ChannelBytes + b * BytesPerTexel + byteIndex])
                                    throw new InvalidDataException($"SVT shared border differs at mip {mip}, axis {axis}, texels {a}/{b}.");
                        report.seamComparisons++;
                    }
            }
        }

        // Mip 8 evaluates the source height, controls, projection and material.
        // Coarser pages filter the resulting material, including the world's fine
        // normal detail. A parent reads the same 2x2 child texels in its border as
        // its neighbor reads in the interior. Packed oct normals must be decoded
        // before averaging to avoid seams around octahedral folds.
        sealed class ParentFilter
        {
            readonly byte[][] children = new byte[16][];
            readonly float[] srgbToLinear = new float[256];

            public ParentFilter()
            {
                for (int i = 0; i < children.Length; i++) children[i] = new byte[PageBytes];
                for (int i = 0; i < srgbToLinear.Length; i++) srgbToLinear[i] = Mathf.GammaToLinearSpace(i / 255f);
            }

            public void Downsample(FileStream stream, Dictionary<uint, int> indexes, int mip, int x, int z, byte[] bytes)
            {
                int childMip = mip - 1;
                int last = MeshWorldVirtualTexture.PagesPerAxis(childMip) - 1;
                int globalX = (x * Interior - Border) * 2, globalZ = (z * Interior - Border) * 2;
                int minX = ChildPage(globalX, last), minZ = ChildPage(globalZ, last);
                int maxX = ChildPage(globalX + Side * 2 - 1, last), maxZ = ChildPage(globalZ + Side * 2 - 1, last);
                int columns = maxX - minX + 1;
                for (int iz = minZ; iz <= maxZ; iz++)
                    for (int ix = minX; ix <= maxX; ix++)
                    {
                        int index = (iz - minZ) * columns + ix - minX;
                        ReadPage(stream, indexes, MeshWorldVirtualTexture.EncodeKey(childMip, ix, iz), children[index]);
                    }

                for (int iz = 0; iz < Side; iz++)
                    for (int ix = 0; ix < Side; ix++)
                    {
                        Vector4 albedoAo = Vector4.zero;
                        Vector3 normal = Vector3.zero;
                        Vector2 roughnessHeight = Vector2.zero;
                        for (int dz = 0; dz < 2; dz++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int gx = globalX + ix * 2 + dx, gz = globalZ + iz * 2 + dz;
                                int px = ChildPage(gx, last), pz = ChildPage(gz, last);
                                int sx = Mathf.Clamp(gx - px * Interior + Border, 0, Side - 1);
                                int sz = Mathf.Clamp(gz - pz * Interior + Border, 0, Side - 1);
                                byte[] child = children[(pz - minZ) * columns + px - minX];
                                int src = (sz * Side + sx) * BytesPerTexel;
                                // Only RGB is sRGB. AO and the second texture store linear data.
                                albedoAo += new Vector4(srgbToLinear[child[src]], srgbToLinear[child[src + 1]],
                                    srgbToLinear[child[src + 2]], child[src + 3] / 255f);
                                src += ChannelBytes;
                                normal += DecodeNormal(child[src] / 255f, child[src + 1] / 255f);
                                // Children have passed ValidatePage, so height code 0 is absent.
                                roughnessHeight += new Vector2(child[src + 2] / 255f, (child[src + 3] - 1) / 254f);
                            }
                        Vector2 encoded = EncodeNormal(normal.sqrMagnitude > 1e-10f ? normal : Vector3.up);
                        int dst = (iz * Side + ix) * BytesPerTexel;
                        bytes[dst] = PackUnorm(Mathf.LinearToGammaSpace(albedoAo.x * .25f));
                        bytes[dst + 1] = PackUnorm(Mathf.LinearToGammaSpace(albedoAo.y * .25f));
                        bytes[dst + 2] = PackUnorm(Mathf.LinearToGammaSpace(albedoAo.z * .25f));
                        bytes[dst + 3] = PackUnorm(albedoAo.w * .25f);
                        dst += ChannelBytes;
                        bytes[dst] = PackUnorm(encoded.x);
                        bytes[dst + 1] = PackUnorm(encoded.y);
                        bytes[dst + 2] = PackUnorm(roughnessHeight.x * .25f);
                        bytes[dst + 3] = (byte)(1 + Mathf.RoundToInt(Mathf.Clamp01(roughnessHeight.y * .25f) * 254f));
                    }
            }

            static byte PackUnorm(float value) => (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255f);

            static int ChildPage(int globalTexel, int last) => Mathf.Clamp(globalTexel / Interior, 0, last);

            static Vector3 DecodeNormal(float r, float g)
            {
                float x = r * 2 - 1, z = g * 2 - 1;
                float y = 1 - Mathf.Abs(x) - Mathf.Abs(z);
                if (y < 0)
                {
                    float oldX = x;
                    x = (1 - Mathf.Abs(z)) * Sign(x);
                    z = (1 - Mathf.Abs(oldX)) * Sign(z);
                }
                return new Vector3(x, y, z).normalized;
            }

            static Vector2 EncodeNormal(Vector3 normal)
            {
                normal /= Mathf.Abs(normal.x) + Mathf.Abs(normal.y) + Mathf.Abs(normal.z);
                float x = normal.x, z = normal.z;
                if (normal.y < 0)
                {
                    x = (1 - Mathf.Abs(normal.z)) * Sign(normal.x);
                    z = (1 - Mathf.Abs(normal.x)) * Sign(normal.z);
                }
                return new Vector2(x * .5f + .5f, z * .5f + .5f);
            }

            static float Sign(float value) => value < 0 ? -1 : 1;
        }
    }
}
