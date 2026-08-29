using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Vehicles;
using Ironfront.Net.Unity.Diagnostics;

namespace Ironfront.Net.LoadHarness
{
    /// <summary>What the drill is doing right now.</summary>
    public enum DrillPhase
    {
        /// <summary>Walking toward a vehicle it intends to sit in.</summary>
        Approach = 0,

        /// <summary>A <c>C_SEAT_REQUEST</c> is in flight and the answer has not come back.</summary>
        AwaitSeat = 1,

        /// <summary>Seated, sending <c>C_VEHICLE_INPUT</c>.</summary>
        Drive = 2,

        /// <summary>Walking toward another actor with the trigger down.</summary>
        Fight = 3,

        /// <summary>Dead, waiting out the respawn gate before asking for a body.</summary>
        Dead = 4,
    }

    /// <summary>Whether this tick carries a seat request, and which kind.</summary>
    public enum SeatIntent
    {
        None = 0,
        Enter = 1,
        Leave = 2,
    }

    /// <summary>One entity as the drill sees it: an id and a position in metres.</summary>
    /// <remarks>
    /// <b>Metres, not quantized units, and the conversion happens at the caller.</b> The drill
    /// does trigonometry; doing it in wire units would mean every constant here — the seat
    /// reach, the hold distance, the drive threshold — carried a 6.25 cm scale factor nobody
    /// would remember to keep in step with <c>Quantize.POS_RANGE</c>. The caller already holds
    /// the shipped <c>Quantize.UnpackPos</c> and is the right place to spend it.
    /// </remarks>
    public readonly struct DrillBody
    {
        public readonly bool Exists;
        public readonly ushort Id;
        public readonly float X, Y, Z;

        /// <summary>Seats the vehicle announced, or 0 for an actor.</summary>
        public readonly byte SeatCount;

        public DrillBody(ushort id, float x, float y, float z, byte seatCount = 0)
        {
            Exists = id != 0;
            Id = id;
            X = x;
            Y = y;
            Z = z;
            SeatCount = seatCount;
        }
    }

    /// <summary>Everything the drill is allowed to know, assembled from the shipped decoders.</summary>
    /// <remarks>
    /// <b>A struct handed IN rather than a router reached FOR.</b> The drill decides; the
    /// caller observes. That split is what lets every rule below be exercised by
    /// <c>dotnet test</c> without a UDP socket, a Unity server or a 120 s run — and
    /// <c>tools/check-harness-no-decoder.ps1</c> stays trivially satisfied here because this
    /// file cannot reach a payload even if somebody wanted it to.
    /// </remarks>
    public readonly struct DrillWorld
    {
        /// <summary>This client's own actor, or a body with <c>Exists == false</c> before S_SPAWN_ACTOR.</summary>
        public readonly DrillBody Me;

        /// <summary>Nearest other actor, or none.</summary>
        public readonly DrillBody NearestActor;

        /// <summary>Nearest vehicle with at least one seat, or none.</summary>
        public readonly DrillBody NearestVehicle;

        /// <summary>Whether the local actor is alive, per the decoded snapshot's flags.</summary>
        public readonly bool Alive;

        /// <summary>
        /// Health of the hull this client is seated in, 0-100, or <see cref="UnknownHealth"/>.
        /// </summary>
        /// <remarks>
        /// The seated hull specifically, not the nearest one. <see cref="DrillBody"/> carries a
        /// position and a seat count because that is all the walking and the asking need; the
        /// finish rule needs one number about one vehicle, and widening DrillBody to carry it
        /// would put a health field on the nearest ACTOR too.
        /// </remarks>
        public readonly byte SeatedVehicleHealth;

        /// <summary>Health the drill reads when the snapshot has not named its hull yet.</summary>
        /// <remarks>
        /// Deliberately neither 0 nor 100, because both are legitimate readings and either
        /// sentinel would make an unknown hull look like a decided one: 0 sends every drill
        /// into the finish rule on the tick it sits down, and 100 silently denies the rule to a
        /// hull whose row simply had not arrived. 255 cannot be a percentage.
        /// </remarks>
        public const byte UnknownHealth = 255;

        /// <remarks>
        /// <paramref name="seatedVehicleHealth"/> is REQUIRED rather than defaulted, for O-D5's
        /// reason one track over: a defaulted reading would let a caller that forgot it keep the
        /// old always-leave behaviour while still compiling, and the only symptom would be the
        /// Burn verb quietly staying missing -- which is the exact failure this parameter exists
        /// to end.
        /// </remarks>
        public DrillWorld(
            DrillBody me, DrillBody nearestActor, DrillBody nearestVehicle, bool alive,
            byte seatedVehicleHealth)
        {
            Me = me;
            NearestActor = nearestActor;
            NearestVehicle = nearestVehicle;
            Alive = alive;
            SeatedVehicleHealth = seatedVehicleHealth;
        }
    }

