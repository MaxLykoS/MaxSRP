using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MaxTerrain.Editor
{
    /// <summary>Explicit Editor GPU check for packed BC3 storage and wide page-table entries.</summary>
    public static class MaxTerrainVtCapacityValidation
    {
        const string ProbeAsset = "Assets/Editor/MaxSRP/MaxTerrainVtCapacityProbe.compute";
        const string ReportPath = "D:/CodexTemp/MaxTerrainVtCapacity_20261118/gpu-validation.txt";
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        public static string Run()
        {
            if (File.Exists(ProbeAsset)) throw new InvalidOperationException("The temporary capacity probe path is already in use.");
            var owned = new List<Object>();
            ComputeBuffer tableBuffer = null, results = null, blockWords = null;
            var log = new StringBuilder();
            try
            {
                int slots = MeshWorldVirtualTexture.SlotsForBudget(360);
                Require(slots == 8672 && MeshWorldVirtualTexture.SlotsForBudget(64) == 1536, "Platform budget rounding");
                Require((long)slots * MeshWorldVtBc3.PageBytes == 359645184, "PC payload accounting");
                Require(1536L * MeshWorldVtBc3.PageBytes == 63700992, "Mobile payload accounting");
                Directory.CreateDirectory(Path.GetDirectoryName(ProbeAsset));
                File.WriteAllText(ProbeAsset, ProbeSource(), new UTF8Encoding(false));
                AssetDatabase.ImportAsset(ProbeAsset, ImportAssetOptions.ForceSynchronousImport);
                var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ProbeAsset);
                Require(shader && shader.HasKernel("Validate"), "Probe shader compiled");
                int kernel = shader.FindKernel("Validate");
                int uploadKernel = shader.FindKernel("UploadBlocks");
                int side = MeshWorldVirtualTexture.PageSide;
                var color = Pool(MeshWorldVtBc3.ColorFormat, MeshWorldVirtualTexture.PoolSide, slots / 16, owned);
                var data = Pool(MeshWorldVtBc3.DataFormat, MeshWorldVirtualTexture.PoolSide, slots / 16, owned);
                var stagingColor = Pool(MeshWorldVtBc3.ColorFormat, side, 4, owned);
                var stagingData = Pool(MeshWorldVtBc3.DataFormat, side, 4, owned);
                var referenceColor = Pool(MeshWorldVtBc3.ColorFormat, side, 1, owned);
                var referenceData = Pool(MeshWorldVtBc3.DataFormat, side, 1, owned);
                var uploadColor = new Texture2D(side, side, MeshWorldVtBc3.ColorFormat, TextureCreationFlags.None);
                var uploadData = new Texture2D(side, side, MeshWorldVtBc3.DataFormat, TextureCreationFlags.None);
                owned.Add(uploadColor); owned.Add(uploadData);
                var blockColor = Blocks(owned); var blockData = Blocks(owned);
                blockWords = new ComputeBuffer(MeshWorldVtBc3.ChannelBytes / sizeof(uint), sizeof(uint));

                // Exercise the production copy method without needing an SVT archive
                // or a running camera. No cache scheduling or scene state is changed.
                var owner = (MeshWorldVirtualTexture)FormatterServices.GetUninitializedObject(typeof(MeshWorldVirtualTexture));
                typeof(MeshWorldVirtualTexture).GetField("<AlbedoAo>k__BackingField", Private).SetValue(owner, color);
                typeof(MeshWorldVirtualTexture).GetField("<NormalRoughnessHeight>k__BackingField", Private).SetValue(owner, data);
                MethodInfo copy = typeof(MeshWorldVirtualTexture).GetMethod("CopyIntoPool", Private);
                MethodInfo nativeRecord = typeof(MeshWorldVtBc3).Assembly.GetType("MaxTerrain.Bc3NativeCopy")
                    .GetMethod("Record", BindingFlags.Static | BindingFlags.Public);
                var resident = new Dictionary<uint, int>();
                var table = new MeshAdaptiveVtTable(resident, slots);
                tableBuffer = new ComputeBuffer(MeshAdaptiveVtTable.WordCount, sizeof(uint));
                results = new ComputeBuffer(3, 4 * sizeof(float));
                shader.SetTexture(kernel, "_DDVtAlbedoAo", color);
                shader.SetTexture(kernel, "_DDVtNormalRoughnessHeight", data);
                shader.SetTexture(kernel, "_ReferenceColor", referenceColor);
                shader.SetTexture(kernel, "_ReferenceData", referenceData);
                shader.SetBuffer(kernel, "_DDVtTable", tableBuffer);
                shader.SetBuffer(kernel, "_Results", results);
                shader.SetInt("_DDVtSlotCount", slots);
                shader.SetFloat("_DDVtReady", 1);
                int[] testedSlots = { 0, 3, 4, 15, 16, 1535, 2047, 2048, 8191, 8671 };
                Vector2[] positions = { Vector2.zero, new Vector2(.003f, .997f), new Vector2(.5f, .5f), Vector2.one };
                int[] mips = { 0, 4, 7, 8, 15 };
                int checks = 0;
                var readback = new Vector4[3];
                uint key = MeshWorldVirtualTexture.EncodeKey(8, 0, 0);
                foreach (int targetSlot in testedSlots)
                {
                    byte[] colorBytes = Pattern(targetSlot, false), dataBytes = Pattern(targetSlot, true);
                    uploadColor.LoadRawTextureData(colorBytes); uploadColor.Apply(false, false);
                    uploadData.LoadRawTextureData(dataBytes); uploadData.Apply(false, false);
                    Graphics.CopyTexture(uploadColor, 0, 0, referenceColor, 0, 0);
                    Graphics.CopyTexture(uploadData, 0, 0, referenceData, 0, 0);
                    UploadBlocks(shader, uploadKernel, blockWords, blockColor, colorBytes);
                    UploadBlocks(shader, uploadKernel, blockWords, blockData, dataBytes);
                    resident[key] = targetSlot;
                    table.PageAdded(key, targetSlot);
                    tableBuffer.SetData(table.Words);
                    shader.SetInt("_TestSlot", targetSlot);
                    for (int sourceKind = 0; sourceKind < 2; sourceKind++)
                    {
                        if (sourceKind == 0)
                            copy.Invoke(owner, new object[] { uploadColor, uploadData, 0, targetSlot });
                        else
                        {
                            using (var command = new CommandBuffer())
                            {
                                nativeRecord.Invoke(null, new object[] { command, blockColor.GetNativeTexturePtr(), blockData.GetNativeTexturePtr(),
                                    stagingColor.GetNativeTexturePtr(), stagingData.GetNativeTexturePtr(), targetSlot % 4 });
                                Graphics.ExecuteCommandBuffer(command);
                            }
                            copy.Invoke(owner, new object[] { stagingColor, stagingData, targetSlot % 4, targetSlot });
                        }
                        foreach (int mip in mips)
                        foreach (Vector2 inside in positions)
                        for (int path = 0; path < 5; path++)
                        {
                            if (path == 4 && (mip != 8 || inside == Vector2.one)) continue;
                            shader.SetInt("_TestMip", mip);
                            shader.SetInt("_TestPath", path);
                            shader.SetVector("_TestInside", inside);
                            shader.SetFloat("_DDVtMaxAnisotropy", path == 2 ? 2 : 4);
                            shader.Dispatch(kernel, 1, 1, 1);
                            results.GetData(readback);
                            for (int i = 0; i < readback.Length; i++)
                            for (int c = 0; c < 4; c++)
                                Require(!float.IsNaN(readback[i][c]) && Mathf.Abs(readback[i][c]) < 1e-6f,
                                    "GPU mismatch slot=" + targetSlot + " source=" + sourceKind + " mip=" + mip + " path=" + path + " uv=" + inside + " result=" + readback[i]);
                            checks++;
                        }
                    }
                    resident.Remove(key);
                    table.PageRemoved(key, targetSlot);
                    foreach (uint word in table.Words) Require(word == 0, "Removed page left a stale table entry");
                }
                log.AppendLine("PASS").AppendLine("GPU: " + SystemInfo.graphicsDeviceName)
                    .AppendLine("PC360MB: 8672 pages / 542 array slices / 359645184 bytes")
                    .AppendLine("Mobile64MB: 1536 pages / 96 array slices / 63700992 bytes")
                    .AppendLine("Packed sampling vs original per-page storage: " + checks + " GPU comparisons, max error <1e-6")
                    .AppendLine("Paths: SVT upload; native RVT compressed staging; AF1; unnormalized; AF2; AF4; actual page-table decode")
                    .AppendLine("Slots include cell/slice boundaries, >2047 and final PC slot8671; mips0,4,7,8,15; border and center samples")
                    .AppendLine("Page removal clears all stale mappings");
            }
            catch (Exception e) { log.AppendLine("FAIL").AppendLine(e.ToString()); throw; }
            finally
            {
                results?.Release(); tableBuffer?.Release(); blockWords?.Release();
                foreach (Object item in owned) if (item) Object.DestroyImmediate(item);
                AssetDatabase.DeleteAsset(ProbeAsset);
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
                File.WriteAllText(ReportPath, log.ToString());
            }
            return log.ToString();
        }

        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        static Texture2DArray Pool(GraphicsFormat format, int side, int depth, List<Object> owned)
        {
            var result = new Texture2DArray(side, side, depth, format, TextureCreationFlags.None);
            owned.Add(result); result.Apply(false, true); return result;
        }

        static RenderTexture Blocks(List<Object> owned)
        {
            var d = new RenderTextureDescriptor(MeshWorldVtBc3.BlockSide, MeshWorldVtBc3.BlockSide, MeshWorldVtBc3.BlockFormat, 0)
            { msaaSamples = 1, mipCount = 1, enableRandomWrite = true };
            var result = new RenderTexture(d); owned.Add(result);
            Require(result.Create(), "Cannot create BC3 block staging texture"); return result;
        }

        static byte[] Pattern(int seed, bool data)
        {
            var bytes = new byte[MeshWorldVtBc3.ChannelBytes];
            int axis = MeshWorldVtBc3.BlockSide;
            for (int y = 0; y < axis; y++) for (int x = 0; x < axis; x++)
            {
                int at = (y * axis + x) * 16;
                bytes[at] = bytes[at + 1] = (byte)(32 + (seed + x * 5 + y * 7 + (data ? 29 : 0)) % 192);
                ushort rgb = (ushort)((((x + seed) % 32) << 11) | (((y * 3 + seed) % 64) << 5) | ((x + y + seed) % 32));
                bytes[at + 8] = bytes[at + 10] = (byte)rgb;
                bytes[at + 9] = bytes[at + 11] = (byte)(rgb >> 8);
            }
            return bytes;
        }

        static void UploadBlocks(ComputeShader shader, int kernel, ComputeBuffer buffer, RenderTexture target, byte[] bytes)
        {
            var words = new uint[bytes.Length / sizeof(uint)];
            Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
            buffer.SetData(words);
            shader.SetBuffer(kernel, "_UploadWords", buffer);
            shader.SetTexture(kernel, "_UploadTarget", target);
            shader.Dispatch(kernel, 5, 5, 1);
        }

        static string ProbeSource()
        {
            string source = File.ReadAllText("Packages/com.maxsrp.terrain/Shaders/VirtualTexturing/World/WorldVtConsumer.cginc");
            source = source.Substring(0, source.IndexOf("// 在同一GBuffer", StringComparison.Ordinal)) + "\n#endif\n";
            source = source.Replace("\"WorldVtAddress.cginc\"", "\"Packages/com.maxsrp.terrain/Shaders/VirtualTexturing/World/WorldVtAddress.cginc\"")
                .Replace("\"WorldVtMainFeedback.cginc\"", "\"Packages/com.maxsrp.terrain/Shaders/VirtualTexturing/World/WorldVtMainFeedback.cginc\"");
            int start = source.IndexOf("float3 DDVtDecodePageNormal", StringComparison.Ordinal);
            string reference = source.Substring(start, source.IndexOf("bool DDVtReadSurface", StringComparison.Ordinal) - start);
            reference = System.Text.RegularExpressions.Regex.Replace(reference,
                @"\b(DDVtDecodePageNormal|DDVtSamplePageUnnormalized|DDVtSampleAnisotropicPage|DDVtSamplePage)\b", "Reference$1");
            reference = reference.Replace("_DDVtAlbedoAo", "_ReferenceColor").Replace("_DDVtNormalRoughnessHeight", "_ReferenceData")
                .Replace("DDVtPhysicalAddress", "ReferenceAddress");
            return @"#pragma kernel Validate
#pragma kernel UploadBlocks
#define DD_WORLD_VT_BC3 1
#define DD_WORLD_VT_ANISOTROPIC 1
#include ""Packages/com.maxsrp.terrain/Shaders/VirtualTexturing/SinglePage/SinglePageRvtCommon.cginc""
struct DDSurface { float3 albedo; float3 normal; float roughness; float ao; float height; };
static float3 DDRvtPositionDx, DDRvtPositionDy;
" + source + @"
Texture2DArray<float4> _ReferenceColor, _ReferenceData;
int4 ReferenceAddress(int2 p, uint slot) { return int4(p,0,0); }
" + reference + @"
uint _TestSlot, _TestMip, _TestPath;
float2 _TestInside;
RWStructuredBuffer<float4> _Results;
StructuredBuffer<uint> _UploadWords;
RWTexture2D<uint4> _UploadTarget;
[numthreads(8,8,1)]
void UploadBlocks(uint3 id: SV_DispatchThreadID)
{
    if (any(id.xy >= 36u)) return;
    uint at=(id.y*36u+id.x)*4u;
    _UploadTarget[id.xy]=uint4(_UploadWords[at],_UploadWords[at+1],_UploadWords[at+2],_UploadWords[at+3]);
}
[numthreads(1,1,1)]
void Validate(uint3 id: SV_DispatchThreadID)
{
    DDVtResidentPage page = (DDVtResidentPage)0;
    page.slot = _TestSlot; page.mip = _TestMip;
    float width = DDVtPageWidthMetres(page.mip);
    float2 world = _TestInside * width;
    DDRvtPositionDx = float3(width/128.0*4.0,0,0);
    DDRvtPositionDy = float3(0,0,width/128.0);
    DDSurface a, b;
    bool okA, okB;
    if (_TestPath == 0) { okA=DDVtSamplePage(world,0,page,a); okB=ReferenceDDVtSamplePage(world,0,page,b); }
    else if (_TestPath == 1) { okA=DDVtSamplePageUnnormalized(world,0,page,a); okB=ReferenceDDVtSamplePageUnnormalized(world,0,page,b); }
    else if (_TestPath == 4) { DDVtResidentPage fetched; okA=DDVtReadSurface(world,page.mip,fetched,a); okB=ReferenceDDVtSampleAnisotropicPage(world,0,page,b); }
    else { okA=DDVtSampleAnisotropicPage(world,0,page,a); okB=ReferenceDDVtSampleAnisotropicPage(world,0,page,b); }
    _Results[0]=float4(a.albedo-b.albedo,a.roughness-b.roughness);
    _Results[1]=float4(a.normal-b.normal,a.ao-b.ao);
    _Results[2]=float4(a.height-b.height,(okA && okB)?0:1,0,0);
}
";
        }
    }
}
