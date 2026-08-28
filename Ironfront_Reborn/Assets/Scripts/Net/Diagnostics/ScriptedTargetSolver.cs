// Diagnostics are compiled OUT of a shipping client build.
//
// The sense is INVERTED on purpose. Unity's BuildPlayerOptions.extraScriptingDefines can only
// ADD symbols, never subtract one, so a positive IRONFRONT_DIAGNOSTICS would have to be off in
// ProjectSettings and switched on for every build that needs it -- which is the Editor, the
// EditMode tests and the lane-B harness, i.e. everything except the one build that does not
// exist yet. Defaulting ON and letting a shipping build ADD IRONFRONT_NO_DIAGNOSTICS is the
// only arrangement the mechanism actually supports.
//
// Nothing outside Assets/Scripts/Net/Diagnostics/ names a type from this folder: the ten
// mentions elsewhere are doc-comments, checked 2026-08-21. So this guard needs no companion
// guard at any call site, and a strip cannot leave a dangling reference behind it.
#if !IRONFRONT_NO_DIAGNOSTICS
using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Unity.Client;
using UnityEngine;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// Resolves a step's <c>aimAtPlayer</c> to a live body and turns it into the yaw, pitch and
    /// approach distance a scripted client drives with. Phase-3D lane B.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The Unity half of <see cref="ScriptedAim"/>, kept apart from it deliberately.</b> All
    /// the arithmetic lives in that class, which is engine-free and therefore the only part of
    /// this a test can reach; what is left here is two lookups and a transform read. Anything
    /// that starts computing in this file has left coverage.
    /// </para>
    /// <para>
    /// <b>Reverse name lookup by scan, and 64 is the whole table.</b>
    /// <c>PlayerNameTable</c> is an array indexed by actor id
    /// (<see cref="ProtocolConstants.MAX_ACTORS"/> long) and exposes forward lookup only, which
    /// is right for its own caller — a killfeed line has the id and wants the name. Scanning it
    /// backwards costs 64 index reads and needs no change to a shipped type for a harness's
    /// convenience.
    /// </para>
    /// <para>
    /// <b>Resolution is cached and re-run on a slow cadence.</b> The name table only moves when
    /// <c>S_PLAYER_LIST</c> arrives, so its <c>Revision</c> is the invalidation signal; the
    /// POSITION is re-read every frame, because the target is walking. Re-scanning names per
    /// frame would be 64 reads a frame to answer a question that changes on join and leave.
    /// </para>
    /// </remarks>
    public sealed class ScriptedTargetSolver
    {
        /// <summary>What a solve found, or why it did not.</summary>
        public struct Solution
        {
            /// <summary>Whether <see cref="Yaw"/>, <see cref="Pitch"/> and <see cref="Distance"/> mean anything.</summary>
            public bool Resolved;

            /// <summary>The target's actor id, or 0 when the name resolved to nobody.</summary>
            public ushort ActorId;

            /// <summary>
            /// The target's VEHICLE id, or 0 when this solve was not for a vehicle. Ledger
            /// <b>X-44</b>.
            /// </summary>
            /// <remarks>
            /// A field of its own rather than reusing <see cref="ActorId"/>: an actor id and a
            /// vehicle id are different namespaces that overlap numerically, so folding them
            /// would make the checkpoint record claim to have found an actor it never looked for.
            /// </remarks>
            public ushort VehicleId;

            public float Yaw;
            public float Pitch;

            /// <summary>Planar distance in metres — what an approach step stops on.</summary>
            public float Distance;
        }

        private NetClientCombatPresenter _presenter;
        private RemoteActorRegistry _registry;
        private RemoteVehicleRegistry _vehicles;

        // Reused across frames so a per-frame vehicle solve allocates nothing. GROWN rather
        // than clamped if the registry ever exceeds the initial size -- a silent clamp would
        // make "the nearest vehicle" mean "the nearest of the first N the registry happened to
        // list", which is not a property any programme states.
        private ushort[] _vehicleIds = new ushort[64];
        private float[] _vehicleX = new float[64];
        private float[] _vehicleY = new float[64];
        private float[] _vehicleZ = new float[64];

        private string _cachedName;
        private int _cachedRevision = -1;
        private ushort _cachedActorId;

        private int _solvedFrame = -1;
        private string _solvedName;
        private bool _solvedIsVehicle;

        /// <summary>The last name a step asked for. Recorded per checkpoint.</summary>
        public string LastRequestedName { get; private set; }

        /// <summary>
        /// True when the last solve was for a VEHICLE rather than a player name. Ledger
        /// <b>X-44</b>.
        /// </summary>
        /// <remarks>
        /// The recorder writes <c>aim: null</c> when no NAME was requested, which was a complete
        /// description while every solve was by name. A vehicle solve has no name, so without
        /// this flag an approach that resolved a vehicle and one that never ran would produce the
        /// same artifact — the exact failure <c>AppendAim</c>'s own remark exists to prevent.
        /// </remarks>
        public bool LastRequestWasVehicle { get; private set; }

        /// <summary>The last solve's outcome. Recorded per checkpoint.</summary>
        public Solution Last { get; private set; }

        /// <summary>
        /// Solves for one player name against the local actor's current eye position.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An unresolvable name returns <c>Resolved = false</c> rather than throwing: the target
        /// may simply not have joined yet, and a run that ended on a name lookup would report a
        /// harness failure where the honest answer is "the target was not there at t=4s". The
        /// caller falls back to the step's declared yaw and the recorder writes the miss down.
        /// </para>
        /// <para>
        /// <b>One solve per frame, memoized on <see cref="Time.frameCount"/>.</b> Three callers
        /// want the same answer in the same frame — <c>IInputSource.Yaw</c>, <c>Pitch</c>, and
        /// the harness building <c>MoveInput</c> — and Unity does not order their <c>Update</c>s
        /// against each other. Recomputing per caller would let the yaw a client TURNS to differ
        /// from the yaw it SHOOTS along whenever the target moved between two reads inside one
        /// frame: a sub-degree error that only appears while the target is walking, which is
        /// exactly when check 1 fires.
        /// </remarks>
        public Solution Solve(string playerName)
        {
            if (_solvedFrame == Time.frameCount
                && !_solvedIsVehicle
                && string.Equals(_solvedName, playerName, StringComparison.Ordinal))
            {
                return Last;
            }

            _solvedFrame = Time.frameCount;
            _solvedName = playerName;
            _solvedIsVehicle = false;
            LastRequestedName = playerName;
            LastRequestWasVehicle = false;

            var miss = default(Solution);

            if (string.IsNullOrEmpty(playerName)) { Last = miss; return miss; }

            ILocalPlayerRig local = NetClientBindings.LocalPlayer;
            if (!local.Exists) { Last = miss; return miss; }

            ushort actorId = ResolveActorId(playerName);
            if (actorId == 0) { Last = miss; return miss; }

            if (!TryRegistry(out RemoteActorRegistry registry)
                || !registry.TryFind(actorId, out Transform target)
                || target == null)
            {
                miss.ActorId = actorId;
                Last = miss;
                return miss;
            }

            Vector3 from = local.Position;
            Vector3 to = target.position;

            // Both transforms are FEET positions, and PitchAtBody raises each end by the
            // height that belongs to it: the shooter by its eye height, the target to its
            // torso centre. Raising both by the eye height instead reads as "aim level" and
            // was ledger X-25 -- level at 1.6 m, which is 2 cm inside the head box's lower
            // edge at every range. See ScriptedAim.TargetAimHeight.
            var solution = new Solution
            {
                Resolved = true,
                ActorId = actorId,
                Yaw = ScriptedAim.YawDegrees(from.x, from.z, to.x, to.z),
                Pitch = ScriptedAim.PitchAtBody(
                    from.x, from.y, from.z,
                    to.x, to.y, to.z),
                Distance = ScriptedAim.PlanarDistance(from.x, from.z, to.x, to.z),
            };

            Last = solution;
            return solution;
        }

        /// <summary>
        /// Solves for the nearest replicated vehicle within <paramref name="maxSearchMetres"/>.
        /// Ledger <b>X-44</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why a vehicle needs a solve of its own.</b> <see cref="Solve"/> takes a player
        /// display name and scans <c>PlayerNameTable</c> backwards. A vehicle has no display
        /// name and no such table, so before this the programme vocabulary could describe
        /// walking to a person and could not describe walking to a car -- while
        /// <c>ClientSeatRequester.TryFindNearestSeat</c> only sees seats within
        /// <c>SeatArbiter.MaxSeatReachMetres</c> of where the player is ALREADY standing.
        /// </para>
        /// <para>
        /// <b>Against <see cref="RemoteVehicleRegistry"/>, through its POSE seam.</b> That
        /// registry is the client's own replicated vehicle set -- every vehicle in a networked
        /// world arrives from <c>S_VEHICLE_SPAWN</c> with the id the server gave it -- and
        /// <c>TryGetPose</c> exists precisely so an observer outside that assembly gets a pose
        /// snapshot rather than a <c>NetClientVehicle</c> it could then drive.
        /// </para>
        /// <para>
        /// <b>The arithmetic is <see cref="ScriptedAim.NearestIndexWithin"/>, not this
        /// method.</b> What is left here is a registry walk and a transform read, which is this
        /// class's standing rule. A nearest-within scan written inline would be untestable, and
        /// its edge cases -- nothing in range, an empty set, a tie at a spawn pad -- are exactly
        /// the ones a run cannot be relied on to produce.
        /// </para>
        /// <para>
        /// <b>Memoized per frame like the player solve, on a key that distinguishes the two.</b>
        /// Sharing <c>_solvedFrame</c> without <c>_solvedIsVehicle</c> would have a player solve
        /// and a vehicle solve in the same frame return each other's answer.
        /// </para>
        /// </remarks>
        public Solution SolveNearestVehicle(float maxSearchMetres)
        {
            if (_solvedFrame == Time.frameCount && _solvedIsVehicle) return Last;

            _solvedFrame = Time.frameCount;
            _solvedIsVehicle = true;
            _solvedName = null;
            LastRequestedName = null;
            LastRequestWasVehicle = true;

            var miss = default(Solution);

            ILocalPlayerRig local = NetClientBindings.LocalPlayer;
            if (!local.Exists) { Last = miss; return miss; }

            if (!TryVehicleRegistry(out RemoteVehicleRegistry vehicles))
            {
                Last = miss;
                return miss;
            }

            Vector3 from = local.Position;
            int count = GatherVehicles(vehicles);

            int index = ScriptedAim.NearestIndexWithin(
                from.x, from.z, _vehicleX, _vehicleZ, count, maxSearchMetres);

            if (index < 0) { Last = miss; return miss; }

            var solution = new Solution
            {
                Resolved = true,
                VehicleId = _vehicleIds[index],
                Yaw = ScriptedAim.YawDegrees(from.x, from.z, _vehicleX[index], _vehicleZ[index]),
                Pitch = ScriptedAim.PitchDegrees(
                    from.x, from.y + ScriptedAim.ShooterEyeHeight, from.z,
                    _vehicleX[index], _vehicleY[index] + ScriptedAim.VehicleAimHeight,
                    _vehicleZ[index]),
                Distance = ScriptedAim.PlanarDistance(
                    from.x, from.z, _vehicleX[index], _vehicleZ[index]),
            };

            Last = solution;
            return solution;
        }

        /// <summary>
        /// Copies every live vehicle's id and position into the reusable arrays, growing them
        /// first if the registry has outgrown them.
        /// </summary>
        /// <remarks>
        /// A vehicle whose pose cannot be read is SKIPPED rather than written as a zero: the
        /// world origin is a real place a client could walk to, so one such entry would pull
        /// every approach toward (0, 0) and look like a solve rather than a miss.
        /// </remarks>
        private int GatherVehicles(RemoteVehicleRegistry vehicles)
        {
            System.Collections.Generic.IReadOnlyList<ushort> ids = vehicles.LiveIds;

            if (ids.Count > _vehicleIds.Length)
            {
                _vehicleIds = new ushort[ids.Count];
                _vehicleX = new float[ids.Count];
                _vehicleY = new float[ids.Count];
                _vehicleZ = new float[ids.Count];
            }

            int count = 0;

            for (int i = 0; i < ids.Count; i++)
            {
                if (!vehicles.TryGetPose(ids[i], out Vector3 position, out _, out _)) continue;

                _vehicleIds[count] = ids[i];
                _vehicleX[count] = position.x;
                _vehicleY[count] = position.y;
                _vehicleZ[count] = position.z;
                count++;
            }

            return count;
        }

        private bool TryVehicleRegistry(out RemoteVehicleRegistry registry)
        {
            if (_vehicles == null)
            {
                _vehicles = UnityEngine.Object.FindFirstObjectByType<RemoteVehicleRegistry>(
                    FindObjectsInactive.Include);
            }

            registry = _vehicles;
            return registry != null;
        }

        private ushort ResolveActorId(string playerName)
        {
            if (!TryPresenter(out NetClientCombatPresenter presenter)) return 0;

            PlayerNameTable names = presenter.Names;

            if (_cachedRevision == names.Revision
                && string.Equals(_cachedName, playerName, StringComparison.Ordinal))
            {
                return _cachedActorId;
            }

            ushort found = Scan(names, playerName);

            // The server does not know "OBS-A". It never parses the join ticket, so the only
            // identity it holds is the transport's PlayerId, and ServerTickLoop.DisplayNameFor
            // renders that as "#5002" — documented on ServerPlayer.DisplayName as deliberate,
            // because a real username needs a new opcode and acceptance criterion 2 forbids
            // moving PROTOCOL_VERSION. So the harness is the side that has to translate.
            //
            // Measured on combat-role01: namedPlayers 3, aim.resolved false, targetActorId 0 —
            // three names in the table, none of them the one three programmes asked for.
            if (found == 0) found = Scan(names, RosterAlias(playerName));

            _cachedName = playerName;
            _cachedRevision = names.Revision;
            _cachedActorId = found;
            return found;
        }

        private static ushort Scan(PlayerNameTable names, string wanted)
        {
            if (string.IsNullOrEmpty(wanted)) return 0;

            // Actor id 0 is the unassigned sentinel, so a hit there would be meaningless anyway;
            // starting at 1 also keeps "found nothing" and "found actor 0" from colliding.
            for (ushort id = 1; id < ProtocolConstants.MAX_ACTORS; id++)
            {
                if (string.Equals(names.NameOf(id), wanted, StringComparison.Ordinal)) return id;
            }

            return 0;
        }

        /// <summary>
        /// The name the SERVER would use for a logical roster name, or null when the roster does
        /// not carry it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>IRONFRONT_LANEB_ROSTER</c> is written by <c>tools/run-lane-b.ps1</c>, which is the
        /// one place that already owns the logical-name-to-player-id mapping. Putting <c>#5002</c>
        /// into the three <c>combat-*.json</c> files instead would couple every recorded
        /// programme to that script's magic ids and rot silently the day they change.
        /// </para>
        /// <para>
        /// Format: <c>DRIVER=5001,OBS-A=5002,OBS-B=5003</c>. Absent or unparsable returns null
        /// and the literal name stands, so an ordinary programme naming a real username is
        /// unaffected.
        /// </para>
        /// </remarks>
        private static string RosterAlias(string logicalName)
        {
            if (string.IsNullOrEmpty(logicalName)) return null;

            if (_roster == null)
            {
                _roster = new System.Collections.Generic.Dictionary<string, string>(
                    StringComparer.Ordinal);

                string raw = Environment.GetEnvironmentVariable("IRONFRONT_LANEB_ROSTER");
                if (!string.IsNullOrEmpty(raw))
                {
                    foreach (string pair in raw.Split(','))
                    {
                        int eq = pair.IndexOf('=');
                        if (eq <= 0 || eq == pair.Length - 1) continue;

                        string name = pair.Substring(0, eq).Trim();
                        string id = pair.Substring(eq + 1).Trim();
                        if (name.Length > 0 && id.Length > 0) _roster[name] = "#" + id;
                    }
                }
            }

            return _roster.TryGetValue(logicalName, out string alias) ? alias : null;
        }

        private static System.Collections.Generic.Dictionary<string, string> _roster;

        private bool TryPresenter(out NetClientCombatPresenter presenter)
        {
            if (_presenter == null)
            {
                _presenter = UnityEngine.Object.FindFirstObjectByType<NetClientCombatPresenter>(
                    FindObjectsInactive.Include);
            }

            presenter = _presenter;
            return presenter != null;
        }

        private bool TryRegistry(out RemoteActorRegistry registry)
        {
            if (_registry == null)
            {
                _registry = UnityEngine.Object.FindFirstObjectByType<RemoteActorRegistry>(
                    FindObjectsInactive.Include);
            }

            registry = _registry;
            return registry != null;
        }
    }
}
#endif