    /// <summary>What the client should put on the wire this tick.</summary>
    /// <remarks>
    /// One struct carrying every channel rather than a union, because a tick can legitimately
    /// carry two of them: a seat request is reliable-ordered on channel 2 while movement keeps
    /// flowing on channel 3, and a drill that had to choose would stop walking for a frame every
    /// time it asked for a seat.
    /// </remarks>
    public readonly struct DrillCommand
    {
        public readonly DrillPhase Phase;

        /// <summary>Whether to frame a <c>C_INPUT</c> this tick.</summary>
        public readonly bool SendActorInput;

        public readonly float MoveX, MoveZ, YawDegrees, PitchDegrees;
        public readonly InputButtons Buttons;

        /// <summary>Whether to frame a <c>C_VEHICLE_INPUT</c> this tick.</summary>
        public readonly bool SendVehicleInput;

        public readonly ushort VehicleId;
        public readonly sbyte Throttle, Steer;

        public readonly SeatIntent Seat;
        public readonly ushort SeatVehicleId;
        public readonly byte SeatIndex;

        /// <summary>Whether to frame a <c>C_SPAWN_REQUEST</c> this tick.</summary>
        public readonly bool SendRespawn;

        internal DrillCommand(
            DrillPhase phase,
            bool sendActorInput, float moveX, float moveZ, float yaw, float pitch,
            InputButtons buttons,
            bool sendVehicleInput, ushort vehicleId, sbyte throttle, sbyte steer,
            SeatIntent seat, ushort seatVehicleId, byte seatIndex,
            bool sendRespawn)
        {
            Phase = phase;
            SendActorInput = sendActorInput;
            MoveX = moveX;
            MoveZ = moveZ;
            YawDegrees = yaw;
            PitchDegrees = pitch;
            Buttons = buttons;
            SendVehicleInput = sendVehicleInput;
            VehicleId = vehicleId;
            Throttle = throttle;
            Steer = steer;
            Seat = seat;
            SeatVehicleId = seatVehicleId;
            SeatIndex = seatIndex;
            SendRespawn = sendRespawn;
        }
    }

