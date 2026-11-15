#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MaxSRP
{
    /// <summary>Checks the production footprint builder without loading or changing a scene.</summary>
    public static class MaxShadowCasterCullingValidation
    {
        [Serializable]
        private sealed class Report
        {
            public bool success;
            public int volumes, supportedPoints, fallbackChecks, receiverCornerChecks;
            public string unityVersion;
        }

        [MenuItem("MaxSRP/Validation/Shadow Caster XY Geometry")]
        public static void Run() => Debug.Log(RunGeometryChecks());

        public static string RunGeometryChecks()
        {
            var report = new Report { unityVersion = Application.unityVersion };
            var volume = new MaxShadowCasterCullingVolume();
            var corners = new Vector3[8];
            var random = new System.Random(170419);
            for (int shape = 0; shape < 6; ++shape)
            for (int orientation = 0; orientation < 24; ++orientation)
            {
                Quaternion cameraRotation = Quaternion.Euler(orientation * 11.3f, orientation * 29.7f, orientation * 3.1f);
                Vector3 cameraPosition = new Vector3(orientation * 137.1f, -31.7f, orientation * -83.9f);
                Matrix4x4 cameraToWorld = Matrix4x4.TRS(cameraPosition, cameraRotation, Vector3.one);
                Quaternion lightRotation = Quaternion.Euler(orientation * 7.3f, orientation * 37.1f, orientation * 5.9f);
                Vector3 handedness = shape == 4 ? new Vector3(-1, 1, -1) :
                    shape == 5 ? new Vector3(1, -1, -1) : new Vector3(1, 1, -1);
                Matrix4x4 worldToLight = Matrix4x4.Scale(handedness) *
                    Matrix4x4.TRS(cameraPosition, lightRotation, Vector3.one).inverse;
                float padding = shape == 3 ? 2.5f : 0.17f;
                for (int i = 0; i < 8; ++i)
                {
                    float depth = i < 4 ? 5.0f : 97.0f;
                    float size = shape == 1 ? 12 : depth * 0.63f;
                    float x = (i % 4 < 2 ? -1 : 1) * size * 1.7f;
                    float y = (i % 4 == 0 || i % 4 == 3 ? -1 : 1) * size;
                    // Off-axis perspective, orthographic, and an additional wide split.
                    if (shape == 2) { x += depth * 0.47f; y -= depth * 0.21f; }
                    if (shape == 3) x *= 3;
                    corners[i] = cameraToWorld.MultiplyPoint3x4(new Vector3(x, y, -depth));
                }
                ScriptableCullingParameters parameters = Box(worldToLight, 1000);
                Matrix4x4 originalMatrix = parameters.cullingMatrix;
                Plane near = parameters.GetCullingPlane(4), far = parameters.GetCullingPlane(5);
                Require(volume.TryApply(ref parameters, corners, worldToLight, padding), "valid receiver footprint rejected");
                Require(parameters.cullingMatrix == originalMatrix, "raster culling matrix changed");
                Require(parameters.GetCullingPlane(parameters.cullingPlaneCount - 2).Equals(near) &&
                    parameters.GetCullingPlane(parameters.cullingPlaneCount - 1).Equals(far), "Z caps changed");
                ++report.volumes;

                // Each receiver vertex plus every sampled filter direction must
                // survive anywhere along the original light-depth range. Convex
                // combinations also exercise interior points and translated views.
                for (int sample = 0; sample < 160; ++sample)
                {
                    Vector3 receiver;
                    if (sample < 8) receiver = corners[sample];
                    else
                    {
                        receiver = Vector3.zero;
                        float sum = 0;
                        for (int i = 0; i < 8; ++i)
                        {
                            float weight = (float)random.NextDouble();
                            receiver += corners[i] * weight;
                            sum += weight;
                        }
                        receiver /= sum;
                    }
                    Vector3 point = worldToLight.MultiplyPoint3x4(receiver);
                    for (int direction = 0; direction < 16; ++direction)
                    {
                        float angle = direction * Mathf.PI / 8;
                        Vector3 supported = new Vector3(point.x + Mathf.Cos(angle) * padding,
                            point.y + Mathf.Sin(angle) * padding, sample % 2 == 0 ? -999 : 999);
                        Vector3 world = worldToLight.inverse.MultiplyPoint3x4(supported);
                        Require(Contains(parameters, world, 0.02f), "required receiver/filter/extrusion point culled");
                        ++report.supportedPoints;
                    }
                }
            }

            // A small receiver proves that we tighten the original box's XY
            // sides, while retaining tangent AABBs and light-depth exclusion.
            for (int i = 0; i < 8; ++i)
            {
                Vector2 vertex = i % 4 == 0 ? new Vector2(-3, 0) : i % 4 == 1 ? new Vector2(0, -3) :
                    i % 4 == 2 ? new Vector2(3, 0) : new Vector2(0, 3);
                corners[i] = new Vector3(vertex.x, vertex.y, i < 4 ? -10 : 10);
            }
            ScriptableCullingParameters diamond = Box(Matrix4x4.identity, 10);
            Require(volume.TryApply(ref diamond, corners, Matrix4x4.identity, 0.25f), "diamond rejected");
            Require(!Contains(diamond, new Vector3(9, 9, 0), 0), "old-box corner not removed");
            Require(!Contains(diamond, new Vector3(0, 0, 11), 0), "original far cap lost");
            Require(Intersects(diamond, new Bounds(new Vector3(3.5f, 0, 0), Vector3.one)), "intersecting bounds rejected");
            ++report.volumes;

            CheckFallback(volume, corners, Matrix4x4.identity, float.NaN, ref report);
            Matrix4x4 scaled = Matrix4x4.Scale(new Vector3(2, 1, 1));
            CheckFallback(volume, corners, scaled, 0.1f, ref report);
            CheckFallback(volume, corners, Matrix4x4.identity, 0.1f, ref report, true);
            corners[0] = new Vector3(float.NaN, 0, 0);
            CheckFallback(volume, corners, Matrix4x4.identity, 0.1f, ref report);
            CheckFallback(volume, new Vector3[7], Matrix4x4.identity, 0.1f, ref report);
            CheckProductionReceiverCorners(ref report);
            report.success = true;
            return JsonUtility.ToJson(report, true);
        }

        private static void CheckProductionReceiverCorners(ref Report report)
        {
            // Preview-scene ownership keeps these cameras out of the user's scene.
            // Closing the preview retires its GameObjects outside a render callback.
            Scene scene = EditorSceneManager.NewPreviewScene();
            RenderTexture target = null;
            string[] scalarNames = { "_OrthoDistance", "_Split0", "_Split1", "_Split2", "_Split3",
                "_ShadingPointNormalBias0", "_ShadingPointNormalBias1", "_ShadingPointNormalBias2", "_ShadingPointNormalBias3",
                "_DepthNormalBias0", "_DepthNormalBias1", "_DepthNormalBias2", "_DepthNormalBias3",
                "_PcssMaxRadius0", "_PcssMaxRadius1", "_PcssMaxRadius2", "_PcssMaxRadius3", "_ShadowNoiseWorldTexelSize" };
            string[] vectorNames = { "_CascadeDepthRange", "_CascadeBlendWidths", "_ShadowDistanceFade", "_ShadowNoiseTextureSize" };
            var scalars = new float[scalarNames.Length];
            var vectors = new Vector4[vectorNames.Length];
            for (int i = 0; i < scalarNames.Length; ++i) scalars[i] = Shader.GetGlobalFloat(scalarNames[i]);
            for (int i = 0; i < vectorNames.Length; ++i) vectors[i] = Shader.GetGlobalVector(vectorNames[i]);
            Texture noise = Shader.GetGlobalTexture("_NoiseTexture");
            Matrix4x4 noiseMatrix = Shader.GetGlobalMatrix("_ShadowNoiseWorldToLight");
            int blockerSamples = Shader.GetGlobalInteger("_PcssBlockerSamples"), filterSamples = Shader.GetGlobalInteger("_PcssFilterSamples");
            try
            {
                var cameraObject = new GameObject("Receiver corner validation") { hideFlags = HideFlags.HideAndDontSave };
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                target = new RenderTexture(640, 360, 0) { hideFlags = HideFlags.HideAndDontSave };
                camera.targetTexture = target;
                var shadowObject = new GameObject("Receiver raster isolation validation") { hideFlags = HideFlags.HideAndDontSave };
                SceneManager.MoveGameObjectToScene(shadowObject, scene);
                Camera shadow = shadowObject.AddComponent<Camera>();
                shadow.enabled = false;
                shadow.orthographic = true;
                var corners = new Vector3[8];
                var splitEnds = new float[4];
                var settings = new CascadeSettings { ShadowDistance = 500 };
                var cascade = new Cascade(settings);
                for (int shape = 0; shape < 4; ++shape)
                for (int blend = 0; blend < 3; ++blend)
                for (int distribution = 0; distribution < 3; ++distribution)
                for (int frame = 0; frame < 2; ++frame)
                {
                    camera.orthographic = shape == 1 || shape == 3;
                    camera.nearClipPlane = shape == 3 ? -2 : 0.3f;
                    camera.farClipPlane = frame == 0 ? 700 : 370;
                    camera.transform.SetPositionAndRotation(new Vector3(frame * 13.7f, 2.3f, -9.1f),
                        Quaternion.Euler(frame * 11.7f, 31.9f + frame * 39.1f, 3.7f));
                    Matrix4x4 projection = camera.orthographic
                        ? Matrix4x4.Ortho(-16, 16, -9, 9, camera.nearClipPlane, camera.farClipPlane)
                        : Matrix4x4.Perspective(57 + frame * 11, 640f / 360, camera.nearClipPlane, camera.farClipPlane);
                    if (shape == 2) { projection.m02 = 0.23f; projection.m12 = -0.17f; }
                    if (shape == 3) { projection.m03 = 0.19f; projection.m13 = -0.13f; }
                    camera.projectionMatrix = projection;
                    settings.CascadeBlendFraction = blend == 0 ? 0 : blend == 1 ? 0.1f : 0.3f;
                    settings.DistributionExponent = distribution == 0 ? 1 : distribution == 1 ? 2 : 4;
                    Vector3 lightDirection = Quaternion.Euler(17 + frame * 23, 93 + frame * 19, 0) * Vector3.forward;
                    Require(cascade.UpdateCascade(camera, ref lightDirection), "production split update failed");

                    float weight = 1, total = 0;
                    for (int i = 0; i < 4; ++i) { total += weight; weight *= settings.DistributionExponent; }
                    weight = 1;
                    float end = 0;
                    for (int i = 0; i < 4; ++i) { end += weight / total; splitEnds[i] = end; weight *= settings.DistributionExponent; }
                    splitEnds[3] = 1;
                    Matrix4x4 view = camera.worldToCameraMatrix;
                    Matrix4x4 originalProjection = camera.projectionMatrix;
                    float originalAspect = camera.aspect;
                    // The Game-camera contract derives horizontal scale from the actual target.
                    projection.m00 = projection.m11 * 360f / 640;
                    float near = camera.nearClipPlane, far = Mathf.Min(camera.farClipPlane, settings.ShadowDistance);
                    for (int level = 0; level < 4; ++level)
                    {
                        cascade.ConfigCameraToShadowSpace(shadow, lightDirection, level, settings.ShadowDistance, 2044);
                        Matrix4x4 rasterView = shadow.worldToCameraMatrix, rasterProjection = shadow.projectionMatrix;
                        cascade.GetReceiverWorldCorners(camera, level, corners);
                        Require(shadow.worldToCameraMatrix == rasterView && shadow.projectionMatrix == rasterProjection,
                            "receiver footprint changed shadow raster matrices");
                        float start = 0;
                        if (level > 0)
                        {
                            float previous = level == 1 ? 0 : splitEnds[level - 2];
                            float boundary = splitEnds[level - 1];
                            float overlap = Mathf.Min(boundary - previous, splitEnds[level] - boundary) * settings.CascadeBlendFraction;
                            start = boundary - overlap;
                        }
                        for (int i = 0; i < 8; ++i)
                        {
                            Vector3 point = view.MultiplyPoint3x4(corners[i]);
                            float expectedDepth = Mathf.Lerp(near, far, i < 4 ? start : splitEnds[level]);
                            Require(Mathf.Abs(-point.z - expectedDepth) < 0.005f, "receiver split/blend depth incorrect");
                            Vector3 ndc = projection.MultiplyPoint(point);
                            float expectedX = (i % 4 < 2 ? -1 : 1) * (1 + 2f / 640);
                            float expectedY = (i % 4 == 0 || i % 4 == 3 ? -1 : 1) * (1 + 2f / 360);
                            Require(Mathf.Abs(ndc.x - expectedX) < 0.0003f && Mathf.Abs(ndc.y - expectedY) < 0.0003f,
                                "receiver did not preserve one-pixel jitter coverage");
                            ++report.receiverCornerChecks;
                        }
                    }
                    Require(camera.projectionMatrix == originalProjection && camera.worldToCameraMatrix == view &&
                        camera.aspect == originalAspect, "receiver footprint mutated the main camera");
                }
            }
            finally
            {
                for (int i = 0; i < scalarNames.Length; ++i) Shader.SetGlobalFloat(scalarNames[i], scalars[i]);
                for (int i = 0; i < vectorNames.Length; ++i) Shader.SetGlobalVector(vectorNames[i], vectors[i]);
                Shader.SetGlobalTexture("_NoiseTexture", noise);
                Shader.SetGlobalMatrix("_ShadowNoiseWorldToLight", noiseMatrix);
                Shader.SetGlobalInteger("_PcssBlockerSamples", blockerSamples);
                Shader.SetGlobalInteger("_PcssFilterSamples", filterSamples);
                EditorSceneManager.ClosePreviewScene(scene);
                if (target != null) UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static void CheckFallback(MaxShadowCasterCullingVolume volume, Vector3[] corners,
            Matrix4x4 matrix, float padding, ref Report report, bool rotatedSide = false)
        {
            ScriptableCullingParameters original = Box(Matrix4x4.identity, 10);
            if (rotatedSide) original.SetCullingPlane(0, new Plane(new Vector3(1, 1, 0).normalized, 10));
            ScriptableCullingParameters candidate = original;
            Require(!volume.TryApply(ref candidate, corners, matrix, padding), "invalid volume did not fall back");
            Require(candidate.cullingPlaneCount == original.cullingPlaneCount && candidate.cullingMatrix == original.cullingMatrix,
                "fallback changed culling parameters");
            for (int i = 0; i < original.cullingPlaneCount; ++i)
                Require(candidate.GetCullingPlane(i).Equals(original.GetCullingPlane(i)), "fallback changed a plane");
            ++report.fallbackChecks;
        }

        private static ScriptableCullingParameters Box(Matrix4x4 worldToLight, float extent)
        {
            var parameters = new ScriptableCullingParameters { cullingPlaneCount = 6, cullingMatrix = worldToLight };
            Vector3[] normals = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            for (int i = 0; i < 6; ++i)
            {
                Vector3 n = normals[i];
                Vector4 equation = worldToLight.transpose * new Vector4(n.x, n.y, n.z, extent);
                parameters.SetCullingPlane(i, new Plane(new Vector3(equation.x, equation.y, equation.z), equation.w));
            }
            return parameters;
        }

        private static bool Contains(ScriptableCullingParameters parameters, Vector3 point, float tolerance)
        {
            for (int i = 0; i < parameters.cullingPlaneCount; ++i)
                if (parameters.GetCullingPlane(i).GetDistanceToPoint(point) < -tolerance) return false;
            return true;
        }

        private static bool Intersects(ScriptableCullingParameters parameters, Bounds bounds)
        {
            for (int i = 0; i < parameters.cullingPlaneCount; ++i)
            {
                Plane plane = parameters.GetCullingPlane(i);
                Vector3 n = plane.normal;
                float radius = Vector3.Dot(new Vector3(Mathf.Abs(n.x), Mathf.Abs(n.y), Mathf.Abs(n.z)), bounds.extents);
                if (plane.GetDistanceToPoint(bounds.center) + radius < 0) return false;
            }
            return true;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Shadow caster XY geometry: " + message);
        }
    }
}
#endif
