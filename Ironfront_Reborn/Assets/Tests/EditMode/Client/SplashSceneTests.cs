using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The intro draws what the original drew (owner report 2026-09-29, image 2: the helicopter a
    /// black silhouette with no rotor).
    /// </summary>
    /// <remarks>
    /// Three breaks, each invisible to the compiler: the rotor and title-mask meshes pointed at
    /// GUIDs this project's decompilation never produced, the mask plane's shader was a stub that
    /// drew an opaque grey block, and the scene carried no baked environment lighting, so a
    /// helicopter lit from behind had no ambient to show anything but its outline.
    /// </remarks>
    public sealed class SplashSceneTests
    {
        private const string SplashPath = "Assets/Scenes/Splash.unity";

        private Scene _scene;
        private bool _opened;

        [OneTimeSetUp]
        public void OpenSplash()
        {
            _scene = SceneManager.GetSceneByPath(SplashPath);
            if (_scene.isLoaded) return;
            _scene = EditorSceneManager.OpenScene(SplashPath, OpenSceneMode.Additive);
            _opened = true;
        }

        [OneTimeTearDown]
        public void CloseSplash()
        {
            if (_opened) EditorSceneManager.CloseScene(_scene, true);
        }

        [Test]
        public void EveryMeshTheIntroDrawsIsThere()
        {
            foreach (GameObject root in _scene.GetRootGameObjects())
            {
                foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    Assert.IsNotNull(filter.sharedMesh,
                        $"'{filter.name}' has no mesh: its reference names an asset this project does not have");
                }
                foreach (SkinnedMeshRenderer skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    Assert.IsNotNull(skin.sharedMesh,
                        $"'{skin.transform.parent.name}/{skin.name}' has no mesh: the pilots are not in the cockpit");
                }
            }
        }

        [Test]
        public void NothingTheIntroDrawsUsesADecompilerStub()
        {
            foreach (GameObject root in _scene.GetRootGameObjects())
            {
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(false))
                {
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        Assert.IsNotNull(material, $"'{renderer.name}' draws with a missing material");
                        string path = AssetDatabase.GetAssetPath(material.shader);
                        if (!path.EndsWith(".shader")) continue;
                        StringAssert.DoesNotContain("DummyShaderTextExporter", File.ReadAllText(path),
                            $"'{renderer.name}' draws with {material.shader.name}, a stub the decompiler left in place of the original");
                    }
                }
            }
        }

        [Test]
        public void TheIntroHasItsEnvironmentLightingBaked()
        {
            Match data = Regex.Match(File.ReadAllText(SplashPath),
                @"m_LightingDataAsset: \{fileID: (-?\d+)(?:, guid: ([0-9a-f]{32}))?");
            Assert.IsTrue(data.Success, "the scene has no lighting settings block");
            Assert.AreNotEqual("0", data.Groups[1].Value,
                "no baked lighting data: the ambient and reflections are black in a build");
            Assert.IsNotEmpty(AssetDatabase.GUIDToAssetPath(data.Groups[2].Value),
                "the baked lighting data the scene names is not in the project");
        }
    }
}
