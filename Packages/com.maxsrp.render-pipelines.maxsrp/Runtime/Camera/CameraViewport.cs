using UnityEngine;

namespace MaxSRP
{
    internal static class CameraViewport
    {
        internal static Rect GetViewport(Camera camera)
        {
            return camera.targetTexture != null
                ? new Rect(0, 0, camera.targetTexture.width, camera.targetTexture.height) : camera.pixelRect;
        }

        internal static Matrix4x4 GetProjection(Camera camera)
        {
            Matrix4x4 projection = camera.projectionMatrix;
            Rect viewport = GetViewport(camera);
            if (camera.cameraType == CameraType.Game && viewport.width > 0 && viewport.height > 0)
            {
                // Game rendering follows the real viewport ratio. Keep vertical FOV,
                // clip planes and lens shift; change horizontal field of view instead
                // of squeezing the image or imposing a fixed-aspect viewport.
                projection.m00 = projection.m11 * viewport.height / viewport.width;
            }
            return projection;
        }

        internal static Vector2Int GetRenderSize(Camera camera)
        {
            Rect viewport = GetViewport(camera);
            return new Vector2Int(Mathf.Max(1, Mathf.RoundToInt(viewport.width)),
                Mathf.Max(1, Mathf.RoundToInt(viewport.height)));
        }

    }
}
