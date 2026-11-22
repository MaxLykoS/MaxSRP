#ifndef DIRECT_DELTA_HEIGHT_NORMALS_INCLUDED
#define DIRECT_DELTA_HEIGHT_NORMALS_INCLUDED

/* 源高度场法线：只影响着色/陡壁选面，不移动顶点，不接VT。
   旧版cross(ddy(world),ddx(world))返回当前三角面法线，会显露三角面和格线。
   这里仿照旧TerrainResearch的源高度差分，在片元位置采左/右/前/后高度。
   [工程选择] 使用已驻留且能覆盖差分足迹的最细高度层，与当前mesh LOD解耦。
   近处是1m源，远处受高度Clipmap覆盖限制，不能声称全世界一直是1m法线。
   双线性高度和中央差分可让法线跨原高度格线连续，不能增加原DEM实测细节，
   也不能改变轮廓、修复源数据中已有的台阶或消除材质/控制图产生的格纹。
*/

float MeshNormalLevelMargin(float2 worldXZ, int level)
{
    float stride = 1 << level;
    float4 origin = _MeshClipOrigins[level];
    // ±一个源格点的差分，每次硬件双线性还需右上邻点；世界边界用单边差分。
    float2 low = max(worldXZ - stride, 0);
    float2 high = min(worldXZ + stride, MESH_WORLD_SIZE);
    // 上界预留一个完整双线性邻点，且用连续世界位置算余量。
    // 这里不能floor到整数格，否则过渡权重也会每格跳一次，重新引入细条带。
    float2 room = min(low - origin.xy, origin.xy + (MESH_HEIGHT_CLIP_SIZE - 2) * stride - high);
    return origin.w > .5 && origin.z == stride ? min(room.x,room.y) / stride : -1;
}

float2 MeshSourceSlopeAtLevel(float2 worldXZ, int level, out float invalid)
{
    float stride = 1 << level;
    float2 low = max(worldXZ - stride, 0), high = min(worldXZ + stride, MESH_WORLD_SIZE);
    float il, ir, ib, it;
    float left = SampleHeight(float2(low.x,worldXZ.y),level,il);
    float right = SampleHeight(float2(high.x,worldXZ.y),level,ir);
    float bottom = SampleHeight(float2(worldXZ.x,low.y),level,ib);
    float top = SampleHeight(float2(worldXZ.x,high.y),level,it);
    invalid = max(max(il,ir),max(ib,it));
    // 分母用实际世界距离；边界被clamp后不能仍除以2*stride，否则边缘坡度会减半。
    return float2(right-left,top-bottom) / max(high-low,float2(1e-5,1e-5));
}

float3 MeshSourceHeightNormal(float2 worldXZ, out float invalid, out float sourceLevel)
{
    worldXZ = clamp(worldXZ,0,MESH_WORLD_SIZE);
    int selected = -1;
    float margin = -1;
    [unroll] for (int level=0;level<5;level++)
    {
        float candidate = MeshNormalLevelMargin(worldXZ,level);
        if (selected < 0 && candidate >= 0) { selected=level; margin=candidate; }
    }
    invalid = selected < 0 ? 1 : 0;
    sourceLevel = max(selected,0);
    if (selected < 0) return float3(0,1,0); // 调用者显示非法源诊断，不静默当作有效平地。
    float slopeInvalid;
    float2 slope = MeshSourceSlopeAtLevel(worldXZ,selected,slopeInvalid);
    invalid = max(invalid,slopeInvalid);
    // 32个源格点宽的过渡带是工程参数。先混坡度，再构造法线，避免窗口边界硬切。
    // 只查询描述符选层；内部4次高度采样，过渡带最多再采粗层4次。不是软件AF。
    if (selected < 4 && margin < 32)
    {
        int parent = selected + 1;
        if (MeshNormalLevelMargin(worldXZ,parent) < 0) invalid=1;
        else
        {
            float parentInvalid;
            float2 parentSlope = MeshSourceSlopeAtLevel(worldXZ,parent,parentInvalid);
            float fineWeight = smoothstep(0,32,margin);
            slope = lerp(parentSlope,slope,fineWeight);
            sourceLevel = lerp((float)parent,(float)selected,fineWeight);
            invalid = max(invalid,parentInvalid);
        }
    }
    // Unity Y-up；dx/dz是世界高度坡度。平坦高度严格返回(0,1,0)。
    return normalize(float3(-slope.x,1,-slope.y));
}
#endif
