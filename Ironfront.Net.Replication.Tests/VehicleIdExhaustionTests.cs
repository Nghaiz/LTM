using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Replication.Vehicles;
using Ironfront.Net.Replication.World;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Protocol 10 § 8.2 and § 10.3 — the id space behaves at and past the raised capacity, and
    /// nothing anywhere hands out 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why these are separate from <c>VehicleIdDemandTests</c>.</b> That file reads the two
    /// shipped scenes and answers "can a map ask for more ids than exist?". These answer "and
    /// what happens at the ceiling?", which does not depend on any map and must keep holding
    /// when somebody authors a third one.
    /// </para>
    /// <para>
    /// <b>The capacity is never written as a digit here.</b> Every count below is derived from
    /// <see cref="ProtocolConstants.MAX_VEHICLES"/>, so raising it again cannot leave a test
    /// asserting the old ceiling while passing — which is the shape the whole protocol-10
    /// vehicle lane exists to remove.
    /// </para>
    /// </remarks>
    public sealed class VehicleIdExhaustionTests
    {
        // ---------------------------------------------------------------- harness

        private sealed class RecordingSender : IReliablePayloadSender
        {
            public readonly List<byte[]> Payloads = new List<byte[]>();

            public void BroadcastReliable(ReadOnlySpan<byte> payload, byte channel)
                => Payloads.Add(payload.ToArray());
        }

        private sealed class Clock
        {
            public uint Tick { get; set; }
            public uint Now() => Tick;
        }

        private static VehicleSpawnReport Report(ushort spawnerId = 1)
            => new VehicleSpawnReport(
                spawnerId, VehicleIds.JEEP, seatCount: 4, new Vec3(1f, 2f, 3f),
                rotationX: 0f, rotationY: 0f, rotationZ: 0f, rotationW: 1f);

        // ------------------------------------------------------- 18+ concurrent vehicles

        /// <summary>
        /// Dustbowl's measured peak — fourteen pads plus four <c>AfterMoved</c> replacements —
        /// is served in full, and every id it gets back is addressable.
        /// </summary>
        /// <remarks>
        /// The count comes from <c>VehicleIdDemandTests</c>'s arithmetic rather than from the
        /// scene, on purpose: this is the ceiling behaving, not the map being read twice.
        /// </remarks>
        [Fact]
        public void TheMeasuredPeakDemandIsServedEntirelyAndNeverWithIdZero()
        {
            const int DustbowlPeak = 14 + 4;

            var sink = new ServerVehicleLifecycleSink(
                new RecordingSender(), new Clock().Now, new VehicleIdPool());

            var issued = new HashSet<ushort>();

            for (int i = 0; i < DustbowlPeak; i++)
            {
                ushort id = sink.OnVehicleSpawned(Report((ushort)(i + 1)));

                Assert.NotEqual(0, id);
                Assert.True(issued.Add(id), $"id {id} was handed out twice");
            }

            Assert.Equal(DustbowlPeak, issued.Count);
            Assert.Equal(0, sink.IdExhaustedCount);

            // The headroom protocol 10 § 8.1 bought, stated as a subtraction rather than as 6 so
            // it stays true if the constant moves again.
            Assert.Equal(ProtocolConstants.MAX_VEHICLES - DustbowlPeak, sink.Ids.FreeCount);
        }

        /// <summary>
        /// Past the ceiling the pool refuses. It does NOT wrap, throw, or return 0 as if it were
        /// an id.
        /// </summary>
        /// <remarks>
        /// The <c>out</c> value on a refusal is asserted explicitly. A caller that ignores the
        /// <c>bool</c> — which is exactly what <c>VehicleSpawner</c> effectively did before the
        /// announce-then-commit reordering — must get something it cannot mistake for a vehicle,
        /// and 0 is that only because every layer below refuses it.
        /// </remarks>
        [Fact]
        public void OneSpawnPastTheCeilingIsRefusedRatherThanGivenIdZero()
        {
            var pool = new VehicleIdPool();

            for (int i = 0; i < ProtocolConstants.MAX_VEHICLES; i++)
                Assert.True(pool.TryAcquire(0u, out ushort id) && id != 0);

            Assert.False(pool.TryAcquire(0u, out ushort refused));
            Assert.Equal(0, refused);
            Assert.Equal(ProtocolConstants.MAX_VEHICLES, pool.InUseCount);
        }

        /// <summary>
        /// Every layer below the spawner refuses id 0 in its own terms, so no single guard is
        /// load-bearing on its own.
        /// </summary>
        /// <remarks>
        /// The two answers differ deliberately. <see cref="VehicleRegistry.Add"/> returns false
        /// because a spawner double-reporting is a duplicate report rather than a second
        /// vehicle, and the vehicle path must not be able to take the tick loop down.
        /// <see cref="VehicleState.Spawned"/> throws because reaching it with 0 means the caller
        /// never asked the pool at all, and there is no recovery that leaves a usable vehicle.
        /// </remarks>
        [Fact]
        public void NoLayerWillBuildOrRegisterAVehicleCarryingIdZero()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => VehicleState.Spawned(0, 1, VehicleKind.Car, 2, 100f, 0));

            var registry = new VehicleRegistry();
            VehicleState live = VehicleState.Spawned(1, 1, VehicleKind.Car, 2, 100f, 0);
            live.VehicleId = 0;

            Assert.False(registry.Add(in live, new VehicleCaptureTests.FakePose()));
            Assert.Equal(0, registry.LiveCount);

            // And the sink never announces one: id 0 means "was never replicated", so a despawn
            // for it would tell every client to remove a vehicle they were never told about.
            var sender = new RecordingSender();
            new ServerVehicleLifecycleSink(sender, new Clock().Now)
                .OnVehicleDespawned(0, VehicleDespawnReason.Destroyed);

            Assert.Empty(sender.Payloads);
        }

        // ------------------------------------------------------------------ quarantine

        /// <summary>
        /// A superseded vehicle's id comes back only after the quarantine, and the pad that was
        /// waiting for it is served the moment it does.
        /// </summary>
        /// <remarks>
        /// This is the <c>AfterMoved</c> shape end to end: the original is driven away and still
        /// holds its id while the replacement is announced, so both are live at once; when the
        /// original finally dies its id is retired, and it must stay retired for
        /// <see cref="ProtocolConstants.VEHICLE_ID_QUARANTINE_TICKS"/> because snapshot entries
        /// naming it are still in flight.
        /// </remarks>
        [Fact]
        public void ASupersededVehiclesIdReturnsOnlyAfterItsQuarantine()
        {
            var clock = new Clock();
            var sink  = new ServerVehicleLifecycleSink(
                new RecordingSender(), clock.Now, new VehicleIdPool(capacity: 2));

            ushort original = sink.OnVehicleSpawned(Report());
            ushort replacement = sink.OnVehicleSpawned(Report());

            Assert.NotEqual(0, original);
            Assert.NotEqual(0, replacement);
            Assert.NotEqual(original, replacement);

            // A third pad asks while both are alive. Refused, not served with 0.
            Assert.Equal(0, sink.OnVehicleSpawned(Report()));

            sink.OnVehicleDespawned(original, VehicleDespawnReason.Destroyed);

            clock.Tick = ProtocolConstants.VEHICLE_ID_QUARANTINE_TICKS - 1;
            Assert.Equal(0, sink.OnVehicleSpawned(Report()));

            clock.Tick = ProtocolConstants.VEHICLE_ID_QUARANTINE_TICKS;
            Assert.Equal(original, sink.OnVehicleSpawned(Report()));
        }

        /// <summary>
        /// An id that was allocated but never announced comes back immediately, and is NOT
        /// quarantined.
        /// </summary>
        /// <remarks>
        /// The quarantine exists to outlast packets naming the id. A spawn that failed to frame
        /// put no such packet on the wire, so cooling it for 150 ticks would take an id out of
        /// circulation to protect against nothing — which on a hot pool is one more pad that
        /// gets no vehicle.
        /// </remarks>
        [Fact]
        public void AnIdForAVehicleThatWasNeverAnnouncedIsNotQuarantined()
        {
            var pool = new VehicleIdPool(capacity: 1);

            Assert.True(pool.TryAcquire(0u, out ushort id));
            pool.ReturnUnused(id);

            Assert.Equal(0, pool.QuarantinedCount);
            Assert.True(pool.TryAcquire(0u, out ushort again));
            Assert.Equal(id, again);
        }

        // ----------------------------------------------------------------- world reset

        /// <summary>
        /// A world reset leaks no vehicle id: live ids, cooling ids and the registry all come
        /// back, in that order, and the next round opens with the full capacity.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The ORDER is pinned because it is the part that can be got wrong silently. The
        /// registry is emptied before the pool is refilled, so no capture between the two can
        /// read a vehicle whose id has already been re-offered; the quarantine is dropped rather
        /// than honoured, because a client tears its whole vehicle table down with the match
        /// phase and there is nothing left for a stale packet to be applied to.
        /// </para>
        /// <para>
        /// The reverse order is what would leak: ids back first, a capture in between, and the
        /// opening spawns of round two collide with entries round one is still sending.
        /// </para>
        /// </remarks>
        [Fact]
        public void AWorldResetReturnsEveryVehicleIdAndEmptiesTheRegistryFirst()
        {
            var pool     = new VehicleIdPool();
            var registry = new VehicleRegistry();
            var clock    = new Clock();

            ushort retired = 0;
            for (int i = 0; i < ProtocolConstants.MAX_VEHICLES; i++)
            {
                Assert.True(pool.TryAcquire(clock.Now(), out ushort id));

                VehicleState state = VehicleStateFor(id);
                Assert.True(registry.Add(in state, new VehicleCaptureTests.FakePose()));

                retired = id;
            }

            // One of them dies just before the boundary, so the reset has to cope with a live
            // set AND a cooling one.
            registry.Remove(retired);
            pool.Release(retired, clock.Now());

            Assert.Equal(1, pool.QuarantinedCount);

            registry.Clear();
            Assert.Equal(0, registry.LiveCount);

            pool.ReleaseAll();

            Assert.Equal(0, pool.InUseCount);
            Assert.Equal(0, pool.QuarantinedCount);
            Assert.Equal(ProtocolConstants.MAX_VEHICLES, pool.FreeCount);

            // And round two really can fill the map again, with addressable ids.
            for (int i = 0; i < ProtocolConstants.MAX_VEHICLES; i++)
                Assert.True(pool.TryAcquire(clock.Now(), out ushort id) && id != 0);
        }

        // ------------------------------------------------------------------ helpers

        private static VehicleState VehicleStateFor(ushort id)
            => VehicleState.Spawned(id, spawnerId: 1, VehicleKind.Car, seatCount: 2,
                maxHealth: 100f, ownerTeam: 0);

    }
}
