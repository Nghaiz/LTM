using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// Pins that the in-match HUD can never sit over the loadout screen, and that nothing on the
    /// HUD catches a click meant for a menu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The v4.6.5 deploy deadlock.</b> On the 2026-11-09 playtest (twelve players, Forest
    /// Lake) nobody could respawn: DEPLOY on the death screen did nothing, so every death meant
    /// quitting to the menu and rejoining. The server log has 67 joins and each connection placed
    /// once; the Windows and Linux client logs have no "deploy requested" line after any death.
    /// Reproduced in the Editor against the live Azure server: the topmost raycast hit over the
    /// DEPLOY button was <c>Ingame UI/Damage Vignette</c>, a full-screen RawImage at alpha 0 with
    /// raycastTarget on.
    /// </para>
    /// <para>
    /// <b>Why it reached the top.</b> The HUD canvas and the loadout canvas were both overlay
    /// canvases at sorting order 0, so their order was a tie Unity breaks however it likes. It
    /// usually broke for the canvas enabled last (the loadout, opened after a death), but
    /// creating a NEW overlay canvas re-sorts the ties: <c>FrozenDeathView</c> (#678) creates its
    /// canvas on the first death of each life in a scene, and from that frame the HUD drew over
    /// the loadout. Measured live: render order loadout 1 / HUD 2 on the first death, HUD 1 /
    /// loadout 2 on the second. Every player rejoined after the first death, so every death was a
    /// first one. The breath bar, the night-vision overlay and the achievement toast create canvases
    /// at runtime too, so the tie was never safe; it is removed, not re-won.
    /// </para>
    /// <para>
    /// Read off the prefab asset, and the graphics by type name, because this assembly does not
    /// reference UnityEngine.UI (see <see cref="MinimapClipTests"/>).
    /// </para>
    /// </remarks>
    public sealed class HudBelowLoadoutTests
    {
        private const string PrefabPath = "Assets/Prefab/Ingame UI Container.prefab";

        private const string HudPath = "Ingame UI";

        private const string LoadoutPath = "Loadout UI Canvas";

        private const string PauseMenuPath = "Menu UI";

        /// <summary>A click-catching graphic this large is a wall between the player and a menu.</summary>
        private const float MaxClickableShareOfScreen = 0.5f;

        [Test]
        public void TheHudSortsStrictlyBelowTheLoadoutAndThePauseMenu()
        {
            Canvas hud = CanvasAt(HudPath);
            Canvas loadout = CanvasAt(LoadoutPath);
            Canvas pause = CanvasAt(PauseMenuPath);

            Assert.Less(
                hud.sortingOrder, loadout.sortingOrder,
                "The HUD canvas does not sort below the loadout screen. A tie is not enough: "
                + "creating any overlay canvas at runtime re-sorts ties, and the HUD then draws over "
                + "the death screen and swallows the DEPLOY click (v4.6.5 playtest, 2026-11-09).");
            Assert.Less(
                hud.sortingOrder, pause.sortingOrder,
                "The HUD canvas does not sort below the pause menu, so QUIT TO MENU can end up "
                + "under it the same way DEPLOY did.");
        }

        [Test]
        public void NoHudGraphicCatchesClicksAcrossTheScreen()
        {
            Type graphicType = Type.GetType("UnityEngine.UI.Graphic, UnityEngine.UI");
            Assert.IsNotNull(graphicType, "Setup: UnityEngine.UI.Graphic is not loaded.");

            // An overlay canvas is sized by the screen only at runtime, so the asset's root rect is
            // empty: lay a copy out on a 1920x1080 screen in a preview scene and measure that.
            Scene preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var copy = (GameObject)PrefabUtility.InstantiatePrefab(
                    AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath), preview);
                Transform hud = copy.transform.Find(HudPath);
                Assert.IsNotNull(hud, $"No '{HudPath}' under {PrefabPath}.");

                // The asset also stores the root at scale 0 (the canvas scales it at runtime),
                // which would fold every corner onto one point.
                var screenRect = (RectTransform)hud;
                screenRect.localScale = Vector3.one;
                screenRect.anchorMin = Vector2.zero;
                screenRect.anchorMax = Vector2.zero;
                screenRect.sizeDelta = new Vector2(1920f, 1080f);
                screenRect.ForceUpdateRectTransforms();
                Rect screen = screenRect.rect;

                Component[] graphics = hud.GetComponentsInChildren(graphicType, includeInactive: true);
                Assert.IsNotEmpty(graphics, "Setup: no graphic under the HUD, so this would prove nothing.");

                string[] walls = graphics
                    .Where(g => (bool)graphicType.GetProperty("raycastTarget").GetValue(g))
                    .Where(g => ShareOf(screen, (RectTransform)g.transform, hud) >= MaxClickableShareOfScreen)
                    .Select(g => g.name)
                    .ToArray();

                Assert.IsEmpty(
                    walls,
                    "These HUD graphics cover half the screen or more and catch clicks; the HUD has "
                    + "no button, so all they can do is take a click from a menu drawn under them. "
                    + "Turn raycastTarget off: " + string.Join(", ", walls));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        private static Canvas CanvasAt(string path)
        {
            GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.IsNotNull(root, $"No prefab at {PrefabPath}.");

            Transform node = root.transform.Find(path);
            Assert.IsNotNull(node, $"No '{path}' under {PrefabPath}.");

            Canvas canvas = node.GetComponent<Canvas>();
            Assert.IsNotNull(canvas, $"'{path}' carries no Canvas.");
            Assert.AreEqual(
                RenderMode.ScreenSpaceOverlay, canvas.renderMode,
                $"Setup: '{path}' is not an overlay canvas, so sorting order is not what orders it.");
            return canvas;
        }

        /// <summary>How much of the HUD canvas a graphic's rectangle covers, from 0 to 1.</summary>
        private static float ShareOf(Rect screen, RectTransform graphic, Transform canvas)
        {
            var corners = new Vector3[4];
            graphic.GetWorldCorners(corners);
            Vector3 min = canvas.InverseTransformPoint(corners[0]);
            Vector3 max = canvas.InverseTransformPoint(corners[2]);

            float width = Mathf.Max(0f, Mathf.Min(max.x, screen.xMax) - Mathf.Max(min.x, screen.xMin));
            float height = Mathf.Max(0f, Mathf.Min(max.y, screen.yMax) - Mathf.Max(min.y, screen.yMin));
            return width * height / (screen.width * screen.height);
        }
    }
}
