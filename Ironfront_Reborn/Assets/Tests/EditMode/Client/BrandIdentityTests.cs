#nullable enable

using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>Guards the identity a player sees in builds and authored scenes.</summary>
    public sealed class BrandIdentityTests
    {
        [Test]
        public void PlayerSettingsUseIronfrontRebornAndTeam10Ltm()
        {
            Assert.AreEqual("Team 10 LTM", PlayerSettings.companyName);
            Assert.AreEqual("Ironfront Reborn", PlayerSettings.productName);
            Assert.AreEqual(
                "com.team10ltm.ironfrontreborn",
                PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Standalone));
        }

        [TestCase("Assets/Scenes/Splash.unity")]
        [TestCase("Assets/Scenes/Menu.unity")]
        public void PlayerFacingScenesContainNoInheritedBrand(string scenePath)
        {
            string copy = ReadSceneCopy(scenePath);

            StringAssert.DoesNotContain("Ravenfield", copy);
            StringAssert.DoesNotContain("SteelRaven7", copy);
            StringAssert.DoesNotContain("Johan Hassel", copy);
        }

        [Test]
        public void SplashPresentsIronfrontRebornByTeam10Ltm()
        {
            string copy = ReadSceneCopy("Assets/Scenes/Splash.unity");

            StringAssert.Contains("IRONFRONT REBORN", copy);
            StringAssert.Contains("TEAM 10 LTM PRESENTS", copy);
        }

        private static string ReadSceneCopy(string path)
        {
            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            return string.Join(
                "\n",
                scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<Text>(includeInactive: true))
                    .Select(label => label.text));
        }
    }
}