    /// <summary>
    /// The behaviour behind <see cref="HarnessBehavior.Combat"/>: sit in a vehicle, drive it,
    /// get out, shoot somebody, die, ask for a body back. Ledger <b>X-34</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was missing was a behaviour, not a capability.</b> <c>SyntheticClient</c> has
    /// always been able to frame any client opcode — it is engine-free and speaks the protocol
    /// directly — and <c>HarnessBehavior</c> offered <c>Idle</c> and <c>Move</c>, so every frame
    /// of every lane-A run carried <c>InputButtons.None</c>. Check 11 names four verbs and the
    /// harness could provoke none of them; B-11's PARTIAL was the honest grade for a run that
    /// measured survival under a load that never fought.
    /// </para>
    /// <para>
    /// <b>It drives the SHIPPED opcodes, and it reads only the SHIPPED decoders.</b> Where the
    /// target is comes from <c>DeltaDecoder.Current</c> via the caller; where to point comes
    /// from <see cref="ScriptedAim"/>, which is the lane-B aim arithmetic linked into this
    /// project rather than transcribed — the same <c>&lt;Compile Include&gt;</c> arrangement
    /// <c>BaselineAckPolicy</c> travels by, and for the same reason: X-25 was one transcription
    /// of an aim convention drifting from the one that ships, and a second one here would be
    /// free to drift the same way.
    /// </para>
    /// <para>
    /// <b>Deterministic, with no clock of its own.</b> Every decision is a function of the
    /// world handed in plus the harness clock passed to <see cref="Decide"/>, so a run at one
    /// seed replays — the property every other part of this harness is built to keep.
    /// </para>
    /// <para>
    /// <b>Nothing here predicts.</b> The drill asks for a seat and waits for
    /// <c>S_SEAT_CHANGE</c>; it never decides locally that it is seated.
    /// <c>ClientSeatRequester</c>'s remark makes the argument for the shipped client and it
    /// applies twice over here: a harness that believed its own optimistic seat would report a
    /// <see cref="HarnessVerb.Drive"/> the server refused.
    /// </para>
    /// </remarks>
    public sealed class CombatDrill
    {
        /// <summary>
        /// How close the drill walks to a seat before asking for it.
        /// </summary>
        /// <remarks>
        /// A metre inside <see cref="SeatArbiter.MaxSeatReachMetres"/>, measured against the
        /// server's own constant for <c>ClientSeatRequester.TryFindNearestSeat</c>'s reason: the
        /// arbiter measures from the SEAT's transform and this measures from the vehicle
        /// origin, so asking at exactly the limit spends a round trip to be told
        /// <c>RejectedTooFar</c>. The margin is not a security property — the arbiter still
        /// checks — it is only about not asking for what will be refused.
        /// </remarks>
        public const float SeatRequestDistanceMetres = SeatArbiter.MaxSeatReachMetres - 1f;

        /// <summary>How close the drill closes on a body before it stops walking.</summary>
        /// <remarks>
        /// Well inside any hitscan range and outside contact, so the shot is a shot rather than
        /// a test of what happens when two capsules interpenetrate. It keeps walking while
        /// firing, which is what a player does.
        /// </remarks>
        public const float FightHoldDistanceMetres = 8f;

        /// <summary>How long the drill stays in a seat before getting out again.</summary>
        /// <remarks>
        /// <para>
        /// Long enough for the vehicle to move a recordable distance, short enough that eight
        /// clients contending for the same few vehicles all get a turn inside a 120 s run.
        /// </para>
        /// <para>
        /// <b>Getting out matters as much as getting in.</b> <c>Vehicle.AutoDamage</c> arms on
        /// the vehicle becoming empty and takes 7% of max health every 2 s from 50 s later —
        /// which is the one route to <see cref="HarnessVerb.Burn"/> a client with no explosive
        /// has. A drill that sat in its vehicle for the whole run would deny itself the verb.
        /// </para>
        /// </remarks>
        public const double SeatedMs = 20_000.0;

        /// <summary>Hull health at or below which the drill stays in to finish it off.</summary>
        /// <remarks>
        /// <para>
        /// <b>Check 11's fourth verb was missing for want of patience, not for want of a
        /// route.</b> The ledger's premise -- "the only route open to a client with no explosive
        /// is <c>Vehicle.AutoDamage</c>" -- stopped being true the moment X-46 closed and the
        /// drill could actually drive. Crash damage is a shipped path
        /// (<c>Vehicle.OnCollisionEnter</c>), the prefabs author it generously (quadbike: 400
        /// max health, 2 m/s threshold, x15 multiplier, so a 10 m/s impact is 120 damage), and
        /// <c>crashSkipsBurn</c> is 0 on every ground vehicle -- so a wrecked hull burns rather
        /// than dying outright, which is what the verb watches for.
        /// </para>
        /// <para>
        /// <b>It was already happening and nobody stayed to watch.</b> In
        /// <c>artifacts/lane-a/o6/o6-combat-04</c> eight of sixteen hulls took real crash damage
        /// and vehicle 4 reached <b>13</b> health -- while every drill let go of its vehicle at
        /// exactly <see cref="SeatedMs"/> whatever state it was in. This rule keeps the driver
        /// of a hull it has nearly wrecked in the seat, which makes Burn a property of the
        /// programme rather than of who happened to be sitting in the last one standing.
        /// </para>
        /// <para>
        /// <b>45 rather than something tighter.</b> Health is a percentage byte on the wire, one
        /// crash is worth roughly 30 points of a quadbike, and the snapshot a drill reads is a
        /// tick or two old -- so a threshold near the floor would be crossed and passed between
        /// two readings. 45 is about one and a half impacts of headroom.
        /// </para>
        /// </remarks>
        public const byte FinishHullAtOrBelowHealth = 45;

