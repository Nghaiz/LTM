using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.EditorTools
{
    /// <summary>Applies the classroom project's owned product identity through Unity APIs.</summary>
    public static class RebrandProjectIdentity
    {
        [MenuItem("Ironfront/Brand/Apply Ironfront Reborn identity")]
        public static void Run()
        {
            PlayerSettings.companyName = "Team 10 LTM";
            PlayerSettings.productName = "Ironfront Reborn";
            PlayerSettings.SetApplicationIdentifier(
                BuildTargetGroup.Standalone, "com.team10ltm.ironfrontreborn");

            RebrandScene("Assets/Scenes/Splash.unity", splash: true);
            RebrandScene("Assets/Scenes/Menu.unity", splash: false);
            AssetDatabase.SaveAssets();
        }

        private static void RebrandScene(string path, bool splash)
        {
            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform item in root.GetComponentsInChildren<Transform>(true))
                    item.name = Replacement(item.name, splash);
                foreach (Text label in root.GetComponentsInChildren<Text>(true))
                    label.text = Replacement(label.text, splash);
                foreach (TextMesh label in root.GetComponentsInChildren<TextMesh>(true))
                    label.text = Replacement(label.text, splash);
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        private static string Replacement(string value, bool splash)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (splash)
            {
                if (value.IndexOf("STEELRAVEN7 PRESENTS", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "TEAM 10 LTM PRESENTS";
                if (value.IndexOf("RAVENFIELD", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "IRONFRONT REBORN";
            }

            if (value.IndexOf("Johan Hassel", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return "Ironfront Reborn • Team 10 LTM";
            if (value.IndexOf("SteelRaven7", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return "TEAM 10 LTM";
            if (value.IndexOf("Ravenfield", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return value.Replace("RAVENFIELD", "IRONFRONT REBORN")
                    .Replace("Ravenfield", "Ironfront Reborn");
            return value;
        }
    }
}
