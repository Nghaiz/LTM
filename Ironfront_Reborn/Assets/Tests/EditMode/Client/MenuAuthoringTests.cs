#nullable enable

using System.Linq;
using NUnit.Framework;
using Ironfront.Net.Unity.Client.Menu;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class MenuAuthoringTests
    {
        [Test]
        public void MenuSceneContainsTheEightHtmlScreensWithoutPrototypeData()
        {
            Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/Menu.unity", OpenSceneMode.Single);
            GameObject root = scene.GetRootGameObjects().Single(item => item.name == "Multiplayer Menu");
            string[] screens =
            {
                "Main Menu", "Sign In", "Create Account", "Practice", "Settings",
                "Rooms", "Create Room", "Waiting Room",
            };

            foreach (string screen in screens)
                Assert.NotNull(root.transform.Find(screen), $"Missing HTML screen '{screen}'.");

            CanvasScaler scaler = root.GetComponent<CanvasScaler>();
            Assert.AreEqual(new Vector2(1920f, 1080f), scaler.referenceResolution);
            Assert.IsNull(root.transform.Find("Sign In/ForgotPassword"));
            Assert.NotNull(root.GetComponentInChildren<MenuToast>(true));

            string allText = string.Join("\n", root.GetComponentsInChildren<Text>(true)
                .Select(label => label.text));
            Assert.That(allText, Does.Not.Contain("VANGUARD_07"));
            Assert.That(allText, Does.Not.Contain("ARCHIPELAGO"));
            Assert.That(allText, Does.Not.Contain("EU-01"));
            Assert.That(allText, Does.Contain("TEAM 10 LTM"));

            Image[] backgrounds = root.GetComponentsInChildren<Image>(true)
                .Where(image => image.name == "Background").ToArray();
            Assert.IsTrue(backgrounds.Any(image => AssetDatabase.GetAssetPath(image.sprite)
                == "Assets/UI/IronfrontReborn/backgrounds/main-menu.png"));
            Assert.IsTrue(backgrounds.Any(image => AssetDatabase.GetAssetPath(image.sprite)
                == "Assets/UI/IronfrontReborn/backgrounds/auth.png"));
            Assert.IsTrue(backgrounds.Any(image => AssetDatabase.GetAssetPath(image.sprite)
                == "Assets/UI/IronfrontReborn/backgrounds/multiplayer.png"));
        }

        /// <summary>
        /// Every screen is built from the pack's own artwork, not from flat rectangles.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the test the previous revision needed and did not have.</b> The builder asked
        /// the asset catalog for names the current pack no longer contains — <c>"panel"</c>,
        /// <c>"preview"</c>, <c>"tab"</c>, <c>"signal"</c>, <c>"toggle-off"</c>,
        /// <c>"fields/input_default.png"</c> — and, when the lookup returned null, painted a flat
        /// tinted rectangle instead. All eight screens therefore shipped with no angular panels, no
        /// icons and no branding, and every existing assertion above still passed: the screens were
        /// there, the right backgrounds were there, the text was there. Nothing checked that the
        /// screens were built out of anything.
        /// </para>
        /// <para>
        /// The lesson is in what these assertions have in common — they all name a specific piece
        /// of supplied art and require the scene to reference it. "Eight screens exist" is
        /// satisfied by eight empty objects.
        /// </para>
        /// </remarks>
        [Test]
        public void MenuSceneIsBuiltFromTheSuppliedPackArt()
        {
            Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/Menu.unity", OpenSceneMode.Single);
            GameObject root = scene.GetRootGameObjects().Single(item => item.name == "Multiplayer Menu");

            // The wordmark, used as artwork. It replaced a Text label spelling the same name in the
            // default font, which is what shipped while the supplied logo went unreferenced.
            string[] sprites = root.GetComponentsInChildren<Image>(true)
                .Where(image => image.sprite != null)
                .Select(image => AssetDatabase.GetAssetPath(image.sprite))
                .ToArray();

            Assert.IsTrue(
                sprites.Any(path => path == "Assets/UI/IronfrontReborn/branding/ironfront-reborn-logo.svg"),
                "The supplied wordmark is not used anywhere in the menu.");

            // The menu rows carry the icons the prototype names for them. Two is a deliberately low
            // bar: it fails for the defect this guards (zero icons referenced) without pinning a
            // count that a redesign would have to keep updating.
            int icons = sprites.Count(path => path.Contains("/icons/"));
            Assert.GreaterOrEqual(icons, 2,
                "The menu does not reference the supplied icon set.");

            // The angular surfaces. These are geometry rather than sprites -- the pack's buttons,
            // panels and fields are SVGs with no nine-slice border -- so they are counted by
            // component, and every screen must have contributed some.
            AngularPanel[] panels = root.GetComponentsInChildren<AngularPanel>(true);
            Assert.GreaterOrEqual(panels.Length, 8,
                "The menu has no angular surfaces; the screens are flat rectangles.");

            foreach (string screen in new[]
                     {
                         "Main Menu", "Sign In", "Create Account", "Practice", "Settings",
                         "Rooms", "Create Room", "Waiting Room",
                     })
            {
                Transform found = root.transform.Find(screen);
                Assert.NotNull(found, $"Missing HTML screen '{screen}'.");
                Assert.Greater(found.GetComponentsInChildren<AngularPanel>(true).Length, 0,
                    $"Screen '{screen}' is drawn entirely from flat rectangles.");
            }
        }
    }
}