        /// <summary>The ceiling on a finishing ride.</summary>
        /// <remarks>
        /// A hull can sit under the threshold and stop taking damage -- wedged against a rock,
        /// or on ground too flat to crash on. Without a ceiling that drill holds a driver seat
        /// for the rest of the run and seven other clients contend for one fewer vehicle: the
        /// rule would buy the fourth verb by damaging the first. Generous rather than tight,
        /// because a finishing ride cut off one impact early has bought nothing.
        /// </remarks>
        public const double MaxSeatedMs = 75_000.0;

        /// <summary>How long to wait for <c>S_SEAT_CHANGE</c> before the key works again.</summary>
        /// <remarks>
        /// <c>ClientSeatRequester._answerTimeoutSeconds</c>'s backstop, one project over and
        /// for the same case: the request is reliable-ordered, so under any healthy connection
        /// the answer lands long before this. It exists for the one the reliability layer
        /// cannot cover — a server that routes the message and never answers — because the
        /// alternative is a synthetic client that stands still for the rest of the run.
        /// </remarks>
        public const double SeatAnswerTimeoutMs = 2_000.0;

        /// <summary>How long the drill fights before it goes looking for a vehicle again.</summary>
        /// <remarks>
        /// <b>Without this the drill has a one-way door.</b> A refused seat sends it to
        /// <see cref="DrillPhase.Fight"/>, and fighting is self-sustaining — so a client refused
        /// once would never ask again, and a run in which every client lost its first race for
        /// the same hull would report zero seat requests after the opening seconds and no
        /// <see cref="HarnessVerb.Drive"/> at all. The cycle is what makes the verb a property of
        /// the run rather than of who won the first race.
        /// </remarks>
        public const double FightBeforeReapproachMs = 15_000.0;

        /// <summary>Grace after the respawn gate elapses before asking for a body.</summary>
        /// <remarks>
        /// <c>ServerRespawnGate</c> refuses an early request as a normal outcome rather than as
        /// corruption, so racing it costs nothing — but a drill that re-asked every tick for
        /// three seconds would put 90 reliable messages on channel 2 per death, per client, and
        /// the bandwidth decomposition phase 4 reads would carry the harness's own impatience.
        /// </remarks>
        public const double RespawnGraceMs = 250.0;

        private readonly int _index;

        private DrillPhase _phase = DrillPhase.Approach;

        // The seat this drill believes the SERVER has granted it. Written only from
        // OnSeatChange, never from having asked -- see the class remark on prediction.
        private ushort _seatedVehicleId;
        private byte _seatedSeatIndex;
        private double _seatedAtMs;

        /// <summary>
        /// Whether <see cref="_seatedAtMs"/> holds a real reading.
        /// </summary>
        /// <remarks>
        /// A separate flag rather than <c>_seatedAtMs &lt;= 0</c>, because zero is a LEGITIMATE
        /// harness clock value — <c>Program.RunFor</c> starts its stopwatch at the top of the
        /// run — and the sentinel version re-latched the timer on the tick after the grant.
        /// Caught by <c>CombatDrillTests.LeavesTheSeatOnceItHasHeldItLongEnough</c>, whose whole
        /// arrangement is a seat taken at t = 0.
        /// </remarks>
        private bool _hasSeatedSince;

        private ushort _pendingVehicleId;
        private byte _pendingSeatIndex;
        private double _pendingExpiresAtMs;

        private double _deadSinceMs;
        private bool _respawnAsked;

