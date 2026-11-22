#ifndef DIRECT_DELTA_SINGLE_PAGE_SURFACE_INCLUDED
#define DIRECT_DELTA_SINGLE_PAGE_SURFACE_INCLUDED

// 两条 Delta 路径都在片元入口、逐像素分支之前计算世界位置导数。
// 写页入口的单位是米/页纹素，屏幕回退入口的单位是米/屏幕像素。
// 只在材质 evaluator 内重定向 ddx/ddy，确保回退发生在缺页边界时仍可使用有效的导数。
static float3 DDRvtPositionDx;
static float3 DDRvtPositionDy;
float3 DDRvtMaterialDdx(float3 position) { return DDRvtPositionDx; }
float3 DDRvtMaterialDdy(float3 position) { return DDRvtPositionDy; }
#define ddx DDRvtMaterialDdx
#define ddy DDRvtMaterialDdy
#include "../../Delta/DeltaSurface.cginc"
#undef ddx
#undef ddy

#endif
