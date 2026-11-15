using System;
using UnityEngine;

namespace MaxSRP
{
    /// <summary>
    /// Per-camera metadata and conservative coverage checks for short-lived far
    /// cascade depth reuse. Texture ownership and scene/settings invalidation
    /// belong to the shadow pass. The sun-error estimate is not a visibility
    /// bound for arbitrary geometry or grazing receiver planes.
    /// </summary>
    public sealed class MaxShadowReusePolicy
    {
        public sealed class CascadeState
        {
            public bool Valid { get; internal set; }
            public Matrix4x4 VP { get; internal set; }
            public Matrix4x4 View { get; internal set; }
            public float Width { get; internal set; }
            public float HalfDepthRange { get; internal set; }
            public Vector3 SunDirection { get; internal set; }
            public long LastRenderSequence { get; internal set; }
            // Complete RECEIVER domain, not the whole raster tile. All required
            // caster data must have been drawn for this domain plus sample support.
            public Rect CoverageRect { get; internal set; }
            public float GuardBandWorld { get; internal set; }
            public float KernelSupportWorld { get; internal set; }
            public float NormalBias { get; internal set; }
            public double EstimatedErrorTexels { get; internal set; }
            public long AgeFrames { get; internal set; }
            public string LastDecision { get; internal set; } = "Uninitialized";
        }

        private readonly CascadeState[] m_cascades = {
            new CascadeState(), new CascadeState(), new CascadeState(), new CascadeState() };

        public CascadeState GetCascade(int cascade) => m_cascades[CheckedIndex(cascade)];

        public void InvalidateAll(string reason = "Invalidated")
        {
            for (int i = 0; i < m_cascades.Length; ++i) Invalidate(i, reason);
        }

        public void Invalidate(int cascade, string reason = "Invalidated")
        {
            CascadeState state = m_cascades[CheckedIndex(cascade)];
            state.Valid = false;
            state.EstimatedErrorTexels = 0;
            state.AgeFrames = 0;
            state.LastDecision = reason ?? "Invalidated";
        }

        /// <param name="kernelSupportWorld">PCSS/PCF XY support including hardware sampling footprint; excludes normalBias.</param>
        /// <remarks>The actual caster cull must include guardBandWorld AND the sample/normal support.</remarks>
        public bool Record(int cascade, Matrix4x4 shadowVP, Matrix4x4 shadowView,
            float width, float halfDepthRange, Vector3 sunDirection, Vector3[] receiverCorners,
            float guardBandWorld, float kernelSupportWorld, float normalBias,
            long renderSequence)
        {
            CascadeState state = m_cascades[CheckedIndex(cascade)];
            state.Valid = false;
            if (!Finite(shadowVP) || !RigidView(shadowView) || !Positive(width) || !Positive(halfDepthRange) ||
                !Nonnegative(guardBandWorld) || !Nonnegative(kernelSupportWorld) || !Finite(normalBias) ||
                renderSequence < 0 || !ValidSun(sunDirection) ||
                !ReceiverBounds(receiverCorners, shadowView, out Vector3 minimum, out Vector3 maximum))
                return Reject(state, "InvalidRecord", out _);

            float bias = Mathf.Abs(normalBias);
            float inset = kernelSupportWorld + bias;
            float rasterLimit = width * 0.5f - inset;
            if (!Positive(rasterLimit)) return Reject(state, "NoRasterCoverage", out _);
            float xMin = Mathf.Max(minimum.x - guardBandWorld, -rasterLimit);
            float xMax = Mathf.Min(maximum.x + guardBandWorld, rasterLimit);
            float yMin = Mathf.Max(minimum.y - guardBandWorld, -rasterLimit);
            float yMax = Mathf.Min(maximum.y + guardBandWorld, rasterLimit);
            if (!Finite(xMin) || !Finite(xMax) || !Finite(yMin) || !Finite(yMax) ||
                xMax <= xMin || yMax <= yMin)
                return Reject(state, "NoReceiverCoverage", out _);

            state.VP = shadowVP;
            state.View = shadowView;
            state.Width = width;
            state.HalfDepthRange = halfDepthRange;
            // Preserve the exact source snapshot; normalizing it again can move
            // a component by an ULP and invent a tiny change for a stationary sun.
            state.SunDirection = sunDirection;
            state.LastRenderSequence = renderSequence;
            state.CoverageRect = new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
            state.GuardBandWorld = guardBandWorld;
            state.KernelSupportWorld = kernelSupportWorld;
            state.NormalBias = bias;
            state.EstimatedErrorTexels = 0;
            state.AgeFrames = 0;
            state.LastDecision = "Rendered";
            state.Valid = true;
            return true;
        }

