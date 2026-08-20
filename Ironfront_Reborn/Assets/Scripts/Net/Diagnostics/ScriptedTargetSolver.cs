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

            public float Yaw;
            public float Pitch;

            /// <summary>Planar distance in metres — what an approach step stops on.</summary>
            public float Distance;
        }

        private NetClientCombatPresenter _presenter;
        private RemoteActorRegistry _registry;

        private string _cachedName;
        private int _cachedRevision = -1;
        private ushort _cachedActorId;

        private int _solvedFrame = -1;
        private string _solvedName;

        /// <summary>The last name a step asked for. Recorded per checkpoint.</summary>
        public string LastRequestedName { get; private set; }

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
                && string.Equals(_solvedName, playerName, StringComparison.Ordinal))
            {
                return Last;
            }

            _solvedFrame = Time.frameCount;
            _solvedName = playerName;
            LastRequestedName = playerName;

            var miss = default(Solution);

            if (string.IsNullOrEmpty(playerName)) { Last = miss; return miss; }

            FpsActorController local = FpsActorController.instance;
            if (local == null) { Last = miss; return miss; }

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

            Vector3 from = local.transform.position;
            Vector3 to = target.position;

            // Both sides raised by the eye height: ServerCombatAuthority.EyePosition raises the
            // SHOOTER, so aiming at the target's origin is a downward shot from 1.6 m at a point
            // 1.6 m below its head. See ScriptedAim.DefaultAimHeight.
            var solution = new Solution
            {
                Resolved = true,
                ActorId = actorId,
                Yaw = ScriptedAim.YawDegrees(from.x, from.z, to.x, to.z),
                Pitch = ScriptedAim.PitchDegrees(
                    from.x, from.y + ScriptedAim.DefaultAimHeight, from.z,
                    to.x, to.y + ScriptedAim.DefaultAimHeight, to.z),
                Distance = ScriptedAim.PlanarDistance(from.x, from.z, to.x, to.z),
            };

            Last = solution;
            return solution;
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

            ushort found = 0;
            for (ushort id = 0; id < ProtocolConstants.MAX_ACTORS; id++)
            {
                if (string.Equals(names.NameOf(id), playerName, StringComparison.Ordinal))
                {
                    found = id;
                    break;
                }
            }

            _cachedName = playerName;
            _cachedRevision = names.Revision;
            _cachedActorId = found;
            return found;
        }

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
