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

        // ------------------------------------------------------------------ a passenger's origin

        [Fact]
        public void APassengerHitsTheDriverTheyRideBehind()
        {
            // Playtest 2026-09-28, bug 2: riding in a car a bot was driving, a magazine emptied
            // into the driver did no damage. A client interpolates every vehicle it does not
            // drive, so the passenger aimed from where the car WAS; fired from where it is, the
            // round left from metres ahead of the driver it was aimed at.
            Assert.Equal(1, PassengerShotAtDriver(ridesAsPassenger: true, out _));
        }

        [Fact]
        public void FromThePresentSeatThePassengersRoundMissesTheDriver()
        {
            // The bug as it shipped, and the control for the test above: only the flag differs.
            Assert.Equal(0, PassengerShotAtDriver(ridesAsPassenger: false, out _));
        }

        [Fact]
        public void APassengersOriginMovesBackByTheirOwnTravel()
        {
            PassengerShotAtDriver(ridesAsPassenger: true, out LagCompensator compensator);

            // Once per trigger pull, and measured -- not the present-origin fallback.
            Assert.Equal(1, compensator.OwnTravelMeasured);
            Assert.Equal(0, compensator.OwnTravelUnmeasured);
        }

        [Fact]
        public void AShooterWithNoHistoryKeepsThePresentOrigin()
        {
            var compensator = new LagCompensator(new HitboxHistory());
            HitscanTarget[] targets =
            {
                new HitscanTarget(Shooter, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 0f))),
            };

            Assert.False(compensator.TryMeasureOwnTravel(targets, Shooter, 100f, 300, out Vec3 travelled));
            Assert.Equal(Vec3.Zero, travelled);
            Assert.Equal(1, compensator.OwnTravelUnmeasured);
        }

        [Fact]
        public void TheOcclusionQueryCarriesBothTravels()
        {
            // The engine judges a hull where the shooter saw it by moving the segment by its
            // crew's travel; the resolver is the only side that knows either number.
            PassengerShotAtDriver(ridesAsPassenger: true, out _, out OcclusionQuery asked);

            float carTravel = CarSpeed * LagCompensator.RewindTicks(PassengerRttMs)
                              * ProtocolConstants.MS_PER_TICK / 1000f;

            Assert.Equal(Crew, asked.VictimActorId);
            Assert.Equal(Shooter, asked.ShooterActorId);
            Assert.InRange(asked.VictimTravel.Z, carTravel - 0.001f, carTravel + 0.001f);
            Assert.InRange(asked.ShooterTravel.Z, carTravel - 0.001f, carTravel + 0.001f);
        }

        [Fact]
        public void AFallbackPoseReportsNoVictimTravel()
        {
            var compensator = new LagCompensator(new HitboxHistory());
            OcclusionQuery asked = default;
            compensator.Occlusion = q => { asked = q; return false; };

            HitResult hit = compensator.ResolveHitscan(
                new[] { new HitscanTarget(Crew, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 10f))) },
                Shooter, new Vec3(0f, 1.5f, 0f), new Vec3(0f, 0f, 1f), 100f, 150f, 300);

            Assert.True(hit.UsedPresentFallback);
            Assert.Equal(Vec3.Zero, asked.VictimTravel);
            Assert.Equal(Vec3.Zero, asked.ShooterTravel);
        }

        // ------------------------------------------------------------------ helpers

        private const float CarSpeed = 15f;
        private const float PassengerRttMs = 100f;

        private static int PassengerShotAtDriver(bool ridesAsPassenger, out LagCompensator compensator)
            => PassengerShotAtDriver(ridesAsPassenger, out compensator, out _);

        /// <summary>
        /// A car doing <see cref="CarSpeed"/> along +Z with the shooter in the back seat and the
        /// driver 1.2 m ahead, both with the history the server captured. One round straight
        /// ahead at <see cref="PassengerRttMs"/>; returns how many connected.
        /// </summary>
        /// <remarks>
        /// Straight ahead at the height of the middle of the driver's torso, so the round hits
        /// from anywhere behind the driver and misses from anywhere in front: the test measures
        /// where the origin was put, and nothing about aim.
        /// </remarks>
        private static int PassengerShotAtDriver(
            bool ridesAsPassenger, out LagCompensator compensator, out OcclusionQuery asked)
        {
            const uint now = 300;
            var driverOffset = new Vec3(0f, 0f, 1.2f);

            var history = new HitboxHistory();
            for (uint tick = now - 20; tick <= now; tick++)
            {
                Vec3 car = CarAt(tick);
                history.Capture(tick, Shooter, SeatedAt(car));
                history.Capture(tick, Crew, SeatedAt(car + driverOffset));
            }

            HitscanTarget[] targets =
            {
                new HitscanTarget(Shooter, true, SeatedAt(CarAt(now))),
                new HitscanTarget(Crew, true, SeatedAt(CarAt(now) + driverOffset)),
            };

            Vec3 presentEye = CarAt(now) + new Vec3(0f, SeatedAt(Vec3.Zero).Torso.Center.Y, 0f);

            OcclusionQuery seen = default;
            compensator = new LagCompensator(history)
            {
                Occlusion = q => { seen = q; return false; },
            };

            var authority = new ServerCombatAuthority(
                new ServerFireResolver(compensator, seed: 7), new NullDamageSink())
            {
                SeatedEye = actorId => actorId == Shooter ? presentEye : (Vec3?)null,
                RidesAsPassenger = actorId => ridesAsPassenger && actorId == Shooter,
            };

            WeaponConfig config = WeaponConfig.Rifle;
            WeaponRuntimeState weapon = WeaponRuntimeState.Loaded(in config);
            EffectiveTrigger trigger = EffectiveTrigger.Idle;
            ActorAmmoSource ammo = ActorAmmoSource.Unlimited(Shooter);
            ActorFireEligibility actor = ActorFireEligibility.OnFoot(isAlive: true);
            MoveState state = MoveState.AtRest(CarAt(now));
            InputFrame fire = InputFrame.FromFloats(0f, 0f, 0f, 0f, InputButtons.Fire);
            var hits = new HitResult[1];

            CombatTickResult result = authority.Step(
                ref weapon, ref trigger, in config, Shooter, in fire, in state, targets,
                in actor, in ammo, 10f, PassengerRttMs, now, hits);

            Assert.True(result.Fired, $"the round was refused: {result.Rejection}");

            asked = seen;
            return result.HitCount;
        }

        private static Vec3 CarAt(uint tick)
            => new Vec3(0f, 0f, CarSpeed * tick * ProtocolConstants.MS_PER_TICK / 1000f);

        private static HitboxSet SeatedAt(Vec3 seat)
            => HitboxSet.Humanoid(in seat, 0f, HumanoidPose.Seated, 0f, 0f);

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
