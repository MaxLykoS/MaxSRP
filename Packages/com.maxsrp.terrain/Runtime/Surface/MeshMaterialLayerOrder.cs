using System;

namespace MaxTerrain
{
    // [工程约定，不是PPT公开的具体材质排序]
    // 全局材质ID同时是数组slice和覆盖层序：小ID在Bottom，大ID在Top。
    // 岩石/砂砾 → 泥土/林地地表 → 枯草/草。所有草ID必须高于所有土壤ID。
    // 这是制作时的覆盖关系，不是按各张Height图的0..1数值、海拔或相机距离排序。
    // Height在运行时只修正既定Bottom/Top之间的局部混合，不决定它们的身份。
    public static class MeshMaterialLayerOrder
    {
        public const string Recipe = "surface16-rock-soil-grass-v1";
        public const string Name = "Rock / sediment / soil / grass";
        public const int FirstSoil = 8, LastSoil = 12, FirstGrass = 13, LastGrass = 15;

        // 原始素材目录Natural16采用下载编号（草排在前），不能将该编号当覆盖层序。
        // 只在离线材质重排/控制图制作时转换；运行时shader无需这张表。
        static readonly int[] SourceIds = { 8, 9, 10, 11, 12, 13, 14, 15, 4, 5, 6, 7, 3, 2, 0, 1 };

        public static int SourceId(int orderedId)
        {
            if (orderedId < 0 || orderedId >= SourceIds.Length) throw new ArgumentOutOfRangeException(nameof(orderedId));
            return SourceIds[orderedId];
        }

        public static int OrderedId(int sourceId)
        {
            for (int id = 0; id < SourceIds.Length; ++id) if (SourceIds[id] == sourceId) return id;
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        }

        public static bool IsSoil(int id) => id >= FirstSoil && id <= LastSoil;
        public static bool IsGrass(int id) => id >= FirstGrass && id <= LastGrass;
    }
}