        /// <summary>Vehicles this drill has given up on, so it does not loop on a refusal.</summary>
        /// <remarks>
        /// One id, not a set: with eight clients and a handful of vehicles, remembering every
        /// refusal would walk a client through the whole map and then leave it with nothing to
        /// approach for the rest of the run. Forgetting the last one is enough to break the
        /// immediate loop, and a vehicle that frees up is legitimately worth asking for again.
        /// </remarks>
        private ushort _avoidVehicleId;

        /// <summary>When the fight gives way to another attempt at a seat. 0 means "not armed".</summary>
        private double _reapproachAtMs;

        public CombatDrill(int index) => _index = index;

        /// <summary>Where the drill is, for the report and the console.</summary>
        public DrillPhase Phase => _phase;

        /// <summary>The vehicle the server has seated this client in, or 0.</summary>
        public ushort SeatedVehicleId => _seatedVehicleId;

        /// <summary>
        /// The seat index the server granted, meaningful only while <see cref="SeatedVehicleId"/>
        /// is non-zero.
        /// </summary>
        /// <remarks>
        /// Exposed so a recorded <see cref="HarnessVerb.Drive"/> can say which seat this client
        /// was in. Only seat 0 receives a driver input sink
        /// (<c>VehicleInputAuthority.DriverSeatIndex</c>), so a hull that moved while this client
        /// sat in seat 2 moved for some other reason — and a verb line that did not say which
        /// seat would let that read as a drive.
        /// </remarks>
        public byte SeatedSeatIndex => _seatedSeatIndex;

        /// <summary>Seat requests this drill has framed. Zero after a full run is the tell.</summary>
        public long SeatRequestsSent { get; private set; }

        /// <summary>Seat requests the server refused, of any kind.</summary>
        public long SeatRequestsRefused { get; private set; }

        /// <summary>Respawn requests framed.</summary>
        public long RespawnRequestsSent { get; private set; }

        /// <summary>Ticks on which the drill held the trigger down.</summary>
        public long TriggerTicks { get; private set; }

        /// <summary>The server's answer to a seat request. Drives the whole seat half.</summary>
        /// <remarks>
        /// Every refusal is handled by name, exactly as <c>ClientSeatRequester.OnSeatChange</c>
        /// does: <see cref="SeatChangeResult.RejectedOccupied"/> walks to the next seat index on
        /// the same vehicle, and everything else abandons this vehicle and re-approaches. The
        /// drill does NOT reproduce the shipped client's lockout retry — a synthetic client
        /// that has just been told to wait has somewhere else to be, and there are seven others
        /// contending for the same hull.
        /// </remarks>
        public void OnSeatChange(ushort actorId, ushort vehicleId, byte seatIndex,
                                 SeatChangeResult result, ushort myActorId)
        {
            if (myActorId == 0 || actorId != myActorId) return;

            if (result == SeatChangeResult.Entered)
            {
                _seatedVehicleId = vehicleId;
                _seatedSeatIndex = seatIndex;
                _hasSeatedSince = false;
                _pendingVehicleId = 0;
                _avoidVehicleId = 0;
                _phase = DrillPhase.Drive;
                return;
            }

            if (result == SeatChangeResult.Left)
            {
                _seatedVehicleId = 0;
                _pendingVehicleId = 0;
                // Out of the seat and straight to the fight: the vehicle it just abandoned is
                // now the one Vehicle.AutoDamage is counting down on, and standing beside it
                // would only mean re-entering the seat that stops the countdown.
                _avoidVehicleId = vehicleId;
                _phase = DrillPhase.Fight;
                return;
            }

            SeatRequestsRefused++;

            ushort refusedVehicle = _pendingVehicleId;
            byte refusedSeat = _pendingSeatIndex;
            _pendingVehicleId = 0;

            // An answer to a request this drill never sent -- a leave arbitrated for this actor
            // by something else, or a refusal that arrived after the timeout cleared the wait.
            if (refusedVehicle == 0)
            {
                _phase = DrillPhase.Approach;
                return;
            }

            if (result == SeatChangeResult.RejectedOccupied && refusedSeat + 1 < byte.MaxValue)
            {
                _pendingSeatIndex = (byte)(refusedSeat + 1);
                _phase = DrillPhase.Approach;
                return;
            }

            _avoidVehicleId = refusedVehicle;
            _pendingSeatIndex = 0;
            _phase = DrillPhase.Approach;
        }

