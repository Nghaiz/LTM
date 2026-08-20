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

        public bool fire = false;
        public bool aim = false;
        public bool reload = false;
        public bool jump = false;
        public bool sprint = false;
        public bool crouch = false;
        public bool use = false;
    }
}
