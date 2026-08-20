using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Unity.Client;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// debt-closure phase 3C — the § 5 pins for debt-ledger row <b>X-3</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What X-3 actually was.</b> Not a wire-format gap. <c>InputButtons</c> has declared
    /// <c>Fire</c>, <c>Aim</c> and <c>Reload</c> since phase-01; <c>ServerCombatAuthority</c>
    /// and <c>MountedWeaponAuthority</c> have read them since phase-05. The client's mask
    /// builder knew Jump, Sprint and Crouch and nothing else, so three declared bits with a
    /// live reader on the far end were permanently zero — and <c>C_ACK_BASELINE</c>, whose
    /// writer and parser both shipped, had no sender at all, which means the delta encoder has
    /// never once produced a delta against a real client.
    /// </para>
    /// <para>
    /// <b>Why half of this file is a source scan.</b> The reachable half of the path
    /// (<see cref="MoveInput.ToButtons"/>, <see cref="BaselineAckPolicy"/>,
    /// <see cref="ServerMessageRouter"/>) can be executed here. The other half —
    /// <c>ClientPredictionStage</c> and <c>NetClientBootstrap</c> — is Unity source that no
    /// gate in this repository compiles, and X-3 lived in exactly that half for four phases.
    /// Pinning only the reachable half would leave a green that proves nothing: every executable
    /// assertion below passed on the day the bug was found. So the Unity half is graded the way
    /// the client-wiring gate grades it, by Roslyn over the real files — comments and
    /// <c>#if</c>-disabled code do not count as a call.
    /// </para>
    /// </remarks>
    public sealed class ClientInputSenderTests
    {
        private const ushort Shooter = 1;
        private const ushort Victim = 2;

        // ------------------------------------------------------------------ § 5 pin 1

        /// <summary>
        /// <b>Pin 1</b> — a <see cref="MoveInput"/> with Fire set produces
        /// <see cref="InputButtons.Fire"/> on the wire. RED when the plumbing regresses.
        /// </summary>
        /// <remarks>
        /// Driven end to end through the real <c>C_INPUT</c> body codec rather than by reading
        /// <see cref="MoveInput.ToButtons"/> alone: the mask being right and the mask reaching
        /// the packet are two claims, and X-3 was a failure of the second.
        /// </remarks>
        [Fact]
        public void AMoveInputWithFireSetPutsTheFireBitOnTheWire()
        {
            InputFrame received = RoundTrip(Intent(fire: true));

            Assert.True(received.IsPressed(InputButtons.Fire));
            Assert.False(received.IsPressed(InputButtons.Aim));
            Assert.False(received.IsPressed(InputButtons.Reload));
        }

        /// <summary>
        /// The movement bits still travel. A "fix" that replaced the mask rather than extending
        /// it would pass pin 1 and silently stop the player jumping.
        /// </summary>
        [Fact]
        public void TheMovementBitsStillTravelAlongsideTheCombatBits()
        {
            var input = new MoveInput(
                0f, 0f, 0f, jump: true, sprint: true, crouch: true,
                fire: true, aim: true, reload: true);

            InputFrame received = RoundTrip(input);

            foreach (InputButtons bit in new[]
                     {
                         InputButtons.Jump, InputButtons.Sprint, InputButtons.Crouch,
                         InputButtons.Fire, InputButtons.Aim, InputButtons.Reload,
                     })
            {
                Assert.True(received.IsPressed(bit), $"{bit} did not survive the wire");
            }
        }

        // ------------------------------------------------------------------ § 5 pin 2

        /// <summary>
        /// <b>Pin 2</b> — a server receiving that frame fires the weapon. RED when the
        /// server-side read is bypassed.
        /// </summary>
        /// <remarks>
        /// The frame handed to the authority is the one that came off the wire in pin 1, not a
        /// hand-built <see cref="InputFrame"/>. A pin that constructs its own frame grades the
        /// server and says nothing about whether the client can reach it.
        /// </remarks>
        [Fact]
        public void AServerReceivingThatFrameFiresTheWeapon()
        {
            var fixture = new SenderCombatFixture();

            CombatTickResult result = fixture.Step(now: 10f, RoundTrip(Intent(fire: true)));

            Assert.True(result.Fired);
            Assert.Equal(1, result.HitCount);
            Assert.True(fixture.Sink.HealthOf(Victim) < 100f, "the shot did no damage");
        }

        /// <summary>
        /// The negative direction of pin 2. Without it, an authority that fired on every frame
        /// regardless of the mask would pass the test above.
        /// </summary>
        [Fact]
        public void AServerReceivingAFrameWithoutFireDoesNotFire()
        {
            var fixture = new SenderCombatFixture();

            Assert.False(fixture.Step(now: 10f, RoundTrip(Intent())).Fired);
        }

        // ------------------------------------------------------------------ § 5 pin 3

        /// <summary>
        /// <b>Pin 3</b> — Aim and Reload round-trip the same way. RED when one bit is wired and
        /// the others are forgotten.
        /// </summary>
        /// <remarks>
        /// Each bit is driven alone, so a mask that ORs the wrong constant in — the single most
        /// likely mistake in a six-line builder — cannot hide behind a sibling that is also set.
        /// </remarks>
        [Theory]
        [InlineData(InputButtons.Fire)]
        [InlineData(InputButtons.Aim)]
        [InlineData(InputButtons.Reload)]
        public void EachCombatBitTravelsAloneAndCarriesNoOther(InputButtons only)
        {
            MoveInput input = Intent(
                fire: only == InputButtons.Fire,
                aim: only == InputButtons.Aim,
                reload: only == InputButtons.Reload);

            InputFrame received = RoundTrip(input);

            Assert.Equal(only, received.Buttons);
        }

        /// <summary>
        /// The dequantize half of pin 3: <see cref="MoveInput.FromFrame"/> reads back what
        /// <see cref="MoveInput.ToButtons"/> wrote.
        /// </summary>
        /// <remarks>
        /// Load-bearing rather than tidy. The server replays a client's frames through
        /// <c>InputAuthority.TryAccept</c>, which converts with <see cref="MoveInput.FromFrame"/>
        /// — so a bit written on the client and dropped on the way back is a frame the two sides
        /// disagree about, and the symptom is a reconciliation that never converges rather than
        /// anything that looks like a missing button.
        /// </remarks>
        [Fact]
        public void TheDequantizePathCarriesTheCombatBitsBack()
        {
            MoveInput sent = Intent(fire: true, aim: true, reload: true);
            MoveInput back = MoveInput.FromFrame(RoundTrip(sent));

            Assert.True(back.Fire);
            Assert.True(back.Aim);
            Assert.True(back.Reload);

            MoveInput empty = MoveInput.FromFrame(RoundTrip(Intent()));

            Assert.False(empty.Fire);
            Assert.False(empty.Aim);
            Assert.False(empty.Reload);
        }

        /// <summary>
        /// <see cref="MoveInput.WithAxes"/> is what the server's speed clamp calls. Dropping the
        /// combat bits there would disarm any client the anti-cheat had to slow down.
        /// </summary>
        [Fact]
        public void ClampingTheMovementAxesKeepsTheCombatBits()
        {
            MoveInput clamped = new MoveInput(
                    1f, 1f, 0f, false, false, false,
                    fire: true, aim: true, reload: true)
                .WithAxes(0.7f, 0.7f);

            Assert.True(clamped.Fire);
            Assert.True(clamped.Aim);
            Assert.True(clamped.Reload);
        }

        // ------------------------------------------------------------------ § 5 pin 4

        /// <summary>
        /// <b>Pin 4</b> — the client sends <c>C_ACK_BASELINE</c> and the server parses it. RED
        /// when the sender is dropped.
        /// </summary>
        /// <remarks>
        /// The payload is the one <see cref="BaselineAckPolicy"/> actually builds, routed
        /// through the real <see cref="ServerMessageRouter"/>. That is the whole claim: before
        /// this phase the two halves existed and had never been introduced.
        /// </remarks>
        [Fact]
        public void TheClientSendsAckBaselineAndTheServerParsesIt()
        {
            var policy = new BaselineAckPolicy();
            var router = new ServerMessageRouter();
            var session = new ClientSession(connectionId: 7, actorId: 3);

            Assert.True(policy.TryBuildAck(512u, out ReadOnlySpan<byte> payload));
            Assert.Equal(1, router.Route(payload, session));

            Assert.Equal(1, router.AcksApplied);
            Assert.Equal(0, router.MalformedMessages);
            Assert.Equal(512u, session.Encoder.AckedBaselineTick);
            Assert.Equal(512u, session.VehicleEncoder.AckedBaselineTick);
            Assert.Equal(BaselineAckPolicy.Channel, ChannelId.ReliableOrdered);
        }

        /// <summary>
        /// The ack is what turns a full snapshot into a delta. Pin 4's reason for existing,
        /// asserted rather than assumed.
        /// </summary>
        [Fact]
        public void WithoutAnAckEverySnapshotIsFullAndWithOneItIsNot()
        {
            var withoutAck = new DeltaEncoder();
            var withAck = new DeltaEncoder();
            var buffer = new byte[ProtocolConstants.MAX_PAYLOAD];

            for (uint tick = 100; tick <= 102; tick++)
            {
                withoutAck.Write(buffer, Snapshot(tick), lastProcessedInputTick: tick);

                withAck.Write(buffer, Snapshot(tick), lastProcessedInputTick: tick);

                // What the client now does on every applied snapshot.
                var policy = new BaselineAckPolicy();
                if (policy.TryBuildAck(tick, out ReadOnlySpan<byte> _)) withAck.OnClientAck(tick);
            }

            Assert.Equal(0, withoutAck.DeltaSnapshotCount);
            Assert.True(withAck.DeltaSnapshotCount > 0, "an acked baseline produced no delta");
        }

        /// <summary>
        /// Tick 0 means "no snapshot applied yet". Sending it would be discarded by
        /// <see cref="DeltaEncoder.OnClientAck"/> in silence.
        /// </summary>
        [Fact]
        public void NoAckIsOwedBeforeTheFirstSnapshotLands()
        {
            var policy = new BaselineAckPolicy();

            Assert.False(policy.TryBuildAck(0u, out ReadOnlySpan<byte> _));
            Assert.Equal(0, policy.AcksSent);
        }

        /// <summary>
        /// A repeated or reordered tick buys nothing, so it is not sent. Reliable-ordered
        /// traffic at 30 Hz is not free.
        /// </summary>
        [Fact]
        public void OnlyANewerBaselineIsAcked()
        {
            var policy = new BaselineAckPolicy();

            Assert.True(policy.TryBuildAck(500u, out ReadOnlySpan<byte> _));
            Assert.False(policy.TryBuildAck(500u, out ReadOnlySpan<byte> _));
            Assert.False(policy.TryBuildAck(499u, out ReadOnlySpan<byte> _));
            Assert.True(policy.TryBuildAck(501u, out ReadOnlySpan<byte> _));

            Assert.Equal(2, policy.AcksSent);
            Assert.Equal(501u, policy.LastAckedTick);
        }

        /// <summary>
        /// The wrap case. At 30 Hz a <c>u32</c> tick wraps after 4.5 years, and a raw comparison
        /// would suppress every ack for the rest of that session.
        /// </summary>
        [Fact]
        public void AWrappedTickIsStillNewer()
        {
            var policy = new BaselineAckPolicy();

            Assert.True(policy.TryBuildAck(uint.MaxValue - 1, out ReadOnlySpan<byte> _));
            Assert.True(policy.TryBuildAck(3u, out ReadOnlySpan<byte> _));
            Assert.Equal(3u, policy.LastAckedTick);
        }

        /// <summary>
        /// Reconnecting must not inherit the previous session's tick — the server resets its
        /// encoder on the same event, so a retained tick suppresses every early ack of the next
        /// connection and the stream silently reverts to full snapshots.
        /// </summary>
        [Fact]
        public void ResetLetsTheNextConnectionAckFromScratch()
        {
            var policy = new BaselineAckPolicy();
            policy.TryBuildAck(9_000u, out ReadOnlySpan<byte> _);

            policy.Reset();

            Assert.Equal(0u, policy.LastAckedTick);
            Assert.True(policy.TryBuildAck(5u, out ReadOnlySpan<byte> _));
        }

        // ------------------------------------------------------ the Unity half, by source scan

        /// <summary>
        /// <c>ClientPredictionStage</c> no longer builds its own button mask. That private
        /// duplicate WAS row X-3: it OR'd Jump, Sprint and Crouch and had never heard of Fire.
        /// </summary>
        /// <remarks>
        /// Named bits rather than "calls ToButtons", because the failure to catch is a SECOND
        /// mask appearing, whatever it is spelled. A file that mentions no <c>InputButtons</c>
        /// constant cannot be assembling one.
        /// </remarks>
        [Fact]
        public void TheClientPredictionStageBuildsNoButtonMaskOfItsOwn()
        {
            SyntaxNode stage = UnitySource("Net/Client/ClientPredictionStage.cs");

            string[] assembled = stage.DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Where(m => m.Expression.ToString() == "InputButtons")
                .Select(m => m.Name.Identifier.ValueText)
                .Where(n => n != "None")
                .ToArray();

            Assert.True(
                assembled.Length == 0,
                "ClientPredictionStage names InputButtons." + string.Join(", InputButtons.", assembled)
                + " — a second mask builder. MoveInput.ToButtons is the only one; that is what "
                + "stops debt row X-3 recurring.");
        }

        /// <summary>
        /// The shared conversion asks <see cref="MoveInput.ToButtons"/> for the mask.
        /// </summary>
        [Fact]
        public void TheSharedFrameConversionAsksMoveInputForTheMask()
        {
            Assert.Contains(
                "ToButtons",
                InvokedNames(UnitySource("Net/Shared/MovementSimulation.cs")));
        }

        /// <summary>
        /// The tick loop asks the installed combat seam for this tick's buttons, and passes them
        /// into the frame it builds.
        /// </summary>
        /// <remarks>
        /// This is what a scripted Lane B client drives: it calls
        /// <c>FpsActorController.SetInputSource</c> and the delegate installed below reads the
        /// replacement live, so no second input path exists to keep in step.
        /// </remarks>
        [Fact]
        public void TheTickLoopReadsTheInstalledCombatSeam()
        {
            InvocationExpressionSyntax fromUnityInput =
                UnitySource("Net/Shared/NetPredictionClock.cs")
                    .DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Single(i => i.Expression is MemberAccessExpressionSyntax m
                                 && m.Name.Identifier.ValueText == "FromUnityInput");

            Assert.Equal(2, fromUnityInput.ArgumentList.Arguments.Count);
            Assert.Contains(
                "CombatButtonSource",
                fromUnityInput.ArgumentList.Arguments[1].ToString());
        }

        /// <summary>
        /// The controller installs that seam. Without this the delegates stay null, the clock
        /// reports "nothing pressed" forever, and every executable pin above still passes.
        /// </summary>
        /// <remarks>
        /// Assembly-CSharp installs into <c>Ironfront.Net.Unity.Shared</c> and never the other
        /// way round: Shared declares no references and is what the dedicated server builds on,
        /// so it cannot name <c>IInputSource</c> at all.
        /// </remarks>
        [Fact]
        public void TheControllerInstallsTheCombatSeamOnTheClock()
        {
            AssignmentExpressionSyntax[] installs =
                UnitySource("Assembly-CSharp/FpsActorController.cs")
                    .DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Where(a => a.Left.ToString().EndsWith("CombatButtonSource", StringComparison.Ordinal)
                                || a.Left.ToString().EndsWith("AimPitchSource", StringComparison.Ordinal))
                    .ToArray();

            Assert.Equal(2, installs.Length);
            Assert.Contains(installs, a => a.Right.ToString().Contains("Buttons", StringComparison.Ordinal));
            Assert.Contains(installs, a => a.Right.ToString().Contains("Pitch", StringComparison.Ordinal));

            // From the FIELD, so a later SetInputSource is picked up with no re-install. A
            // closure over a local copy compiles, passes the two assertions above, and freezes
            // a scripted client's intent at whatever it was during Awake.
            Assert.DoesNotContain(installs, a => a.Right.ToString().Contains("NullInputSource", StringComparison.Ordinal));
        }

        /// <summary>
        /// The frame the client sends carries a real pitch.
        /// </summary>
        /// <remarks>
        /// It used to hard-code <c>0f</c>. <c>ServerCombatAuthority.AimDirection</c> and
        /// <c>ShotOrigin</c> both read it, so every shot a networked client fired went out
        /// perfectly level — the trigger could work and the bullet still never arrive. A
        /// literal in that argument position is the regression; anything else is a read.
        /// </remarks>
        [Fact]
        public void TheClientSendsARealPitchAndNotAHardCodedZero()
        {
            // The QUALIFIED call only. The file also calls its own private ToFrame(in input),
            // which is an unqualified identifier and takes no pitch argument at all.
            InvocationExpressionSyntax toFrame = UnitySource("Net/Client/ClientPredictionStage.cs")
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Single(i => i.Expression is MemberAccessExpressionSyntax member
                             && member.Name.Identifier.ValueText == "ToFrame");

            ArgumentSyntax pitch = toFrame.ArgumentList.Arguments[1];

            Assert.False(
                pitch.Expression is LiteralExpressionSyntax,
                $"ClientPredictionStage sends a constant pitch ({pitch}). The server aims with "
                + "it; a constant means every networked shot is level.");
        }

        /// <summary>
        /// The tick loop samples the aim pitch, so the value the sender reads is a real one.
        /// </summary>
        /// <remarks>
        /// Without this the pin above still passes: <c>AimPitchDegrees</c> would be a property
        /// nobody ever writes, the sender would faithfully forward its default 0, and every
        /// networked shot would be level again — the exact regression, with the gate green.
        /// </remarks>
        [Fact]
        public void TheTickLoopSamplesTheAimPitch()
        {
            bool sampled = UnitySource("Net/Shared/NetPredictionClock.cs")
                .DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left.ToString().EndsWith("AimPitchDegrees", StringComparison.Ordinal)
                          && a.Right.ToString().Contains("AimPitchSource", StringComparison.Ordinal));

            Assert.True(sampled, "NetPredictionClock never assigns AimPitchDegrees from the "
                                 + "installed aim source, so the sender forwards a default 0 and "
                                 + "every shot is level.");
        }

        /// <summary>
        /// <c>NetClientBootstrap</c> actually calls the ack policy on an applied snapshot.
        /// </summary>
        /// <remarks>
        /// The <c>wired-not-just-present</c> half of pin 4. <see cref="BaselineAckPolicy"/>
        /// existing and being exercised by this file proves only that it works — which is
        /// exactly what was true of <c>AckBaselineMessage</c> for four phases while nothing
        /// called it.
        /// </remarks>
        [Fact]
        public void TheBootstrapSendsTheAckWhenASnapshotIsApplied()
        {
            SyntaxNode bootstrap = UnitySource("Net/Client/NetClientBootstrap.cs");

            Assert.Contains("TryBuildAck", InvokedNames(bootstrap));

            bool subscribes = bootstrap.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Any(a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                          && a.Left.ToString().EndsWith("OnSnapshotApplied", StringComparison.Ordinal));

            Assert.True(subscribes, "NetClientBootstrap does not subscribe OnSnapshotApplied, so "
                                    + "the ack policy it holds is never asked for an ack.");
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Combat intent with no movement, so a pin reads as what it is testing.</summary>
        private static MoveInput Intent(bool fire = false, bool aim = false, bool reload = false)
            => new MoveInput(0f, 0f, 0f, false, false, false, fire, aim, reload);

        /// <summary>
        /// Quantizes intent the way the client does, frames it as a <c>C_INPUT</c> body, and
        /// reads it back out.
        /// </summary>
        /// <remarks>
        /// <b>Deliberately not a call into the Unity sender</b>, which no gate here compiles.
        /// The mask comes from <see cref="MoveInput.ToButtons"/> — the same method the Unity
        /// side is pinned above to call and to be the only caller of — so this grades the real
        /// path rather than a re-implementation of it.
        /// </remarks>
        private static InputFrame RoundTrip(in MoveInput input)
        {
            InputFrame sent = InputFrame.FromFloats(
                input.MoveX, input.MoveZ, input.YawDegrees, 0f, input.ToButtons());

            var body = new byte[ClientInputMessage.SizeFor(1)];
            Assert.Equal(body.Length, ClientInputMessage.Write(body, 1u, new[] { sent }));

            var frames = new InputFrame[1];
            Assert.True(ClientInputMessage.TryParse(body, frames, out uint _, out int count));
            Assert.Equal(1, count);

            return frames[0];
        }

        private static WorldSnapshot Snapshot(uint tick)
        {
            var snapshot = new WorldSnapshot();
            snapshot.ServerTick = tick;
            return snapshot;
        }

        private static ISet<string> InvokedNames(SyntaxNode root)
            => root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(i => i.Expression is MemberAccessExpressionSyntax member
                    ? member.Name.Identifier.ValueText
                    : i.Expression.ToString())
                .ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// Parses one Unity source file, relative to <c>Assets/Scripts</c>.
        /// </summary>
        /// <remarks>
        /// A missing file FAILS rather than reporting an empty scan. Same rule the writer
        /// coverage gate states: a check that looked at nothing has proved nothing, and from
        /// the wrong working directory it would report green forever.
        /// </remarks>
        private static SyntaxNode UnitySource(string relativePath)
        {
            string root = RepoRoot();
            string path = Path.Combine(
                root, "Ironfront_Reborn", "Assets", "Scripts",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing Unity source: {path}");

            return CSharpSyntaxTree
                .ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp9))
                .GetRoot();
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

        /// <summary>
        /// The minimum around <see cref="ServerCombatAuthority"/> that lets a real wire frame be
        /// fired. A trimmed sibling of <c>ServerCombatAuthorityTests.CombatFixture</c>, which is
        /// private to that file.
        /// </summary>
        private sealed class SenderCombatFixture
        {
            private readonly WeaponConfig _config = WeaponConfig.Rifle;
            private readonly HitscanTarget[] _targets;
            private readonly HitResult[] _hits;

            public SenderCombatFixture()
            {
                Sink = new SenderDamageSink();
                Authority = new ServerCombatAuthority(
                    new ServerFireResolver(new LagCompensator(new HitboxHistory()), seed: 7),
                    Sink,
                    new ServerRespawnGate());

                Weapon = WeaponRuntimeState.Loaded(in _config);
                State = MoveState.AtRest(Vec3.Zero);
                _hits = new HitResult[Math.Max(1, _config.ProjectilesPerShot)];

                Sink.SetHealth(Victim, 100f);

                _targets = new[]
                {
                    new HitscanTarget(Victim, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 10f))),
                };
            }

            public SenderDamageSink Sink { get; }
            public ServerCombatAuthority Authority { get; }

            public WeaponRuntimeState Weapon;
            public MoveState State;

            public CombatTickResult Step(float now, in InputFrame frame)
                => Authority.Step(
                    ref Weapon, in _config, Shooter, in frame, in State,
                    _targets, shooterIsAlive: true, now, smoothedRttMs: 0f,
                    currentTick: (uint)(now * ProtocolConstants.SIM_TICK_RATE), _hits);

        }

        /// <summary>Health in a dictionary. Enough for "did the shot land"; nothing more.</summary>
        private sealed class SenderDamageSink : IActorDamageSink
        {
            private readonly Dictionary<ushort, float> _health = new Dictionary<ushort, float>();

            public void SetHealth(ushort actorId, float health) => _health[actorId] = health;

            public float HealthOf(ushort actorId)
                => _health.TryGetValue(actorId, out float health) ? health : 0f;

            public DamageOutcome ApplyDamage(
                ushort victimId, float healthDamage, float balanceDamage, ushort attackerId)
            {
                if (!_health.TryGetValue(victimId, out float health)) return DamageOutcome.NoOp;

                float remaining = Math.Max(0f, health - healthDamage);
                _health[victimId] = remaining;

                return new DamageOutcome(remaining, died: remaining <= 0f);
            }

            public float ApplyHeal(ushort actorId, float amount) => 0f;
        }
    }
}
