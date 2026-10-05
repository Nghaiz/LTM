using System;
using System.Collections.Generic;
using System.Linq;
using Ironfront.Net.Protocol;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// Every projectile a weapon fires has its own slot in every map's kind table, and the server's
    /// table and the client's agree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A projectile travels as the kind whose slot holds its prefab</b>
    /// (<c>ProjectileNetAnnouncer.KindOfLaunch</c>), and a client draws a kind with that slot's
    /// prefab at that slot's speed. A prefab missing from the table falls back to its class, and
    /// that fallback is the v4.3.0 defect: the tank coaxial gun, the helicopter door gun, the tank
    /// cannon and the BEU-AW1 all fell back to <c>Rocket</c> and every client drew each of them as
    /// the helicopter's pod rocket at 120 m/s.
    /// </para>
    /// <para>
    /// The weapons are read off every prefab under <c>Assets/Prefab</c>, so a new weapon with a new
    /// projectile fails here until the scenes give it a slot. The two arrays live on
    /// <c>ProjectileCatalogInstaller</c> (server) and <c>NetClientProjectilePresenter</c> (client),
    /// in Assembly-CSharp and the client assembly, so they are read through
    /// <see cref="SerializedObject"/> rather than by type.
    /// </para>
    /// </remarks>
    public sealed class ProjectileKindTableTests
    {
        private static readonly string[] Maps =
        {
            "Assets/Scenes/Dustbowl.unity",
            "Assets/Scenes/Island.unity",
            "Assets/Scenes/ForestLake.unity",
        };

        private static readonly int KindCount = Enum.GetValues(typeof(ProjectileKind)).Length;

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TestCaseSource(nameof(Maps))]
        public void EveryFiredProjectileHasItsOwnSlotAndBothTablesAgree(string map)
        {
            EditorSceneManager.OpenScene(map, OpenSceneMode.Single);

            GameObject[] server = TableOn("ProjectileCatalogInstaller");
            GameObject[] client = TableOn("NetClientProjectilePresenter");

            Assert.AreEqual(KindCount, server.Length, $"{map}: the server table needs one slot per ProjectileKind");
            CollectionAssert.AreEqual(server, client,
                $"{map}: the server announces by one table and the client draws by the other");

            var seen = new HashSet<GameObject>();
            foreach (GameObject prefab in server.Where(p => p != null))
            {
                Assert.IsTrue(seen.Add(prefab), $"{map}: '{prefab.name}' holds two slots, so its kind is ambiguous");
            }

            foreach ((string weapon, GameObject projectile) in FiredProjectiles())
            {
                int slot = Array.IndexOf(server, projectile);
                Assert.GreaterOrEqual(slot, 0,
                    $"{map}: '{weapon}' fires '{projectile.name}', which has no slot, so every client "
                    + "draws it as whatever its class falls back to");
            }
        }

        [Test]
        public void TheVehicleGunsAndTheLauncherAreNotDrawnAsThePodRocket()
        {
            EditorSceneManager.OpenScene(Maps[0], OpenSceneMode.Single);
            GameObject[] table = TableOn("ProjectileCatalogInstaller");

            Assert.AreEqual("Gatling Tracer", table[(int)ProjectileKind.GatlingRound].name);
            Assert.AreEqual("rocket", table[(int)ProjectileKind.LauncherRocket].name);
            Assert.AreEqual("Tank Projectile", table[(int)ProjectileKind.Shell].name);
            Assert.AreEqual("pod rocket", table[(int)ProjectileKind.Rocket].name);
        }

        private static GameObject[] TableOn(string componentName)
        {
            Component owner = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(c => c != null && c.GetType().Name == componentName);
            Assert.IsNotNull(owner, $"no {componentName} in the open scene");

            SerializedProperty array = new SerializedObject(owner).FindProperty("_prefabsByKind");
            Assert.IsNotNull(array, $"{componentName} has no _prefabsByKind");

            var table = new GameObject[array.arraySize];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = array.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
            }
            return table;
        }

        /// <summary>Every weapon prefab's projectile, by weapon name.</summary>
        private static IEnumerable<(string weapon, GameObject projectile)> FiredProjectiles()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Prefab" }))
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (root == null) continue;

                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || !IsWeapon(behaviour.GetType())) continue;

                    SerializedProperty prefab = new SerializedObject(behaviour)
                        .FindProperty("configuration.projectilePrefab");
                    // A plain bullet is resolved by hitscan and never announced: its streak rides
                    // S_WEAPON_FIRE and the cosmetic tracer pool draws it, so it needs no slot.
                    if (prefab?.objectReferenceValue is GameObject projectile && !IsPlainBullet(projectile))
                    {
                        yield return ($"{root.name}/{behaviour.name}", projectile);
                    }
                }
            }
        }

        private static bool IsPlainBullet(GameObject projectile)
        {
            MonoBehaviour body = projectile.GetComponents<MonoBehaviour>()
                .FirstOrDefault(c => c != null && IsA(c.GetType(), "Projectile"));
            return body != null && body.GetType().Name == "Projectile";
        }

        private static bool IsWeapon(Type type) => IsA(type, "Weapon");

        private static bool IsA(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                if (t.Name == name) return true;
            }
            return false;
        }
    }
}
