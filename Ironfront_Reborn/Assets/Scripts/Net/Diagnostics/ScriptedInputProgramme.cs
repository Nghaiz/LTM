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
// #nullable disable, because this file is compiled TWICE: by Unity, whose
// Assembly-CSharp has no nullable context, and by Ironfront.Net.Replication.Tests
// through a <Compile Include> link, where Directory.Build.props turns every nullable
// warning into an error. Annotating for the second compiler emits CS8632 in the
// first; disabling the context satisfies both and changes no generated code.
#nullable disable

using System;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// One recorded input programme: an ordered list of held-input steps, each with a duration
    /// and an optional checkpoint name. Phase-3D lane B.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No UnityEngine here, on purpose</b> — the same arrangement <c>IInputSource</c>'s file
    /// explains. This model and <see cref="ScriptedInputCursor"/> are compiled a second time by
    /// <c>Ironfront.Net.Replication.Tests</c> through a <c>&lt;Compile Include&gt;</c> link,
    /// which is the only way anything under <c>Assets/</c> is reachable by <c>dotnet test</c>.
    /// Adding a <c>using UnityEngine;</c> to either silently drops it out of coverage.
    /// <c>JsonUtility</c> still parses it: it needs public fields and
    /// <see cref="SerializableAttribute"/>, and that attribute is <c>System</c>'s, not Unity's.
    /// </para>
    /// <para>
    /// <b>Held levels, not edges.</b> <c>IInputSource</c> reports what is currently pressed and
    /// every consumer re-reads it per frame, so a programme says "fire held for 0.4 s" rather
    /// than "press fire". A step is therefore a state the client sits in, and the run is the
    /// concatenation of those states — which is what makes two clients fed the same programme
    /// comparable frame for frame.
    /// </para>
    /// </remarks>
    [Serializable]
    public sealed class ScriptedInputProgramme
    {
        /// <summary>Name carried into the artifact records, so a capture names its programme.</summary>
        public string name = "unnamed";

        /// <summary>The steps, in order. An empty programme finishes immediately.</summary>
        public ScriptedInputStep[] steps = Array.Empty<ScriptedInputStep>();

        /// <summary>
        /// The 1-based index of the first step whose verbs contradict each other, or 0 when
        /// none do. Ledger <b>X-44</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>At load time, because a programme defect is not a runtime condition.</b>
        /// <see cref="ScriptedInputStep.approachVehicle"/> and
        /// <see cref="ScriptedInputStep.aimAtPlayer"/> name two different targets, and whichever
        /// the code picks, the other one is a sentence somebody wrote and the run did not
        /// honour. Discovering that from an artifact means reading a bearing and inferring what
        /// it was pointed at.
        /// </para>
        /// <para>
        /// <b>1-based, and 0 is "clean".</b> A step index is quoted to a human editing a JSON
        /// file, where the first step is step 1; returning -1 for clean and 0 for the first step
        /// would make the two most common values adjacent and easy to invert.
        /// </para>
        /// </remarks>
        public int FindConflictingStep()
        {
            if (steps == null) return 0;

            for (int i = 0; i < steps.Length; i++)
            {
                ScriptedInputStep step = steps[i];
                if (step == null) continue;

                if (step.approachVehicle && !string.IsNullOrEmpty(step.aimAtPlayer)) return i + 1;
            }

            return 0;
        }

        /// <summary>Total scripted duration in seconds.</summary>
        public float TotalSeconds
        {
            get
            {
                float total = 0f;
                if (steps == null) return 0f;
                for (int i = 0; i < steps.Length; i++)
                {
                    if (steps[i] != null) total += steps[i].seconds;
                }

                return total;
            }
        }
    }

    /// <summary>
    /// One held-input state and how long it is held for.
    /// </summary>
    /// <remarks>
    /// <b><see cref="yawDegrees"/> is absolute and <see cref="yawRateDegreesPerSecond"/> is
    /// added to it.</b> The protocol carries an absolute facing (<c>C_INPUT</c>, spec § 4.2) and
    /// so does <c>MoveInput</c>, so a programme that only had a rate could never state where a
    /// run starts — and two clients that start facing differently cannot be compared. The rate
    /// exists because a stationary turret sweep is the shape check 12 needs.
    /// </remarks>
    [Serializable]
    public sealed class ScriptedInputStep
    {
        /// <summary>
        /// Optional. Captured once, when the step is entered — never on the way out, so a
        /// checkpoint names the state the client is about to be in for <see cref="seconds"/>.
        /// </summary>
        public string checkpoint = null;

        /// <summary>How long this state is held. Non-positive is treated as an instant step.</summary>
        public float seconds = 1f;

        /// <summary>Strafe axis, -1..1.</summary>
        public float moveX = 0f;

        /// <summary>Forward axis, -1..1.</summary>
        public float moveZ = 0f;

        /// <summary>Absolute facing in degrees at the start of the step.</summary>
        public float yawDegrees = 0f;

        /// <summary>Degrees per second added to <see cref="yawDegrees"/> while the step runs.</summary>
        public float yawRateDegreesPerSecond = 0f;

        /// <summary>Absolute aim pitch, -90..90. Positive looks down (<see cref="ScriptedAim"/>).</summary>
        public float pitchDegrees = 0f;

        /// <summary>
        /// Display name of another player to face for the duration of this step. Empty means
        /// the step's own <see cref="yawDegrees"/>/<see cref="pitchDegrees"/> stand.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A NAME, not an actor id.</b> Actor ids are the server's to hand out and depend on
        /// join order, so a programme written against one would be a programme about one run.
        /// The display name is the runner's own input (<c>IRONFRONT_CLIENT_DISPLAY_NAME</c>) and
        /// resolves through the same <c>PlayerNameTable</c> the killfeed reads — which check 1
        /// grades anyway, so a programme that cannot find its target by name has already found
        /// the defect it was going to look for.
        /// </para>
        /// <para>
        /// <b>Unresolvable is not fatal.</b> The target may not have joined yet, or may be dead.
        /// The step falls back to its declared yaw and pitch and the recorder writes that the
        /// target was missing, rather than the run ending on a name lookup.
        /// </para>
        /// </remarks>
        public string aimAtPlayer = null;

        /// <summary>
        /// Walk toward <see cref="aimAtPlayer"/> instead of using <see cref="moveZ"/>, stopping
        /// at <see cref="holdDistanceMeters"/>. Ignored when no target resolves.
        /// </summary>
        public bool approach = false;

        /// <summary>How close <see cref="approach"/> gets before it stops. Metres.</summary>
        public float holdDistanceMeters = 8f;

        /// <summary>
        /// Walk toward the nearest replicated VEHICLE instead of toward a player, stopping at
        /// <see cref="vehicleHoldDistanceMeters"/>. Ledger <b>X-44</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A vehicle has no display name, which is the whole of the row.</b>
        /// <see cref="approach"/> resolves through <c>ScriptedTargetSolver.Solve(aimAtPlayer)</c>,
        /// which takes a player display name and scans <c>PlayerNameTable</c>; there is no such
        /// table for vehicles and no name to put in one. So before this verb a driver programme
        /// whose first step is <i>enter a vehicle</i> only worked if a vehicle happened to be
        /// parked within <c>SeatArbiter.MaxSeatReachMetres</c> of the pinned spawn point — not a
        /// property any run controls.
        /// </para>
        /// <para>
        /// <b>Resolved against the CLIENT's own vehicle registry</b>, which is the only vehicle
        /// truth a client has: every vehicle in a networked world arrives from
        /// <c>S_VEHICLE_SPAWN</c> with the id the server gave it.
        /// </para>
        /// <para>
        /// <b>Mutually exclusive with <see cref="aimAtPlayer"/>, and the vehicle wins loudly.</b>
        /// A step naming both is a programme bug; a silent precedence would grade a run nobody
        /// wrote.
        /// </para>
        /// </remarks>
        public bool approachVehicle = false;

        /// <summary>
        /// How far <see cref="approachVehicle"/> looks for a vehicle. Metres.
        /// </summary>
        /// <remarks>
        /// Generous on purpose: the point of the verb is that the vehicle is NOT already in
        /// reach. A search that found nothing leaves the step's declared yaw and
        /// <see cref="moveZ"/> standing and the recorder writes the miss down, so an
        /// over-generous radius costs nothing and an under-generous one silently reproduces the
        /// defect.
        /// </remarks>
        public float vehicleSearchMetres = 120f;

        /// <summary>
        /// How close <see cref="approachVehicle"/> gets before it stops. Metres.
        /// </summary>
        /// <remarks>
        /// <b>A separate field from <see cref="holdDistanceMeters"/>, because 8 m is the wrong
        /// answer for a vehicle and the right one for a player.</b> A step that precedes a
        /// <see cref="seatToggle"/> must stop INSIDE <c>SeatArbiter.MaxSeatReachMetres</c> (6 m),
        /// which the arbiter measures from its own transforms — stopping outside it produces
        /// <c>RejectedTooFar</c>, a round trip spent to be told no. Sharing one field and
        /// documenting "set it lower for vehicles" would make the default silently wrong, so the
        /// default here is 4 m and a test pins it against the arbiter's constant rather than
        /// against a number restated in a comment.
        /// </remarks>
        public float vehicleHoldDistanceMeters = 4f;

        public bool fire = false;
        public bool aim = false;
        public bool reload = false;
        public bool jump = false;
        public bool sprint = false;
        public bool crouch = false;
        public bool use = false;

        /// <summary>
        /// Request a respawn once, on entering this step. An EDGE, not a hold.
        /// </summary>
        /// <remarks>
        /// Respawn is <c>C_SPAWN_REQUEST</c>, a reliable message of its own, so holding it would
        /// mean asking every frame. The driver still gates on its own death clock; this only
        /// says the player pressed the key.
        /// </remarks>
        public bool respawn = false;

        /// <summary>
        /// Ask to enter the nearest seat, or to leave the current one, once on entering this
        /// step. An EDGE, not a hold.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is what checks B-7 and B-13 were blocked on, and it is not <see cref="use"/>.</b>
        /// A seat change is <c>C_SEAT_REQUEST</c>, a reliable message of its own; <c>use</c> is
        /// bit 10 of <c>C_INPUT</c>, held for the step, and no server code reads it. A programme
        /// could therefore describe a player pressing Use and could not describe one entering a
        /// vehicle — which is why ledger X-30 reads as a client capability gap rather than as
        /// programme work.
        /// </para>
        /// <para>
        /// <b>Enter or leave is decided by the client, from the server's own last
        /// <c>S_SEAT_CHANGE</c></b>, not by the programme. A step cannot say "enter" and be
        /// wrong about whether this actor is already seated — see <c>ClientSeatRequester</c>.
        /// </para>
        /// </remarks>
        public bool seatToggle = false;

        /// <summary>
        /// Weapon slot to select, 0..3. Negative means "leave the weapon alone".
        /// </summary>
        /// <remarks>
        /// <para>
        /// Held for the step, not edged: <c>InputButtons.SwitchWeapon0..3</c> are ordinary bits
        /// on <c>C_INPUT</c> (protocol-spec § 4.2 bits 11-14) and the server edges them itself.
        /// </para>
        /// <para>
        /// <b>This is how a grenade is thrown</b>, and the reason check 4 needs no new wire bit:
        /// select the gear slot, then <c>fire</c>. Bit 7 was <c>ThrowGrenade</c> and V7-D10
        /// retired it rather than implementing it, because a dedicated throw bit is a second
        /// route to firing that does not pass <c>Weapon.CanFire()</c>.
        /// </para>
        /// </remarks>
        public int switchWeaponSlot = -1;
    }
}
#endif