        public bool CanReuse(int cascade, Vector3[] receiverCorners, Vector3 sunDirection,
            long renderSequence, int maxAgeFrames,
            float maxErrorTexels, float kernelSupportWorld, float normalBias, int effectiveResolution,
            out string reason)
        {
            CascadeState state = m_cascades[CheckedIndex(cascade)];
            if (cascade < 2) return Reject(state, "NearCascadeRealtime", out reason);
            if (!state.Valid) return Reject(state, "Uninitialized", out reason);
            if (maxAgeFrames <= 0 ||
                !Nonnegative(maxErrorTexels) || effectiveResolution <= 0 ||
                !Nonnegative(kernelSupportWorld) || !Finite(normalBias) ||
                !ValidSun(sunDirection))
                return Reject(state, "InvalidReuseParameters", out reason);
            if (renderSequence < state.LastRenderSequence)
                return Reject(state, "RenderSequenceReversed", out reason);
            state.AgeFrames = renderSequence - state.LastRenderSequence;
            state.EstimatedErrorTexels = EstimateErrorTexels(state, sunDirection, effectiveResolution);
            if (kernelSupportWorld != state.KernelSupportWorld || Mathf.Abs(normalBias) != state.NormalBias)
                return Reject(state, "SamplingSupportChanged", out reason);
            if (state.AgeFrames >= maxAgeFrames) return Reject(state, "FrameAge", out reason);
            if (!Finite(state.EstimatedErrorTexels) || state.EstimatedErrorTexels > maxErrorTexels)
                return Reject(state, "SunError", out reason);
            if (!ReceiverBounds(receiverCorners, state.View, out Vector3 minimum, out Vector3 maximum))
                return Reject(state, "InvalidReceiver", out reason);

            // Erode the complete domain instead of accepting points just outside
            // it. A zero guard band may intentionally provide no useful reuse.
            float epsilon = Mathf.Max(0.0001f, state.Width * 0.000001f);
            Rect coverage = state.CoverageRect;
            if (minimum.x < coverage.xMin + epsilon || maximum.x > coverage.xMax - epsilon ||
                minimum.y < coverage.yMin + epsilon || maximum.y > coverage.yMax - epsilon)
                return Reject(state, "ReceiverCoverage", out reason);
            float depthLimit = state.HalfDepthRange - Mathf.Abs(normalBias) - epsilon;
            if (depthLimit <= 0 || minimum.z < -depthLimit || maximum.z > depthLimit)
                return Reject(state, "ReceiverDepthCoverage", out reason);

            state.LastDecision = reason = "Reusable";
            return true;
        }

        /// <summary>
        /// A deliberately conservative scale estimate, not a strict error bound:
        /// depthRange * tan(deltaSun) / (worldTexel * max(abs(sunHeight), .1)).
        /// Double atan2 avoids rounding small TOD rotations to zero via float acos.
        /// </summary>
        public static double EstimateErrorTexels(CascadeState state, Vector3 sunDirection, int effectiveResolution)
        {
            if (state == null || !state.Valid || effectiveResolution <= 0 || !ValidSun(sunDirection))
                return double.PositiveInfinity;
            Vector3 old = state.SunDirection;
            double ax = old.x, ay = old.y, az = old.z;
            double bx = sunDirection.x, by = sunDirection.y, bz = sunDirection.z;
            double aLength = Math.Sqrt(ax * ax + ay * ay + az * az);
            double bLength = Math.Sqrt(bx * bx + by * by + bz * bz);
            ax /= aLength; ay /= aLength; az /= aLength;
            bx /= bLength; by /= bLength; bz /= bLength;
            double dot = ax * bx + ay * by + az * bz;
            double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
            double angle = Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), dot);
            if (!Finite(angle) || angle >= Math.PI * 0.5) return double.PositiveInfinity;
            double height = Math.Max(Math.Min(Math.Abs(ay), Math.Abs(by)), 0.1);
            double worldTexel = (double)state.Width / effectiveResolution;
            return (2.0 * state.HalfDepthRange) * Math.Tan(angle) / (worldTexel * height);
        }

        private static bool ReceiverBounds(Vector3[] corners, Matrix4x4 view, out Vector3 minimum, out Vector3 maximum)
        {
            minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            if (corners == null || corners.Length != 8) return false;
            for (int i = 0; i < corners.Length; ++i)
            {
                if (!Finite(corners[i])) return false;
                Vector3 point = view.MultiplyPoint3x4(corners[i]);
                if (!Finite(point)) return false;
                minimum = Vector3.Min(minimum, point);
                maximum = Vector3.Max(maximum, point);
            }
            return true;
        }

        private static bool Reject(CascadeState state, string value, out string reason)
        {
            state.LastDecision = reason = value;
            return false;
        }
        private static int CheckedIndex(int cascade)
        {
            if ((uint)cascade >= 4) throw new ArgumentOutOfRangeException(nameof(cascade));
            return cascade;
        }
        private static bool ValidSun(Vector3 sun) => Finite(sun) && sun.sqrMagnitude > 0.000001f;
        private static bool Positive(float value) => Finite(value) && value > 0;
        private static bool Nonnegative(float value) => Finite(value) && value >= 0;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(Matrix4x4 matrix)
        {
            for (int i = 0; i < 16; ++i) if (!Finite(matrix[i])) return false;
            return true;
        }
        private static bool RigidView(Matrix4x4 matrix)
        {
            if (!Finite(matrix) || Mathf.Abs(matrix.m30) > 0.00001f || Mathf.Abs(matrix.m31) > 0.00001f ||
                Mathf.Abs(matrix.m32) > 0.00001f || Mathf.Abs(matrix.m33 - 1) > 0.00001f) return false;
            Vector3 x = new Vector3(matrix.m00, matrix.m01, matrix.m02);
            Vector3 y = new Vector3(matrix.m10, matrix.m11, matrix.m12);
            Vector3 z = new Vector3(matrix.m20, matrix.m21, matrix.m22);
            return Mathf.Abs(x.sqrMagnitude - 1) < 0.0001f && Mathf.Abs(y.sqrMagnitude - 1) < 0.0001f &&
                Mathf.Abs(z.sqrMagnitude - 1) < 0.0001f && Mathf.Abs(Vector3.Dot(x, y)) < 0.0001f &&
                Mathf.Abs(Vector3.Dot(x, z)) < 0.0001f && Mathf.Abs(Vector3.Dot(y, z)) < 0.0001f;
        }
    }
}
