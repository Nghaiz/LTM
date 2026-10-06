using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ironfront.Net.Replication.Combat;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The server fires each gun with the cadence, clip and reserve its prefab gives the client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A player's client fires the engine weapon off its prefab; the server judges the same trigger
    /// off <see cref="WeaponCatalog"/>. Wherever the two part, the client shows shots the server
    /// never fires. Owner report 2026-10-07: the SIGNAL DMR "fires forever: the count drops by one
    /// and comes straight back". Its prefab is automatic and its catalog row was semi-automatic, so
    /// a held trigger fired on the client at the cooldown while the server fired once per press and
    /// handed the unspent round back in every snapshot.
    /// </para>
    /// <para>
    /// Every weapon in the game's own registry (<c>Resources/_Managers.prefab</c>) with a catalog
    /// row is checked, so a gun added later is covered without a new case.
    /// </para>
    /// </remarks>
    public sealed class WeaponCatalogPrefabTests
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

        private static IEnumerable<(byte id, GameObject prefab)> RegisteredWeapons()
        {
            var managers = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/_Managers.prefab");
            Component manager = managers.GetComponentInChildren(TypeNamed("WeaponManager"), true);
            foreach (object entry in ((IList)Field(manager, "weapons")).Cast<object>())
            {
                var prefab = (GameObject)Field(entry, "prefab");
                int id = (int)Field(entry, "NetworkId");
                if (prefab != null && id > 0 && id <= byte.MaxValue) yield return ((byte)id, prefab);
            }
        }

        [Test]
        public void EveryGunFiresOnTheServerAsItsPrefabFiresOnTheClient()
        {
            var mismatches = new List<string>();
            int compared = 0;
            foreach ((byte id, GameObject prefab) in RegisteredWeapons())
            {
                if (id > Ironfront.Net.Protocol.WeaponIds.MAX_ASSIGNED) continue;
                WeaponConfig row = WeaponCatalog.For(id);
                // Gear the server never fires as a gun -- binoculars, goggles, the wrenches -- has
                // an inert row (no clip), and nothing on either side is predicted from it.
                if (row.ClipSize == 0) continue;
                Component weapon = prefab.GetComponent(TypeNamed("Weapon"));
                if (weapon == null) continue;
                object prefabConfig = Field(weapon, "configuration");
                compared++;

                bool auto = (bool)Field(prefabConfig, "auto");
                int clip = (int)Field(prefabConfig, "ammo");
                float cooldown = (float)Field(prefabConfig, "cooldown");
                if (auto != row.Automatic) mismatches.Add($"{prefab.name} ({id}): prefab auto={auto}, catalog automatic={row.Automatic}");
                if (clip != row.ClipSize) mismatches.Add($"{prefab.name} ({id}): prefab clip {clip}, catalog {row.ClipSize}");
                if (Mathf.Abs(cooldown - row.Cooldown) > 1e-4f) mismatches.Add($"{prefab.name} ({id}): prefab cooldown {cooldown}, catalog {row.Cooldown}");
            }

            Assert.Greater(compared, 10, "the registry was read");
            Assert.IsEmpty(mismatches, string.Join("\n", mismatches));
        }
    }
}