        /// <summary>This client's own actor died. The seat, if any, went with it.</summary>
        public void OnLocalDeath(double nowMs)
        {
            _phase = DrillPhase.Dead;
            _deadSinceMs = nowMs;
            _respawnAsked = false;
            _seatedVehicleId = 0;
            _pendingVehicleId = 0;
        }

        /// <summary>A fresh body arrived for this client. Back to the top of the drill.</summary>
        public void OnLocalSpawn()
        {
            if (_phase != DrillPhase.Dead) return;

            _phase = DrillPhase.Approach;
            _respawnAsked = false;
        }

        /// <summary>What to send this tick.</summary>
        public DrillCommand Decide(in DrillWorld world, double nowMs)
        {
            // Before S_SPAWN_ACTOR names this client's body there is no position to walk from
            // and no yaw that means anything. Sending a frame anyway would be movement input
            // for an actor the drill cannot see, which the server applies to whatever body the
            // connection owns -- a real send with no observable intent behind it.
            if (!world.Me.Exists) return Idle(DrillPhase.Approach);

            if (_phase == DrillPhase.Dead || !world.Alive) return DecideDead(nowMs);

            ExpirePendingSeat(nowMs);
            ExpireFight(nowMs);

            switch (_phase)
            {
                case DrillPhase.Drive: return DecideDrive(in world, nowMs);
                case DrillPhase.AwaitSeat: return DecideAwaitSeat(in world);
                case DrillPhase.Fight: return DecideFight(in world, nowMs);
                default: return DecideApproach(in world, nowMs);
            }
        }

        // ------------------------------------------------------------------ phases

        private DrillCommand DecideDead(double nowMs)
        {
            // Latched here as well as in OnLocalDeath: a client can learn it is dead from the
            // snapshot's IsAlive flag without ever seeing S_DEATH, because that message is
            // reliable-ordered on another channel and can arrive second. Whichever gets here
            // first starts the clock; the other is a no-op.
            if (_phase != DrillPhase.Dead)
            {
                _phase = DrillPhase.Dead;
                _deadSinceMs = nowMs;
                _respawnAsked = false;
                _seatedVehicleId = 0;
            }

            double dueAt = _deadSinceMs
                           + ProtocolConstants.RESPAWN_SECONDS * 1000.0
                           + RespawnGraceMs;

            if (_respawnAsked || nowMs < dueAt) return Idle(DrillPhase.Dead);

            _respawnAsked = true;
            RespawnRequestsSent++;

            return new DrillCommand(
                DrillPhase.Dead,
                sendActorInput: false, 0f, 0f, 0f, 0f, InputButtons.None,
                sendVehicleInput: false, 0, 0, 0,
                SeatIntent.None, 0, 0,
                sendRespawn: true);
        }

        private DrillCommand DecideApproach(in DrillWorld world, double nowMs)
        {
            _phase = DrillPhase.Approach;

            DrillBody vehicle = world.NearestVehicle;

            // No vehicle in this client's interest set is not a fault -- interest management
            // culls, and a map corner may genuinely hold none. It is a reason to go and fight
            // rather than to stand still, and standing still is what an Idle behaviour is for.
            if (!vehicle.Exists || vehicle.Id == _avoidVehicleId) return DecideFight(in world, nowMs);

            float distance = PlanarDistance(in world.Me, in vehicle);
            float yaw = ScriptedAim.YawDegrees(world.Me.X, world.Me.Z, vehicle.X, vehicle.Z);

            if (distance > SeatRequestDistanceMetres)
            {
                return new DrillCommand(
                    DrillPhase.Approach,
                    sendActorInput: true,
                    moveX: 0f, moveZ: 1f, yaw, pitch: 0f, InputButtons.None,
                    sendVehicleInput: false, 0, 0, 0,
                    SeatIntent.None, 0, 0,
                    sendRespawn: false);
            }

            byte seat = _pendingSeatIndex;
            if (vehicle.SeatCount != 0 && seat >= vehicle.SeatCount) seat = 0;

            _pendingVehicleId = vehicle.Id;
            _pendingSeatIndex = seat;
            _pendingExpiresAtMs = nowMs + SeatAnswerTimeoutMs;
            _phase = DrillPhase.AwaitSeat;
            SeatRequestsSent++;

            return new DrillCommand(
                DrillPhase.AwaitSeat,
                sendActorInput: true, 0f, 0f, yaw, 0f, InputButtons.None,
                sendVehicleInput: false, 0, 0, 0,
                SeatIntent.Enter, vehicle.Id, seat,
                sendRespawn: false);
        }

