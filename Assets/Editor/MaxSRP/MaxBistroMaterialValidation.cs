#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    // Exercise the production writer, rather than reproducing its AO math in C#.
    // No scene, material asset, camera or saved pipeline configuration is changed.
    public static class MaxBistroMaterialValidation
    {
        private const int Size = 8;
        private static readonly string[] MatrixNames = {
            "unity_MatrixV", "unity_MatrixP", "unity_MatrixVP",
            "unity_MatrixInvV", "unity_MatrixInvP", "unity_MatrixInvVP"
        };

        [MenuItem("MaxSRP/Bistro/Validate Material AO")]
        public static void Run() => Debug.Log(RunChecks());

        public static string RunChecks()
        {
            if (SystemInfo.supportedRenderTargetCount < 4 ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) ||
                !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
                throw new NotSupportedException("Bistro material validation requires four floating-point MRTs and readback.");

            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Scenes/BistroTest/Shaders/BistroGBuffer.shader");
            if (!shader || !shader.isSupported || ShaderUtil.ShaderHasError(shader))
                throw new InvalidOperationException("The production Bistro GBuffer shader must be supported and compile without errors.");

            var owned = new List<UnityEngine.Object>();
            var previousTarget = RenderTexture.active;
            var previousMatrices = new Matrix4x4[MatrixNames.Length];
            for (int i = 0; i < MatrixNames.Length; ++i)
                previousMatrices[i] = Shader.GetGlobalMatrix(MatrixNames[i]);
            float previousDiagnostic = Shader.GetGlobalFloat("_MaxLightingDiagnosticMode");
            float previousSkyReady = Shader.GetGlobalFloat("_MaxSkyVisibilityReady");
            bool previousLightmap = Shader.IsKeywordEnabled("LIGHTMAP_ON");
            bool previousDirectional = Shader.IsKeywordEnabled("DIRLIGHTMAP_COMBINED");

            try
            {
                var targets = new RenderTexture[4];
                var attachments = new RenderTargetIdentifier[4];
                for (int i = 0; i < targets.Length; ++i)
                {
                    targets[i] = new RenderTexture(Size, Size, i == 0 ? 24 : 0,
                        RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                    {
                        name = "Bistro material validation GBuffer " + i,
                        hideFlags = HideFlags.HideAndDontSave,
                        filterMode = FilterMode.Point
                    };
                    owned.Add(targets[i]);
                    if (!targets[i].Create()) throw new InvalidOperationException("Could not allocate validation MRTs.");
                    attachments[i] = targets[i];
                }

                var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                owned.Add(material);
                int pass = material.FindPass("DEFAULT");
                if (pass < 0) throw new InvalidOperationException("The Bistro production GBuffer pass is missing.");
                material.SetFloat("_Cull", (float)CullMode.Off);
                material.SetFloat("_Cutoff", 0f);
                material.SetFloat("_EmissionNits", 100000f);
                material.SetFloat("_MaxSkyVisibilitySlice", -1f);
                Color albedo = new Color(0.2f, 0.4f, 0.6f, 1f);
                Color emission = new Color(0.125f, 0.25f, 0.5f, 1f);
                material.SetTexture("_BaseColorMap", Constant(albedo, owned));
                material.SetTexture("_EmissionMap", Constant(emission, owned));

                var mesh = new Mesh { name = "Bistro material validation quad", hideFlags = HideFlags.HideAndDontSave };
                owned.Add(mesh);
                mesh.vertices = new[] { new Vector3(-0.9f, -0.9f, 0.5f), new Vector3(0.9f, -0.9f, 0.5f),
                    new Vector3(0.9f, 0.9f, 0.5f), new Vector3(-0.9f, 0.9f, 0.5f) };
                mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
                mesh.tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1),
                    new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                mesh.uv2 = mesh.uv;
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };

                Color? firstPacked = null;
                // The two masks differ only in R; the second matches the scene
                // builder's fallback. Neither source R value is an AO visibility.
                foreach (float sourceRed in new[] { 0f, 1f })
                {
                    material.SetTexture("_MaskMap", Constant(new Color(sourceRed, 0.6f, 0f, 1f), owned));
                    using (var commands = new CommandBuffer { name = "Validate Bistro source mask AO" })
                    {
                        commands.SetRenderTarget(attachments, targets[0]);
                        commands.SetViewport(new Rect(0, 0, Size, Size));
                        // Match the host's normalized clear-depth API. Unity
                        // performs the reversed-Z conversion for this command.
                        commands.ClearRenderTarget(true, true, Color.clear);
                        // The custom SRP uploads these globals directly; the
                        // Built-in-only SetViewProjectionMatrices path must not
                        // replace its GPU-adjusted matrices during DrawMesh.
                        foreach (string name in MatrixNames) commands.SetGlobalMatrix(name, Matrix4x4.identity);
                        commands.SetGlobalFloat("_MaxLightingDiagnosticMode", 0f);
                        commands.SetGlobalFloat("_MaxSkyVisibilityReady", 0f);
                        commands.DisableShaderKeyword("LIGHTMAP_ON");
                        commands.DisableShaderKeyword("DIRLIGHTMAP_COMBINED");
                        ShaderUtil.SetAsyncCompilation(commands, false);
                        commands.DrawMesh(mesh, Matrix4x4.identity, material, 0, pass);
                        ShaderUtil.RestoreAsyncCompilation(commands);
                        Graphics.ExecuteCommandBuffer(commands);
                    }

                    Color coverage = ReadCenter(targets[0]);
                    Color packed = ReadCenter(targets[2]);
                    Color light = ReadCenter(targets[3]);
                    string label = "source mask R=" + sourceRed;
                    Near(coverage.r, albedo.r, label + " draw coverage");
                    Near(coverage.a, 1f, label + " opacity");
                    Near(packed.b, 0.6f, label + " roughness");
                    Near(packed.a, 0f, label + " metalness");
                    Near(light.a, 1f, label + " neutral material AO visibility");
                    Near(light.r, emission.r, label + " emission R");
                    Near(light.g, emission.g, label + " emission G");
                    Near(light.b, emission.b, label + " emission B");
                    if (firstPacked.HasValue)
                    {
                        Near(packed.b, firstPacked.Value.b, "roughness independent of source R");
                        Near(packed.a, firstPacked.Value.a, "metalness independent of source R");
                    }
                    firstPacked = packed;
                }
                return "PASS: production Bistro four-MRT draw preserves emission, roughness and metalness; source mask R=0 and fallback R=1 both produce material AO visibility 1.";
            }
            finally
            {
                using (var restore = new CommandBuffer { name = "Restore Bistro material validation state" })
                {
                    for (int i = 0; i < MatrixNames.Length; ++i) restore.SetGlobalMatrix(MatrixNames[i], previousMatrices[i]);
                    restore.SetGlobalFloat("_MaxLightingDiagnosticMode", previousDiagnostic);
                    restore.SetGlobalFloat("_MaxSkyVisibilityReady", previousSkyReady);
                    if (previousLightmap) restore.EnableShaderKeyword("LIGHTMAP_ON"); else restore.DisableShaderKeyword("LIGHTMAP_ON");
                    if (previousDirectional) restore.EnableShaderKeyword("DIRLIGHTMAP_COMBINED"); else restore.DisableShaderKeyword("DIRLIGHTMAP_COMBINED");
                    Graphics.ExecuteCommandBuffer(restore);
                }
                RenderTexture.active = previousTarget;
                for (int i = owned.Count - 1; i >= 0; --i)
                {
                    if (owned[i] is RenderTexture target) target.Release();
                    UnityEngine.Object.DestroyImmediate(owned[i]);
                }
            }
        }

        private static Texture2D Constant(Color color, List<UnityEngine.Object> owned)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)
            { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
            owned.Add(texture);
            texture.SetPixel(0, 0, color);
            texture.Apply(false, false);
            return texture;
        }

        private static Color ReadCenter(RenderTexture target)
        {
            var previous = RenderTexture.active;
            var readback = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)
            { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(Size / 2, Size / 2, 1, 1), 0, 0, false);
                readback.Apply(false, false);
                return readback.GetPixel(0, 0);
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(readback); }
        }

        private static void Near(float actual, float expected, string label)
        {
            if (float.IsNaN(actual) || float.IsInfinity(actual) || Mathf.Abs(actual - expected) > 0.0001f)
                throw new InvalidOperationException($"Bistro material validation failed: {label}, expected {expected}, actual {actual}.");
        }
    }
}
#endif
