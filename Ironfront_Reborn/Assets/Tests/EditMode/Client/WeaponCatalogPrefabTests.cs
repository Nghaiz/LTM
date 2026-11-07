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

        /// <summary>
        /// A player's body on the dedicated server takes the third-person branch of
        /// <c>Actor.SpawnWeapon</c>, so every weapon a loadout can hold has to survive it, the
        /// goggles with no third-person model included.
        /// </summary>
        [Test]
        public void EveryRegisteredWeaponCanBeArmedOnABodyWithNoFirstPersonView()
        {
            Type weaponType = TypeNamed("Weapon");
            var failures = new List<string>();
            int armed = 0;
            bool sawOneWithNoThirdPersonModel = false;

            foreach ((byte id, GameObject prefab) in RegisteredWeapons())
            {
                GameObject instance = UnityEngine.Object.Instantiate(prefab);
                try
                {
                    Component weapon = instance.GetComponent(weaponType);
                    if (weapon == null) continue;

                    armed++;
                    if ((Transform)Field(weapon, "thirdPersonTransform") == null) sawOneWithNoThirdPersonModel = true;

                    weaponType.GetMethod("PoseForThirdPerson").Invoke(weapon, null);
                    weaponType.GetMethod("FindRenderers").Invoke(weapon, new object[] { true });
                }
                catch (TargetInvocationException ex)
                {
                    failures.Add($"{prefab.name} (id {id}): {ex.InnerException?.GetType().Name}");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }

            Assert.Greater(armed, 0, "the registry gave no weapon to arm");
            Assert.IsTrue(sawOneWithNoThirdPersonModel,
                "no registered weapon lacks a third-person model, so this test no longer proves the guard");
            Assert.IsEmpty(failures, string.Join("\n", failures));
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
                // Gear the server never fires -- binoculars, goggles, the golden wrench -- has an
                // inert row (no clip), and nothing on either side is predicted from it. The WRENCH is
                // compared: its melee row must swing at the prefab's cadence (2026-10-12).
                if (row.ClipSize == 0) continue;
                Component weapon = prefab.GetComponent(TypeNamed("Weapon"));
                if (weapon == null) continue;
                object prefabConfig = Field(weapon, "configuration");
                compared++;

                bool auto = (bool)Field(prefabConfig, "auto");
                int clip = (int)Field(prefabConfig, "ammo");
                float cooldown = (float)Field(prefabConfig, "cooldown");
                if (auto != row.Automatic) mismatches.Add($"{prefab.name} ({id}): prefab auto={auto}, catalog automatic={row.Automatic}");
                // -1 is "never spends" (the wrench), which a byte clip says with SpendsAmmo instead.
                bool neverSpends = clip == -1 && !row.SpendsAmmo;
                if (!neverSpends && clip != row.ClipSize) mismatches.Add($"{prefab.name} ({id}): prefab clip {clip}, catalog {row.ClipSize}");
                if (Mathf.Abs(cooldown - row.Cooldown) > 1e-4f) mismatches.Add($"{prefab.name} ({id}): prefab cooldown {cooldown}, catalog {row.Cooldown}");
            }

            Assert.Greater(compared, 10, "the registry was read");
            Assert.IsEmpty(mismatches, string.Join("\n", mismatches));
        }
    }
}
