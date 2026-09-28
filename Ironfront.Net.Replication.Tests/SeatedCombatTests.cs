using System;
using System.IO;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Shots at and from vehicle seats: an enclosed crew is reached only by a piercing round, and
    /// a seated shooter fires from the seat's camera.
    /// </summary>
    /// <remarks>
    /// Found auditing bug 5 of the 2026-09-28 playtest across every vehicle. The original refuses
    /// every non-piercing hit on a crew in an enclosed seat (<c>Actor.DamageAttributed</c>), and
    /// only <c>sniper.prefab</c>'s round pierces; the server's hitscan path owns health itself and
    /// never ran that rule. A seated player's camera is the seat's
    /// (<c>FpsActorController.StartSeated</c>), and the server fired from a standing eye on the
    /// seat's root instead: 0.22 m below and 0.2 m behind it.
    /// </remarks>
    public class SeatedCombatTests
    {
        private const ushort Shooter = 1;
        private const ushort Crew = 2;
        private const ushort Behind = 3;

        [Fact]
        public void ARifleRoundDoesNotReachAnEnclosedCrew()
        {
            Assert.False(ShotAtCrew(piercing: false, enclosed: true).Hit);
        }

        [Fact]
        public void ASniperRoundReachesAnEnclosedCrew()
        {
            HitResult hit = ShotAtCrew(piercing: true, enclosed: true);

            Assert.True(hit.Hit);
            Assert.Equal(Crew, hit.TargetActorId);
        }

        [Fact]
        public void AnOpenSeatIsNotProtected()
        {
            HitResult hit = ShotAtCrew(piercing: false, enclosed: false);

            Assert.True(hit.Hit);
            Assert.Equal(Crew, hit.TargetActorId);
        }

        [Fact]
        public void AnEnclosedCrewIsNotAShieldEither()
        {
            // Skipped, not absorbed: the hull is what stops the round, and the engine's occlusion
            // test is what says so. Without a hull the round carries on to whoever is behind.
            var compensator = new LagCompensator(new HitboxHistory());
            HitscanTarget[] targets =
            {
                new HitscanTarget(Crew, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 10f), 180f, HumanoidPose.Seated, 0f, 0f), inEnclosedSeat: true),
                new HitscanTarget(Behind, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 20f)), inEnclosedSeat: false),
            };

            HitResult hit = compensator.ResolveHitscan(
                targets, Shooter, new Vec3(0f, 0.6f, 0f), new Vec3(0f, 0f, 1f),
                100f, 0f, 10, piercing: false);

            Assert.True(hit.Hit);
            Assert.Equal(Behind, hit.TargetActorId);
        }

        [Fact]
        public void TheSniperIsTheOnlyPiercingWeapon()
        {
            for (int id = 0; id <= byte.MaxValue; id++)
            {
                WeaponConfig config = WeaponCatalog.For((byte)id);
                Assert.True(config.Piercing == (id == WeaponIds.SL_DEFENDER),
                    $"weapon {id} piercing={config.Piercing}: only sniper.prefab's round is authored "
                    + "piercing, so only SL_DEFENDER may reach an enclosed crew.");
            }
        }

        [Fact]
        public void ASeatedShooterFiresFromTheSeatsEye()
        {
            var seatedEye = new Vec3(40f, 6.85f, 12.2f);
            var authority = new ServerCombatAuthority(
                new ServerFireResolver(new LagCompensator(new HitboxHistory())), new NullDamageSink())
            {
                SeatedEye = actorId => actorId == Shooter ? seatedEye : (Vec3?)null,
            };

            // The session sits on the seat's root, which as a standing capsule would put the eye
            // 0.63 m up -- the wrong answer this replaces.
            MoveState onSeat = MoveState.AtRest(new Vec3(40f, 6f, 12f));
            InputFrame frame = InputFrame.FromFloats(0f, 0f, 0f, 0f, InputButtons.None);

            Assert.Equal(seatedEye, authority.ShotOriginFor(Shooter, in onSeat, in frame));

            // A shooter the engine does not seat keeps the standing eye.
            Assert.Equal(
                ServerCombatAuthority.EyePosition(in onSeat, in frame),
                authority.ShotOriginFor(Behind, in onSeat, in frame));
        }

        [Fact]
        public void ASeatedLeanStillLeans()
        {
            var seatedEye = new Vec3(0f, 0.85f, 0.2f);
            var authority = new ServerCombatAuthority(
                new ServerFireResolver(new LagCompensator(new HitboxHistory())), new NullDamageSink())
            {
                SeatedEye = _ => seatedEye,
            };

            // The FP camera leans in a seat as well (PlayerFpParent.LateUpdate does not ask).
            InputFrame leaning = InputFrame.FromFloats(0f, 0f, 0f, 0f, InputButtons.LeanRight);
            MoveState state = MoveState.AtRest(Vec3.Zero);

            Assert.Equal(
                seatedEye.X + ProtocolConstants.LEAN_EYE_OFFSET,
                authority.ShotOriginFor(Shooter, in state, in leaning).X, 4);
        }

        [Fact]
        public void TheSeatedEyeIsTheSeatCameraTheClientAuthors()
        {
            string controller = File.ReadAllText(Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Assembly-CSharp", "FpsActorController.cs"));

            string authored = FormattableString.Invariant(
                $"fpCameraParent.localPosition = Vector3.up * {ProtocolConstants.SEATED_EYE_HEIGHT}f + Vector3.forward * {ProtocolConstants.SEATED_EYE_FORWARD}f;");

            Assert.True(controller.Contains(authored, StringComparison.Ordinal),
                "FpsActorController.StartSeated no longer places the seated camera at "
                + $"SEATED_EYE_HEIGHT / SEATED_EYE_FORWARD ('{authored}'). The server fires a seated "
                + "player's rounds from those two constants; move them together.");
        }

        // ------------------------------------------------------------------ helpers

        private static HitResult ShotAtCrew(bool piercing, bool enclosed)
        {
            var compensator = new LagCompensator(new HitboxHistory());
            HitscanTarget[] targets =
            {
                new HitscanTarget(Crew, true,
                    HitboxSet.Humanoid(new Vec3(0f, 0f, 10f), 180f, HumanoidPose.Seated, 0f, 0f),
                    inEnclosedSeat: enclosed),
            };

            return compensator.ResolveHitscan(
                targets, Shooter, new Vec3(0f, 0.95f, 0f), new Vec3(0f, 0f, 1f),
                100f, 0f, 10, piercing);
        }

        private sealed class NullDamageSink : IActorDamageSink
        {
            public DamageOutcome ApplyDamage(ushort victimId, float healthDamage, float balanceDamage, ushort attackerId)
                => DamageOutcome.NoOp;

            public float ApplyHeal(ushort actorId, float amount) => 0f;
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null;
                 d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
