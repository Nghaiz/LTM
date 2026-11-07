using System;
using System.Linq;
using System.Reflection;
using Ironfront.Net.Unity.Client.Hud;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The RHIB's bow gun can be reached, aimed and seen through, and a beached boat can always
    /// get back into the water. Owner report 2026-11-07: "the boat's machine gun cannot be used",
    /// and "it runs aground and stays stuck if it hits land too fast".
    /// </summary>
    public sealed class BoatAndMountedGunTests
    {
        private static readonly string[] MountedGunPrefabs =
        {
            "Assets/Prefab/rhib.prefab",
            "Assets/Prefab/helicopter.prefab",
        };

        private static Type TypeNamed(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name, false))
            .First(type => type != null);

        private static object Field(object owner, string name)
        {
            FieldInfo field = owner.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{owner.GetType().Name}.{name}");
            return field.GetValue(owner);
        }

        // ------------------------------------------------------------------ thrust

        [Test]
        public void AFloatingHullGetsTheWholeEngine()
        {
            Assert.AreEqual(1f, BoatThrust.Drive(1f, -1f, 1f), 1e-5f);
            Assert.AreEqual(1f, BoatThrust.Drive(0.75f, -1f, 1f), 1e-5f);
        }

        [Test]
        public void AsternIsTheOriginalSlowReverseWhileAfloat()
        {
            Assert.AreEqual(-BoatThrust.AsternAfloat, BoatThrust.Drive(1f, -1f, -1f), 1e-5f);
        }

        /// <summary>
        /// The defect: two wet samplers of four gave the original nothing at all, either way.
        /// </summary>
        [Test]
        public void AHalfBeachedHullStillDrivesBothWays()
        {
            float ahead = BoatThrust.Drive(0.5f, -1f, 1f);
            float astern = BoatThrust.Drive(0.5f, -1f, -1f);

            Assert.Greater(ahead, BoatThrust.DryCrawl);
            Assert.Less(ahead, 1f);
            Assert.Less(astern, -BoatThrust.AsternAfloat,
                "a beached hull must back off the sand harder than the afloat reverse");
        }

        [Test]
        public void ADryHullAtTheWatersEdgeCrawls()
        {
            Assert.AreEqual(BoatThrust.DryCrawl, BoatThrust.Drive(0f, 0.5f, 1f), 1e-5f);
            Assert.AreEqual(-BoatThrust.DryCrawl, BoatThrust.Drive(0f, 0.5f, -1f), 1e-5f);
        }

        [Test]
        public void AHullFarUpTheBankGetsNothing()
        {
            Assert.AreEqual(0f, BoatThrust.Drive(0f, BoatThrust.ShoreReachMetres + 0.5f, 1f));
            Assert.AreEqual(0f, BoatThrust.Drive(0f, float.PositiveInfinity, -1f));
        }

        [Test]
        public void NoThrottleNoThrust()
        {
            Assert.AreEqual(0f, BoatThrust.Drive(1f, -1f, 0f));
            Assert.AreEqual(0f, BoatThrust.Drive(0.25f, -1f, 0f));
        }

        [Test]
        public void ReachGrowsWithTheWetHull()
        {
            float last = BoatThrust.Reach(0f, 0f);
            foreach (float wet in new[] { 0.25f, 0.5f, 0.75f })
            {
                float reach = BoatThrust.Reach(wet, -1f);
                Assert.Greater(reach, last, "at " + wet);
                last = reach;
            }
        }

        // ------------------------------------------------------------------ seat order

        /// <summary>The RHIB: driver, three benches, then the bow gun at seat 4.</summary>
        private static NetSeatRole Rhib(int seat) => seat == 0 ? NetSeatRole.Driver
            : seat == 4 ? NetSeatRole.Gunner : NetSeatRole.Passenger;

        [Test]
        public void TheSeatKeyAsksForTheGunRightAfterTheDriver()
        {
            CollectionAssert.AreEqual(new[] { 0, 4, 1, 2, 3 }, SeatEntryOrder.Walk(Rhib, 5).ToArray());
            Assert.AreEqual(4, SeatEntryOrder.Next(Rhib, 5, 0));
            Assert.AreEqual(1, SeatEntryOrder.Next(Rhib, 5, 4));
            Assert.AreEqual(-1, SeatEntryOrder.Next(Rhib, 5, 3), "the walk ends after the last bench");
        }

        [Test]
        public void ASeatWalkWithNoSeatsIsOver()
        {
            Assert.AreEqual(-1, SeatEntryOrder.Next(Rhib, 0, 0));
            Assert.AreEqual(-1, SeatEntryOrder.Next(null, 5, 0));
        }

        [Test]
        public void EverySeatRoleHasAName()
        {
            foreach (NetSeatRole role in Enum.GetValues(typeof(NetSeatRole)))
                Assert.IsNotEmpty(SeatEntryOrder.Label(role), role.ToString());
            Assert.AreEqual("GUNNER", SeatEntryOrder.Label(NetSeatRole.Gunner));
        }

        // ------------------------------------------------------------------ the guns

        /// <summary>
        /// Every seat that mans a camera gun shows its reticle, and the reticle is drawn over the
        /// scene: the RHIB's bow gun had no HUD linked at all, and both guns' cross was an opaque,
        /// depth-tested quad 200 m out that anything nearer hid.
        /// </summary>
        [Test]
        public void EveryCameraGunSeatHasAReticleDrawnOverTheScene()
        {
            Type seatType = TypeNamed("Seat");
            Type cameraGun = TypeNamed("MountedCameraWeapon");
            int checkedSeats = 0;

            foreach (string path in MountedGunPrefabs)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.IsNotNull(prefab, path);
                foreach (Component seat in prefab.GetComponentsInChildren(seatType, true))
                {
                    object weapon = Field(seat, "weapon");
                    if (weapon == null || !cameraGun.IsInstanceOfType(weapon)) continue;
                    checkedSeats++;

                    var hud = Field(seat, "hud") as GameObject;
                    Assert.IsNotNull(hud, $"{path}: {seat.name} mans a camera gun with no HUD");
                    var renderer = hud.GetComponent<Renderer>();
                    Assert.IsNotNull(renderer, $"{path}: {hud.name} draws nothing");
                    Shader shader = renderer.sharedMaterial.shader;
                    Assert.AreEqual("Ironfront/HUD Reticle", shader.name, $"{path}: {hud.name}");
                    Assert.AreEqual(UnityEngine.Rendering.ShadowCastingMode.Off, renderer.shadowCastingMode,
                        $"{path}: a reticle casts no shadow");
                }
            }

            Assert.AreEqual(2, checkedSeats, "the RHIB's bow gun and the helicopter's door gun");
        }

        [Test]
        public void TheRhibGunnerLooksThroughTheGun()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefab/rhib.prefab");
            Component gun = prefab.GetComponentInChildren(TypeNamed("MountedCameraWeapon"), true);

            Assert.IsTrue((bool)Field(gun, "viewThroughGun"),
                "the bow gun stands a third of a metre in front of its gunner's face");
            Assert.Less((float)Field(gun, "aimedFieldOfView"), 60f, "aiming zooms");
            Assert.IsNotNull(gun.GetComponentInChildren<Camera>(true), "the gun has its own camera");
        }

        // ------------------------------------------------------------------ the seat list

        [Test]
        public void TheMatchHudCarriesASeatRowForEverySeatKey()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefab/Ingame UI Container.prefab");
            var view = prefab.GetComponentInChildren<VehicleSeatsView>(true);
            Assert.IsNotNull(view, "run Ironfront/Net/Build in-match readout");

            var so = new SerializedObject(view);
            foreach (string field in new[] { "_rows", "_rowBackings", "_rowKeys", "_rowRoles", "_rowStates" })
            {
                SerializedProperty rows = so.FindProperty(field);
                Assert.AreEqual(VehicleSeatsView.MaxSeats, rows.arraySize, field);
                for (int i = 0; i < rows.arraySize; i++)
                    Assert.IsNotNull(rows.GetArrayElementAtIndex(i).objectReferenceValue, $"{field}[{i}]");
            }
            Assert.IsNotNull(so.FindProperty("_group").objectReferenceValue);
            Assert.AreEqual(0f, ((CanvasGroup)so.FindProperty("_group").objectReferenceValue).alpha,
                "authored hidden: it shows only while seated");

            var keys = (Text)so.FindProperty("_rowKeys").GetArrayElementAtIndex(4).objectReferenceValue;
            Assert.AreEqual("F5", keys.text, "seat 4 is the key F5, the RHIB's gun");
        }
    }
}
