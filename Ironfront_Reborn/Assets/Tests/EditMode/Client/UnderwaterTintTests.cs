using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The under-water wash darkens the world, never the menus: it sorts under the HUD and the
    /// loadout, and nothing draws it in <c>OnGUI</c>, which IMGUI puts over every canvas.
    /// </summary>
    /// <remarks>
    /// Owner's screenshot, 2026-11-10: a player who died in water saw the whole death screen, DEPLOY
    /// included, under a 72 % dark blue wash, because the corpse camera sat under the surface and
    /// <c>UnderwaterCamera.OnGUI</c> drew over everything. Read by reflection because this assembly
    /// cannot reference Assembly-CSharp.
    /// </remarks>
    public sealed class UnderwaterTintTests
    {
        private const string PrefabPath = "Assets/Prefab/Ingame UI Container.prefab";

        private static Type Underwater => Type.GetType("UnderwaterCamera, Assembly-CSharp");

        [Test]
        public void NothingDrawsTheWashOverTheMenus()
        {
            Assert.IsNotNull(Underwater, "Setup: no UnderwaterCamera in Assembly-CSharp.");

            MethodInfo onGui = Underwater.GetMethod(
                "OnGUI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNull(onGui, "UnderwaterCamera draws in OnGUI again, which IMGUI puts over the death screen.");
        }

        [Test]
        public void TheWashSortsOverTheFrozenViewAndUnderTheHudAndLoadout()
        {
            int wash = ConstOf(Underwater, "SortingOrder");
            int frozen = ConstOf(Type.GetType("FrozenDeathView, Assembly-CSharp"), "SortingOrder");

            GameObject container = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.IsNotNull(container, $"No prefab at {PrefabPath}.");
            int hud = container.transform.Find("Ingame UI").GetComponent<Canvas>().sortingOrder;
            int loadout = container.transform.Find("Loadout UI Canvas").GetComponent<Canvas>().sortingOrder;

            Assert.Greater(wash, frozen, "The wash sorts under the frozen death view and darkens nothing.");
            Assert.Less(wash, hud, "The wash sorts over the HUD, so the breath bar is under it while the player is under.");
            Assert.Less(wash, loadout, "The wash sorts over the loadout: dying in water hides the death screen again.");
        }

        private static int ConstOf(Type type, string name)
        {
            Assert.IsNotNull(type, $"Setup: no type for '{name}'.");
            FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"{type.Name} has no {name}.");
            return (int)field.GetRawConstantValue();
        }
    }
}
