using System;
using Ironfront.Net.Replication.Match;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// One capture point as the authority needs to see it at construction time.
    /// </summary>
    /// <remarks>
    /// Radius and capture speed are per point, which is the whole reason this struct exists:
    /// before phase-V8 they were two controller-wide floats and a level designer had no way to
    /// make one flag slower to take than another.
    /// </remarks>
    public readonly struct CapturePointDefinition
    {
        public readonly Vector3 Position;

        /// <summary>The point's own capture radius.</summary>
        public readonly float Radius;

        /// <summary>
        /// Ownership gained per second per attacker.
        /// </summary>
        /// <remarks>
        /// <b>Zero and negative mean different things and must not be conflated.</b> Zero is how
        /// an uncapturable point is expressed — an HQ still counts for spawning, bleed and
        /// elimination, but never moves — so zero cannot also mean "the author left this blank".
        /// Negative is the unset signal, and the caller substitutes its own default for it.
        /// </remarks>
        public readonly float CaptureSpeed;

        /// <summary>For the id-order log line. Never used as an identity.</summary>
        public readonly string Name;

        public CapturePointDefinition(in Vector3 position, float radius, float captureSpeed, string name)
        {
            Position     = position;
            Radius       = radius;
            CaptureSpeed = captureSpeed;
            Name         = name;
        }
    }

    /// <summary>
    /// The scene's capture points, indexed by the id the wire uses, with a write path back into
    /// them. Phase-V8 tasks 2 and 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a seam and not a <c>CapturePoint[]</c> field.</b> Phase-V8 D6 asked for
    /// <c>MatchController._capturePoints</c> to change type from <c>Transform[]</c> to
    /// <c>CapturePoint[]</c>. It cannot: <c>CapturePoint</c> compiles into
    /// <c>Assembly-CSharp</c>, which is compiled last and which no assembly definition may
    /// reference — the same constraint that produced <c>ISpawnPointDirectory</c> and
    /// <c>IGameplayActorSource</c>, documented at length in <c>IronfrontNetBindings</c>.
    /// D6's actual content — radius and capture speed authored per point, id staying the array
    /// index — is delivered through here instead.
    /// </para>
    /// <para>
    /// <b>And it retires D7's risk rather than mitigating it.</b> The type change was the
    /// phase's only score-20 risk because Unity drops serialized references when a field's type
    /// changes, so the array would have come back all-null and the server would have quietly
    /// played deathmatch. No serialized field changes type here, so no scene binding is lost and
    /// no rebinding step is owed to anyone. The name-ordinal fallback below still exists, for
    /// the different and pre-existing case of a scene that never authored the array at all.
    /// </para>
    /// <para>
    /// <b>Indexed, and bound once.</b> <see cref="Bind"/> fixes the id order at
    /// <c>Awake</c>; every call afterwards is by index, so the tick path does no lookup, no
    /// allocation and no <c>GetComponent</c>.
    /// </para>
    /// </remarks>
    public interface ICapturePointDirectory
    {
        /// <summary>
        /// Fixes the id order and returns how many points resolved.
        /// </summary>
        /// <param name="authored">
        /// The controller's authored transforms, in id order. Slots that carry no capture point
        /// are skipped by the implementation and reported through <paramref name="skipped"/>.
        /// </param>
        /// <param name="discovered">
        /// True when <paramref name="authored"/> was empty and the scene was searched instead.
        /// The order is then name-ordinal, which is deterministic across runs and platforms but
        /// is NOT what a level designer authored — the caller logs it at error with the resolved
        /// id order, per "Errors Over Silent Fallbacks".
        /// </param>
        /// <param name="skipped">Authored slots that held no capture point.</param>
        int Bind(Transform[] authored, out bool discovered, out int skipped);

        /// <summary>Points bound. Zero before <see cref="Bind"/>, and on a deathmatch map.</summary>
        int Count { get; }

        /// <summary>The authored values for one bound point.</summary>
        CapturePointDefinition GetDefinition(int index);

        /// <summary>
        /// The single write path into the scene component's ownership state. Phase-V8 D3.
        /// </summary>
        /// <param name="index">Bound index, which is also the wire id.</param>
        /// <param name="spawnPointOwner">
        /// The team, already mapped to <c>SpawnPoint.owner</c>'s convention by
        /// <see cref="CapturePointOwnership.ToSpawnPointOwner"/> — <c>-1</c> is neutral.
        /// </param>
        /// <param name="control">0..1 capture progress, for the flag-pole height.</param>
        /// <param name="contested">
        /// Whether somebody hostile to the owner is inside the radius, as
        /// <see cref="RefreshPresence"/> last computed it. NOT the wire's contested flag, which
        /// means "both teams present" — a point owned by team 0 with only team 1 standing on it
        /// is contested for spawning purposes and is not contested on the wire, and it is the
        /// spawning sense that decides where a defender lands.
        /// </param>
        void ApplyAuthoritativeOwner(int index, int spawnPointOwner, float control, bool contested);

        /// <summary>
        /// Recomputes one point's contested-spawn safe directions from authoritative presence,
        /// and reports whether anyone hostile to its owner is inside the radius.
        /// </summary>
        /// <remarks>
        /// Phase-V8 D4. Disabling the scene component's own 1 Hz scan wholesale would leave
        /// these flags stuck at all-true, which turns safe spawning into random spawning with
        /// nothing logged anywhere — the silent degradation this phase exists to remove.
        /// </remarks>
        bool RefreshPresence(int index, ReadOnlySpan<ActorPresence> actors);

        /// <summary>
        /// Scene spawn points owned by <paramref name="team"/>, counted the way
        /// <c>ActorManager.HasSpawnPoint</c> counts them. Phase-V8 D10 — every spawn point,
        /// including uncapturable HQs, not just capture points.
        /// </summary>
        int CountSpawnPointsOwnedBy(int team);

        /// <summary>
        /// The point's CURRENT owner as the scene holds it: -1 neutral, 0 team 0, 1 team 1.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Read at <c>Start</c>, never at <c>Awake</c>, and that is the whole reason this is
        /// an accessor rather than a field on <see cref="CapturePointDefinition"/>.</b>
        /// <c>CapturePoint.Start</c> is what settles the OPENING ownership — it applies
        /// <c>GameManager.reverseMode</c> (which swaps teams 0 and 1) and <c>assaultMode</c>
        /// (which hands a neutral point to team 1) — and its own remark says the server "then
        /// adopts it as its own initial value". The definitions are built in
        /// <c>MatchController.Awake</c>, before any of that has run, so a field baked in there
        /// would carry the pre-swap value and a reversed match would start with the two teams'
        /// bases exchanged on the server only.
        /// </para>
        /// <para>
        /// Reading it here also keeps the mode logic in ONE place. The alternative — re-applying
        /// reverse/assault on the server — is two implementations of the same rule, free to
        /// disagree, which is the drift <c>development-principles.md</c> § SSOT exists to stop.
        /// </para>
        /// </remarks>
        int GetOwner(int index);
    }
}