        private DrillCommand DecideAwaitSeat(in DrillWorld world)
        {
            // Standing still with the trigger up while the answer is in flight. Walking on
            // would risk leaving the reach the request was measured at, so a grant would seat a
            // body the arbiter had already refused by the time it arrived.
            float yaw = world.NearestVehicle.Exists
                ? ScriptedAim.YawDegrees(
                    world.Me.X, world.Me.Z, world.NearestVehicle.X, world.NearestVehicle.Z)
                : 0f;

            return new DrillCommand(
                DrillPhase.AwaitSeat,
                sendActorInput: true, 0f, 0f, yaw, 0f, InputButtons.None,
                sendVehicleInput: false, 0, 0, 0,
                SeatIntent.None, 0, 0,
                sendRespawn: false);
        }

        private DrillCommand DecideDrive(in DrillWorld world, double nowMs)
        {
            if (_seatedVehicleId == 0) return DecideFight(in world, nowMs);

            if (!_hasSeatedSince)
            {
                _seatedAtMs = nowMs;
                _hasSeatedSince = true;
            }

            double heldMs = nowMs - _seatedAtMs;

            // A hull this client has driven under the threshold is FINISHED, not abandoned.
            // See FinishHullAtOrBelowHealth for why the fourth verb was missing without this.
            bool finishing =
                world.SeatedVehicleHealth != DrillWorld.UnknownHealth
                && world.SeatedVehicleHealth <= FinishHullAtOrBelowHealth
                && heldMs < MaxSeatedMs;

            if (heldMs >= SeatedMs && !finishing)
            {
                ushort leaving = _seatedVehicleId;
                byte seat = _seatedSeatIndex;
                _hasSeatedSince = false;

                // The seat is NOT cleared here. It is cleared by S_SEAT_CHANGE(Left), for the
                // same reason the entry is not predicted: a refused leave must leave this
                // client driving rather than walking on foot inside a vehicle it still occupies.
                return new DrillCommand(
                    DrillPhase.Drive,
                    sendActorInput: false, 0f, 0f, 0f, 0f, InputButtons.None,
                    sendVehicleInput: false, 0, 0, 0,
                    SeatIntent.Leave, leaving, seat,
                    sendRespawn: false);
            }

            // Full throttle with a slow, seeded steer. Straight ahead would leave a vehicle
            // that has driven into the first wall it met reporting a Drive it did not do; a
            // steer that changes sign makes the hull describe an arc, which moves it off the
            // spawn whichever way the map is shaped.
            sbyte steer = (sbyte)(Math.Sin((nowMs + _index * 1000.0) / 4000.0) * 96.0);

            return new DrillCommand(
                DrillPhase.Drive,
                sendActorInput: false, 0f, 0f, 0f, 0f, InputButtons.None,
                sendVehicleInput: true, _seatedVehicleId, throttle: 127, steer: steer,
                SeatIntent.None, 0, 0,
                sendRespawn: false);
        }

