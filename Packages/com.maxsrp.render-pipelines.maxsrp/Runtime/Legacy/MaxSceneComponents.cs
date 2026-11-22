using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MaxSRP
{
    internal static class MaxSceneComponents<T> where T : MonoBehaviour
    {
        private static readonly List<T> Active = new List<T>();

        public static void Register(T component)
        {
            if (!Active.Contains(component)) Active.Add(component);
        }

        public static void Unregister(T component) => Active.Remove(component);

        public static T Find(Camera camera, Predicate<T> accepts = null)
        {
            if (!camera) return null;
            Scene scene;
            switch (camera.cameraType)
            {
                case CameraType.Game:
                    scene = camera.gameObject.scene;
                    break;
                case CameraType.SceneView:
                    scene = SceneManager.GetActiveScene();
                    break;
                default:
                    return null;
            }

            T chosen = null;
            for (int i = Active.Count - 1; i >= 0; --i)
            {
                T entry = Active[i];
                if (!entry) { Active.RemoveAt(i); continue; }
                if (!entry.isActiveAndEnabled || entry.gameObject.scene != scene ||
                    (accepts != null && !accepts(entry))) continue;
                // Additive scenes and editor reloads can change registration order.
                if (!chosen || entry.GetInstanceID() < chosen.GetInstanceID()) chosen = entry;
            }
            return chosen;
        }
    }
}
