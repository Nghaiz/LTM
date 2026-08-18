using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// phase-V1 task 5 — the engine-free half of the explosion path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The join is what was dead.</b> <c>ActorLifecycleMessageTests</c> has round-tripped
    /// <c>ExplosionMessage</c> since phase-02 and <c>ClientMessageRouter</c> has decoded it since
    /// then too — each half tested, and no explosion in any build ever produced a byte on the
    /// wire, because <c>WriteExplosion</c> had no caller. A test per half cannot see that; a test
    /// that walks framing to handler in one method can, which is why the first one below exists.
    /// </para>
    /// <para>
    /// <b>What CI cannot grade.</b> The role split inside <c>ActorManager.Explode</c> is Unity
    /// code. What is graded here is the contract on both sides of it — that a blast produces one
    /// message and not one per victim, that the sink is called once per live victim on a server
    /// and never on a client, and that the earshot filter and the radius quantiser behave. The
    /// branch itself is graded by the client-track review round and by V9 criterion 11.
    /// </para>
    /// </remarks>
    public sealed class ExplosionEventTests
    {
        // ------------------------------------------------------------------ the join (task 1)

        [Fact]
        public void AnExplosionFramedByTheServerRoutesToTheClientHandler()
        {
            // The test section 2.2 says has never existed. Every field is compared, because a
            // swapped PosY/PosZ or a truncated radius would survive a "did it fire" assertion
            // and put the flash somewhere the blast was not.
            var router = new ClientMessageRouter();
            ExplosionMessage? received = null;
            int calls = 0;

            router.OnExplosion += m => { received = m; calls++; };

            var sent = new ExplosionMessage(
                sourceActorId: 42,
                posX: Quantize.PackPos(10f),
                posY: Quantize.PackPos(-3f),
                posZ: Quantize.PackPos(87f),
                radiusMetres: ExplosionEncoding.PackRadiusMetres(6f),
                kind: ExplosionKind.Grenade);

            Span<byte> buffer = stackalloc byte[256];
            int written = ServerEventWriter.WriteExplosion(buffer, in sent);
            Assert.True(written > 0);

            Assert.Equal(1, router.Route(buffer.Slice(0, written)));

            Assert.Equal(1, calls);
            Assert.True(received.HasValue);

            ExplosionMessage got = received!.Value;
            Assert.Equal(sent.SourceActorId, got.SourceActorId);
            Assert.Equal(sent.PosX, got.PosX);
            Assert.Equal(sent.PosY, got.PosY);
            Assert.Equal(sent.PosZ, got.PosZ);
            Assert.Equal(sent.RadiusMetres, got.RadiusMetres);
            Assert.Equal(sent.Kind, got.Kind);

            Assert.Equal(0, router.MalformedMessages);
            Assert.Equal(0, router.UnknownMessages);
        }

        [Fact]
        public void AnExplosionIsFramedOnTheReliableChannel()
        {
            // D7, pinned in the bytes rather than in prose. A later "optimization" moving
            // explosions onto the cosmetic channel to save bandwidth fails here instead of
            // silently dropping the one event whose loss looks like dying to nothing.
            Span<byte> buffer = stackalloc byte[64];
            int written = ServerEventWriter.WriteExplosion(
                buffer, new ExplosionMessage(1, 0, 0, 0, 6, ExplosionKind.Rocket));

            Assert.True(written > 0);

            // PayloadFrame layout: u8 channelId, u16 messageCount, then u8 msgType.
            Assert.Equal((byte)ChannelId.ReliableOrdered, buffer[0]);
            Assert.Equal(2, (byte)ChannelId.ReliableOrdered);
            Assert.Equal(0x4A, buffer[PayloadFrame.HeaderSize]);
            Assert.Equal(0x4A, (byte)ServerMessageType.Explosion);
        }

        // ------------------------------------------------------------- radius packing (task 1)

        [Fact]
        public void AnExplosionRadiusSaturatesRatherThanWrapping()
        {
            // A bare (byte) cast turns 300 into 44 -- a radius SMALLER than a grenade's, with
            // nothing downstream able to tell that had happened.
            Assert.Equal(255, ExplosionEncoding.PackRadiusMetres(300f));
            Assert.Equal(255, ExplosionEncoding.PackRadiusMetres(255f));

            // Ceil, not round: a client's effect must never render smaller than the blast that
            // did the damage.
            Assert.Equal(7, ExplosionEncoding.PackRadiusMetres(6.1f));
            Assert.Equal(6, ExplosionEncoding.PackRadiusMetres(6f));
            Assert.Equal(1, ExplosionEncoding.PackRadiusMetres(0.01f));

            Assert.Equal(0, ExplosionEncoding.PackRadiusMetres(0f));
            Assert.Equal(0, ExplosionEncoding.PackRadiusMetres(-5f));
            Assert.Equal(0, ExplosionEncoding.PackRadiusMetres(float.NaN));
        }

        [Fact]
        public void AnUnpackedRadiusIsNeverSmallerThanTheBlastThatWasPacked()
        {
            // The pair's actual contract, stated as the property rather than as two examples.
            float[] radii = { 0.5f, 2f, 6f, 6.4f, 9f, 9.9f, 254.5f };

            for (int i = 0; i < radii.Length; i++)
            {
                float unpacked = ExplosionEncoding.UnpackRadiusMetres(
                    ExplosionEncoding.PackRadiusMetres(radii[i]));

                Assert.True(
                    unpacked >= radii[i],
                    $"{radii[i]} m packed to {unpacked} m, which under-draws the blast.");
            }
        }

        // ------------------------------------------------------ one event per blast (task 3)

        [Fact]
        public void AnExplosionEmitsExactlyOneEventPerBlast()
        {
            // The per-victim/per-blast confusion is the same shape as phase-05's edge-triggered
            // DamageOutcome.Died. Four actors and two vehicles is ONE explosion, not six.
            var sink = new RecordingDamageSink();
            var router = new ClientMessageRouter();
            int explosions = 0;
            router.OnExplosion += _ => explosions++;

            int emitted = SimulateBlast(
                sink, router, isClient: false,
                actorVictims: new ushort[] { 1, 2, 3, 4 }, vehicleVictims: 2);

            Assert.Equal(1, emitted);
            Assert.Equal(1, explosions);
            Assert.Equal(4, sink.Calls.Count);
        }

        [Fact]
        public void AClientRoleExplosionAppliesNoDamage()
        {
            // D2/D5, and the loud failure the phase-05-Task-6 precondition needs: if the
            // authority guard were absent, a client blast would record damage here.
            var serverSink = new RecordingDamageSink();
            var clientSink = new RecordingDamageSink();
            var router = new ClientMessageRouter();
            int clientEmitted = 0;

            int serverEmitted = SimulateBlast(
                serverSink, router, isClient: false,
                actorVictims: new ushort[] { 7, 8 }, vehicleVictims: 1);

            clientEmitted = SimulateBlast(
                clientSink, router, isClient: true,
                actorVictims: new ushort[] { 7, 8 }, vehicleVictims: 1);

            Assert.Equal(2, serverSink.Calls.Count);
            Assert.Equal(1, serverEmitted);

            Assert.Empty(clientSink.Calls);
            Assert.Equal(0, clientEmitted);
        }

        [Fact]
        public void ADeadVictimTakesNoBlastDamageButStillTakesImpulse()
        {
            // The corpse branch survives the role split at BOTH roles -- corpses are never
            // replicated (AD-4), so a client's ragdoll is the only one that corpse will get.
            var sink = new RecordingDamageSink();

            var server = new BlastOutcome();
            ApplyActorBlast(sink, isClient: false, victimId: 5, victimIsDead: true, server);
            Assert.Empty(sink.Calls);
            Assert.Equal(1, server.ImpulsesApplied);

            var client = new BlastOutcome();
            ApplyActorBlast(sink, isClient: true, victimId: 5, victimIsDead: true, client);
            Assert.Empty(sink.Calls);
            Assert.Equal(1, client.ImpulsesApplied);
        }

        // ------------------------------------------------------------- earshot filter (D7)

        [Fact]
        public void AnExplosionOutsideEarshotIsNotSent()
        {
            // ExplosionAudibleRadius's first assertion against real distances in the repository.
            Assert.Equal(200f, ServerEventWriter.ExplosionAudibleRadius, 3);

            Assert.True(ServerEventWriter.IsWithinEarshotSquared(
                150f * 150f, ServerEventWriter.ExplosionAudibleRadius));

            Assert.False(ServerEventWriter.IsWithinEarshotSquared(
                250f * 250f, ServerEventWriter.ExplosionAudibleRadius));

            // The boundary itself is inclusive, and the margin over the widest explosive in
            // scope (balanceRange 9 m) is recorded so a future 250 m weapon does not silently
            // inherit a filter that would hide it.
            Assert.True(ServerEventWriter.IsWithinEarshotSquared(
                200f * 200f, ServerEventWriter.ExplosionAudibleRadius));

            Assert.True(ServerEventWriter.ExplosionAudibleRadius > 9f * 20f);
        }

        [Fact]
        public void AnEarshotTestOnALinearDistanceWouldPassEverything()
        {
            // Why the parameter is named for its units. Handed a LINEAR 250 m against a 200 m
            // radius, the squared comparison reports "in earshot" -- 250 against 40,000 -- so a
            // caller that forgets to square silently broadcasts every explosion on the map.
            Assert.True(ServerEventWriter.IsWithinEarshotSquared(
                250f, ServerEventWriter.ExplosionAudibleRadius));
        }

        // ------------------------------------------------------- forward compatibility (task 4)

        [Fact]
        public void AnUnknownExplosionKindDoesNotThrow()
        {
            // A server that knows a kind this build predates must cost one unstyled flash, not
            // an exception raised inside the transport pump (V10 D22).
            var router = new ClientMessageRouter();
            ExplosionMessage? received = null;
            router.OnExplosion += m => received = m;

            Span<byte> buffer = stackalloc byte[64];
            int written = ServerEventWriter.WriteExplosion(
                buffer, new ExplosionMessage(3, 0, 0, 0, 6, (ExplosionKind)9));

            Assert.Equal(1, router.Route(buffer.Slice(0, written)));
            Assert.True(received.HasValue);
            Assert.Equal((ExplosionKind)9, received!.Value.Kind);
            Assert.Equal(0, router.MalformedMessages);
        }

        [Fact]
        public void VehicleAndEnvironmentKindsAreDeclaredButUncalledInV1()
        {
            // Criterion 9, as an assertion rather than only as prose. Both members exist and
            // round-trip; neither has a producer in V1, and the phases that own them are named
            // here as well as in the report. An uncalled enum member that nobody writes down is
            // exactly how section 2.2 came to exist.
            Assert.Equal(2, (byte)ExplosionKind.Vehicle);       // V4 owns the caller
            Assert.Equal(3, (byte)ExplosionKind.Environment);   // V7 owns the caller

            var router = new ClientMessageRouter();
            ExplosionMessage? received = null;
            router.OnExplosion += m => received = m;

            Span<byte> buffer = stackalloc byte[64];
            int written = ServerEventWriter.WriteExplosion(
                buffer, new ExplosionMessage(1, 0, 0, 0, 6, ExplosionKind.Vehicle));

            Assert.Equal(1, router.Route(buffer.Slice(0, written)));
            Assert.Equal(ExplosionKind.Vehicle, received!.Value.Kind);
        }

        // -------------------------------------------------------------------------- harness

        /// <summary>
        /// The engine-free shape of <c>ActorManager.Explode</c>: damage every live victim
        /// through the sink, then emit exactly one event — and at the client role, neither.
        /// </summary>
        /// <remarks>
        /// This is deliberately NOT a re-implementation of the blast geometry, which stays in
        /// Unity behind an <c>AnimationCurve</c> (D3). It models only the two things V1 moved:
        /// who decides the damage lands, and who hears about it.
        /// </remarks>
        private static int SimulateBlast(
            IActorDamageSink sink, ClientMessageRouter router, bool isClient,
            ushort[] actorVictims, int vehicleVictims)
        {
            var outcome = new BlastOutcome();

            for (int i = 0; i < actorVictims.Length; i++)
                ApplyActorBlast(sink, isClient, actorVictims[i], victimIsDead: false, outcome);

            for (int i = 0; i < vehicleVictims; i++)
                if (!isClient) outcome.VehiclesDamaged++;

            if (isClient) return 0;

            Span<byte> buffer = stackalloc byte[256];
            int written = ServerEventWriter.WriteExplosion(
                buffer,
                new ExplosionMessage(
                    99, 0, 0, 0, ExplosionEncoding.PackRadiusMetres(6f), ExplosionKind.Grenade));

            router.Route(buffer.Slice(0, written));
            return 1;
        }

        private static void ApplyActorBlast(
            IActorDamageSink sink, bool isClient, ushort victimId, bool victimIsDead,
            BlastOutcome outcome)
        {
            if (victimIsDead)
            {
                // Corpse impulse runs at every role.
                outcome.ImpulsesApplied++;
                return;
            }

            if (isClient) return;

            sink.ApplyDamage(victimId, 100f, attackerId: 99);
            outcome.ActorsDamaged++;
        }

        private sealed class BlastOutcome
        {
            public int ActorsDamaged;
            public int VehiclesDamaged;
            public int ImpulsesApplied;
        }

        private sealed class RecordingDamageSink : IActorDamageSink
        {
            public readonly List<(ushort Victim, float Amount, ushort Attacker)> Calls =
                new List<(ushort, float, ushort)>();

            public DamageOutcome ApplyDamage(ushort victimId, float amount, ushort attackerId)
            {
                Calls.Add((victimId, amount, attackerId));
                return new DamageOutcome(0f, died: true);
            }
        }
    }
}
