using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MaxTerrain.Editor
{
    // Offline source preparation only. It reads original CC0 image files and
    // metadata, with no reference to the legacy TerrainResearch assemblies.
    public static class MeshMaterialBuilder
    {
        public const string AssetPath = "Assets/Samples/MaxTerrain/Materials/Surface16.asset";
        const string SourceDirectory = "Assets/TerrainResearch/Materials/Natural16";
        [Serializable] sealed class SourceMap { public string channel, path; }
        [Serializable] sealed class SourceMaterial { public int id; public string slug, name; public float tileMetres; public SourceMap[] maps; }
        [Serializable] sealed class SourceManifest { public int sourceResolution; public string licenseUrl; public SourceMaterial[] materials; }

        public static MeshMaterialSet Build(string sourceProjectRoot, string outputAssetPath = AssetPath, int resolution = 1024)
        {
            sourceProjectRoot = Path.GetFullPath(sourceProjectRoot);
            if (!outputAssetPath.StartsWith("Assets/",StringComparison.Ordinal) || !outputAssetPath.EndsWith(".asset",StringComparison.Ordinal))
                throw new ArgumentException("The new material set must be an asset under Assets.");
            if (resolution != 512 && resolution != 1024) throw new ArgumentOutOfRangeException(nameof(resolution));
            string manifestPath = Path.Combine(sourceProjectRoot,SourceDirectory,"source-manifest.json");
            string json = File.ReadAllText(manifestPath);
            var source = JsonUtility.FromJson<SourceManifest>(json);
            if (source == null || source.materials == null || source.materials.Length != MeshMaterialSet.LegacyCount || source.sourceResolution < resolution)
                throw new InvalidDataException("Exactly sixteen original Natural16 materials at the requested resolution are required.");
            var ordered = new SourceMaterial[MeshMaterialSet.LegacyCount];
            var identity = new StringBuilder("DirectDeltaSurface16-v1|sRGB-AH|linear-NRAO|BC7-ASTC6x6|"+resolution+"|");
            identity.Append(MeshMaterialResident.Sha256(Encoding.UTF8.GetBytes(json)));
            foreach (var entry in source.materials)
            {
                if (entry.id < 0 || entry.id >= ordered.Length || ordered[entry.id] != null) throw new InvalidDataException("Material IDs must be the unique fixed range 0..15.");
                ordered[entry.id] = entry;
                foreach (string channel in new[] { "albedo", "normal", "roughness", "ao", "height" })
                {
                    string path = SourcePath(sourceProjectRoot,entry,channel);
                    identity.Append('|').Append(entry.id).Append(':').Append(channel).Append(':').Append(MeshMaterialResident.Sha256(File.ReadAllBytes(path)));
                }
                string layer = LayerPath(sourceProjectRoot,entry);
                identity.Append('|').Append(File.ReadAllText(layer));
            }
            string fingerprint = MeshMaterialResident.Sha256(Encoding.UTF8.GetBytes(identity.ToString()));
            var old = AssetDatabase.LoadMainAssetAtPath(outputAssetPath);
            if (old)
            {
                var existing = old as MeshMaterialSet;
                if (!existing || existing.sourceFingerprint != fingerprint || existing.resolution != resolution)
                    throw new IOException("Preserve existing material assets before rebuilding changed sources: "+outputAssetPath);
                existing.Validate(); ValidatePayloads(existing); return existing;
            }
            // Check both offline encoders BEFORE processing the sixteen sources.
            CheckEncoder(TextureFormat.BC7);
            CheckEncoder(TextureFormat.ASTC_6x6);
            var set = ScriptableObject.CreateInstance<MeshMaterialSet>(); set.resolution = resolution; set.sourceFingerprint = fingerprint;
            string directory = Path.Combine(Application.streamingAssetsPath,set.relativeDirectory);
            if (Directory.Exists(directory) && Directory.GetFiles(directory).Length != 0)
            { Object.DestroyImmediate(set); throw new IOException("Preserve existing compressed material payloads before rebuilding: "+directory); }
            Directory.CreateDirectory(directory); Directory.CreateDirectory(Path.GetDirectoryName(outputAssetPath));
            var ahBC7 = new MemoryStream(); var nraoBC7 = new MemoryStream();
            var ahASTC = new MemoryStream(); var nraoASTC = new MemoryStream();
            bool saved = false;
            try
            {
                for (int id = 0; id < ordered.Length; ++id)
                {
                    var entry = ordered[id];
                    if (entry.tileMetres <= 0) throw new InvalidDataException("Positive material tiling required.");
                    set.names[id] = entry.name;
                    set.uvPerMetre[id] = new Vector4(1/entry.tileMetres,1/entry.tileMetres,0,0);
                    set.parameters[id] = new Vector4(NormalStrength(LayerPath(sourceProjectRoot,entry)),resolution,set.MipCount-1,1);
                    Color32[] a = ReadImage(SourcePath(sourceProjectRoot,entry,"albedo"),resolution,true);
                    Color32[] n = ReadImage(SourcePath(sourceProjectRoot,entry,"normal"),resolution,false);
                    Color32[] r = ReadImage(SourcePath(sourceProjectRoot,entry,"roughness"),resolution,false);
                    Color32[] o = ReadImage(SourcePath(sourceProjectRoot,entry,"ao"),resolution,false);
                    Color32[] h = ReadImage(SourcePath(sourceProjectRoot,entry,"height"),resolution,false);
                    var ah = new Color32[a.Length]; var nrao = new Color32[a.Length];
                    for (int p = 0; p < a.Length; ++p)
                    { ah[p] = new Color32(a[p].r,a[p].g,a[p].b,h[p].r); nrao[p] = new Color32(n[p].r,n[p].g,r[p].r,o[p].r); }
                    var ahMips = Mips(ah,resolution,true); var nraoMips = Mips(nrao,resolution,false);
                    Compress(ahMips,resolution,true,TextureFormat.BC7,ahBC7);
                    Compress(nraoMips,resolution,false,TextureFormat.BC7,nraoBC7);
                    Compress(ahMips,resolution,true,TextureFormat.ASTC_6x6,ahASTC);
                    Compress(nraoMips,resolution,false,TextureFormat.ASTC_6x6,nraoASTC);
                    Debug.Log("Packed compressed Surface16 material "+id+": "+entry.name);
                }
                set.variants[0] = WriteVariant(directory,TextureFormat.BC7,ahBC7.ToArray(),nraoBC7.ToArray(),resolution);
                set.variants[1] = WriteVariant(directory,TextureFormat.ASTC_6x6,ahASTC.ToArray(),nraoASTC.ToArray(),resolution);
                set.Validate(); ValidatePayloads(set);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.CreateAsset(set,outputAssetPath); saved = true; EditorUtility.SetDirty(set); AssetDatabase.SaveAssets();
                return set;
            }
            finally
            {
                ahBC7.Dispose(); nraoBC7.Dispose(); ahASTC.Dispose(); nraoASTC.Dispose();
                if (!saved) Object.DestroyImmediate(set);
            }
        }

        static string LayerPath(string root, SourceMaterial material)
        { return Path.Combine(root,SourceDirectory,material.id.ToString("00")+"-"+material.slug+".terrainlayer"); }
        static string SourcePath(string root, SourceMaterial material, string channel)
        {
            foreach (var map in material.maps) if (map.channel == channel)
            {
                MeshMaterialSet.SafeRelative(map.path); return Path.Combine(root,map.path);
            }
            throw new InvalidDataException("Missing "+channel+" source for material "+material.id);
        }
        static float NormalStrength(string layer)
        {
            string value = Regex.Match(File.ReadAllText(layer),@"m_NormalScale:\s*([-+.0-9eE]+)").Groups[1].Value;
            return float.Parse(value,CultureInfo.InvariantCulture);
        }
        internal static Color32[] ReadImage(string path,int size,bool srgb)
        {
            // Read original encoded channel values, never Unity normal-importer
            // DXT5nm swizzles or an already platform-compressed source texture.
            var texture = new Texture2D(2,2,TextureFormat.RGBA32,false,true);
            try
            {
                if (!ImageConversion.LoadImage(texture,File.ReadAllBytes(path),false)) throw new InvalidDataException("Could not decode original image: "+path);
                var pixels = texture.GetPixels32(); int width=texture.width,height=texture.height;
                if (width == size && height == size) return pixels;
                if (width != height || width < size || (width & (width-1)) != 0) throw new InvalidDataException("Source images must be square powers of two at least as large as the material source.");
                // Power-of-two source reduction. Color RGB is filtered in linear
                // light; height/normal/roughness/AO remain ordinary linear data.
                while (width > size) { pixels = Reduce(pixels,width,srgb,false); width >>= 1; }
                return pixels;
            }
            finally { Object.DestroyImmediate(texture); }
        }
        internal static List<Color32[]> Mips(Color32[] first,int size,bool colorHeight)
        {
            var result = new List<Color32[]> { first };
            while (size > 1) { first=Reduce(first,size,colorHeight,!colorHeight); size >>= 1; result.Add(first); }
            return result;
        }
        static Color32[] Reduce(Color32[] source,int size,bool srgb,bool normalRg)
        {
            int half=size/2; var target=new Color32[half*half];
            for(int z=0;z<half;z++) for(int x=0;x<half;x++)
            {
                Color a=source[(z*2)*size+x*2],b=source[(z*2)*size+x*2+1],c=source[(z*2+1)*size+x*2],d=source[(z*2+1)*size+x*2+1];
                Color value=(a+b+c+d)*.25f;
                if(srgb)
                {
                    value.r=GammaAverage(a.r,b.r,c.r,d.r); value.g=GammaAverage(a.g,b.g,c.g,d.g); value.b=GammaAverage(a.b,b.b,c.b,d.b);
                }
                else if(normalRg)
                {
                    Vector3 normal=(DecodeNormal(a)+DecodeNormal(b)+DecodeNormal(c)+DecodeNormal(d)).normalized;
                    value.r=normal.x*.5f+.5f; value.g=normal.y*.5f+.5f;
                }
                target[z*half+x]=value;
            }
            return target;
        }
        static Vector3 DecodeNormal(Color value)
        { float x=value.r*2-1,y=value.g*2-1; return new Vector3(x,y,Mathf.Sqrt(Mathf.Max(0,1-x*x-y*y))); }
        static float GammaAverage(float a,float b,float c,float d)
        { return Mathf.LinearToGammaSpace((Mathf.GammaToLinearSpace(a)+Mathf.GammaToLinearSpace(b)+Mathf.GammaToLinearSpace(c)+Mathf.GammaToLinearSpace(d))*.25f); }

        internal static void Compress(List<Color32[]> chain,int size,bool srgb,TextureFormat format,Stream output,
            TextureCompressionQuality quality=TextureCompressionQuality.Fast)
        {
            var texture=new Texture2D(size,size,TextureFormat.RGBA32,true,!srgb);
            try
            {
                for(int mip=0;mip<chain.Count;mip++) texture.SetPixels32(chain[mip],mip);
                texture.Apply(false,false);
                // Offline compression only. ASTC blocks are read from CPU and
                // stored as files; no ASTC GPU array is created on D3D11.
                EditorUtility.CompressTexture(texture,format,quality);
                if(texture.format!=format || texture.mipmapCount!=chain.Count) throw new InvalidOperationException("The offline compressor did not retain the requested block format/mips: "+format);
                for(int mip=0;mip<chain.Count;mip++)
                {
                    var blocks=texture.GetPixelData<byte>(mip);
                    int expected=MeshMaterialSet.MipBytes(Math.Max(1,size>>mip),format);
                    if(blocks.Length!=expected) throw new InvalidOperationException("The compressor returned decompressed or incorrectly sized data for "+format+" mip "+mip+": "+blocks.Length+" != "+expected);
                    byte[] bytes=blocks.ToArray(); output.Write(bytes,0,bytes.Length);
                }
            }
            finally{Object.DestroyImmediate(texture);}
        }
        static MeshMaterialSet.Variant WriteVariant(string directory,TextureFormat format,byte[] ah,byte[] nrao,int size)
        {
            long expected=MeshMaterialSet.ArrayBytes(size,format);
            if(ah.LongLength!=expected || nrao.LongLength!=expected) throw new InvalidDataException("Each encoded array must contain all sixteen complete source mip chains.");
            string suffix=format==TextureFormat.BC7?"bc7":"astc6x6";
            var result=new MeshMaterialSet.Variant {format=format,albedoHeightFile="albedo-height."+suffix+".bin",normalRoughAoFile="normal-rough-ao."+suffix+".bin",arrayBytes=expected,
                albedoHeightSha256=MeshMaterialResident.Sha256(ah),normalRoughAoSha256=MeshMaterialResident.Sha256(nrao)};
            File.WriteAllBytes(Path.Combine(directory,result.albedoHeightFile),ah); File.WriteAllBytes(Path.Combine(directory,result.normalRoughAoFile),nrao); return result;
        }
        public static void ValidatePayloads(MeshMaterialSet set)
        {
            string directory=Path.Combine(Application.streamingAssetsPath,set.relativeDirectory);
            foreach(var variant in set.variants)
            {
                foreach(var pair in new[]{new[]{variant.albedoHeightFile,variant.albedoHeightSha256},new[]{variant.normalRoughAoFile,variant.normalRoughAoSha256}})
                { byte[] bytes=File.ReadAllBytes(Path.Combine(directory,pair[0])); if(bytes.LongLength!=variant.arrayBytes || MeshMaterialResident.Sha256(bytes)!=pair[1]) throw new InvalidDataException("Compressed source payload identity mismatch: "+pair[0]); }
            }
        }
        static void CheckEncoder(TextureFormat format)
        {
            var pixels = new Color32[64];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32((byte)(i * 3), (byte)(i * 7), (byte)(255 - i * 2), (byte)(i * 4));
            Compress(Mips(pixels, 8, true), 8, true, format, Stream.Null);
        }
    }
}
