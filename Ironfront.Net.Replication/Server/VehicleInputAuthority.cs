using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Vehicles;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// The server's record of what each driver is currently asking their vehicle to do:
    /// who is allowed to send it, and how long a silence is allowed to last before the
    /// controls centre themselves. V5-D5 and V5-D11.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The hold window is the whole reason this class exists rather than a field on the
    /// driver.</b> <c>C_VEHICLE_INPUT</c> is unreliable on channel 3, so a dropped packet leaves
    /// the last axes standing. Standing forever means a driver whose connection dies keeps full
    /// throttle and drives into the sea, at speed, with nobody in it — and the server has no way
    /// to tell that from a driver holding the key down, because a level-triggered axis carries
    /// no edges. After <see cref="VehicleInputHoldTicks"/> the axes centre, so a stalled
    /// connection coasts to a stop.
    /// </para>
    /// <para>
    /// <b>Server policy, not a wire constant.</b> The window needs no protocol change and no
    /// client agreement: it is entirely a statement about how long the server is willing to keep
    /// believing a fact it has not heard repeated. Six ticks is 200 ms at 30 Hz — several
    /// missed 20 Hz input messages, comfortably past ordinary loss, and short enough that a real
    /// disconnection is a stop rather than a departure.
    /// </para>
    /// <para>
    /// <b>Only the driver may steer, and only the vehicle they are actually in (V5-D5).</b> The
    /// router cannot check this — it holds no seat table, and the message's <c>vehicleId</c> is
    /// the client's claim rather than the server's record. So the check lands here, against
    /// <see cref="VehicleRegistry.TryFindSeatOf"/>, which is the same table
    /// <c>SeatArbiter</c> writes. A passenger sending driver input is refused and counted, not
    /// ignored silently: <see cref="RefusedNotDriver"/> rising is either a bug or somebody
    /// probing.
    /// </para>
    /// <para>
    /// Nothing here accepts a position, rotation or velocity, and nothing should ever be added
    /// that does. That is what makes the client's local simulation safe — it predicts a value
    /// the server computes independently, exactly as infantry prediction already does.
    /// </para>
    /// </remarks>
    public sealed class VehicleInputAuthority
    {
        /// <summary>
        /// How many server ticks the last received axes stay live before centring. 6 ticks =
        /// 200 ms at <see cref="ProtocolConstants.SIM_TICK_RATE"/>.
        /// </summary>
        public const int VehicleInputHoldTicks = 6;

        /// <summary>
        /// The driver's seat. <c>Vehicle.cs</c> has treated <c>seats[0]</c> as the driver since
        /// before any of this existed (<c>Vehicle.IsDriven</c>, <c>Vehicle.Driver</c>); this
        /// constant names that fact rather than restating the literal at each comparison.
        /// </summary>
        public const byte DriverSeatIndex = 0;

        private struct Record
        {
            public bool Occupied;
            public uint ReceivedAtTick;
            public ClampedVehicleInput Input;
        }

        private readonly VehicleRegistry _registry;
        private readonly Record[] _byActor;

        public VehicleInputAuthority(VehicleRegistry registry, int maxActors = ProtocolConstants.MAX_ACTORS)
        {
            _registry = registry ?? throw new System.ArgumentNullException(nameof(registry));

            // Indexed by actorId, allocated once. Actor ids are a small dense u16 space, so a
            // flat array beats a dictionary on both lookup cost and per-tick allocation.
            _byActor = new Record[maxActors + 1];
        }

        /// <summary>Inputs accepted and recorded.</summary>
        public long Accepted { get; private set; }

        /// <summary>Refused: the sender is not in the driver seat of the vehicle it named.</summary>
        public long RefusedNotDriver { get; private set; }

        /// <summary>Refused: an input not newer than the one already held. Reorder or replay.</summary>
        public long RefusedStale { get; private set; }

        /// <summary>Reads that found the hold window expired and centred the axes.</summary>
        public long DecayedReads { get; private set; }

        /// <summary>
        /// Records an input, if the sender is the driver of the vehicle it names.
        /// </summary>
        /// <param name="actorId">The sending session's actor.</param>
        /// <param name="input">Already decoded and clamped by <see cref="ClampedVehicleInput"/>.</param>
        /// <param name="serverTick">Now, for the hold window.</param>
        /// <returns>False when refused. The counters say which refusal it was.</returns>
        public bool TryAccept(ushort actorId, in ClampedVehicleInput input, uint serverTick)
        {
            if (actorId == 0 || actorId >= _byActor.Length) { RefusedNotDriver++; return false; }

            if (!_registry.TryFindSeatOf(actorId, out ushort seatedIn, out byte seatIndex)
                || seatIndex != DriverSeatIndex
                || seatedIn != input.VehicleId
                || input.VehicleId == 0)
            {
                RefusedNotDriver++;
                return false;
            }

            ref Record record = ref _byActor[actorId];

            // IsNewer32, not >: the tick is a u32 that wraps. A plain comparison would refuse
            // every input for a while after the wrap, which reads as a driver whose controls
            // stopped working for no reason anybody can reproduce.
            if (record.Occupied
                && record.Input.VehicleId == input.VehicleId
                && !SequenceMath.IsNewer32(input.Tick, record.Input.Tick))
            {
                RefusedStale++;
                return false;
            }

            record.Occupied = true;
            record.ReceivedAtTick = serverTick;
            record.Input = input;

            Accepted++;
            return true;
        }

        /// <summary>
        /// The axes to drive <paramref name="actorId"/>'s vehicle with right now.
        /// </summary>
        /// <returns>
        /// False when nothing has been received, or when the hold window has expired — in which
        /// case <paramref name="input"/> is the centred default and the caller must apply it
        /// rather than keep the last one.
        /// </returns>
        public bool TryGetCurrent(ushort actorId, uint serverTick, out ClampedVehicleInput input)
        {
            input = default;

            if (actorId == 0 || actorId >= _byActor.Length) return false;

            ref Record record = ref _byActor[actorId];
            if (!record.Occupied) return false;

            if (TicksSince(record.ReceivedAtTick, serverTick) > VehicleInputHoldTicks)
            {
                DecayedReads++;
                return false;
            }

            input = record.Input;
            return true;
        }

        /// <summary>Ticks since this actor's last accepted input, saturating at the window.</summary>
        /// <remarks>Surfaced so the net-debug overlay can show a stalling driver before it stops.</remarks>
        public int TicksSinceLastInput(ushort actorId, uint serverTick)
        {
            if (actorId == 0 || actorId >= _byActor.Length) return int.MaxValue;
            if (!_byActor[actorId].Occupied) return int.MaxValue;

            return TicksSince(_byActor[actorId].ReceivedAtTick, serverTick);
        }

        /// <summary>
        /// Drops an actor's record. Call on disconnect and on seat exit.
        /// </summary>
        /// <remarks>
        /// Seat exit matters as much as disconnect: without it, an actor who leaves a vehicle and
        /// re-enters within the hold window resumes with the throttle setting they left at.
        /// </remarks>
        public void Forget(ushort actorId)
        {
            if (actorId == 0 || actorId >= _byActor.Length) return;
            _byActor[actorId] = default;
        }

        /// <summary>Drops every record and every counter. Match teardown.</summary>
        public void Reset()
        {
            for (int i = 0; i < _byActor.Length; i++) _byActor[i] = default;

            Accepted = 0;
            RefusedNotDriver = 0;
            RefusedStale = 0;
            DecayedReads = 0;
        }

        /// <summary>
        /// Elapsed ticks, wrap-safe. A server that has been up long enough to wrap the tick
        /// counter must not decide every driver went silent 4 billion ticks ago.
        /// </summary>
        private static int TicksSince(uint then, uint now)
        {
            uint elapsed = unchecked(now - then);
            return elapsed > int.MaxValue ? 0 : (int)elapsed;
        }
    }
}
