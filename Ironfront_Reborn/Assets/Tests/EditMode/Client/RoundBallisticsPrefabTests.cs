using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// Each gun's round prefab flies exactly the flight the server sweeps that gun's shots along.
    /// </summary>
    /// <remarks>
    /// The engine flies a bot's round and draws every round off the prefab
    /// (<c>Projectile.Round</c>); the server judges a player's shot off
    /// <see cref="WeaponCatalog"/>. Two copies of one fact, so this fails the moment they part:
    /// a player would then see a round land where the server says it did not (owner request
    /// 2026-10-06, per-gun ballistics). The weapon is found through the game's own registry,
    /// <c>Resources/_Managers.prefab</c>, by its network id, so a gun given a different round
    /// is checked against that round.
    /// </remarks>
    public sealed class RoundBallisticsPrefabTests
    {
        private static Type TypeNamed(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name, false))
            .First(type => type != null);

        private static object Field(object owner, string name)
        {
            FieldInfo field = owner.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{owner.GetType().Name}.{name}");
            return field.GetValue(owner);
        }

        [TestCase(WeaponIds.RK44)]
        [TestCase(WeaponIds.SIND7)]
        [TestCase(WeaponIds.SIND7_SUPPRESSED)]
        [TestCase(WeaponIds.EAGLE_76)]
        [TestCase(WeaponIds.SL_DEFENDER)]
        [TestCase(WeaponIds.SIGNAL_DMR)]
        [TestCase(WeaponIds.RECON_LRR)]
        public void TheRoundPrefabFliesTheFlightTheServerSweeps(byte weaponId)
        {
            var managers = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/_Managers.prefab");
            Assert.IsNotNull(managers, "the weapon registry");
            Component manager = managers.GetComponentInChildren(TypeNamed("WeaponManager"), true);
            object entry = ((IList)Field(manager, "weapons")).Cast<object>()
                .First(candidate => (int)Field(candidate, "NetworkId") == weaponId);
            var weaponPrefab = (GameObject)Field(entry, "prefab");
            Component weapon = weaponPrefab.GetComponent(TypeNamed("Weapon"));
            var roundPrefab = (GameObject)Field(Field(weapon, "configuration"), "projectilePrefab");
            Type projectileType = TypeNamed("Projectile");
            Component round = roundPrefab.GetComponent(projectileType);
            Assert.AreEqual(projectileType, round.GetType(), $"{roundPrefab.name} is not a plain round");
            object flight = Field(round, "configuration");

            RoundBallistics expected = WeaponCatalog.For(weaponId).Round;
            string where = $"{weaponPrefab.name} -> {roundPrefab.name}";
            Assert.AreEqual(expected.MuzzleVelocity, (float)Field(flight, "speed"), 1e-3f, where + " muzzle velocity");
            Assert.AreEqual(expected.DragPerMetre, (float)Field(flight, "dragPerMetre"), 1e-7f, where + " drag");
            Assert.AreEqual(expected.ZeroMetres, (float)Field(flight, "zeroMetres"), 1e-3f, where + " zero");
        }
    }
}
