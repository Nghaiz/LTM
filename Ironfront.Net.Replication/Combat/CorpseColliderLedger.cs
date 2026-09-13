using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// Tracks whether each corpse's gameplay colliders have been taken out of the way, and names
    /// the ones that have not. Protocol-10 handoff § 7, last paragraph.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The symptom is a vehicle pad that never spawns again.</b> Island logged a pad refused
    /// for thirty consecutive retries by a collider called <c>Bone_002</c> on layer
    /// <c>Hitbox</c> — a ragdoll bone belonging to an actor that had been dead for minutes.
    /// <c>VehicleSpawner</c> re-tests the pad once a second and eventually gives up, so one
    /// corpse that was never cleaned up costs the map a vehicle for the rest of the round.
    /// </para>
    /// <para>
    /// <b>A living actor standing on a pad is NOT this.</b> § 7 is explicit: a real body blocking
    /// a spawn is legitimate, and a check that could not tell the two apart would "fix" it by
    /// spawning a jeep inside a player. So this ledger only ever holds actors that have DIED —
    /// a living actor is not in it, and <see cref="IsStale"/> answers false for one by
    /// construction rather than by a test it might get wrong.
    /// </para>
    /// <para>
    /// <b>It reports; it does not clean.</b> Disabling the colliders needs the engine and belongs
    /// to the component that owns them (<c>NetServerActor</c>); what belongs here is the deadline
    /// and the verdict, because those are the parts CI can grade. A stale corpse is a defect in
    /// the cleanup, and the value of this class is that the defect has a name and a counter
    /// instead of being a pad that quietly stopped working.
    /// </para>
    /// </remarks>
    public sealed class CorpseColliderLedger
    {
        /// <summary>
        /// Layers a vehicle pad refuses to spawn into: <c>Hitbox</c> (8), <c>Ragdoll</c> (10) and
        /// <c>Vehicle</c> (12).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Declared here and not read from <c>VehicleSpawner.SPAWN_BLOCK_MASK</c>, because it
        /// cannot be.</b> That constant lives in <c>Assembly-CSharp</c>, which is a predefined
        /// assembly no <c>.asmdef</c> may reference — so a library that needs the same number has
        /// no way to import it, and a test that needs it has no way to see it at all. Spelling
        /// the layers out with their names is the closest thing to a shared definition available;
        /// the value is pinned by <c>CorpseColliderCleanupTests</c> so a silent divergence is a
        /// red test rather than a pad that stops working.
        /// </para>
        /// <para>
        /// <b><c>SeatedHitbox</c> (16) is deliberately absent.</b> Island's report names
        /// <c>Hitbox</c>/<c>SeatedHitbox</c> together, but the pad mask tests only the first —
        /// so a seated hitbox left behind does not block a pad, and adding the bit here would
        /// make this ledger disagree with the thing it is modelling. A corpse's seated hitboxes
        /// are still cleaned up; they are simply not what this verdict is about.
        /// </para>
        /// </remarks>
        public const int SpawnBlockMask = (1 << 8) | (1 << 10) | (1 << 12);

        /// <summary>
        /// Seconds a corpse's blocking colliders may still be present after death.
        /// </summary>
        /// <remarks>
        /// One second, which is under <see cref="ProtocolConstants.RESPAWN_SECONDS"/> and well
        /// under <c>VehicleSpawner</c>'s one-second pad retry — so a corpse that is cleaned up
        /// on time cannot lose a pad even one retry, and a corpse that is late is reported before
        /// the retries have run out rather than after the pad has given up.
        /// </remarks>
        public const float CleanupDeadlineSeconds = 1f;

        private readonly float[] _diedAt;
        private readonly bool[] _dead;
        private readonly bool[] _collidersDisabled;

        public CorpseColliderLedger(int maxActors = ProtocolConstants.MAX_ACTORS)
        {
            if (maxActors <= 0) throw new ArgumentOutOfRangeException(nameof(maxActors));

            _diedAt            = new float[maxActors + 1];
            _dead              = new bool[maxActors + 1];
            _collidersDisabled = new bool[maxActors + 1];
        }

        /// <summary>Corpses observed still blocking a pad past the deadline.</summary>
        /// <remarks>
        /// Counted on the observation rather than on the death, so one corpse observed on ten
        /// consecutive ticks counts ten times. That is the useful shape: the number answers "how
        /// long was a pad blocked", which is the question the thirty-retry log asked.
        /// </remarks>
        public long StaleObservations { get; private set; }

        /// <summary>Whether a layer is one a vehicle pad refuses to spawn into.</summary>
        public static bool BlocksVehicleSpawn(int layer)
            => layer >= 0 && layer < 32 && (SpawnBlockMask & (1 << layer)) != 0;

        /// <summary>
        /// Starts this actor's cleanup deadline. Idempotent within one life, for
        /// <see cref="ServerRespawnGate.TryBeginDeath"/>'s reason.
        /// </summary>
        public void NoteDeath(ushort actorId, float nowSeconds)
        {
            if (actorId >= _dead.Length) return;
            if (_dead[actorId]) return;

            _dead[actorId]              = true;
            _diedAt[actorId]            = nowSeconds;
            _collidersDisabled[actorId] = false;
        }

        /// <summary>Records that the blocking colliders are out of the way.</summary>
        public void NoteCollidersDisabled(ushort actorId)
        {
            if (actorId >= _dead.Length) return;
            _collidersDisabled[actorId] = true;
        }

        /// <summary>
        /// Records a respawn: the colliders are back and this actor is no longer a corpse.
        /// </summary>
        /// <remarks>
        /// Clearing <see cref="_dead"/> is what keeps a respawned player standing on a pad out of
        /// <see cref="IsStale"/> — they block it, legitimately, exactly like anyone else who is
        /// alive and in the way.
        /// </remarks>
        public void NoteRespawn(ushort actorId)
        {
            if (actorId >= _dead.Length) return;

            _dead[actorId]              = false;
            _diedAt[actorId]            = 0f;
            _collidersDisabled[actorId] = false;
        }

        /// <summary>Whether this actor's corpse is still blocking pads past the deadline.</summary>
        /// <remarks>
        /// Reads as a question and counts as an observation, which is a side effect worth naming:
        /// the alternative was a separate sweep that every caller would have to remember to run,
        /// and a counter nobody increments is a counter nobody can trust.
        /// </remarks>
        public bool IsStale(ushort actorId, float nowSeconds)
        {
            if (actorId >= _dead.Length) return false;
            if (!_dead[actorId]) return false;
            if (_collidersDisabled[actorId]) return false;
            if (nowSeconds - _diedAt[actorId] < CleanupDeadlineSeconds) return false;

            StaleObservations++;
            return true;
        }

        /// <summary>Whether this actor is recorded as a corpse at all.</summary>
        public bool IsCorpse(ushort actorId) => actorId < _dead.Length && _dead[actorId];

        /// <summary>Whether this corpse's blocking colliders have been disabled.</summary>
        public bool CollidersDisabled(ushort actorId)
            => actorId < _dead.Length && _collidersDisabled[actorId];

        /// <summary>Forgets every corpse. Round teardown.</summary>
        public void Reset()
        {
            for (int i = 0; i < _dead.Length; i++)
            {
                _dead[i]              = false;
                _diedAt[i]            = 0f;
                _collidersDisabled[i] = false;
            }

            StaleObservations = 0;
        }
    }
}
