#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace MaxSRP
{
    public static class MaxShadowReusePolicyValidation
    {
        [Serializable] private sealed class Report
        {
            public bool success;
            public int checks;
            public string unityVersion;
        }

        [MenuItem("MaxSRP/Validation/Shadow Reuse Policy")]
        public static void Run() => Debug.Log(RunChecks());

        public static string RunChecks()
        {
            var report = new Report { unityVersion = Application.unityVersion };
            var policy = new MaxShadowReusePolicy();
            var corners = Corners(Vector3.zero);
            var sun = new Vector3(0.2f, 0.8f, 0.3f).normalized;
            Matrix4x4 vp = Matrix4x4.Ortho(-25, 25, -25, 25, -100, 100);
            Require(policy.Record(2, vp, Matrix4x4.identity, 50, 100, sun, corners, 5, 2, 1, 100),
                "valid cache record", report);
            var state = policy.GetCascade(2);
            Require(state.Valid && state.VP == vp && state.View == Matrix4x4.identity && state.Width == 50 &&
                state.HalfDepthRange == 100 && state.SunDirection.Equals(sun) && state.LastRenderSequence == 100 &&
                state.AgeFrames == 0 && state.CoverageRect.Equals(new Rect(-15, -13, 30, 26)),
                "complete metadata snapshot", report);

            Require(Reuse(policy, 2, corners, sun, 101, out string reason), "stationary reuse", report);
            Require(state.EstimatedErrorTexels == 0, "stationary sun has exactly zero error", report);
            Require(Reuse(policy, 2, Corners(new Vector3(4, 0, 0)), sun, 101, out reason),
                "camera movement inside receiver guard band", report);
            Require(!Reuse(policy, 2, Corners(new Vector3(6, 0, 0)), sun, 101, out reason) && reason == "ReceiverCoverage",
                "camera movement outside complete receiver domain", report);
            Require(!Reuse(policy, 2, Corners(new Vector3(0, 0, 97)), sun, 101, out reason) && reason == "ReceiverDepthCoverage",
                "cached depth coverage includes normal offset", report);

            // No wall-clock input: the same camera render sequence has these
            // boundaries at every FPS, including when skipped frames take longer.
            for (long sequence = 101; sequence <= 103; ++sequence)
                Require(Reuse(policy, 2, corners, sun, sequence, out reason, 4),
                    "4-frame interval reuses render sequence " + sequence, report);
            Require(!Reuse(policy, 2, corners, sun, 104, out reason, 4) && reason == "FrameAge",
                "4-frame interval refreshes exactly at sequence 104", report);
            Require(Reuse(policy, 2, corners, sun, 107, out reason),
                "8-frame interval still reuses sequence 107", report);
            Require(!Reuse(policy, 2, corners, sun, 108, out reason) && reason == "FrameAge",
                "8-frame interval refreshes exactly at sequence 108", report);
            Require(!Reuse(policy, 2, corners, sun, 99, out reason) && reason == "RenderSequenceReversed",
                "sequence reversal rejects cache", report);
            Require(!policy.CanReuse(2, corners, sun, 101, 8, .5f, 2.1f, 1, 2044, out reason) &&
                reason == "SamplingSupportChanged", "kernel support revision rejects cache", report);
            Require(!policy.CanReuse(2, corners, sun, 101, 8, .5f, 2, 1.1f, 2044, out reason) &&
                reason == "SamplingSupportChanged", "normal offset revision rejects cache", report);

            Vector3 movedSun = Quaternion.AngleAxis(.1f, Vector3.up) * sun;
            Require(!Reuse(policy, 2, corners, movedSun, 101, out reason) && reason == "SunError" &&
                state.EstimatedErrorTexels > .5, "sun-error budget rejects drift", report);
            double tiny = MaxShadowReusePolicy.EstimateErrorTexels(state,
                Quaternion.AngleAxis(.001f, Vector3.up) * sun, 2044);
            Require(tiny > 0 && !double.IsNaN(tiny) && !double.IsInfinity(tiny),
                "small TOD rotation remains measurable", report);

            // Guard band cannot claim padding texels or unrendered raster edges.
            Require(policy.Record(3, vp, Matrix4x4.identity, 50, 100, sun, corners, 100, 2, 1, 100),
                "large guard band record", report);
            Require(policy.GetCascade(3).CoverageRect.Equals(new Rect(-22, -22, 44, 44)),
                "receiver guarantee intersects raster inset", report);
            Require(Reuse(policy, 3, Corners(new Vector3(11, 0, 0)), sun, 101, out reason),
                "receiver inside raster inset reuses", report);
            Require(!Reuse(policy, 3, Corners(new Vector3(13, 0, 0)), sun, 101, out reason) && reason == "ReceiverCoverage",
                "raster border is not camera guard band", report);
            Require(!Reuse(policy, 0, corners, sun, 101, out reason) && reason == "NearCascadeRealtime" &&
                !Reuse(policy, 1, corners, sun, 101, out reason), "near cascades remain real-time", report);
            policy.Invalidate(2, "TestRevision");
            Require(!policy.GetCascade(2).Valid && !Reuse(policy, 2, corners, sun, 101, out reason),
                "explicit invalidation rejects cache", report);
            policy.InvalidateAll();
            Require(!policy.GetCascade(3).Valid, "per-camera policy invalidates all cascades", report);

            var view = Matrix4x4.TRS(new Vector3(17, -23, 11), Quaternion.Euler(20, 37, 5), Vector3.one).inverse;
            var world = new Vector3[8];
            for (int i = 0; i < 8; ++i) world[i] = view.inverse.MultiplyPoint3x4(corners[i]);
            Require(policy.Record(2, vp * view, view, 50, 100, sun, world, 5, 2, 1, 200) &&
                Reuse(policy, 2, world, sun, 201, out reason), "rotated translated light-space coverage", report);
            var otherCamera = new MaxShadowReusePolicy();
            Require(!otherCamera.GetCascade(2).Valid && policy.GetCascade(2).Valid,
                "policies do not share camera state", report);

            Require(!policy.Record(2, vp, Matrix4x4.identity, 50, 100, Vector3.zero, corners, 5, 2, 1, 100) &&
                !policy.GetCascade(2).Valid, "invalid sun record rejects and invalidates", report);
            Require(!policy.Record(2, vp, Matrix4x4.identity, 4, 100, sun, corners, 5, 2, 1, 100),
                "empty raster support rejects record", report);
            corners[0].x = float.NaN;
            Require(!policy.Record(2, vp, Matrix4x4.identity, 50, 100, sun, corners, 5, 2, 1, 100),
                "nonfinite receiver rejects record", report);
            report.success = true;
            return JsonUtility.ToJson(report, true);
        }

        private static bool Reuse(MaxShadowReusePolicy policy, int level, Vector3[] receiver, Vector3 sun,
            long sequence, out string reason, int maxAgeFrames = 8) =>
            policy.CanReuse(level, receiver, sun, sequence, maxAgeFrames, .5f, 2, 1, 2044, out reason);

        private static Vector3[] Corners(Vector3 offset)
        {
            var corners = new Vector3[8];
            for (int i = 0; i < corners.Length; ++i)
                corners[i] = new Vector3((i & 1) == 0 ? -10 : 10, (i & 2) == 0 ? -8 : 8,
                    (i & 4) == 0 ? -3 : 3) + offset;
            return corners;
        }

        private static void Require(bool condition, string name, Report report)
        {
            ++report.checks;
            if (!condition) throw new InvalidOperationException("Shadow reuse policy: " + name);
        }
    }
}
#endif
