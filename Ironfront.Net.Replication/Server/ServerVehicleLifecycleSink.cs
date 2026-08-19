using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.World;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Turns a spawner's lifecycle reports into <c>S_VEHICLE_SPAWN</c> and
    /// <c>S_VEHICLE_DESPAWN</c>. Phase-V8 task 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase-V8 shipped <see cref="IVehicleLifecycleSink"/> with one implementation that did
    /// nothing, because <c>S_VEHICLE_SPAWN (0x4D)</c> did not exist yet. V3 shipped the opcode
    /// and the codec; this is the sender, and without it the pair would join
    /// <c>S_EXPLOSION</c> and <c>S_PLAYER_LIST</c> on the list of messages that had a codec and
    /// a test and no caller for several phases.
    /// </para>
    /// <para>
    /// <b>Engine-free on purpose.</b> Everything here is a decision — which id, whether the
    /// prefab is authored, whether the frame fit — and decisions that live in a
    /// <c>MonoBehaviour</c> are decisions no CI run ever exercises. Unity supplies one adapter
    /// over <see cref="IReliablePayloadSender"/> and the tick, and nothing else.
    /// </para>
    /// <para>
    /// <b>It refuses rather than guesses, and counts what it refused.</b> An unauthored prefab
    /// (<c>networkId == 0</c>) and an exhausted id pool both return 0, which the caller reads
    /// as "not replicated". This library has no logger — the counters below are the signal, and
    /// the Unity seam is what turns a non-zero one into a log line, the same arrangement
    /// <c>LagCompensator.ShotsOccluded</c> uses.
    /// </para>
    /// </remarks>
    public sealed class ServerVehicleLifecycleSink : IVehicleLifecycleSink
    {
        private readonly IReliablePayloadSender _sender;
        private readonly Func<uint> _currentTick;
        private readonly VehicleIdPool _ids;

        // One buffer, reused. A vehicle spawn is not the hot path, but MAX_PAYLOAD is the known
        // ceiling and there is no reason to hand the GC an array per spawn on a server that
        // replaces a vehicle every few seconds for days.
        private readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];

        public ServerVehicleLifecycleSink(
            IReliablePayloadSender sender, Func<uint> currentTick, VehicleIdPool? ids = null)
        {
            _sender      = sender ?? throw new ArgumentNullException(nameof(sender));
            _currentTick = currentTick ?? throw new ArgumentNullException(nameof(currentTick));
            _ids         = ids ?? new VehicleIdPool();
        }

        /// <summary>The id pool, so a round boundary can return every id at once.</summary>
        public VehicleIdPool Ids => _ids;

        /// <summary>Spawns refused because <see cref="ProtocolConstants.MAX_VEHICLES"/> ids are live or cooling.</summary>
        public int IdExhaustedCount { get; private set; }

        /// <summary>
        /// Spawns refused because the prefab's <c>networkId</c> is 0 or names a vehicle this
        /// build does not know. A level asset problem, not a netcode one.
        /// </summary>
        public int UnauthoredPrefabCount { get; private set; }

        /// <summary>Messages that did not frame. Non-zero means a codec or buffer defect.</summary>
        public int FramingFailureCount { get; private set; }

        /// <summary>Spawns actually put on the wire.</summary>
        public int SpawnsSent { get; private set; }

        /// <summary>Despawns actually put on the wire.</summary>
        public int DespawnsSent { get; private set; }

        /// <inheritdoc />
        public ushort OnVehicleSpawned(in VehicleSpawnReport report)
        {
            if (!VehicleIds.TryGetKind(report.NetworkTypeId, out VehicleKind kind))
            {
                UnauthoredPrefabCount++;
                return 0;
            }

            uint now = _currentTick();
            if (!_ids.TryAcquire(now, out ushort vehicleId))
            {
                IdExhaustedCount++;
                return 0;
            }

            var message = new VehicleSpawnMessage(
                vehicleId,
                kind,
                report.NetworkTypeId,
                Quantize.PackPos(report.Position.X),
                Quantize.PackPos(report.Position.Y),
                Quantize.PackPos(report.Position.Z),
                Quantize.PackQuat(
                    report.RotationX, report.RotationY, report.RotationZ, report.RotationW),
                report.SeatCount,
                flags: 0);

            int written = ServerEventWriter.WriteVehicleSpawn(_payload, in message);
            if (written < 0)
            {
                // The id is handed straight back rather than quarantined: nothing went out, so
                // nothing is in flight naming it, and taking it out of circulation for five
                // seconds would punish the pool for a framing bug.
                _ids.ReturnUnused(vehicleId);
                FramingFailureCount++;
                return 0;
            }

            _sender.BroadcastReliable(
                new ReadOnlySpan<byte>(_payload, 0, written),
                (byte)ServerEventWriter.ReliableChannel);

            SpawnsSent++;
            return vehicleId;
        }

        /// <inheritdoc />
        public void OnVehicleDespawned(ushort vehicleId, VehicleDespawnReason reason)
        {
            // Id 0 is "was never replicated" — an unauthored prefab or a pool that had nothing
            // left. Sending a despawn for it would tell every client to remove a vehicle they
            // were never told about, and the id it names is the protocol's "no vehicle".
            if (vehicleId == 0) return;

            // A second despawn for the same vehicle is dropped here rather than deduplicated on
            // the client. VehicleSpawner reports a death and a world reset can arrive for the
            // same wreck, and a client that removes a vehicle twice removes its replacement.
            if (!_ids.IsInUse(vehicleId)) return;

            var message = new VehicleDespawnMessage(vehicleId, reason);

            int written = ServerEventWriter.WriteVehicleDespawn(_payload, in message);
            if (written < 0)
            {
                FramingFailureCount++;
                return;
            }

            _sender.BroadcastReliable(
                new ReadOnlySpan<byte>(_payload, 0, written),
                (byte)ServerEventWriter.ReliableChannel);

            // Quarantined only after the despawn is out. Releasing first would let the very next
            // spawn reuse the id inside the same tick, which is the collision the quarantine
            // exists to prevent.
            _ids.Release(vehicleId, _currentTick());
            DespawnsSent++;
        }
    }
}
