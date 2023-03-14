using System.Collections;
using UnityEngine;

namespace MaxSRP
{
    public class MaxProbeBase : MonoBehaviour
    {
        public bool bUpdate = false;

        [SerializeField]
        public Cubemap GroundTruthCubemap;
        [SerializeField]
        protected Cubemap capturedCubemap;

        protected const string destFolder = "Assets/Scenes/ProbeCubemaps";

        public void RenderCubeMap()
        {
            capturedCubemap = new Cubemap(64, TextureFormat.RGBA32, false);

            /*Camera staticCam = new Camera
            {
                cameraType = CameraType.Reflection,
                cullingMask = 2
            };*/
            GameObject go = new GameObject("CubemapCamera");
            go.AddComponent<Camera>();
            go.transform.position = this.transform.position;
            go.transform.rotation = Quaternion.identity;
            go.GetComponent<Camera>().RenderToCubemap(capturedCubemap);

            DestroyImmediate(go);
        }
        public void SaveCubeMap()
        {
            if (capturedCubemap == null)
            {
                Debug.LogError("先渲染Cubemap再保存！");
                return;
            }
            SaveCubemapFace(CubemapFace.NegativeX, "right", "jpg", destFolder);
            SaveCubemapFace(CubemapFace.PositiveX, "left", "jpg", destFolder);
            SaveCubemapFace(CubemapFace.PositiveZ, "front", "jpg", destFolder);
            SaveCubemapFace(CubemapFace.NegativeZ, "back", "jpg", destFolder);
            SaveCubemapFace(CubemapFace.PositiveY, "top", "jpg", destFolder);
            SaveCubemapFace(CubemapFace.NegativeY, "bottom", "jpg", destFolder);
        }
        void SaveCubemapFace(CubemapFace face, string prefix, string type, string destFolder)
        {
            int width = capturedCubemap.width;
            int height = capturedCubemap.height;
            Texture2D tex = new Texture2D(width, height, capturedCubemap.format, 0, false);

            // we need to flip our textures on Y before saving
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    tex.SetPixel(x, height - 1 - y, capturedCubemap.GetPixel(face, x, y));
                }
            }

            tex.Apply();
            byte[] data = null;
            if (type == "png")
            {
                data = tex.EncodeToPNG();
            }
            else if (type == "jpg")
            {
                data = tex.EncodeToJPG(100);
            }

            if (data != null)
            {
                System.IO.File.WriteAllBytes(destFolder + "/" + prefix + "." + type, data);
            }
            else
            {
                Debug.LogError("[SaveCubemapFace] - failed to encode tex2d");
            }
        }

        public virtual bool ProbeUpdate()
        {
            return bUpdate;
        }

        public virtual void ProbeInit()
        { 
            
        }

        public virtual void Clear()
        {
            Destroy(capturedCubemap);
        }
    }
}