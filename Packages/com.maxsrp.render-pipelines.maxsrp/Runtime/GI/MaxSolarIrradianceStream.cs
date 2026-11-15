using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxSRP
{
    // Owned by one scene's TOD component. File I/O stays off the Unity thread;
    // only that thread creates and binds textures.
    internal sealed class MaxSolarIrradianceStream : IDisposable
    {
        private const int UploadBudget = 4 * 1024 * 1024;
        private const int ReadChunkSize = 1024 * 1024;
        private const double PrefetchLifetime = 2.0;
        private readonly Dictionary<int, RenderTexture> m_loaded = new Dictionary<int, RenderTexture>(3);
        private readonly HashSet<int> m_failed = new HashSet<int>();
        private readonly List<int> m_retired = new List<int>(3);
        private readonly CancellationTokenSource m_cancellation = new CancellationTokenSource();
        private readonly float[] m_hours;
        private readonly string m_relativeDirectory;
        private readonly string m_directory;
        private readonly int m_width, m_height, m_layers, m_rowBytes, m_layerBytes, m_fileBytes, m_stripHeight;
        private readonly TextureFormat m_format;
        private readonly GraphicsFormat m_graphicsFormat;

        private Task<ReadResult> m_read;
        private byte[] m_bytes;
        private RenderTexture m_upload;
        private Texture2D m_strip;
        private int m_jobKey = -1, m_uploadLayer, m_uploadRow;
        private int m_prefetch = -1;
        private float m_previousHour = float.NaN;
        private double m_prefetchExpiry;
        private bool m_disposed;

        internal MaxBakedSolarProbeData Source { get; }

        internal MaxSolarIrradianceStream(MaxBakedSolarProbeData source)
        {
            Source = source;
            var descriptor = source.StreamedIrradiance;
            m_hours = (float[])source.Hours?.Clone();
            m_relativeDirectory = descriptor?.RelativeDirectory;
            m_width = descriptor?.Width ?? 0;
            m_height = descriptor?.Height ?? 0;
            m_layers = descriptor?.Layers ?? 0;
            m_format = descriptor?.Format ?? default;
            try
            {
                if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.WindowsPlayer)
                    throw new NotSupportedException("Streamed solar TOD currently requires Windows.");
                int stride = BytesPerTexel(m_format);
                m_graphicsFormat = GraphicsFormatUtility.GetGraphicsFormat(m_format, false);
                if (!BitConverter.IsLittleEndian || !SystemInfo.supports2DArrayTextures ||
                    !SystemInfo.SupportsTextureFormat(m_format) ||
                    !SystemInfo.IsFormatSupported(m_graphicsFormat, FormatUsage.Linear) ||
                    !SystemInfo.IsFormatSupported(m_graphicsFormat, FormatUsage.Render) ||
                    (SystemInfo.copyTextureSupport & (CopyTextureSupport.TextureToRT | CopyTextureSupport.DifferentTypes)) !=
                        (CopyTextureSupport.TextureToRT | CopyTextureSupport.DifferentTypes) ||
                    m_width < 1 || m_width > SystemInfo.maxTextureSize ||
                    m_height < 1 || m_height > SystemInfo.maxTextureSize ||
                    m_layers < 1 || m_layers > SystemInfo.maxTextureArraySlices)
                    throw new InvalidDataException("Unsupported solar irradiance atlas format or dimensions.");
                if (m_hours == null || m_hours.Length == 0)
                    throw new InvalidDataException("The streamed solar volume has no TOD keys.");
                for (int i = 0; i < m_hours.Length; ++i)
                    if (float.IsNaN(m_hours[i]) || float.IsInfinity(m_hours[i]) ||
                        (i > 0 && m_hours[i] <= m_hours[i - 1]))
                        throw new InvalidDataException("The streamed solar volume has invalid TOD keys.");

                m_rowBytes = checked(m_width * stride);
                m_layerBytes = checked(m_rowBytes * m_height);
                m_fileBytes = checked(m_layerBytes * m_layers);
                m_stripHeight = Math.Min(m_height, Math.Max(1, UploadBudget / m_rowBytes));
                m_directory = ResolveDirectory(m_relativeDirectory);
            }
            catch (Exception exception)
            {
                Debug.LogError($"Could not initialize streamed solar TOD for '{source.name}': {exception.Message}", source);
            }
        }

        internal bool MatchesSourceConfiguration(MaxBakedSolarProbeData source)
        {
            if (m_disposed || !source || Source != source) return false;
            var descriptor = source.StreamedIrradiance;
            if (descriptor == null || !string.Equals(descriptor.RelativeDirectory, m_relativeDirectory, StringComparison.Ordinal) ||
                descriptor.Width != m_width || descriptor.Height != m_height ||
                descriptor.Layers != m_layers || descriptor.Format != m_format)
                return false;

            float[] hours = source.Hours;
            if (hours == null || m_hours == null) return hours == null && m_hours == null;
            if (hours.Length != m_hours.Length) return false;
            for (int i = 0; i < hours.Length; ++i)
                if (!hours[i].Equals(m_hours[i])) return false;
            return true;
        }

        internal float Blend(float hour, int lower, int upper) =>
            Mathf.InverseLerp(m_hours[lower], m_hours[upper], hour);

        internal static int BytesPerTexel(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.RGBAHalf: return 8;
                case TextureFormat.RGBAFloat: return 16;
                default: throw new NotSupportedException("Streamed solar irradiance requires RGBAHalf or RGBAFloat.");
            }
        }

        internal static string ResolveDirectory(string relativeDirectory)
        {
            if (string.IsNullOrWhiteSpace(relativeDirectory) || Path.IsPathRooted(relativeDirectory))
                throw new InvalidDataException("The solar TOD stream directory must be relative to StreamingAssets.");
            string root = Path.GetFullPath(Application.streamingAssetsPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, relativeDirectory));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || path.Length <= root.Length)
                throw new InvalidDataException("The solar TOD stream directory leaves StreamingAssets.");
            return path;
        }

        internal bool Tick(float hour)
        {
            if (m_disposed || m_directory == null) return false;
            TryGetTextures(hour, out _, out _, out var beforeFirst, out var beforeSecond);
            MaxBakedSolarProbeData.GetIrradianceKeys(m_hours, hour, out int lower, out int upper);
            UpdatePrefetch(hour, lower, upper);
            bool lostBacking = RetireUnused(hour, lower, upper);
            AdvanceJob(lower, upper);
            if (m_jobKey < 0)
            {
                // Load the strongest endpoint first so a cold volume can light the scene sooner.
                float blend = Blend(hour, lower, upper);
                int first = blend <= .5f ? lower : upper;
                int second = first == lower ? upper : lower;
                if (NeedsKey(first)) BeginRead(first);
                else if (NeedsKey(second)) BeginRead(second);
                else if (NeedsKey(m_prefetch)) BeginRead(m_prefetch);
            }
            TryGetTextures(hour, out _, out _, out var firstTexture, out var secondTexture);
            return lostBacking || !ReferenceEquals(beforeFirst, firstTexture) || !ReferenceEquals(beforeSecond, secondTexture);
        }

        internal bool TryGetTextures(float hour, out int lower, out int upper,
            out RenderTexture first, out RenderTexture second)
        {
            lower = upper = -1;
            first = second = null;
            if (m_loaded.Count == 0) return false;
            MaxBakedSolarProbeData.GetIrradianceKeys(m_hours, hour, out lower, out upper);
            bool hasLower = m_loaded.TryGetValue(lower, out first) && IsReady(first);
            bool hasUpper = m_loaded.TryGetValue(upper, out second) && IsReady(second);
            if (hasLower && hasUpper) return true;

            // Keep one valid endpoint during a scrub, cold start, or failed read.
            lower = upper = hasLower ? lower : hasUpper ? upper : ClosestLoaded(hour);
            if (lower < 0)
            {
                first = second = null;
                return false;
            }
            first = second = m_loaded[lower];
            return true;
        }

        private static bool IsReady(RenderTexture texture) => texture && texture.IsCreated();

        private int ClosestLoaded(float hour)
        {
            int closest = -1;
            float minimum = float.PositiveInfinity;
            foreach (var entry in m_loaded)
            {
                if (!IsReady(entry.Value)) continue;
                int key = entry.Key;
                float distance = Mathf.Abs(m_hours[key] - hour);
                if (closest < 0 || distance < minimum || (distance == minimum && key < closest))
                {
                    closest = key;
                    minimum = distance;
                }
            }
            return closest;
        }

        private void UpdatePrefetch(float hour, int lower, int upper)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (hour != m_previousHour)
            {
                float blend = Blend(hour, lower, upper);
                int candidate = hour > m_previousHour && blend > .75f ? upper + 1 :
                    hour < m_previousHour && blend < .25f ? lower - 1 : -1;
                m_prefetch = candidate >= 0 && candidate < m_hours.Length ? candidate : -1;
                m_prefetchExpiry = now + PrefetchLifetime;
                m_previousHour = hour;
            }
            if (now >= m_prefetchExpiry) m_prefetch = -1;
        }

        private bool RetireUnused(float hour, int lower, int upper)
        {
            bool hasLower = m_loaded.TryGetValue(lower, out var first) && IsReady(first);
            bool hasUpper = m_loaded.TryGetValue(upper, out var second) && IsReady(second);
            int fallback = !hasLower && !hasUpper ? ClosestLoaded(hour) : -1;
            bool lostBacking = false;
            m_retired.Clear();
            foreach (var entry in m_loaded)
            {
                int key = entry.Key;
                bool lost = !IsReady(entry.Value);
                if (lost || (key != lower && key != upper && key != m_prefetch && key != fallback))
                    m_retired.Add(key);
                lostBacking |= lost;
            }
            foreach (int key in m_retired)
            {
                var texture = m_loaded[key];
                m_loaded.Remove(key);
                MaxRenderResources.Destroy(ref texture);
            }
            return lostBacking;
        }

        private bool NeedsKey(int key) => key >= 0 && !m_loaded.ContainsKey(key) && !m_failed.Contains(key);

        private void BeginRead(int key)
        {
            m_jobKey = key;
            string path = Path.Combine(m_directory, $"irradiance_{key}.bin");
            int length = m_fileBytes;
            CancellationToken token = m_cancellation.Token;
            m_read = Task.Run(() => ReadFile(path, length, token));
        }

        private static ReadResult ReadFile(string path, int length, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                    FileOptions.SequentialScan);
                if (stream.Length != length)
                    throw new InvalidDataException($"Unexpected byte count for {Path.GetFileName(path)}.");
                var bytes = new byte[length];
                for (int offset = 0; offset < bytes.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    int count = stream.Read(bytes, offset, Math.Min(ReadChunkSize, bytes.Length - offset));
                    if (count == 0) throw new EndOfStreamException(path);
                    offset += count;
                }
                return new ReadResult(bytes, null);
            }
            catch (Exception exception)
            {
                return new ReadResult(null, exception);
            }
        }

        private void AdvanceJob(int lower, int upper)
        {
            if (m_jobKey < 0 || (m_read != null && !m_read.IsCompleted)) return;
            if ((m_jobKey != lower && m_jobKey != upper && m_jobKey != m_prefetch) ||
                (m_read == null && !IsReady(m_upload)))
            {
                ClearJob();
                return;
            }

            try
            {
                if (m_read != null)
                {
                    var result = m_read.GetAwaiter().GetResult();
                    m_read = null;
                    if (result.Error != null) throw result.Error;
                    m_bytes = result.Bytes;
                    m_upload = new RenderTexture(new RenderTextureDescriptor(m_width, m_height, m_graphicsFormat, 0)
                    {
                        dimension = TextureDimension.Tex2DArray,
                        volumeDepth = m_layers,
                        useMipMap = false,
                        autoGenerateMips = false
                    })
                    {
                        name = $"{Source.name} TOD {m_hours[m_jobKey]:g}",
                        hideFlags = HideFlags.HideAndDontSave,
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp,
                        anisoLevel = 1
                    };
                    if (!m_upload.Create())
                        throw new InvalidOperationException("Could not allocate a solar irradiance texture array.");
                    m_strip = new Texture2D(m_width, m_stripHeight, m_format, false, true)
                    {
                        name = "Solar TOD upload buffer",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                }

                int stripsThisTick = Math.Max(1, UploadBudget / (m_stripHeight * m_rowBytes));
                for (int strip = 0; strip < stripsThisTick && m_uploadLayer < m_layers; ++strip)
                {
                    int firstRow = Math.Min(m_uploadRow, m_height - m_stripHeight);
                    int rowCount = Math.Min(m_stripHeight, m_height - m_uploadRow);
                    m_strip.SetPixelData(m_bytes, 0, m_uploadLayer * m_layerBytes + firstRow * m_rowBytes);
                    m_strip.Apply(false, false);
                    Graphics.CopyTexture(m_strip, 0, 0, 0, m_uploadRow - firstRow, m_width, rowCount,
                        m_upload, m_uploadLayer, 0, 0, m_uploadRow);
                    m_uploadRow += rowCount;
                    if (m_uploadRow == m_height)
                    {
                        ++m_uploadLayer;
                        m_uploadRow = 0;
                    }
                }
                if (m_uploadLayer != m_layers) return;
                if (!IsReady(m_upload))
                {
                    ClearJob();
                    return;
                }
                m_loaded.Add(m_jobKey, m_upload);
                m_upload = null;
                ClearJob();
            }
            catch (Exception exception)
            {
                m_failed.Add(m_jobKey);
                Debug.LogError($"Could not load '{Source.name}' solar TOD key {m_hours[m_jobKey]:g}: {exception.Message}. " +
                    "Using an available key until the volume is reloaded.", Source);
                ClearJob();
            }
        }

        private void ClearJob()
        {
            MaxRenderResources.Destroy(ref m_upload);
            MaxRenderResources.Destroy(ref m_strip);
            m_read = null;
            m_bytes = null;
            m_jobKey = -1;
            m_uploadLayer = m_uploadRow = 0;
        }

        public void Dispose()
        {
            if (m_disposed) return;
            m_disposed = true;
            m_cancellation.Cancel();
            m_cancellation.Dispose();
            ClearJob();
            foreach (var loaded in m_loaded.Values)
            {
                var texture = loaded;
                MaxRenderResources.Destroy(ref texture);
            }
            m_loaded.Clear();
        }

        private readonly struct ReadResult
        {
            internal readonly byte[] Bytes;
            internal readonly Exception Error;

            internal ReadResult(byte[] bytes, Exception error)
            {
                Bytes = bytes;
                Error = error;
            }
        }
    }
}
