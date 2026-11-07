using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Replication.Combat;
using NUnit.Framework;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The rifle scopes (owner's run of 2026-10-10, phase P38: "the three scoped guns feel the same --
    /// one should zoom very far, for the long shots").
    /// </summary>
    public sealed class ScopeProfilesTests
    {
        /// <summary>SIGNAL DMR's prism sight: a fixed field of view on its prefab, 15 degrees.</summary>
        private const float DmrAimFovDegrees = 15f;

        [Test]
        public void TheSniperZoomsFarPastTheOtherTwo()
        {
            Assert.IsTrue(ScopeProfiles.TryGet(WeaponIds.SL_DEFENDER, out ScopeProfile sniper));
            Assert.IsTrue(ScopeProfiles.TryGet(WeaponIds.RECON_LRR, out ScopeProfile lrr));
            Assert.IsFalse(ScopeProfiles.TryGet(WeaponIds.SIGNAL_DMR, out _), "the DMR's sight is a model, not a profile");

            float sniperMax = sniper.MagnificationAt(sniper.Magnifications.Count - 1);
            float dmr = MagnificationOf(DmrAimFovDegrees, 60f);
            Assert.GreaterOrEqual(sniperMax, 20f, "a 900 m head is a speck below 20x");
            Assert.Greater(sniperMax, 3f * lrr.MagnificationAt(0));
            Assert.Greater(lrr.MagnificationAt(0), dmr);
            Assert.IsTrue(sniper.VariableZoom && !lrr.VariableZoom);
        }

        [Test]
        public void TheSniperCarriesTheToolsForTheLongShot()
        {
            ScopeProfile sniper = ScopeProfiles.SlDefender;
            Assert.IsTrue(sniper.HasRangefinder);
            Assert.IsTrue(sniper.AdjustableZero);
            Assert.AreEqual(100, sniper.ZeroMinMetres);
            long curvature = AchievementCatalog.Find("curvature")!.Target;
            Assert.GreaterOrEqual(sniper.ZeroMaxMetres, curvature, "CURVATURE's headshot must be a dialled zero, not a guess");
        }

        /// <summary>
        /// Owner report 2026-11-08, "the sniper's zoom is too short": CURVATURE's head must be a
        /// mark one can lay a crosshair on, at least 10 px tall at 1080 p at the scope's top power.
        /// </summary>
        [Test]
        public void TheSnipersTopPowerShowsCurvaturesHeadAtTenPixels()
        {
            ScopeProfile sniper = ScopeProfiles.SlDefender;
            float top = sniper.MagnificationAt(sniper.Magnifications.Count - 1);
            float fov = ScopeProfiles.VerticalFovFor(top, 60f);
            long metres = AchievementCatalog.Find("curvature")!.Target;
            double headDegrees = System.Math.Atan(0.25 / metres) * 180.0 / System.Math.PI;
            Assert.GreaterOrEqual(1080.0 * headDegrees / fov, 10.0, $"{top}x at {metres} m");
        }

        /// <summary>The same report's other half: past 500 m the haze, not the zoom, hid the target.</summary>
        [Test]
        public void AScopeThinsTheHazeItsPictureIsDrawnThrough()
        {
            Assert.AreEqual(1f, ScopeProfiles.HazeScale(1f));
            Assert.AreEqual(1f, ScopeProfiles.HazeScale(0.5f), "nothing below the naked eye");
            Assert.AreEqual(0.2f, ScopeProfiles.HazeScale(25f), 1e-5f);
            Assert.Less(ScopeProfiles.HazeScale(35f), ScopeProfiles.HazeScale(25f));

            // Exponential squared haze: what share of its colour a body keeps at d metres.
            static double Kept(double density, double metres) => System.Math.Exp(-System.Math.Pow(density * metres, 2));
            const double ForestLake = 0.001, Island = 0.003;
            Assert.Less(Kept(ForestLake, 900), 0.5, "the defect: under half a soldier's colour at 900 m");
            Assert.Greater(Kept(ForestLake * ScopeProfiles.HazeScale(25f), 900), 0.95);
            Assert.Less(Kept(Island, 500), 0.15, "the defect on Island at its longest");
            Assert.Greater(Kept(Island * ScopeProfiles.HazeScale(6f), 500), 0.65, "even at the lowest power");
        }

        [Test]
        public void NoScopeIsZeroedPastWhatTheServerJudges()
        {
            // A reported hit is judged against the highest zero the weapon can be set to
            // (ReportedHitJudge.IsOnAim): a scope zeroed past it would have its honest long
            // shots refused as off the aim.
            foreach (byte id in new[] { WeaponIds.SL_DEFENDER, WeaponIds.RECON_LRR })
            {
                Assert.IsTrue(ScopeProfiles.TryGet(id, out ScopeProfile profile));
                float judged = System.Math.Min(ReportedHitJudge.MaxZeroMetres, WeaponCatalog.For(id).Range);
                Assert.LessOrEqual(profile.ZeroMaxMetres, judged, $"weapon {id}");
            }
        }

        [Test]
        public void TheZeroClicksInStepsAndStopsAtItsEnds()
        {
            ScopeProfile sniper = ScopeProfiles.SlDefender;
            Assert.AreEqual(200, sniper.StepZero(100, 1));
            Assert.AreEqual(100, sniper.StepZero(100, -1));
            Assert.AreEqual(sniper.ZeroMaxMetres, sniper.StepZero(sniper.ZeroMaxMetres, 1));
        }

        [Test]
        public void MagnificationNarrowsTheFieldOfViewByItsPower()
        {
            Assert.AreEqual(60f, ScopeProfiles.VerticalFovFor(1f, 60f), 1e-3f);
            Assert.AreEqual(2f * System.Math.Atan(System.Math.Tan(System.Math.PI / 6.0) / 4.0) * 180.0 / System.Math.PI,
                ScopeProfiles.VerticalFovFor(4f, 60f), 1e-3);
            Assert.AreEqual(4f, MagnificationOf(ScopeProfiles.VerticalFovFor(4f, 60f), 60f), 1e-3f);
        }

        [Test]
        public void TheMouseSlowsWithThePower()
        {
            ScopeProfile sniper = ScopeProfiles.SlDefender;
            Assert.AreEqual(1f, ScopeProfiles.SensitivityScale(sniper, sniper.MagnificationAt(0)), 1e-5f);
            Assert.AreEqual(6f / 25f, ScopeProfiles.SensitivityScale(sniper, 25f), 1e-5f);
            Assert.AreEqual(1f, ScopeProfiles.SensitivityScale(null, 25f));
        }

        private static float MagnificationOf(float fovDegrees, float normalFovDegrees)
            => (float)(System.Math.Tan(normalFovDegrees * 0.5 * System.Math.PI / 180.0)
                       / System.Math.Tan(fovDegrees * 0.5 * System.Math.PI / 180.0));
    }
}
