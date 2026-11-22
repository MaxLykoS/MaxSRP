using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxTerrain
{
    // A terrain comparison shares one wind clock across its cameras. Preparing
    // another view in the same frame only reads the already prepared phase.
    internal sealed class GotGrassWind
    {
        static readonly Vector4 Noise = new Vector4(.0008f,.0008f,1.5f,2f);
        readonly GotGrassSettings settings;
        int frame = int.MinValue;
        double lastTime;
        Vector2 windOffset;
        Vector4 wind, direction, shape, sample;
        float phase, gustTime, influence;

        internal GotGrassWind(GotGrassSettings settings) => this.settings = settings;

        internal void Update()
        {
            if (frame == Time.frameCount) return;
            double now = Time.timeAsDouble;
            float elapsed = Application.isPlaying && frame != int.MinValue ? (float)Math.Max(0, now - lastTime) : 0;
            frame = Time.frameCount;
            lastTime = now;

            Vector2 heading = settings.WindDirection.sqrMagnitude > 1e-8f ? settings.WindDirection.normalized : Vector2.right;
            float strength = Mathf.Max(0,settings.WindStrength);
            float speed = Mathf.Max(0,settings.WindSpeed);
            float nativeSpeed = strength * 660;
            phase = Mathf.Repeat(phase + elapsed * nativeSpeed / 6600, 2 * Mathf.PI);
            gustTime = Mathf.Repeat(gustTime + elapsed * .2f, 1080);
            windOffset -= heading * (elapsed * nativeSpeed * speed * Noise.x);
            wind = new Vector4(settings.WindDirection.x,settings.WindDirection.y,strength,speed);
            direction = new Vector4(heading.x,heading.y,0,0);
            shape = new Vector4(3,2,.1f,nativeSpeed);
            sample = new Vector4(windOffset.x,windOffset.y,gustTime,nativeSpeed);
            influence = nativeSpeed > 0 ? 1 : 0;
        }

        internal void Bind(CommandBuffer commands, ComputeShader shader)
        {
            commands.SetComputeIntParam(shader,"_WindEnabled",(int)influence);
            commands.SetComputeVectorParam(shader,"_Wind",wind);
            commands.SetComputeFloatParam(shader,"_AnimationTime",phase);
            commands.SetComputeFloatParam(shader,"_MaxWindInfluence",influence);
            commands.SetComputeVectorParam(shader,"_GoTWind",shape);
            commands.SetComputeVectorParam(shader,"_GoTGlobalFacingBias",direction);
            commands.SetComputeVectorParam(shader,"_GoTWindCurrent",sample);
            commands.SetComputeVectorParam(shader,"_GoTWindNoise",Noise);
            commands.SetComputeFloatParam(shader,"_GoTWindThresholdBias",-.5f);
        }

        internal void Bind(MaterialPropertyBlock properties)
        {
            properties.SetInt("_WindEnabled",(int)influence);
            properties.SetVector("_Wind",wind);
            properties.SetFloat("_AnimationTime",phase);
            properties.SetFloat("_MaxWindInfluence",influence);
            properties.SetVector("_GoTWind",shape);
            properties.SetVector("_GoTGlobalFacingBias",direction);
            properties.SetVector("_GoTWindCurrent",sample);
            properties.SetVector("_GoTWindNoise",Noise);
            properties.SetFloat("_GoTWindThresholdBias",-.5f);
        }
    }

    // One owner per terrain view. The terrain prepares its height and control
    // windows before calling into foliage, and records foliage into its GBuffer.
    internal sealed class GotGrassRenderer : IDisposable
    {
        const int TypeCount = 49, BinCount = TypeCount * 2;
        const int PatchSize = 8, SeedsPerPatch = 82 * 82;
        const int GroupSize = 64, BatchSize = 512;
        const int DrawArgumentBytes = 5 * sizeof(uint);
        const float SeedSpacing = 25f / 256;
        // Conservative allowance for the recovered jitter, clump move and blade size.
        const float PatchPadding = 10;
        static readonly Vector4 Spawn = new Vector4(.75f,.02f,0,0);

        [Serializable] sealed class Catalog { public CatalogEntry[] Entries = null; }
        [Serializable] sealed class CatalogEntry { public int TypeId = 0; public float[] GpuFloat = null; }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct TypeRecord
        {
            // The moved catalog keeps the last 35 floats. The unused texture
            // references in the first 96 bytes still occupy their original offsets.
            public Vector4 A,B,C,D,E,F,G,H,I,J,K,L,M,N;
            public Vector3 O;

            static Vector4 Read(float[] values,int i) => new Vector4(values[i],values[i+1],values[i+2],values[i+3]);

            public TypeRecord(float[] values)
            {
                A=B=C=D=E=F=default;
                G=Read(values,0); H=Read(values,4); I=Read(values,8); J=Read(values,12);
                K=Read(values,16); L=Read(values,20); M=Read(values,24); N=Read(values,28);
                N.y=BitConverter.Int32BitsToSingle((int)values[29]);
                O=new Vector3(values[32],values[33],values[34]);
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct DrawTask
        {
            public int X,Z,End,Padding;
            public DrawTask(int x,int z,int end) { X=x; Z=z; End=end; Padding=0; }
        }

        struct Patch
        {
            public int X,Z;
            public float DistanceSquared;
        }

        readonly GotGrassSettings settings;
        readonly GotGrassWind wind;
        readonly GotGrassTypeMaterial[] bindings = new GotGrassTypeMaterial[TypeCount];
        readonly Plane[] planes = new Plane[6];
        readonly Vector4[] frustum = new Vector4[6];
        readonly DrawTask[] tasks = new DrawTask[BatchSize];
        readonly List<Patch> patches = new List<Patch>();
        readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        ComputeShader generation;
        Material material;
        ComputeBuffer types,taskBuffer,records,counters,typeCounts,typeOffsets,typeCursors,drawIndices,drawArguments,scatterArgs;
        GraphicsBuffer highIndices,lowIndices;
        int clearKernel,directKernel,deltaKernel,offsetsKernel,scatterKernel;
        int capacity;
        bool prepared,disposed;

        internal GotGrassRenderer(GotGrassSettings settings, GotGrassWind wind)
        {
            this.settings=settings;
            this.wind=wind;
        }

        internal GotGrassDisplacement Displacement { get; set; }

        void Initialize()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsShaderLevel < 50)
                throw new NotSupportedException("GotGrass mesh generation requires compute shaders and Shader Model 5.");
            if (!settings.GenerationShader || !settings.DrawShader || !settings.TypeCatalog)
                throw new InvalidOperationException("Assign the GotGrass generation shader, GBuffer shader and type catalog.");

            foreach (GotGrassTypeMaterial binding in settings.TypeMaterials ?? Array.Empty<GotGrassTypeMaterial>())
            {
                if (binding == null || binding.TypeId <= 0 || binding.TypeId >= TypeCount) continue;
                if (bindings[binding.TypeId] != null || !binding.ColorRamp || !binding.ColorDetail || !binding.ScalarRamp)
                    throw new InvalidOperationException("GotGrass material types require one complete binding per ID.");
                bindings[binding.TypeId]=binding;
            }
            var data = JsonUtility.FromJson<Catalog>(settings.TypeCatalog.text);
            var typeData = new TypeRecord[TypeCount];
            var loaded = new bool[TypeCount];
            foreach (CatalogEntry entry in data?.Entries ?? Array.Empty<CatalogEntry>())
            {
                if (entry == null || entry.TypeId <= 0 || entry.TypeId >= TypeCount || bindings[entry.TypeId] == null) continue;
                if (loaded[entry.TypeId] || entry.GpuFloat == null || entry.GpuFloat.Length != 35)
                    throw new InvalidOperationException("Invalid GotGrass type catalog entry: " + entry.TypeId);
                typeData[entry.TypeId]=new TypeRecord(entry.GpuFloat);
                loaded[entry.TypeId]=true;
            }
            for (int i=1;i<TypeCount;i++)
                if (bindings[i] != null && !loaded[i]) throw new InvalidOperationException("Missing GotGrass type record: " + i);

            generation=settings.GenerationShader;
            clearKernel=generation.FindKernel("ClearWorldDraw");
            directKernel=generation.FindKernel("GenerateGotTileDirect");
            deltaKernel=generation.FindKernel("GenerateGotTile");
            offsetsKernel=generation.FindKernel("WorldDrawOffsets");
            scatterKernel=generation.FindKernel("ScatterWorldDraw");
            capacity=Mathf.Clamp(settings.VisibleCapacity,65536,1048576);
            types=Buffer(TypeCount,236,"Types");
            types.SetData(typeData);
            taskBuffer=Buffer(BatchSize,16,"DrawTasks");
            records=Buffer(capacity,48,"DisplayRecords");
            counters=Buffer(2,4,"DisplayCounters");
            typeCounts=Buffer(BinCount,4,"TypeCounts");
            typeOffsets=Buffer(BinCount,4,"TypeOffsets");
            typeCursors=Buffer(BinCount,4,"TypeCursors");
            drawIndices=Buffer(capacity,4,"DrawIndices");
            drawArguments=Buffer(5*BinCount,4,"DrawArguments",ComputeBufferType.IndirectArguments);
            scatterArgs=Buffer(3,4,"ScatterArguments",ComputeBufferType.IndirectArguments);
            highIndices=CreateIndices(7);
            lowIndices=CreateIndices(3);
            material=new Material(settings.DrawShader) { name="GotGrass GBuffer", hideFlags=HideFlags.HideAndDontSave };
        }

        internal void Prepare(ScriptableRenderContext context, Camera camera, MeshHeightClipmap height,
            MeshCdlod geometry, float heightScale, MeshResidentControlMap resident, MeshControlMap clip)
        {
            prepared=false;
            if (disposed || !settings || !height.Texture) return;
            if (resident != null ? !resident.Texture : clip == null || !clip.Texture) return;
            if (!material)
            {
                try { Initialize(); }
                catch (Exception exception)
                {
                    Dispose();
                    Debug.LogException(exception,settings);
                    return;
                }
            }

            Vector3 observer=camera.transform.position;
            float distance=Mathf.Max(1,settings.MaxDistance);
            MaxTerrainGeometryPasses.CullingPlanes(camera,planes);
            if (!SelectPatches(geometry,observer,distance,out Vector2 morph)) return;
            for (int i=0;i<planes.Length;i++)
            {
                Vector3 normal=planes[i].normal;
                frustum[i]=new Vector4(normal.x,normal.y,normal.z,planes[i].distance);
            }

            wind.Update();
            var commands=new CommandBuffer { name="GotGrass Generate" };
            try
            {
                Displacement?.Record(commands);
                commands.BeginSample("GotGrass Generate");
                int kernel=resident != null ? directKernel : deltaKernel;
                BindGeneration(commands,kernel,observer,distance,morph,height,heightScale,resident,clip);
                commands.DispatchCompute(generation,clearKernel,(BinCount+GroupSize-1)/GroupSize,1,1);
                for (int start=0;start<patches.Count;start+=BatchSize)
                {
                    int count=Math.Min(BatchSize,patches.Count-start);
                    for (int i=0;i<count;i++)
                    {
                        Patch patch=patches[start+i];
                        tasks[i]=new DrawTask(patch.X,patch.Z,(i+1)*SeedsPerPatch);
                    }
                    commands.SetBufferData(taskBuffer,tasks,0,0,count);
                    commands.SetComputeIntParam(generation,"_WorldDrawTaskCount",count);
                    commands.DispatchCompute(generation,kernel,(count*SeedsPerPatch+GroupSize-1)/GroupSize,1,1);
                }
                commands.DispatchCompute(generation,offsetsKernel,1,1,1);
                commands.DispatchCompute(generation,scatterKernel,scatterArgs,0);
                commands.EndSample("GotGrass Generate");
                context.ExecuteCommandBuffer(commands);
                prepared=true;
            }
            finally { commands.Release(); }
        }

        bool SelectPatches(MeshCdlod geometry, Vector3 observer, float distance, out Vector2 morph)
        {
            morph=default;
            patches.Clear();
            foreach (MeshCdlod.Selection selection in geometry.Selections)
            {
                // Current root sampling matches the un-morphed 1m terrain triangles.
                if (selection.Lod != 0) continue;
                morph=new Vector2(selection.Parameters.x,selection.VerticalDistanceScale);
                Bounds node=selection.Bounds;
                int nodeX=Mathf.RoundToInt(selection.Patch.x)/PatchSize;
                int nodeZ=Mathf.RoundToInt(selection.Patch.y)/PatchSize;
                int side=MeshCdlod.GridQuads/PatchSize;
                for (int z=0;z<side;z++) for (int x=0;x<side;x++)
                {
                    int quarter=(x>=side/2?1:0)+(z>=side/2?2:0);
                    if ((selection.Mask & (1<<quarter)) == 0) continue;
                    int px=nodeX+x,pz=nodeZ+z;
                    Vector3 center=new Vector3((px+.5f)*PatchSize,node.center.y,(pz+.5f)*PatchSize);
                    var bounds=new Bounds(center,new Vector3(PatchSize+2*PatchPadding,node.size.y+2*PatchPadding,PatchSize+2*PatchPadding));
                    Vector3 nearest=bounds.ClosestPoint(new Vector3(observer.x,center.y,observer.z));
                    float distanceSquared=(new Vector2(observer.x-nearest.x,observer.z-nearest.z)).sqrMagnitude;
                    if (distanceSquared>distance*distance || !GeometryUtility.TestPlanesAABB(planes,bounds)) continue;
                    patches.Add(new Patch { X=px, Z=pz, DistanceSquared=distanceSquared });
                }
            }
            // When draw capacity is constrained, record the nearby work first.
            patches.Sort((a,b)=>a.DistanceSquared.CompareTo(b.DistanceSquared));
            return patches.Count>0;
        }

        void BindGeneration(CommandBuffer c, int kernel, Vector3 observer, float distance, Vector2 morph,
            MeshHeightClipmap height, float heightScale, MeshResidentControlMap resident, MeshControlMap clip)
        {
            var shader=generation;
            c.SetComputeIntParam(shader,"_GoTTypeCount",TypeCount);
            c.SetComputeIntParam(shader,"_Seed",settings.Seed);
            c.SetComputeVectorParam(shader,"_MaxLatticeOrigin",Vector4.zero);
            c.SetComputeFloatParam(shader,"_MaxSpacing",SeedSpacing);
            c.SetComputeVectorParam(shader,"_GoTSpawn",Spawn);
            c.SetComputeVectorParam(shader,"_Dimensions",new Vector4(1,0,.0256f,0));
            c.SetComputeVectorParam(shader,"_GoTGeneration",new Vector4(1,settings.ThinningStart,distance,0));
            c.SetComputeVectorArrayParam(shader,"_Frustum",frustum);
            c.SetComputeVectorParam(shader,"_CameraPosition",observer);
            c.SetComputeFloatParam(shader,"_MaxDistance",distance);
            c.SetComputeIntParam(shader,"_EnableCulling",1);
            c.SetComputeIntParam(shader,"_MaxUseDistanceThinning",settings.DistanceThinning?1:0);
            c.SetComputeIntParam(shader,"_WorldCandidateStride",1);
            c.SetComputeIntParam(shader,"_WorldDisplayCapacity",capacity);
            c.SetComputeIntParam(shader,"_WorldUseHighTopology",settings.HighTopology?1:0);
            c.SetComputeFloatParam(shader,"_WorldLowTopologyDistance",settings.LowTopologyDistance);
            c.SetComputeIntParam(shader,"_WorldHighIndexCount",highIndices.count);
            c.SetComputeIntParam(shader,"_WorldLowIndexCount",lowIndices.count);
            c.SetComputeTextureParam(shader,kernel,"_MeshHeightClip",height.Texture);
            c.SetComputeVectorArrayParam(shader,"_MeshClipOrigins",height.Origins);
            c.SetComputeFloatParam(shader,"_MeshHeightScale",heightScale);
            c.SetComputeFloatParam(shader,"_GotTerrainMorphStart",morph.x);
            c.SetComputeFloatParam(shader,"_GotTerrainVerticalScale",morph.y);
            c.SetComputeVectorParam(shader,"_GotTerrainObserver",observer);
            c.SetComputeFloatParam(shader,"_GotTerrainMinHeight",settings.MinimumHeight);
            c.SetComputeFloatParam(shader,"_GotTerrainMinNormalY",Mathf.Cos(Mathf.Clamp(settings.MaxSlope,0,90)*Mathf.Deg2Rad));
            c.SetComputeVectorParam(shader,"_GotTerrainGrassMaterials",new Vector4(settings.GrassMaterialA,settings.GrassMaterialB,settings.GrassTypeA,settings.GrassTypeB));
            c.SetComputeVectorParam(shader,"_GotTerrainGrassDensity",new Vector4(settings.DensityA,settings.DensityB,0,0));
            c.SetComputeVectorParam(shader,"_GotTerrainGrassHeight",new Vector4(settings.GrassHeightA,settings.GrassHeightB,0,0));
            if (resident != null)
            {
                c.SetComputeTextureParam(shader,kernel,"_DDControlResident",resident.Texture);
                c.SetComputeVectorParam(shader,"_DDControlInfo",new Vector4(MeshControlContract.WorldSize,resident.Texture.width,1,1));
            }
            else
            {
                c.SetComputeTextureParam(shader,kernel,"_DDControlClip",clip.Texture);
                c.SetComputeVectorArrayParam(shader,"_DDControlOrigins",clip.Origins);
                c.SetComputeVectorParam(shader,"_DDControlInfo",new Vector4(MeshControlContract.WorldSize,MeshControlContract.ClipSize,MeshControlContract.LevelCount,1));
            }

            c.SetComputeBufferParam(shader,clearKernel,"_WorldDisplayCounters",counters);
            c.SetComputeBufferParam(shader,clearKernel,"_WorldTypeCounts",typeCounts);
            c.SetComputeBufferParam(shader,clearKernel,"_WorldTypeCursors",typeCursors);
            c.SetComputeBufferParam(shader,kernel,"_GoTTypes",types);
            c.SetComputeBufferParam(shader,kernel,"_WorldDrawTasks",taskBuffer);
            c.SetComputeBufferParam(shader,kernel,"_WorldDisplayRecords",records);
            c.SetComputeBufferParam(shader,kernel,"_WorldDisplayCounters",counters);
            c.SetComputeBufferParam(shader,kernel,"_WorldTypeCounts",typeCounts);
            c.SetComputeBufferParam(shader,offsetsKernel,"_WorldDisplayCounters",counters);
            c.SetComputeBufferParam(shader,offsetsKernel,"_WorldTypeCounts",typeCounts);
            c.SetComputeBufferParam(shader,offsetsKernel,"_WorldTypeOffsets",typeOffsets);
            c.SetComputeBufferParam(shader,offsetsKernel,"_WorldDrawArguments",drawArguments);
            c.SetComputeBufferParam(shader,offsetsKernel,"_WorldScatterArgs",scatterArgs);
            c.SetComputeBufferParam(shader,scatterKernel,"_WorldDisplayCounters",counters);
            c.SetComputeBufferParam(shader,scatterKernel,"_WorldDisplayRecords",records);
            c.SetComputeBufferParam(shader,scatterKernel,"_WorldTypeOffsets",typeOffsets);
            c.SetComputeBufferParam(shader,scatterKernel,"_WorldTypeCursors",typeCursors);
            c.SetComputeBufferParam(shader,scatterKernel,"_WorldDrawIndices",drawIndices);
            wind.Bind(c,shader);
        }

        internal void RecordGBuffer(CommandBuffer commands, Camera camera)
        {
            if (!prepared) return;
            commands.BeginSample("GotGrass GBuffer");
            properties.Clear();
            properties.SetBuffer("_GoTTypes",types);
            properties.SetBuffer("_WorldDisplayRecords",records);
            properties.SetBuffer("_WorldDrawIndices",drawIndices);
            properties.SetBuffer("_WorldTypeOffsets",typeOffsets);
            properties.SetInt("_GoTTypeCount",TypeCount);
            properties.SetInt("_Seed",settings.Seed);
            properties.SetFloat("_MaxSpacing",SeedSpacing);
            properties.SetVector("_MaxLatticeOrigin",Vector4.zero);
            properties.SetVector("_GoTSpawn",Spawn);
            properties.SetVector("_CameraPosition",camera.transform.position);
            properties.SetFloat("_GoTWidthCompensation",0);
            properties.SetVector("_GoTLodMorph",new Vector4(25,40,.2f,1));
            wind.Bind(properties);
            if (Displacement != null) Displacement.Bind(properties);
            else GotGrassDisplacement.BindDisabled(properties);

            for (int id=1;id<TypeCount;id++)
            {
                var binding=bindings[id];
                if (binding == null) continue;
                properties.SetTexture("_GoTColorRamp",binding.ColorRamp);
                properties.SetTexture("_GoTColorDetail",binding.ColorDetail);
                properties.SetTexture("_GoTScalarRamp",binding.ScalarRamp);
                properties.SetFloat("_GoTColorDetailFlipY",binding.ColorDetailTopDown ? 1 : 0);
                if (settings.HighTopology) RecordBin(commands,id,highIndices,7);
                RecordBin(commands,id+TypeCount,lowIndices,3);
            }
            commands.EndSample("GotGrass GBuffer");
        }

        void RecordBin(CommandBuffer commands,int bin,GraphicsBuffer indices,int segments)
        {
            properties.SetInt("_WorldDrawBin",bin);
            properties.SetInt("_Segments",segments);
            commands.DrawProceduralIndirect(indices,Matrix4x4.identity,material,0,MeshTopology.Triangles,
                drawArguments,bin*DrawArgumentBytes,properties);
        }

        static ComputeBuffer Buffer(int count,int stride,string name,ComputeBufferType type=ComputeBufferType.Structured) =>
            new ComputeBuffer(count,stride,type) { name="GotGrass/"+name };

        static GraphicsBuffer CreateIndices(int segments)
        {
            var data=new ushort[(segments-1)*6+3];
            int at=0;
            for (int s=0;s<segments-1;s++)
            {
                int i=s*2;
                data[at++]=(ushort)i; data[at++]=(ushort)(i+1); data[at++]=(ushort)(i+2);
                data[at++]=(ushort)(i+1); data[at++]=(ushort)(i+3); data[at++]=(ushort)(i+2);
            }
            data[at++]=(ushort)((segments-1)*2);
            data[at++]=(ushort)((segments-1)*2+1);
            data[at]=(ushort)(segments*2);
            var result=new GraphicsBuffer(GraphicsBuffer.Target.Index,data.Length,sizeof(ushort));
            result.SetData(data);
            return result;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed=true;
            prepared=false;
            types?.Dispose(); taskBuffer?.Dispose(); records?.Dispose(); counters?.Dispose();
            typeCounts?.Dispose(); typeOffsets?.Dispose(); typeCursors?.Dispose();
            drawIndices?.Dispose(); drawArguments?.Dispose(); scatterArgs?.Dispose();
            highIndices?.Dispose(); lowIndices?.Dispose();
            MeshResidentHeight.Destroy(material);
            material=null;
        }
    }
}
