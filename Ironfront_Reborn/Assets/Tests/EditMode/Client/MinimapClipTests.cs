using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// Pins that the minimap picture clips the icons placed on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every minimap icon — spawn buttons, capture-point and body markers, actor blips — is a
    /// child of this RawImage, placed by <c>WorldToViewportPoint</c> against the minimap camera.
    /// P3 task 3.5 re-framed that camera onto the spawn points, so on Island it shows a 326 m square
    /// where the original's authored camera showed 432 m, and anything outside it — the coast, the
    /// sea, boats, aircraft — was drawn beside the map (2026-09-27 report, loadout screen).
    /// </para>
    /// <para>
    /// Read off the prefab asset rather than through <c>MinimapUi</c>, which compiles into
    /// <c>Assembly-CSharp</c> and is unreachable from any test assembly (ledger E-11b); and by
    /// type name, so this assembly needs no reference to UnityEngine.UI to say what it checks.
    /// </para>
    /// <para>
    /// <b>A stencil <c>Mask</c>, not a <c>RectMask2D</c>.</b> In this uGUI a <c>RectMask2D</c>
    /// re-culls every enabled graphic under it every frame, moved or not
    /// (<c>RectMask2D.PerformClipping</c>), and the map carries an icon and a seven-dot trail for
    /// every soldier it shows: 1.42 ms of <c>ClipperRegistry.Cull</c> a frame in a 100-bot match
    /// (development profile, 2026-10-02). A stencil mask clips the same rectangle on the GPU.
    /// </para>
    /// </remarks>
    public sealed class MinimapClipTests
    {
        private const string PrefabPath = "Assets/Prefab/Ingame UI Container.prefab";

        private const string MinimapPath =
            "Minimap UI/Minimap Container/Minimap Sliding Container/Minimap";

        [Test]
        public void TheMinimapPictureClipsEveryIconPlacedOnIt()
        {
            GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.IsNotNull(root, $"No prefab at {PrefabPath}.");

            Transform minimap = root.transform.Find(MinimapPath);
            Assert.IsNotNull(minimap, $"No '{MinimapPath}' under {PrefabPath}.");
            Assert.IsTrue(
                HasComponentNamed(minimap, "UnityEngine.UI.RawImage"),
                "Setup: this is not the map picture, so the assertion below would prove nothing.");

            Component mask = minimap.GetComponents<Component>()
                .FirstOrDefault(c => c != null && c.GetType().FullName == "UnityEngine.UI.Mask");
            Assert.IsNotNull(
                mask,
                "The minimap picture does not clip its children: any icon whose subject lies "
                + "outside what the minimap camera frames is drawn beside the map.");
            Assert.IsTrue(
                (bool)mask.GetType().GetProperty("showMaskGraphic").GetValue(mask),
                "The mask hides the graphic it masks with, and that graphic is the map itself.");

            Assert.IsFalse(
                HasComponentNamed(minimap, "UnityEngine.UI.RectMask2D"),
                "A RectMask2D re-culls every icon and trail dot on the map every frame; the stencil "
                + "Mask already clips them.");
        }

        private static bool HasComponentNamed(Transform subject, string fullName)
        {
            return subject.GetComponents<Component>()
                .Any(c => c != null && c.GetType().FullName == fullName);
        }
    }
}
