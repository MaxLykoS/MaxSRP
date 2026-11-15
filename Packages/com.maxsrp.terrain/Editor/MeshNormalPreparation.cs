using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MaxTerrain.Editor
{
    // 仅离线制作：Gravel原始法线平均偏斜约33度，Hex旋转会把它变成蜂窝受光差。
    // 为整张法线施加同一个正交旋转，保留局部法线间夹角；不是在片元里额外校正。
    // 不声称旋转等同于拟合可周期高度场。原素材/原压缩数组保留为回归对照。
    public static class MeshNormalPreparation
    {
        public const string AssetPath = "Assets/Samples/MaxTerrain/Materials/Surface16HexCorrected.asset";
        public const string Recipe = "gravel-rgb-mean-rotation-native-png-vector-mips-best-v2";
        public const string IdentityFile = "normal-upright-identity.json";
        public const int MaterialId = 3;
        const string SourceFolder = "Assets/TerrainResearch/Materials/Natural16/Sources/gravel_ground_01";

        [Serializable] public sealed class LayerRecord
        {
            public string format, oldSha256, newSha256;
            public int id, bytes;
            public bool byteIdentical;
        }
        [Serializable] public sealed class Identity
        {
            public string recipe = Recipe, inputFingerprint, outputFingerprint, normalSha256, roughnessSha256, aoSha256;
            public string scope = "Offline ID3 normal-only preparation. AH payloads and other 15 NRAO layers are byte-identical. ASTC is encoded, not mobile GPU validated.";
            public int bitDepth, resolution, negativeZCount;
            public Vector3 meanBefore, meanAfter;
            public Quaternion rotation;
            public float tiltBeforeDegrees, tiltAfterDegrees, minimumCorrectedZ, maximumPairDotError;
            public List<LayerRecord> layers = new List<LayerRecord>();
        }

        public static MeshMaterialSet Build(string sourceRoot, MeshMaterialSet source)
        {
            if (!source) throw new ArgumentNullException(nameof(source));
            source.Validate(); MeshMaterialBuilder.ValidatePayloads(source);
            if (source.layerOrder != MeshMaterialLayerOrder.Recipe || source.names[MaterialId] != "Gravel Ground 01")
                throw new InvalidDataException("Gravel correction requires the unchanged ordered Surface16 IDs.");
            string normalPath = Path.Combine(sourceRoot, SourceFolder, "normal.png");
            string roughPath = Path.Combine(sourceRoot, SourceFolder, "roughness.jpg");
            string aoPath = Path.Combine(sourceRoot, SourceFolder, "ao.jpg");
            var identity = new Identity { inputFingerprint = source.sourceFingerprint, resolution = source.resolution,
                normalSha256 = HashFile(normalPath), roughnessSha256 = HashFile(roughPath), aoSha256 = HashFile(aoPath) };
            string fingerprint = MeshMaterialResident.Sha256(Encoding.UTF8.GetBytes(Recipe + "|" + source.sourceFingerprint + "|" +
                identity.normalSha256 + "|" + identity.roughnessSha256 + "|" + identity.aoSha256));
            identity.outputFingerprint = fingerprint;
            var existing = AssetDatabase.LoadAssetAtPath<MeshMaterialSet>(AssetPath);
            if (existing)
            {
                if (existing.sourceFingerprint != fingerprint || existing.layerOrder != source.layerOrder)
                    throw new IOException("Preserve the existing corrected material set before changing its recipe or input.");
                existing.Validate(); MeshMaterialBuilder.ValidatePayloads(existing); return existing;
            }
            string directory = Path.Combine(Application.streamingAssetsPath, source.relativeDirectory);
            foreach (string suffix in new[] { "bc7", "astc6x6" })
                if (File.Exists(Path.Combine(directory, "normal-rough-ao-upright-best." + suffix + ".bin")))
                    throw new IOException("Preserve partial/previous upright normal payloads before rebuilding.");

            int side, depth;
            Vector3[] normals = ReadPngNormals(File.ReadAllBytes(normalPath), out side, out depth);
            if (side != source.resolution) throw new InvalidDataException("Normal preparation requires the original source resolution.");
            identity.bitDepth = depth;
            RotateMeanToZ(normals, identity);
            Color32[] rough = MeshMaterialBuilder.ReadImage(roughPath, side, false);
            Color32[] ao = MeshMaterialBuilder.ReadImage(aoPath, side, false);
            var packed = new Color32[rough.Length];
            for (int i = 0; i < packed.Length; i++) packed[i] = new Color32(128,128,rough[i].r,ao[i].r);
            // 保留旧制作中BA的标量过滤/量化配方，Normal独立在浮点向量域逐级过滤。
            List<Color32[]> chain = MeshMaterialBuilder.Mips(packed, side, false);
            Vector3[] level = normals;
            for (int mip = 0, size = side; mip < chain.Count; mip++, size >>= 1)
            {
                for (int i = 0; i < level.Length; i++)
                {
                    Color c = chain[mip][i]; c.r = level[i].x * .5f + .5f; c.g = level[i].y * .5f + .5f;
                    chain[mip][i] = c;
                }
                if (size > 1)
                {
                    int half = size / 2; var next = new Vector3[half*half];
                    for (int y=0;y<half;y++) for(int x=0;x<half;x++)
                    { int p=(y*2)*size+x*2; next[y*half+x]=(level[p]+level[p+1]+level[p+size]+level[p+size+1]).normalized; }
                    level = next;
                }
            }
            var output = Object.Instantiate(source); output.name = "Surface16HexCorrected"; output.sourceFingerprint = fingerprint;
            bool saved = false;
            try
            {
                output.variants = new MeshMaterialSet.Variant[source.variants.Length];
                for (int v=0;v<source.variants.Length;v++)
                {
                    var original = source.variants[v]; byte[] old = File.ReadAllBytes(Path.Combine(directory,original.normalRoughAoFile));
                    byte[] bytes = (byte[])old.Clone();
                    int layerBytes = checked((int)(original.arrayBytes / MeshMaterialSet.Capacity));
                    byte[] replacement;
                    using (var stream = new MemoryStream())
                    { MeshMaterialBuilder.Compress(chain,side,false,original.format,stream,TextureCompressionQuality.Best); replacement=stream.ToArray(); }
                    if (replacement.Length != layerBytes) throw new InvalidDataException("Corrected mip chain changed the compressed layer layout.");
                    Buffer.BlockCopy(replacement,0,bytes,MaterialId*layerBytes,layerBytes);
                    for (int id=0;id<MeshMaterialSet.Capacity;id++)
                    {
                        string before = HashRange(old,id*layerBytes,layerBytes), after = HashRange(bytes,id*layerBytes,layerBytes);
                        if ((id==MaterialId) == (before==after)) throw new InvalidDataException("Unexpected modified material layer: "+id);
                        identity.layers.Add(new LayerRecord { id=id,format=original.format.ToString(),bytes=layerBytes,
                            oldSha256=before,newSha256=after,byteIdentical=before==after });
                    }
                    string suffix = original.format==TextureFormat.BC7 ? "bc7" : "astc6x6";
                    string file = "normal-rough-ao-upright-best."+suffix+".bin";
                    File.WriteAllBytes(Path.Combine(directory,file),bytes);
                    output.variants[v] = new MeshMaterialSet.Variant { format=original.format,arrayBytes=original.arrayBytes,
                        albedoHeightFile=original.albedoHeightFile,albedoHeightSha256=original.albedoHeightSha256,
                        normalRoughAoFile=file,normalRoughAoSha256=MeshMaterialResident.Sha256(bytes) };
                }
                output.Validate(); MeshMaterialBuilder.ValidatePayloads(output);
                File.WriteAllText(Path.Combine(directory,IdentityFile),JsonUtility.ToJson(identity,true),new UTF8Encoding(false));
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.CreateAsset(output,AssetPath); saved=true; AssetDatabase.SaveAssets(); return output;
            }
            finally { if (!saved) Object.DestroyImmediate(output); }
        }

        static void RotateMeanToZ(Vector3[] normals, Identity identity)
        {
            identity.meanBefore = Mean(normals); identity.tiltBeforeDegrees = Vector3.Angle(identity.meanBefore,Vector3.forward);
            identity.rotation = Quaternion.FromToRotation(identity.meanBefore.normalized,Vector3.forward);
            identity.minimumCorrectedZ = 1;
            for (int i=0;i<normals.Length;i++)
            {
                Vector3 old=normals[i], corrected=(identity.rotation*old).normalized;
                if(i>0 && i%997==0)
                {
                    Vector3 another=normals[i+1<normals.Length?i+1:i];
                    identity.maximumPairDotError=Mathf.Max(identity.maximumPairDotError,
                        Mathf.Abs(Vector3.Dot(old,another)-Vector3.Dot(corrected,identity.rotation*another)));
                }
                identity.minimumCorrectedZ=Mathf.Min(identity.minimumCorrectedZ,corrected.z);
                if(corrected.z<=0)identity.negativeZCount++;
                normals[i]=corrected;
            }
            if(identity.negativeZCount>0 || identity.minimumCorrectedZ<.05f)
                throw new InvalidDataException("Mean rotation cannot safely use the positive-Z RG normal representation.");
            identity.meanAfter=Mean(normals); identity.tiltAfterDegrees=Vector3.Angle(identity.meanAfter,Vector3.forward);
            if(identity.tiltAfterDegrees>.05f || identity.maximumPairDotError>2e-5f)
                throw new InvalidDataException("The offline normal rotation did not preserve its intended orientation/detail.");
        }
        static Vector3 Mean(Vector3[] data)
        { double x=0,y=0,z=0;foreach(var n in data){x+=n.x;y+=n.y;z+=n.z;}return new Vector3((float)(x/data.Length),(float)(y/data.Length),(float)(z/data.Length)); }

        // PNG原始8/16-bit RGB(A)样本，不执行gAMA/sRGB变换，不经TextureImporter或Color32解码。
        // 当前Gravel源本身为8-bit RGB；16-bit源也保留原精度。显式拒绝不支持的布局。
        internal static Vector3[] ReadPngNormals(byte[] png, out int side, out int depth)
        {
            byte[] signature={137,80,78,71,13,10,26,10};
            if(png.Length<33)throw new InvalidDataException("Truncated normal PNG.");
            for(int i=0;i<8;i++)if(png[i]!=signature[i])throw new InvalidDataException("Expected PNG normal source.");
            int width=0,height=0,channels=0;depth=0;
            byte[] compressed;
            using(var idat=new MemoryStream())
            {
                for(int p=8;p<png.Length;)
                {
                    if(p+12>png.Length)throw new InvalidDataException("Truncated PNG chunk.");
                    int count=checked((int)Be32(png,p));string type=Encoding.ASCII.GetString(png,p+4,4);p+=8;
                    if(count<0 || count>png.Length-p-4)throw new InvalidDataException("Invalid PNG chunk size.");
                    if(type=="IHDR")
                    {
                        if(count!=13)throw new InvalidDataException("Invalid PNG header.");
                        width=checked((int)Be32(png,p));height=checked((int)Be32(png,p+4));depth=png[p+8];int color=png[p+9];
                        channels=color==2?3:color==6?4:0;
                        if(width!=height || width<4 || width>8192 || (width&(width-1))!=0 || channels==0 ||
                            (depth!=8 && depth!=16) || png[p+10]!=0 || png[p+11]!=0 || png[p+12]!=0)
                            throw new InvalidDataException("Normal PNG must be square power-of-two, non-interlaced RGB(A) 8/16 bit.");
                    }
                    if(type=="IDAT")idat.Write(png,p,count);
                    p+=count+4;if(type=="IEND")break;
                }
                compressed=idat.ToArray();
            }
            if(width==0 || compressed.Length<6 || (compressed[0]&15)!=8 || (compressed[1]&32)!=0 ||
                ((compressed[0]<<8)+compressed[1])%31!=0)throw new InvalidDataException("Invalid PNG zlib stream.");
            int bpp=channels*(depth/8),stride=checked(width*bpp),expected=checked((stride+1)*height);
            var filtered=new byte[expected];
            using(var data=new MemoryStream(compressed,2,compressed.Length-6))using(var deflate=new DeflateStream(data,CompressionMode.Decompress))
            { int p=0,n;while(p<expected && (n=deflate.Read(filtered,p,expected-p))>0)p+=n;if(p!=expected || deflate.ReadByte()!=-1)throw new InvalidDataException("PNG decoded length mismatch."); }
            var row=new byte[stride];var previous=new byte[stride];var result=new Vector3[width*height];
            for(int y=0;y<height;y++)
            {
                int p=y*(stride+1),filter=filtered[p++];if(filter>4)throw new InvalidDataException("Unknown PNG row filter.");
                for(int x=0;x<stride;x++)
                {
                    int a=x>=bpp?row[x-bpp]:0,b=previous[x],c=x>=bpp?previous[x-bpp]:0;
                    int predict=filter==0?0:filter==1?a:filter==2?b:filter==3?(a+b)/2:Paeth(a,b,c);
                    row[x]=unchecked((byte)(filtered[p+x]+predict));
                }
                for(int x=0;x<width;x++)
                {
                    int start=x*bpp,step=depth/8;
                    float r=Channel(row,start,depth),g=Channel(row,start+step,depth),b=Channel(row,start+2*step,depth);
                    Vector3 n=new Vector3(r*2-1,g*2-1,b*2-1);if(n.sqrMagnitude<1e-8f)throw new InvalidDataException("Zero-length source normal.");
                    // PNG行自上而下；Unity GetPixels/SetPixels顺序自下而上。只翻行，不翻法线G。
                    result[(height-1-y)*width+x]=n.normalized;
                }
                var swap=previous;previous=row;row=swap;
            }
            side=width;return result;
        }
        static float Channel(byte[] bytes,int p,int depth)=>depth==8?bytes[p]/255f:((bytes[p]<<8)|bytes[p+1])/65535f;
        static uint Be32(byte[] data,int p)=>(uint)data[p]<<24|(uint)data[p+1]<<16|(uint)data[p+2]<<8|data[p+3];
        static int Paeth(int a,int b,int c)
        {int p=a+b-c,pa=Math.Abs(p-a),pb=Math.Abs(p-b),pc=Math.Abs(p-c);return pa<=pb && pa<=pc?a:pb<=pc?b:c;}
        static string HashFile(string path)=>MeshMaterialResident.Sha256(File.ReadAllBytes(path));
        static string HashRange(byte[] data,int offset,int count)
        {using(var sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(data,offset,count)).Replace("-", "").ToLowerInvariant();}
    }
}
