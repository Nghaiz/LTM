#nullable enable

using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class BrandIdentityTests
    {
        [Test]
        public void PlayerSettingsUseIronfrontRebornAndLtm10()
        {
            Assert.AreEqual("LTM10", PlayerSettings.companyName);
            Assert.AreEqual("IronfrontReborn", PlayerSettings.productName);
            Assert.AreEqual("com.ltm10.ironfrontreborn",
                PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Standalone));
        }

        [TestCase("Assets/Scenes/Splash.unity")]
        [TestCase("Assets/Scenes/Menu.unity")]
        public void PlayerFacingScenesContainNoInheritedBrand(string scenePath)
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            string copy = ReadPlayerFacingText(scene);

            StringAssert.DoesNotContain("Ravenfield", copy);
            StringAssert.DoesNotContain("SteelRaven7", copy);
            StringAssert.DoesNotContain("Johan Hassel", copy);
            StringAssert.DoesNotContain("GREENLIGHT", copy.ToUpperInvariant());
            StringAssert.DoesNotContain("BETA 5", copy.ToUpperInvariant());
        }

        [Test]
        public void SplashPresentsIronfrontRebornByLtm10()
        {
            Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/Splash.unity", OpenSceneMode.Single);
            string copy = ReadPlayerFacingText(scene);
            StringAssert.Contains("IRONFRONT: REBORN", copy);
            StringAssert.Contains("LTM10 PRESENTS", copy);
        }

        private static string ReadPlayerFacingText(Scene scene)
            => string.Join("\n", scene.GetRootGameObjects().SelectMany(root =>
                root.GetComponentsInChildren<Text>(true).Select(label => label.text)
                    .Concat(root.GetComponentsInChildren<UnityEngine.TextMesh>(true)
                        .Select(label => label.text))));
    }
}
