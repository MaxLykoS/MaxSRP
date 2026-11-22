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
        public long GpuBytes => Texture ? (long)Texture.width * Texture.height * 4 : 0;
        public long BytesRead { get; private set; }
        public long UploadedBytes { get; private set; }

        public MeshResidentControlMap(string directory)
        {
            directory = Path.GetFullPath(directory ?? throw new ArgumentNullException(nameof(directory)));
            MeshControlMetadata metadata = MeshControlBake.ReadMetadata(directory);
            int width = metadata.width;
            if (SystemInfo.maxTextureSize < width || !SystemInfo.SupportsTextureFormat(TextureFormat.RGBA32) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8A8_UNorm, FormatUsage.Sample))
                throw new NotSupportedException("DirectDelta's resident one-metre ID map requires a " + width + "x" + width + " RGBA8 texture; this device supports at most " + SystemInfo.maxTextureSize + ".");
            try
            {
                MeshControlLevel entry = metadata.levels[0];
                byte[] bytes = File.ReadAllBytes(Path.Combine(directory, entry.fileName));
                BytesRead = bytes.LongLength;
                if (bytes.LongLength != entry.bytes || MeshControlBake.Hash(bytes) != entry.sha256)
                    throw new InvalidDataException("Resident control source extent or SHA mismatch.");
                // Legacy source files retain their SHA and expand only at the GPU boundary.
                var packed = new uint[width*width];
                for (int i = 0; i < packed.Length; ++i)
                    packed[i] = MeshControlContract.ReadStored(bytes,i*metadata.BytesPerPoint,metadata.BytesPerPoint);
                MeshControlContract.Validate(packed,width,width);
                Texture = new Texture2D(width, width, TextureFormat.RGBA32, false, true)
                {
                    name = "DirectDelta resident 1m IDs and opacity " + width + "x" + width + " RGBA8",
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, anisoLevel = 1,
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (Texture.format != TextureFormat.RGBA32 || Texture.graphicsFormat != GraphicsFormat.R8G8B8A8_UNorm || Texture.mipmapCount != 1)
                    throw new NotSupportedException("Resident control map requires native RGBA8_UNorm without generated mips.");
                Texture.SetPixelData(packed,0);
                Texture.Apply(false, true); // Release Unity's CPU texture mirror.
                UploadedBytes = packed.LongLength*4;
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
