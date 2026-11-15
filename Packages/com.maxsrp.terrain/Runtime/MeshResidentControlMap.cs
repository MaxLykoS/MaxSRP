using System;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace MaxTerrain
{
    // DirectDelta reference: the complete repaired one-metre lattice, uploaded
    // once. Categorical IDs must never receive automatically averaged mips.
    public sealed class MeshResidentControlMap : IDisposable
    {
        public Texture2D Texture { get; private set; }
        public long GpuBytes => Texture ? (long)Texture.width * Texture.height * 2 : 0;
        public long BytesRead { get; private set; }
        public long UploadedBytes { get; private set; }

        public MeshResidentControlMap(string directory)
        {
            directory = Path.GetFullPath(directory ?? throw new ArgumentNullException(nameof(directory)));
            MeshControlMetadata metadata = MeshControlBake.ReadMetadata(directory);
            int width = metadata.width;
            if (SystemInfo.maxTextureSize < width || !SystemInfo.SupportsTextureFormat(TextureFormat.R16) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16_UNorm, FormatUsage.Sample))
                throw new NotSupportedException("DirectDelta's resident one-metre ID map requires a " + width + "x" + width + " R16 texture; this device supports at most " + SystemInfo.maxTextureSize + ".");
            try
            {
                MeshControlLevel entry = metadata.levels[0];
                byte[] bytes = File.ReadAllBytes(Path.Combine(directory, entry.fileName));
                BytesRead = bytes.LongLength;
                if (bytes.LongLength != entry.bytes || MeshControlBake.Hash(bytes) != entry.sha256)
                    throw new InvalidDataException("Resident control source extent or SHA mismatch.");
                if (!BitConverter.IsLittleEndian) MeshControlBake.SwapPairs(bytes);
                Texture = new Texture2D(width, width, TextureFormat.R16, false, true)
                {
                    name = "DirectDelta resident 1m IDs and opacity " + width + "x" + width + " R16",
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, anisoLevel = 1,
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (Texture.format != TextureFormat.R16 || Texture.graphicsFormat != GraphicsFormat.R16_UNorm || Texture.mipmapCount != 1)
                    throw new NotSupportedException("Resident control map requires native R16_UNorm without generated mips.");
                Texture.LoadRawTextureData(bytes);
                Texture.Apply(false, true); // Release Unity's CPU texture mirror.
                UploadedBytes = bytes.LongLength;
            }
            catch { Dispose(); throw; }
        }

        public void Bind(Material material)
        {
            if (!Texture) throw new ObjectDisposedException(nameof(MeshResidentControlMap));
            material.EnableKeyword("DD_CONTROL_RESIDENT");
            material.SetTexture("_DDControlResident", Texture);
            material.SetVector("_DDControlInfo", new Vector4(MeshControlContract.WorldSize, Texture.width, 1, 1));
        }

        public void Dispose()
        {
            MeshResidentHeight.Destroy(Texture);
            Texture = null;
        }
    }
}
