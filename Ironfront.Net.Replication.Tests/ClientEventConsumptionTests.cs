using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// phase-V10 task 12 — the engine-free half of the client event layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Almost every deliverable of V10 is a cosmetic, and CI has no Unity Editor. What is graded
    /// here is the part that decides <i>what</i> the cosmetic should be: the decode, the
    /// severity ordering, the suppression window, the timer rule. What is not graded is whether
    /// anything appears on screen — that is the client-track E-list, enumerated with pass
    /// conditions rather than handed over as a category.
    /// </para>
    /// <para>
    /// Several of these pin a <b>failure</b> that would otherwise be silent and would not
    /// reproduce on a clean network: the wrong quantiser width, a swapped victim/killer pair, a
    /// prediction that never expires. Those are the reason the file exists.
    /// </para>
    /// </remarks>
    public sealed class ClientEventConsumptionTests
    {
        // ------------------------------------------------------------------ identity (D2)

        [Fact]
        public void IsLocalActorMatchesOnlyTheBootstrapActorId()
        {
            Assert.True(LocalActorIdentity.IsLocalActorId(7, 7));
            Assert.False(LocalActorIdentity.IsLocalActorId(7, 8));
        }

        [Fact]
        public void NoActorIsLocalBeforeTheServerAssignsOne()
        {
            // LocalActorId is 0 until the welcome message lands and again after a disconnect.
            // A naive equality check would call every message about actor 0 "ours" in that
            // window, and route a stranger's death into the local respawn path.
            Assert.False(LocalActorIdentity.IsLocalActorId(LocalActorIdentity.UnassignedActorId, 0));
        }

        [Theory]
        [InlineData(true)]   // an AI actor
        [InlineData(false)]  // the player
        public void OfflineLocalActorGatingMatchesAiControlled(bool aiControlled)
        {
            // Task 3's safety proof. Offline, the new predicate must return EXACTLY what
            // `!aiControlled` returned, whatever the rig says — otherwise the mechanical gating
            // of eight singleton touches is a behaviour change to single-player, not a fix.
            Assert.Equal(
                !aiControlled,
                LocalActorIdentity.IsLocalActor(isOffline: true, aiControlled, isLocalPlayerRig: false));

            Assert.Equal(
                !aiControlled,
                LocalActorIdentity.IsLocalActor(isOffline: true, aiControlled, isLocalPlayerRig: true));
        }

        [Fact]
        public void OnAClientTheRigDecidesIdentityAndAiControlledIsIgnored()
        {
            // The whole point of D2: a remote HUMAN has aiControlled == false, and must still
            // not be treated as the local player.
            Assert.False(LocalActorIdentity.IsLocalActor(
                isOffline: false, aiControlled: false, isLocalPlayerRig: false));

            Assert.True(LocalActorIdentity.IsLocalActor(
                isOffline: false, aiControlled: false, isLocalPlayerRig: true));
        }

        [Fact]
        public void OnAServerNoActorIsLocal()
        {
            // There is no FpsActorController on a headless build, so the rig flag is false for
            // everything — which is what keeps IngameUi out of the server's per-actor paths.
            Assert.False(LocalActorIdentity.IsLocalActor(
                isOffline: false, aiControlled: true, isLocalPlayerRig: false));
        }

        // ------------------------------------------------------------------ death (task 5)

        [Fact]
        public void ADeathMessageProducesOneKillfeedLineAndOneImpulse()
        {
            DeathMessage message = Death(victim: 11, killer: 4, forceMetresPerSecond: 30f);

            var feed = new KillfeedModel();
            feed.Push(in message, nowSeconds: 1f);
            DeathImpulse impulse = DeathImpulse.From(in message);

            // D19's fork: one message, two consumers, neither shipped type changed.
            Assert.Equal(1, feed.Count);
            Assert.Equal(11, feed[0].VictimActorId);
            Assert.Equal(4, feed[0].KillerActorId);

            Assert.Equal(11, impulse.VictimActorId);
            Assert.Equal(4, impulse.KillerActorId);
            Assert.True(impulse.Force.SqrMagnitude > 0f);
        }

        [Fact]
        public void TheDeathForceUnpacksThroughVel16NotVel8()
        {
            // Trap 1, and the one that would be invisible in play: the i8 slot saturates at
            // VEL_MAX (64 m/s), so decoding a 200 m/s rocket impulse through UnpackVel would
            // clamp it and make every weapon's kill look identical.
            const float sent = 200f;
            Assert.True(sent > Quantize.VEL_MAX);

            DeathMessage message = Death(victim: 1, killer: 2, forceMetresPerSecond: sent);
            DeathImpulse impulse = DeathImpulse.From(in message);

            Assert.Equal(sent, impulse.Force.X, 0);

            // And the failure this guards against, spelled out: the narrow form clamps.
            float clamped = Quantize.UnpackVel(Quantize.PackVel(sent));
            Assert.True(Math.Abs(clamped) <= Quantize.VEL_MAX);
            Assert.True(Math.Abs(impulse.Force.X - clamped) > 100f);
        }

        [Fact]
        public void TheKillfeedEntryArgumentOrderIsVictimKillerCorrect()
        {
            // Trap 2. DeathMessage is victim-first; KillfeedEntry's ctor is killer-first. The
            // swap compiles, and shows up as every killfeed line naming the wrong two people.
            DeathMessage message = Death(victim: 42, killer: 9, forceMetresPerSecond: 5f);
            KillfeedEntry entry = KillfeedEntry.From(in message, nowSeconds: 0f);

            Assert.Equal(42, entry.VictimActorId);
            Assert.Equal(9, entry.KillerActorId);
        }

        [Fact]
        public void AnEnvironmentKillerResolvesToTheEnvironmentFlag()
        {
            // 0xFFFF, not "actor 65535".
            DeathMessage message = Death(
                victim: 3, killer: DeathMessage.EnvironmentKiller, forceMetresPerSecond: 12f);

            Assert.True(DeathImpulse.From(in message).KilledByEnvironment);
            Assert.True(KillfeedEntry.From(in message, 0f).KilledByEnvironment);
        }

        [Fact]
        public void ALocalDeathIsLeftToClientCombatState()
        {
            // The local player's death state is ClientCombatState's, and V10 does not duplicate
            // it. The type ignores everybody else's death, which is what makes that split safe.
            var state = new ClientCombatState { LocalActorId = 5 };

            DeathMessage someoneElse = Death(victim: 6, killer: 1, forceMetresPerSecond: 1f);
            DeathMessage ours = Death(victim: 5, killer: 1, forceMetresPerSecond: 1f);

            Assert.False(state.ApplyDeath(in someoneElse, nowSeconds: 0f));
            Assert.True(state.ApplyDeath(in ours, nowSeconds: 0f));
            Assert.False(state.IsAlive);
        }

        // ------------------------------------------------------------- weapon fire (task 6)

        [Fact]
        public void AWeaponFireMessageDecodesToAShotEvent()
        {
            var message = new WeaponFireMessage(
                shooterActorId: 13,
                weaponId: 2,
                Quantize.PackVel16(0f), Quantize.PackVel16(0f), Quantize.PackVel16(50f));

            ShotEvent shot = ShotEvent.From(in message);

            Assert.Equal(13, shot.ShooterActorId);
            Assert.Equal(2, shot.WeaponId);
            Assert.Equal(50f, shot.Direction.Z, 0);
        }

        [Fact]
        public void AnUnknownWeaponIdDoesNotThrow()
        {
            // Forward compatibility: a newer server may name a weapon this build has never
            // heard of. That costs the right model, never an exception on the transport pump.
            var message = new WeaponFireMessage(1, byte.MaxValue, 0, 0, 0);

            ShotEvent shot = ShotEvent.From(in message);
            Assert.Equal(byte.MaxValue, shot.WeaponId);
        }

        [Fact]
        public void TheCosmeticPathNeverAdvancesAMuzzleIndex()
        {
            // D9. Weapon fire rides the cosmetic channel and is documented safe to drop, so
            // nothing decoded from it may carry state. Decoding the same message twice must
            // produce the same value — if a counter had crept in, the second would differ, and
            // in play it would desynchronise permanently on the first dropped packet.
            var message = new WeaponFireMessage(4, 1, 100, 0, 0);

            ShotEvent first = ShotEvent.From(in message);
            ShotEvent second = ShotEvent.From(in message);

            Assert.Equal(first.ShooterActorId, second.ShooterActorId);
            Assert.Equal(first.WeaponId, second.WeaponId);
            Assert.Equal(first.Direction.X, second.Direction.X);

            // And structurally: every field is readonly, so there is no counter to advance even
            // if a later author reached for one.
            foreach (System.Reflection.FieldInfo field in typeof(ShotEvent).GetFields(
                         System.Reflection.BindingFlags.Public
                         | System.Reflection.BindingFlags.NonPublic
                         | System.Reflection.BindingFlags.Instance
                         | System.Reflection.BindingFlags.Static
                         | System.Reflection.BindingFlags.DeclaredOnly))
            {
                Assert.True(field.IsInitOnly || field.IsLiteral, field.Name + " is mutable");
            }
        }

        // -------------------------------------------------------------- hitmarker (task 6)

        [Fact]
        public void AHitConfirmRaisesTheMarkerAndTheNewestHitWins()
        {
            var model = new HitmarkerModel();

            model.Push(Hit(target: 3, killed: true, headshot: false), atTick: 1, nowSeconds: 0f);
            model.Push(Hit(target: 3, killed: false, headshot: false), atTick: 2, nowSeconds: 0.01f);

            // Not a high-water mark: an automatic weapon confirms a hit every tenth of a second,
            // and freezing a kill marker over a target who is still alive is the bug.
            Assert.Equal(HitmarkerSeverity.Normal, model.Current.Severity);
            Assert.True(model.IsVisible(0.02f));
            Assert.False(model.IsVisible(0.01f + HitmarkerModel.DefaultDisplaySeconds));
        }

        [Fact]
        public void AKillHitmarkerOutranksAHeadshot()
        {
            // The int this maps to is what IngameUi.Hit(int) receives, so the order is load-bearing.
            Assert.Equal(HitmarkerSeverity.Kill, HitmarkerEvent.SeverityOf(killed: true, headshot: true));
            Assert.Equal(HitmarkerSeverity.Headshot, HitmarkerEvent.SeverityOf(false, true));
            Assert.Equal(HitmarkerSeverity.Normal, HitmarkerEvent.SeverityOf(false, false));

            Assert.Equal(0, (int)HitmarkerSeverity.Normal);
            Assert.Equal(1, (int)HitmarkerSeverity.Headshot);
            Assert.Equal(2, (int)HitmarkerSeverity.Kill);
        }

        // ------------------------------------------------------------- match state (task 7)

        [Fact]
        public void AMatchStateMessageAppliesEveryField()
        {
            var model = new MatchStateModel();
            var message = new MatchStateMessage(MatchPhase.Warmup, 300, 250, 45, 12);

            model.Apply(in message, nowSeconds: 100f);

            Assert.True(model.HasState);
            Assert.Equal(MatchPhase.Warmup, model.Current.Phase);
            Assert.Equal(300, model.Current.Tickets0);
            Assert.Equal(250, model.Current.Tickets1);
            Assert.Equal(45, model.Current.PhaseSecondsRemaining);
            Assert.Equal(12, model.Current.HumanPlayerCount);
        }

        [Fact]
        public void ThePlayingPhaseRendersNoTimer()
        {
            // PhaseSecondsRemaining is 0 during Playing by design — that phase ends on tickets.
            // A HUD that renders the field unconditionally shows "0:00" for the whole round and
            // tells every player it is already over.
            var model = new MatchStateModel();
            model.Apply(new MatchStateMessage(MatchPhase.Playing, 300, 300, 0, 8), nowSeconds: 0f);

            Assert.False(model.HasTimer);
            Assert.Equal(0f, model.SecondsRemaining(5f));
        }

        [Fact]
        public void ThePhaseTimerInterpolatesOutsidePlaying()
        {
            // The message arrives at most once a second. A timer that only moves when a packet
            // lands reads as a stutter, not as a clock.
            var model = new MatchStateModel();
            model.Apply(new MatchStateMessage(MatchPhase.Warmup, 0, 0, 30, 2), nowSeconds: 10f);

            Assert.True(model.HasTimer);
            Assert.Equal(30f, model.SecondsRemaining(10f), 3);
            Assert.Equal(29.5f, model.SecondsRemaining(10.5f), 3);
            Assert.Equal(0f, model.SecondsRemaining(999f));
        }

        [Fact]
        public void AStaleMatchStateIsReportedStaleNotZero()
        {
            var model = new MatchStateModel();

            // Unknown must not render as good: before anything arrives, this is stale.
            Assert.True(model.IsStale(0f));

            model.Apply(new MatchStateMessage(MatchPhase.Warmup, 1, 1, 10, 1), nowSeconds: 0f);
            Assert.False(model.IsStale(1f));
            Assert.True(model.IsStale(MatchStateModel.DefaultStaleAfterSeconds));

            // And the number it still reports is the last real one, not a zero.
            Assert.Equal(1, model.Current.Tickets0);
        }

        [Fact]
        public void ATieResolvesToTeamIdNone()
        {
            var model = new MatchStateModel();
            model.Apply(new MatchStateMessage(MatchPhase.Ended, 120, 120, 0, 6), nowSeconds: 0f);

            // 255, not 2 — chosen so a client switching on 0/1 falls through rather than
            // rendering "nobody" as a third team.
            Assert.Equal(TeamId.None, model.WinningTeam);
            Assert.Equal(255, TeamId.None);

            model.Apply(new MatchStateMessage(MatchPhase.Ended, 120, 10, 0, 6), nowSeconds: 1f);
            Assert.Equal(TeamId.Team0, model.WinningTeam);
        }

        [Fact]
        public void AnUndecidedMatchHasNoWinner()
        {
            var model = new MatchStateModel();
            model.Apply(new MatchStateMessage(MatchPhase.Playing, 300, 10, 0, 6), nowSeconds: 0f);

            Assert.Equal(TeamId.None, model.WinningTeam);
        }

        // ------------------------------------------------------------ capture points (task 8)

        [Fact]
        public void ACapturePointMessageAppliesToTheView()
        {
            var view = new CapturePointView();

            Assert.True(view.Apply(new CapturePointMessage(2, -100, CaptureFlags.None)));
            Assert.True(view.IsKnown(2));
            Assert.Equal(TeamId.Team0, view.OwningTeam(2));
            Assert.Equal(1f, view.Control(2), 3);
        }

        [Fact]
        public void AnOwnedPointCanAlsoBeContested()
        {
            // Trap 5, and the reason Contested is the one genuinely new bit in the message:
            // it is NOT derivable from the ownership value.
            var view = new CapturePointView();
            view.Apply(new CapturePointMessage(0, 100, CaptureFlags.Contested));

            Assert.Equal(TeamId.Team1, view.OwningTeam(0));
            Assert.True(view.IsContested(0));
        }

        [Fact]
        public void ANeutralPointDoesNotResolveToTeamZero()
        {
            var view = new CapturePointView();
            view.Apply(new CapturePointMessage(1, 0, CaptureFlags.None));

            Assert.Equal(TeamId.None, view.OwningTeam(1));
            Assert.NotEqual(TeamId.Team0, view.OwningTeam(1));

            // An unreported point is also not team 0.
            Assert.Equal(TeamId.None, view.OwningTeam(63));
        }

        [Fact]
        public void TheViewMarksOnlyChangedPointsDirty()
        {
            var view = new CapturePointView();

            view.Apply(new CapturePointMessage(4, 50, CaptureFlags.None));
            Assert.True(view.DirtySinceLastRead(4));
            Assert.False(view.DirtySinceLastRead(4));

            // A 1 Hz rebroadcast of an unchanging point must cost no repaint.
            view.Apply(new CapturePointMessage(4, 50, CaptureFlags.None));
            Assert.False(view.DirtySinceLastRead(4));

            view.Apply(new CapturePointMessage(4, 50, CaptureFlags.Contested));
            Assert.True(view.DirtySinceLastRead(4));
        }

        [Fact]
        public void ControlUsesTheSameAbsMappingAsTheServer()
        {
            var view = new CapturePointView();
            view.Apply(new CapturePointMessage(0, -50, CaptureFlags.None));
            view.Apply(new CapturePointMessage(1, 50, CaptureFlags.None));

            // Half captured is half captured, whichever team is doing it.
            Assert.Equal(view.Control(0), view.Control(1), 3);
            Assert.Equal(0.5f, view.Control(0), 3);
        }

        // --------------------------------------------------------------- explosions (task 10)

        [Fact]
        public void AnOwnExplosionIsSuppressedOnce()
        {
            var suppressor = new ExplosionSuppressor();
            suppressor.PredictLocal(sourceActorId: 7, nowSeconds: 0f);

            ExplosionMessage confirmation = Explosion(source: 7);

            Assert.True(suppressor.ShouldSuppress(in confirmation, 0.1f));

            // One prediction swallows exactly one confirmation. A second blast from the same
            // player must still be drawn.
            Assert.False(suppressor.ShouldSuppress(in confirmation, 0.2f));
        }

        [Fact]
        public void ASuppressedPredictionExpiresAndDoesNotEatTheNextBlast()
        {
            // The failure this bounds: a grenade shot out of the air never produces an
            // S_EXPLOSION at all, so an entry held until confirmed would sit there forever and
            // swallow the NEXT real blast — a missing explosion, which is far worse than the
            // one-RTT delay the prediction was buying.
            var suppressor = new ExplosionSuppressor { SuppressionWindowSeconds = 1f };
            suppressor.PredictLocal(sourceActorId: 7, nowSeconds: 0f);

            ExplosionMessage later = Explosion(source: 7);

            Assert.False(suppressor.ShouldSuppress(in later, nowSeconds: 1.5f));
            Assert.Equal(0, suppressor.LiveCount(1.5f));
        }

        [Fact]
        public void AForeignExplosionIsNeverSuppressed()
        {
            var suppressor = new ExplosionSuppressor();
            suppressor.PredictLocal(sourceActorId: 7, nowSeconds: 0f);

            ExplosionMessage theirs = Explosion(source: 8);
            Assert.False(suppressor.ShouldSuppress(in theirs, 0.1f));
        }

        [Fact]
        public void AWorldSourcedExplosionIsNeverSuppressed()
        {
            // 0xFFFF is not a legal actor id, so it can never match a local one. Correct by
            // construction — this test exists so nobody adds a special case for it.
            var suppressor = new ExplosionSuppressor();
            suppressor.PredictLocal(DeathMessage.EnvironmentKiller, nowSeconds: 0f);

            ExplosionMessage world = Explosion(source: DeathMessage.EnvironmentKiller);

            Assert.Equal(0, suppressor.PredictedCount);
            Assert.False(suppressor.ShouldSuppress(in world, 0.1f));
        }

        // ----------------------------------------------------------------------- invariants

        [Fact]
        public void NoClientModelAllocatesOverAThousandEvents()
        {
            // conventions section 3.2: nothing on the hot path allocates. All five models are
            // exercised, because "no allocation" that was only ever measured on one of them is
            // a claim about that one.
            var feed = new KillfeedModel();
            var hits = new HitmarkerModel();
            var match = new MatchStateModel();
            var points = new CapturePointView();
            var suppressor = new ExplosionSuppressor();

            DeathMessage death = Death(1, 2, 10f);
            HitConfirmMessage hit = Hit(3, killed: false, headshot: true);
            var state = new MatchStateMessage(MatchPhase.Playing, 100, 100, 0, 4);
            var point = new CapturePointMessage(1, 25, CaptureFlags.None);
            ExplosionMessage boom = Explosion(source: 5);

            // Warm every path first: the first call through a code path JITs it, and that
            // allocation is the compiler's, not the model's.
            feed.Push(in death, 0f);
            feed.Prune(0f);
            hits.Push(in hit, 0, 0f);
            match.Apply(in state, 0f);
            points.Apply(in point);
            suppressor.PredictLocal(5, 0f);
            suppressor.ShouldSuppress(in boom, 0f);

            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 1000; i++)
            {
                float now = i * 0.01f;
                feed.Push(in death, now);
                feed.Prune(now);
                hits.Push(in hit, (uint)i, now);
                match.Apply(in state, now);
                match.SecondsRemaining(now);
                points.Apply(in point);
                points.DirtySinceLastRead(1);
                suppressor.PredictLocal(5, now);
                suppressor.ShouldSuppress(in boom, now);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        [Fact]
        public void NoHandlerThrowsOnAMalformedMessage()
        {
            // D22's premise: ClientMessageRouter.Route counts malformed input rather than
            // throwing, because an exception raised here propagates into the transport pump.
            var router = new ClientMessageRouter();

            router.Route(new byte[] { 0xFF });
            router.Route(new byte[] { (byte)ServerMessageType.Death, 0x01 });
            router.Route(new byte[] { (byte)ServerMessageType.Explosion });
            router.Route(ReadOnlySpan<byte>.Empty);

            Assert.True(router.MalformedMessages > 0);
        }

        // ------------------------------------------------------------------------- helpers

        private static DeathMessage Death(ushort victim, ushort killer, float forceMetresPerSecond)
            => new DeathMessage(
                victim, killer, CauseOfDeath.Bullet,
                Quantize.PackVel16(forceMetresPerSecond),
                Quantize.PackVel16(0f),
                Quantize.PackVel16(0f),
                (byte)HitboxType.Body);

        private static HitConfirmMessage Hit(ushort target, bool killed, bool headshot)
        {
            HitFlags flags = HitFlags.None;
            if (killed) flags |= HitFlags.Killed;
            if (headshot) flags |= HitFlags.Headshot;

            return new HitConfirmMessage(
                target, HitConfirmMessage.PackDamage(25f),
                headshot ? HitboxType.Head : HitboxType.Body, flags);
        }

        private static ExplosionMessage Explosion(ushort source)
            => new ExplosionMessage(
                source,
                Quantize.PackPos(10f), Quantize.PackPos(0f), Quantize.PackPos(10f),
                radiusMetres: 8, ExplosionKind.Grenade);
    }
}
