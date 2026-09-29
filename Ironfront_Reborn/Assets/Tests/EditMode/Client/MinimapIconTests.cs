using System.Linq;
using Ironfront.Net.Protocol;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// Pins the minimap icon rework of 2026-09-29 (owner report: icons too small and blurry,
    /// vehicles need icons that show their movement, a clearer view cone, a bigger map).
    /// </summary>
    /// <remarks>
    /// The drawing components (<c>ActorBlip</c>, <c>MinimapMarker</c>, <c>MinimapViewCone</c>)
    /// compile into <c>Assembly-CSharp</c>, which no test assembly can reference (ledger E-11b),
    /// so their rules live in the seam assembly and are pinned here, and the prefabs are read off
    /// disk by component type name, as <see cref="MinimapClipTests"/> does.
    /// </remarks>
    public sealed class MinimapIconTests
    {
        private const string HudPrefab = "Assets/Prefab/Ingame UI Container.prefab";
        private const string MapPath = "Minimap UI/Minimap Container/Minimap Sliding Container/Minimap";
        private const string BlipPrefab = "Assets/Prefab/Actor Blip.prefab";
        private const string SelfDecorationPrefab = "Assets/Prefab/Actor Blip Sightcone.prefab";

        // ---- sizes ----------------------------------------------------------------------

        [Test]
        public void ASoldierIsAFixedShareOfTheMapBetweenTheClamps()
        {
            Assert.AreEqual(1000f * MinimapIconLayout.SoldierShareOfMap, MinimapIconLayout.SoldierPixels(1000f), 1e-3f,
                "A soldier on a mid-sized map must be its share of the map, not a fixed pixel size.");
            Assert.AreEqual(MinimapIconLayout.MinSoldierPixels, MinimapIconLayout.SoldierPixels(200f),
                "A small map must not shrink a soldier below the size its heading can be read at.");
            Assert.AreEqual(MinimapIconLayout.MaxSoldierPixels, MinimapIconLayout.SoldierPixels(5000f),
                "A huge map must not grow a soldier until it covers the ground it stands on.");
        }

        [Test]
        public void AMapWithNoWidthStillDrawsReadableIcons()
        {
            Assert.AreEqual(MinimapIconLayout.MinSoldierPixels, MinimapIconLayout.SoldierPixels(0f));
            Assert.AreEqual(MinimapIconLayout.MinSoldierPixels, MinimapIconLayout.SoldierPixels(float.NaN));
        }

        [Test]
        public void ThePlayersOwnArrowOutranksEveryOtherIcon()
        {
            Assert.Greater(MinimapIconLayout.SelfScale, MinimapIconLayout.HumanScale);
            Assert.Greater(MinimapIconLayout.VehicleScale, MinimapIconLayout.HumanScale,
                "A vehicle must read as bigger than the soldiers around it.");
        }

        [Test]
        public void AFlagIsNeverSmallerThanTheIconsCrowdingIt()
        {
            Assert.GreaterOrEqual(MinimapIconLayout.FlagScale, MinimapIconLayout.VehicleScale,
                "Owner report 2026-09-29: the icons covered the flags. A flag must be at least as big "
                + "as the largest icon that can stand on it.");
            Assert.Greater(MinimapIconLayout.FlagScale, MinimapIconLayout.HumanScale);
        }

        // ---- the speed leader -----------------------------------------------------------

        [Test]
        public void ALeaderPointsAsFarAsTheVehicleWillTravelInItsHorizon()
        {
            float length = MinimapIconLayout.LeaderPixels(speed: 10f, pixelsPerMetre: 0.5f, iconPixels: 100f);
            Assert.AreEqual(10f * MinimapIconLayout.LeaderSeconds * 0.5f, length, 1e-3f);
        }

        [Test]
        public void AParkedOrCrawlingVehicleDrawsNoLeader()
        {
            Assert.AreEqual(0f, MinimapIconLayout.LeaderPixels(0f, 0.5f, 50f));
            Assert.AreEqual(0f, MinimapIconLayout.LeaderPixels(MinimapIconLayout.LeaderMinSpeed - 0.1f, 0.5f, 50f));
            Assert.AreEqual(0f, MinimapIconLayout.LeaderPixels(float.NaN, 0.5f, 50f));
        }

        [Test]
        public void ALeaderNeverOutgrowsItsCapOrDrawsAsAStub()
        {
            Assert.AreEqual(50f * MinimapIconLayout.LeaderMaxIconLengths,
                MinimapIconLayout.LeaderPixels(speed: 200f, pixelsPerMetre: 1f, iconPixels: 50f), 1e-3f);
            Assert.AreEqual(0f, MinimapIconLayout.LeaderPixels(speed: 3f, pixelsPerMetre: 0.1f, iconPixels: 50f),
                "A leader shorter than LeaderMinPixels must be hidden, not drawn as a stub.");
        }

        // ---- the trail ------------------------------------------------------------------

        [Test]
        public void ATrailKeepsAPositionOnlyAfterEnoughTimeAndEnoughDistance()
        {
            Vector3 origin = Vector3.zero;
            Vector3 farEnough = new Vector3(MinimapTrailRules.MinStepMetres + 0.1f, 0f, 0f);
            Vector3 tooClose = new Vector3(MinimapTrailRules.MinStepMetres * 0.5f, 0f, 0f);
            float later = MinimapTrailRules.SampleSeconds + 0.01f;
            float tooSoon = MinimapTrailRules.SampleSeconds * 0.5f;

            Assert.IsTrue(MinimapTrailRules.ShouldSample(origin, 0f, farEnough, later));
            Assert.IsFalse(MinimapTrailRules.ShouldSample(origin, 0f, farEnough, tooSoon),
                "A step taken too soon after the last dot would crowd the trail.");
            Assert.IsFalse(MinimapTrailRules.ShouldSample(origin, 0f, tooClose, later),
                "A body standing still must leave no dots: jitter is not movement.");
        }

        [Test]
        public void ARespawnIsAJumpNotATrail()
        {
            Assert.IsTrue(MinimapTrailRules.IsJump(Vector3.zero, new Vector3(0f, 0f, MinimapTrailRules.JumpMetres + 1f)));
            Assert.IsFalse(MinimapTrailRules.IsJump(Vector3.zero, new Vector3(0f, 0f, MinimapTrailRules.JumpMetres - 1f)));
        }

        [Test]
        public void ADotFadesFromFullToNothingOverItsLifetime()
        {
            Assert.AreEqual(1f, MinimapTrailRules.FadeOf(0f));
            Assert.AreEqual(0f, MinimapTrailRules.FadeOf(MinimapTrailRules.LifetimeSeconds));
            float previous = 1f;
            for (float age = 0.1f; age < MinimapTrailRules.LifetimeSeconds; age += 0.1f)
            {
                float fade = MinimapTrailRules.FadeOf(age);
                Assert.LessOrEqual(fade, previous, "A dot must never brighten as it ages (age " + age + ").");
                previous = fade;
            }
        }

        // ---- which vehicles show --------------------------------------------------------

        [Test]
        public void AnEmptyVehicleIsNeverOnTheMap()
        {
            Assert.IsFalse(RemoteActorRegistry.ShouldMarkVehicle(false, TeamId.None, TeamId.Team0, 0f),
                "Owner ruling 2026-09-29: empty vehicles are not drawn; parked rides buried the flags.");
            Assert.IsFalse(RemoteActorRegistry.ShouldMarkVehicle(false, TeamId.Team0, TeamId.Team0, 0f),
                "An empty vehicle must not borrow a team from a stale crew reading.");
        }

        [Test]
        public void ACrewedVehicleIsExactlyAsVisibleAsItsCrew()
        {
            float r = RemoteActorRegistry.EnemyRevealRadius;
            float far = (r * 10f) * (r * 10f);
            float near = (r - 1f) * (r - 1f);

            Assert.IsTrue(RemoteActorRegistry.ShouldMarkVehicle(true, TeamId.Team0, TeamId.Team0, far),
                "A team-mate's vehicle must show anywhere on the map.");
            Assert.IsTrue(RemoteActorRegistry.ShouldMarkVehicle(true, TeamId.Team1, TeamId.Team0, near),
                "An enemy vehicle next to the player must show, as an enemy soldier there would.");
            Assert.IsFalse(RemoteActorRegistry.ShouldMarkVehicle(true, TeamId.Team1, TeamId.Team0, far),
                "An enemy-driven vehicle far away must not show: that would be a map hack.");
            Assert.IsFalse(RemoteActorRegistry.ShouldMarkVehicle(true, TeamId.Team1, TeamId.None, 0f),
                "Before this client knows its side it cannot tell a friendly crew from an enemy one.");
        }

        // ---- the zoom -------------------------------------------------------------------

        [Test]
        public void TheWheelZoomsInAndOutWithinItsLimits()
        {
            Assert.AreEqual(MinimapZoom.StepPerNotch, MinimapZoom.Next(1f, 1f), 1e-4f);
            Assert.AreEqual(MinimapZoom.MinZoom, MinimapZoom.Next(1f, -3f), "Zooming out stops at the whole map.");
            Assert.AreEqual(MinimapZoom.MaxZoom, MinimapZoom.Next(2.9f, 5f), "Zooming in stops at the closest view.");
            Assert.AreEqual(2f, MinimapZoom.Next(2f, 0f), 1e-4f, "No notch, no change.");
        }

        [Test]
        public void TheWholeMapIsShownUnzoomed()
        {
            Rect view = MinimapZoom.ViewRect(new Vector2(0.9f, 0.1f), MinimapZoom.MinZoom);
            Assert.AreEqual(new Rect(0f, 0f, 1f, 1f), view);
            Assert.AreEqual(new Vector2(0.25f, 0.75f), MinimapZoom.ToMap(new Vector2(0.25f, 0.75f), view));
        }

        [Test]
        public void AZoomedViewCentresOnThePlayerAndStaysInsideThePicture()
        {
            Rect middle = MinimapZoom.ViewRect(new Vector2(0.5f, 0.5f), 2f);
            Assert.AreEqual(0.25f, middle.x, 1e-4f);
            Assert.AreEqual(0.5f, middle.width, 1e-4f);
            Assert.AreEqual(new Vector2(0.5f, 0.5f), MinimapZoom.ToMap(new Vector2(0.5f, 0.5f), middle),
                "The player at the centre of a zoomed view is drawn at the centre of the map.");

            Rect corner = MinimapZoom.ViewRect(new Vector2(0.02f, 0.98f), 2f);
            Assert.AreEqual(0f, corner.xMin, 1e-4f, "A view near the edge slides back inside the picture.");
            Assert.AreEqual(1f, corner.yMax, 1e-4f);
            Assert.IsTrue(MinimapZoom.IsOnMap(MinimapZoom.ToMap(new Vector2(0.02f, 0.98f), corner)),
                "The player must stay on the map even when the view is pushed against its edge.");
        }

        [Test]
        public void SomethingOutsideAZoomedViewIsOffTheMap()
        {
            Rect view = MinimapZoom.ViewRect(new Vector2(0.5f, 0.5f), 3f);
            Assert.IsFalse(MinimapZoom.IsOnMap(MinimapZoom.ToMap(new Vector2(0.05f, 0.5f), view)));
            Assert.IsTrue(MinimapZoom.IsOnMap(MinimapZoom.ToMap(new Vector2(0.45f, 0.55f), view)));
        }

        // ---- the authored prefabs -------------------------------------------------------

        [Test]
        public void TheMapDrawsVehiclesUnderSoldiersUnderThePlayersOwnIcon()
        {
            Transform map = LoadMap();
            Transform vehicles = map.Find("Vehicle Icons");
            Transform soldiers = map.Find("Soldier Icons");
            Transform self = map.Find("Own Icon");
            Assert.IsNotNull(vehicles, "No 'Vehicle Icons' layer under the map.");
            Assert.IsNotNull(soldiers, "No 'Soldier Icons' layer under the map.");
            Assert.IsNotNull(self, "No 'Own Icon' layer under the map.");
            Assert.Less(vehicles.GetSiblingIndex(), soldiers.GetSiblingIndex(),
                "Vehicles must draw under soldiers, or a soldier beside a tank is hidden under it.");
            Assert.Less(soldiers.GetSiblingIndex(), self.GetSiblingIndex(),
                "The player's own arrow must draw over every other icon.");
        }

        [Test]
        public void IconsAreBlendedNormallyAndNeverTakeAClick()
        {
            GameObject blip = AssetDatabase.LoadAssetAtPath<GameObject>(BlipPrefab);
            Assert.IsNotNull(blip, "No prefab at " + BlipPrefab + ".");
            Component raw = blip.GetComponents<Component>().First(c => c != null && c.GetType().FullName == "UnityEngine.UI.RawImage");
            var so = new SerializedObject(raw);
            Assert.IsNull(so.FindProperty("m_Material").objectReferenceValue,
                "The blip is drawn through a custom material again. The additive 'HUD White' made every "
                + "icon a faint smudge on sand; icons blend normally over their own dark rim.");
            Assert.IsFalse(so.FindProperty("m_RaycastTarget").boolValue,
                "A blip that takes clicks blocks the spawn button under it on the deploy screen.");
        }

        [Test]
        public void TheViewConeIsDrawnFromTheCamerasRealAngle()
        {
            GameObject decoration = AssetDatabase.LoadAssetAtPath<GameObject>(SelfDecorationPrefab);
            Assert.IsNotNull(decoration, "No prefab at " + SelfDecorationPrefab + ".");
            Assert.IsTrue(decoration.GetComponents<Component>().Any(c => c != null && c.GetType().Name == "MinimapViewCone"),
                "The view cone is a fixed sprite again; it must be the mesh cone that takes the camera's field of view.");
            Assert.IsNotNull(decoration.transform.Find("Halo"), "The player's team ring is missing from the decoration.");
        }

        [Test]
        public void TheDeployScreenMapIsFittedSquareInsideItsRegion()
        {
            GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(HudPrefab);
            Transform region = root.transform.Find("Loadout UI Canvas/Background Panel/Minimap Region");
            Assert.IsNotNull(region, "The deploy screen's map has no region to fit into.");
            Transform container = region.Find("Minimap Container");
            Assert.IsNotNull(container, "The deploy screen's map container is not inside its region.");
            Component fitter = container.GetComponents<Component>().First(c => c != null && c.GetType().Name == "AspectRatioFitter");
            var so = new SerializedObject(fitter);
            Assert.AreEqual(3 /* FitInParent */, so.FindProperty("m_AspectMode").enumValueIndex,
                "Width-controls-height let the map run off a wide screen; it must fit inside its region.");
            var rect = (RectTransform)region;
            Assert.Greater(rect.anchorMax.x - rect.anchorMin.x, 0.29f + 0.05f,
                "The deploy screen's map must be larger than the original's 29% of the screen width.");
        }

        private static Transform LoadMap()
        {
            GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(HudPrefab);
            Assert.IsNotNull(root, "No prefab at " + HudPrefab + ".");
            Transform map = root.transform.Find(MapPath);
            Assert.IsNotNull(map, "No '" + MapPath + "' under " + HudPrefab + ".");
            return map;
        }
    }
}
