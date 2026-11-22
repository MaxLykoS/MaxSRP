using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    /// <summary>
    /// Tightens the receiver's light-space XY bounds using the six-plane camera
    /// culling path supported by Unity 2022.3. Raster projection and Z stay unchanged.
    /// </summary>
    public sealed class MaxShadowCasterCullingVolume
    {
        private readonly Plane[] m_planes = new Plane[6];

        public int SidePlaneCount { get; private set; }

        /// <param name="receiverWorldCorners">Eight corners including cascade overlap and camera jitter coverage.</param>
        /// <param name="paddingWorld">Maximum sample footprint and receiver displacement, in world metres.</param>
        /// <returns>False leaves the original culling parameters untouched.</returns>
        public bool TryApply(ref ScriptableCullingParameters parameters, Vector3[] receiverWorldCorners,
            Matrix4x4 worldToLight, float paddingWorld)
        {
            SidePlaneCount = 0;
            if (receiverWorldCorners == null || receiverWorldCorners.Length != 8 ||
                !Finite(paddingWorld) || paddingWorld < 0 || !RigidAffine(worldToLight) ||
                parameters.cullingPlaneCount != 6)
                return false;

            float magnitude = 1;
            for (int i = 0; i < 8; ++i)
            {
                Vector3 point = receiverWorldCorners[i];
                if (!Finite(point)) return false;
                magnitude = Mathf.Max(magnitude, Mathf.Max(Mathf.Abs(point.x),
                    Mathf.Max(Mathf.Abs(point.y), Mathf.Abs(point.z))));
            }
            // Numerical slack covers conversion to world planes at large coordinates.
            float padding = paddingWorld + Mathf.Max(0.0001f, magnitude * 0.000001f);
            if (!Finite(padding)) return false;

            Vector3 lightX = new Vector3(worldToLight.m00, worldToLight.m01, worldToLight.m02).normalized;
            Vector3 lightY = new Vector3(worldToLight.m10, worldToLight.m11, worldToLight.m12).normalized;
            Vector3 lightZ = new Vector3(worldToLight.m20, worldToLight.m21, worldToLight.m22).normalized;
            int capCount = 0, sideMask = 0;
            float capDirection = 0;
            for (int i = 0; i < 6; ++i)
            {
                Plane plane = parameters.GetCullingPlane(i);
                float normalLength = plane.normal.magnitude;
                if (!Finite(plane.normal) || !Finite(plane.distance) || !Finite(normalLength) || normalLength < 0.5f)
                    return false;
                Vector3 normal = plane.normal / normalLength;
                float alignment = Vector3.Dot(normal, lightZ);
                if (Mathf.Abs(alignment) > 0.9999f)
                {
                    if (capCount == 0) capDirection = alignment;
                    else if (capCount != 1 || alignment * capDirection >= 0) return false;
                    ++capCount;
                    m_planes[i] = plane;
                    continue;
                }
                if (Mathf.Abs(alignment) > 0.0001f) return false;
                float x = Vector3.Dot(normal, lightX), y = Vector3.Dot(normal, lightY);
                int bit;
                if (Mathf.Abs(x) > 0.9999f) bit = x > 0 ? 1 : 2;
                else if (Mathf.Abs(y) > 0.9999f) bit = y > 0 ? 4 : 8;
                else return false;
                if ((sideMask & bit) != 0) return false;
                sideMask |= bit;

                double minimum = double.PositiveInfinity;
                Vector3 n = plane.normal;
                for (int corner = 0; corner < 8; ++corner)
                {
                    Vector3 point = receiverWorldCorners[corner];
                    double value = (double)n.x * point.x + (double)n.y * point.y + (double)n.z * point.z;
                    if (value < minimum) minimum = value;
                }
                float distance = (float)((double)padding * normalLength - minimum);
                if (!Finite(distance)) return false;
                // Intersect with the original box; never expand caster coverage.
                // Preserve every normal and index, including native far-plane semantics.
                plane.distance = Mathf.Min(plane.distance, distance);
                m_planes[i] = plane;
            }
            if (capCount != 2 || sideMask != 15) return false;

            // Commit after complete validation. Count, matrix, options, and Z
            // retain their original values; only four side distances change.
            for (int i = 0; i < 6; ++i) parameters.SetCullingPlane(i, m_planes[i]);
            SidePlaneCount = 4;
            return true;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);

        private static bool RigidAffine(Matrix4x4 matrix)
        {
            for (int i = 0; i < 16; ++i) if (!Finite(matrix[i])) return false;
            if (Mathf.Abs(matrix.m30) > 0.00001f || Mathf.Abs(matrix.m31) > 0.00001f ||
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
