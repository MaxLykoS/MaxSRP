using System;
using System.Collections.Generic;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace MaxSRP
{
    // Components can publish defaults before Unity constructs the pipeline.
    // Each pipeline owns one live clock and one set of probe streams.
    internal sealed class MaxTimeOfDay : IDisposable
    {
        private readonly struct Publication
        {
            internal readonly int AuthorId;
            internal readonly Scene Scene;
            internal readonly bool IsPlaying;
            internal readonly MaxTimeOfDaySettings Settings;

            internal Publication(int authorId, Scene scene, bool isPlaying, MaxTimeOfDaySettings settings)
            {
                AuthorId = authorId;
                Scene = scene;
                IsPlaying = isPlaying;
                Settings = settings;
            }
        }

        private static readonly Dictionary<int, Publication> s_publications = new Dictionary<int, Publication>();
        private static event Action s_publicationChanged;

        private Publication m_selected;
        private bool m_publicationPending;
        private float? m_pendingHour;

        internal MaxTimeOfDayState Current { get; private set; }
        internal bool IsLoading => Current?.ProbeStreams.IsLoading ?? false;

        internal MaxTimeOfDay()
        {
            SelectPublication();
            ApplyPending();
            s_publicationChanged += SelectPublication;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
#if UNITY_EDITOR
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
#endif
        }

        internal static void Publish(int authorId, Scene scene, bool isPlaying, MaxTimeOfDaySettings settings)
        {
            s_publications[authorId] = new Publication(authorId, scene, isPlaying, settings);
            s_publicationChanged?.Invoke();
        }

        internal static void Withdraw(int authorId)
        {
            if (s_publications.Remove(authorId))
                s_publicationChanged?.Invoke();
        }

        private void OnActiveSceneChanged(Scene previous, Scene next) => SelectPublication();

        private void SelectPublication()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            Publication active = default, fallback = default;
            foreach (Publication published in s_publications.Values)
            {
                if (!published.Scene.IsValid()) continue;
                if (published.Scene == activeScene)
                {
                    if (active.AuthorId == 0 || published.AuthorId < active.AuthorId)
                        active = published;
                }
                else if (fallback.AuthorId == 0 || published.AuthorId < fallback.AuthorId)
                {
                    fallback = published;
                }
            }

            // Resolve competing authoring components when scenes or settings change.
            Publication selected = active.AuthorId != 0 ? active : fallback;
            if (selected.AuthorId == 0)
                m_pendingHour = null;
            else if (selected.AuthorId != m_selected.AuthorId || !selected.Settings.Hour.Equals(m_selected.Settings.Hour))
                m_pendingHour = MaxTimeOfDayState.NormalizeHour(selected.Settings.Hour);

            m_selected = selected;
            m_publicationPending = true;
        }

        internal bool IsAuthor(int authorId) => m_selected.AuthorId == authorId;

        internal bool TryGetHour(out float hour)
        {
            hour = m_pendingHour ?? Current?.ClockHour ?? default;
            return m_selected.AuthorId != 0;
        }

        internal bool SetHour(float hour)
        {
            if (m_selected.AuthorId == 0) return false;
            m_pendingHour = MaxTimeOfDayState.NormalizeHour(hour);
            return true;
        }

        // Writes made from inside a render callback are queued for the next batch.
        internal void ApplyPending()
        {
            if (m_publicationPending)
            {
                if (Current != null && Current.AuthorId != m_selected.AuthorId)
                {
                    DisposeStreams();
                    Current = null;
                }

                if (m_selected.AuthorId != 0)
                {
                    if (Current == null)
                        Current = new MaxTimeOfDayState(m_selected.AuthorId, m_selected.Settings);
                    else
                        Current.ApplySettings(m_selected.Settings);
                    Current.IsPlaying = m_selected.IsPlaying;
                }

                m_publicationPending = false;
            }

            if (m_pendingHour.HasValue)
                Current?.SetHour(m_pendingHour.Value);
            m_pendingHour = null;
        }

        internal void DisposeStreams() => Current?.ProbeStreams.Dispose();

        public void Dispose()
        {
            s_publicationChanged -= SelectPublication;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
#if UNITY_EDITOR
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
#endif
            DisposeStreams();
            Current = null;
            m_selected = default;
            m_pendingHour = null;
        }
    }
}
