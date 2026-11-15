using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace MaxTerrain.Editor
{
    public static class MeshSinglePageScene
    {
        public const string ScenePath = "Assets/Samples/MaxTerrain/Scenes/DirectDeltaSinglePageRvt10km.unity";
        public static void Open()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            MeshSceneArchive.Restore(ScenePath);
            EditorSceneManager.OpenScene(ScenePath);
            EditorApplication.ExecuteMenuItem("Window/General/Game");
        }
    }
}
