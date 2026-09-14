using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// What a vehicle pad's spawner found in the way, in the one dimension that decides whether
    /// an operator should investigate: whose body it is, and whether that body is alive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The give-up line could not say any of this, and that was its own defect.</b> Island
    /// logged <c>the pad is obstructed by 'Bone_002' (layer Hitbox)</c>, which is true of a bot
    /// standing on the pad — legitimate, § 7 — and equally true of a corpse whose colliders were
    /// never switched off — the § 2.4 defect. One bit separates "the pad is waiting" from "the
    /// cleanup is broken", and the message omitted exactly that bit.
    /// </para>
    /// <para>
    /// <b><see cref="NotProbed"/> is the one that was actually happening.</b>
    /// <c>VehicleSpawner.SpawnIsBlocked</c> answers "blocked" for TWO reasons: physics found
    /// something, or the vehicle-id pool had nothing left. The second returns before the
    /// <c>OverlapSphere</c> runs, so no collider is read at all — and the message still named
    /// one, out of a <c>static</c> scratch array that <c>OverlapSphereNonAlloc</c> does not
    /// clear. That is how Island printed a blocker on layer <c>SeatedHitbox</c>: the pad mask
    /// is 5376, which has no bit 16, so that collider provably did not come from the query
    /// being reported.
    /// </para>
    /// </remarks>
    public enum PadBlockerKind : byte
    {
        /// <summary>
        /// No physics query ran. The refusal was capacity — no free vehicle id — and the pad
        /// may be completely clear.
        /// </summary>
        NotProbed = 0,

        /// <summary>The query ran and the collider it named is gone by the time it is read.</summary>
        Gone = 1,

        /// <summary>Scenery, a wreck, a parked vehicle. Nothing to do with an actor.</summary>
        NotAnActor = 2,

        /// <summary>A living body standing on the pad. Allowed by § 7; the pad is waiting.</summary>
        LivingActor = 3,

        /// <summary>
        /// A corpse whose pad-blocking colliders ARE switched off — so whatever is in the way
        /// is not one of them, and the cleanup is not the thing to go and look at.
        /// </summary>
        CleanedCorpse = 4,

        /// <summary>A corpse whose pad-blocking colliders were never switched off. § 2.4.</summary>
        UncleanedCorpse = 5,
    }

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
        /// <para>
        /// <b>And a give-up line naming layer <c>SeatedHitbox</c> is not a reason to add the
        /// bit — it is proof the line was lying.</b> The mask is what
        /// <c>Physics.OverlapSphereNonAlloc</c> is handed, so a query with no bit 16 cannot
        /// RETURN a layer-16 collider. Island printed one anyway, which means the collider it
        /// named came from some earlier query and not from the refusal being reported:
        /// <c>VehicleSpawner.SpawnIsBlocked</c> answers "blocked" for lack of a vehicle id
        /// without running physics at all, and the scratch array it reads is <c>static</c> and
        /// is not cleared. <c>Actor.EnterSeat</c> then moves that bot's hitbox bones from layer
        /// 8 to 16, which is the layer that got printed. Widening this mask would have "fixed"
        /// a reading that was never about a corpse. See <see cref="PadBlockerKind.NotProbed"/>.
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
        /// Decides which of <see cref="PadBlockerKind"/> a spawner's refusal actually was.
        /// </summary>
        /// <param name="probeRan">
        /// Whether a physics query was executed for the refusal being reported. False means the
        /// pad was refused for vehicle-id capacity and nothing was ever asked of physics — the
        /// distinction the old message could not make and the one Island's log turned on.
        /// </param>
        /// <param name="hasBlocker">Whether that query produced a collider still alive to read.</param>
        /// <param name="blockerBelongsToActor">Whether the collider hangs off a replicated body.</param>
        /// <param name="actorIsAlive">That body's authoritative life flag.</param>
        /// <param name="corpseCollidersDisabled">
        /// Whether that body's own cleanup has run — read from the component that does the
        /// disabling rather than from this ledger, because the component is the thing that
        /// knows. A body killed by a path that never reached the death funnel is absent from
        /// the ledger entirely and would otherwise be reported as clean by omission.
        /// </param>
        /// <remarks>
        /// Static and pure. It takes no instance state, so a caller cannot get a different
        /// verdict by holding a different ledger — and <c>dotnet test</c> can grade the whole
        /// decision without an engine, which is the only reason any of this is out here rather
        /// than inside <c>VehicleSpawner</c> where <c>Assembly-CSharp</c> puts it beyond CI.
        /// </remarks>
        public static PadBlockerKind ClassifyPadBlocker(
            bool probeRan,
            bool hasBlocker,
            bool blockerBelongsToActor,
            bool actorIsAlive,
            bool corpseCollidersDisabled)
        {
            if (!probeRan) return PadBlockerKind.NotProbed;
            if (!hasBlocker) return PadBlockerKind.Gone;
            if (!blockerBelongsToActor) return PadBlockerKind.NotAnActor;
            if (actorIsAlive) return PadBlockerKind.LivingActor;

            return corpseCollidersDisabled
                ? PadBlockerKind.CleanedCorpse
                : PadBlockerKind.UncleanedCorpse;
        }

        /// <summary>
        /// The sentence a spawner's give-up line carries, so the wording is graded by CI rather
        /// than living as an interpolated string in a file no test can reach.
        /// </summary>
        /// <param name="kind">The verdict from <see cref="ClassifyPadBlocker"/>.</param>
        /// <param name="blockerDescription">
        /// The engine's own words for the collider — <c>'Bone_002' (layer Hitbox)</c>. Passed in
        /// rather than built here because a name and a layer NAME both need Unity.
        /// </param>
        /// <param name="actorId">
        /// The owning actor, or 0 for a body the registry never gave an id. Named as
        /// "unregistered" rather than as actor 0, because 0 is the spec's "unknown" and printing
        /// it as an id sends the reader looking for an actor that does not exist.
        /// </param>
        /// <remarks>
        /// Every branch says what the reader should DO, because a diagnostic that reports a
        /// state without naming the consequence is what the original line was: correct, and
        /// still leaving the next person to repeat the investigation.
        /// </remarks>
        public static string DescribePadBlocker(
            PadBlockerKind kind, string blockerDescription, ushort actorId)
        {
            string what = string.IsNullOrEmpty(blockerDescription)
                ? "an unnamed collider"
                : blockerDescription;

            string who = actorId == 0 ? "an unregistered actor" : $"actor {actorId}";

            switch (kind)
            {
                case PadBlockerKind.NotProbed:
                    return "No obstruction probe ran for this refusal: the pad was refused "
                         + "because the vehicle-id pool had no free id, so this is a CAPACITY "
                         + "refusal and the pad may be completely clear. No collider is named "
                         + "because none was read.";

                case PadBlockerKind.Gone:
                    return "The pad was obstructed by a collider that is no longer there.";

                case PadBlockerKind.NotAnActor:
                    return $"The pad is obstructed by {what}, which belongs to no actor — "
                         + "scenery, a wreck, or a parked vehicle.";

                case PadBlockerKind.LivingActor:
                    return $"The pad is obstructed by {what} on LIVING {who}, which § 7 allows: "
                         + "the pad is waiting for a body to move, not broken.";

                case PadBlockerKind.CleanedCorpse:
                    return $"The pad is obstructed by {what} on DEAD {who}, whose pad-blocking "
                         + "colliders ARE disabled — so the blocker is not one of them and the "
                         + "corpse cleanup is not what to go and look at.";

                case PadBlockerKind.UncleanedCorpse:
                    return $"The pad is obstructed by {what} on DEAD {who}, whose pad-blocking "
                         + "colliders were NEVER disabled. That is the corpse-cleanup defect "
                         + "(§ 2.4), not a busy pad.";

                default:
                    // Errors over silent fallbacks: a kind added without a sentence must say so
                    // rather than print an empty clause that reads as "nothing was wrong".
                    throw new ArgumentOutOfRangeException(
                        nameof(kind), kind, "no give-up sentence for this blocker kind");
            }
        }

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