        private DrillCommand DecideFight(in DrillWorld world, double nowMs)
        {
            // Armed on ENTRY to the fight rather than refreshed per tick, so the window is
            // fifteen seconds of fighting and not fifteen seconds after the last one.
            if (_phase != DrillPhase.Fight) _reapproachAtMs = nowMs + FightBeforeReapproachMs;
            _phase = DrillPhase.Fight;

            DrillBody target = world.NearestActor;

            // Nobody in the interest set. Walking a fixed heading rather than standing still:
            // Move's whole argument is that a world where nothing moves is the case delta
            // encoding is best at, and a Combat run that degenerated into eight statues would
            // report a bandwidth figure describing no game anybody plays.
            if (!target.Exists)
            {
                float wander = (_index * 47f) % 360f;
                return new DrillCommand(
                    DrillPhase.Fight,
                    sendActorInput: true, 0f, 1f, wander, 0f, InputButtons.None,
                    sendVehicleInput: false, 0, 0, 0,
                    SeatIntent.None, 0, 0,
                    sendRespawn: false);
            }

            float distance = ScriptedAim.PlanarDistance(world.Me.X, world.Me.Z, target.X, target.Z);
            float yaw = ScriptedAim.YawDegrees(world.Me.X, world.Me.Z, target.X, target.Z);

            // Feet to torso, with the two ends raised by DIFFERENT heights. Raising both by the
            // eye height is ledger X-25 -- it reads as "aim level", puts every shot through the
            // 1.550..1.580 gap X-24 names, and is why no lane-B combat run scored a hit for a
            // month. PitchAtBody is the shipped correction; calling PitchDegrees here with the
            // same height on both ends would re-import the defect.
            float pitch = ScriptedAim.PitchAtBody(
                world.Me.X, world.Me.Y, world.Me.Z, target.X, target.Y, target.Z);

            // Held down every tick rather than pulsed. ServerCombatAuthority runs the cooldown,
            // the clip and the reload, so the extra frames are rejected by the shipped
            // predicate -- and a pulse written here would be a second fire-rate model, free to
            // disagree with the one that ships. NetClientLocalCombatDriver.Update makes the
            // same call for the same reason.
            InputButtons buttons = InputButtons.Fire | InputButtons.Aim;
            TriggerTicks++;

            float moveZ = ScriptedAim.ApproachMoveZ(distance, FightHoldDistanceMetres);

            return new DrillCommand(
                DrillPhase.Fight,
                sendActorInput: true, 0f, moveZ, yaw, pitch, buttons,
                sendVehicleInput: false, 0, 0, 0,
                SeatIntent.None, 0, 0,
                sendRespawn: false);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Ends a fight that has run its window, and forgives the vehicle it fled.</summary>
        /// <remarks>
        /// Forgiving <see cref="_avoidVehicleId"/> here is the point: the refusal that sent this
        /// client away was a fact about one moment — an occupied seat, a lockout, a hull that
        /// had just been claimed — and none of those is still true fifteen seconds later. A
        /// permanent avoid list would shrink the drill's world every time it lost a race.
        /// </remarks>
        private void ExpireFight(double nowMs)
        {
            if (_phase != DrillPhase.Fight || _reapproachAtMs <= 0.0 || nowMs < _reapproachAtMs)
                return;

            _reapproachAtMs = 0.0;
            _avoidVehicleId = 0;
            _pendingSeatIndex = 0;
            _phase = DrillPhase.Approach;
        }

        private void ExpirePendingSeat(double nowMs)
        {
            if (_pendingVehicleId == 0 || nowMs < _pendingExpiresAtMs) return;

            _avoidVehicleId = _pendingVehicleId;
            _pendingVehicleId = 0;
            _pendingSeatIndex = 0;
            if (_phase == DrillPhase.AwaitSeat) _phase = DrillPhase.Approach;
        }

        private static float PlanarDistance(in DrillBody from, in DrillBody to)
            => ScriptedAim.PlanarDistance(from.X, from.Z, to.X, to.Z);

        private DrillCommand Idle(DrillPhase phase)
            => new DrillCommand(
                phase,
                sendActorInput: false, 0f, 0f, 0f, 0f, InputButtons.None,
                sendVehicleInput: false, 0, 0, 0,
                SeatIntent.None, 0, 0,
                sendRespawn: false);
    }
}
