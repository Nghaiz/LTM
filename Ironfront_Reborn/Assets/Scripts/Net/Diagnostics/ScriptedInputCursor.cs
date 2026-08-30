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
using System.Collections.Generic;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>One checkpoint that has come due, and the programme time it came due at.</summary>
    /// <remarks>
    /// <b>The due time is carried, not inferred.</b> A capture happens after the frame that
    /// crossed into the step, so it is always slightly late; recording how late lets a reader
    /// see whether two clients' captures are comparable instead of assuming they are. On a
    /// frame that crosses several steps the lateness is the whole reason to look.
    /// </remarks>
    public struct ScriptedCheckpoint
    {
        public string Name;
        public float DueAtSeconds;
    }

    /// <summary>
    /// Walks a <see cref="ScriptedInputProgramme"/> in real time and reports which step is
    /// live, what yaw it has integrated to, and which checkpoints have come due. Phase-3D lane B.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Pure, and separated from the driver for exactly that reason</b> — this is the only
    /// arithmetic in lane B's client half, and the rest of that half is Unity wiring no gate in
    /// this repository compiles. See <see cref="ScriptedInputProgramme"/>'s remark on the
    /// <c>&lt;Compile Include&gt;</c> arrangement.
    /// </para>
    /// <para>
    /// <b>A step's remainder is carried, not discarded.</b> A 0.4 s step advanced in 1/60 s
    /// frames ends 0.00667 s into the next one, and dropping that would slide every later
    /// checkpoint further from where the programme says it is. Two clients on the same
    /// programme but different frame rates would then diverge for a reason that is this class's
    /// fault and would read as a replication defect — the whole failure mode
    /// <c>phase-3d-lane-b.md</c> § 8 row 2 exists to keep out of the results.
    /// </para>
    /// <para>
    /// <b>Checkpoints queue; they do not overwrite.</b> One long frame can cross several steps,
    /// and a single pending slot would silently keep only the last of them. The client whose
    /// window opened last has the longest first frame, so the client most likely to lose a
    /// checkpoint that way is the third one — the hardest version of the bug to notice, since
    /// the run still reports success with a shorter artifact list.
    /// </para>
    /// </remarks>
    public sealed class ScriptedInputCursor
    {
        private readonly ScriptedInputProgramme _programme;
        private readonly Queue<ScriptedCheckpoint> _due = new Queue<ScriptedCheckpoint>();
        private float _stepElapsed;
        private bool _entered;

        public ScriptedInputCursor(ScriptedInputProgramme programme)
        {
            _programme = programme ?? throw new ArgumentNullException(nameof(programme));
            StepIndex = 0;
            Yaw = FirstYaw();
        }

        /// <summary>Index of the live step, or <see cref="StepCount"/> once the run is over.</summary>
        private int _respawnConsumedForStep = -1;

        private int _seatToggleConsumedForStep = -1;

        public int StepIndex { get; private set; }

        /// <summary>How many steps the programme holds.</summary>
        public int StepCount => _programme.steps?.Length ?? 0;

        /// <summary>Seconds elapsed across the whole programme.</summary>
        public float TotalElapsed { get; private set; }

        /// <summary>Absolute facing in degrees, integrated from the live step's rate.</summary>
        public float Yaw { get; private set; }

        /// <summary>True once every step has been consumed.</summary>
        public bool Finished => StepIndex >= StepCount;

        /// <summary>The live step, or <see langword="null"/> once <see cref="Finished"/>.</summary>
        public ScriptedInputStep Current
            => StepIndex >= 0 && StepIndex < StepCount ? _programme.steps[StepIndex] : null;

        /// <summary>
        /// Advances by <paramref name="deltaSeconds"/>, crossing as many step boundaries as the
        /// elapsed time covers.
        /// </summary>
        /// <remarks>
        /// Returns false once the programme is spent, so the caller can stop the run rather than
        /// asking <see cref="Finished"/> separately and getting a different answer next frame.
        /// </remarks>
        public bool Advance(float deltaSeconds)
        {
            if (Finished) return false;
            if (deltaSeconds < 0f) throw new ArgumentOutOfRangeException(nameof(deltaSeconds));

            float clock = TotalElapsed;
            EnterStepIfNeeded(clock);

            float remaining = deltaSeconds;

            while (!Finished)
            {
                ScriptedInputStep step = Current;
                float duration = step != null && step.seconds > 0f ? step.seconds : 0f;
                float left = duration - _stepElapsed;

                if (remaining < left)
                {
                    _stepElapsed += remaining;
                    Yaw = WrapDegrees(Yaw + YawRate(step) * remaining);
                    TotalElapsed = clock + remaining;
                    return true;
                }

                // Integrate only the part of this frame that belongs to the step being left,
                // then carry the rest into the next one. See the class remark.
                Yaw = WrapDegrees(Yaw + YawRate(step) * left);
                remaining -= left;
                clock += left;

                StepIndex++;
                _stepElapsed = 0f;
                _entered = false;

                if (Finished)
                {
                    // Time past the end of the programme still elapsed; it just has no step.
                    TotalElapsed = clock + remaining;
                    return false;
                }

                EnterStepIfNeeded(clock);
            }

            TotalElapsed = clock;
            return false;
        }

        /// <summary>
        /// Takes the oldest checkpoint that has come due, in programme order.
        /// </summary>
        /// <returns>False when nothing is owed.</returns>
        /// <summary>
        /// True the first time it is asked on a step that declares <c>respawn</c>, and false
        /// every time after until a different step arrives.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Here rather than on the source, so it can be pinned.</b>
        /// <c>ScriptedInputSource</c> implements a Unity interface and cannot be linked into a
        /// test project without dragging UnityEngine in behind it; this class already is linked,
        /// and it already owns the one other edge in the harness -- <see cref="TryTakeCheckpoint"/>.
        /// </para>
        /// <para>
        /// <b>Consuming, like TryTakeCheckpoint.</b> One caller exists. A second would silently
        /// eat the press, so if one ever appears this needs a name that says so.
        /// </para>
        /// </remarks>
        public bool TryConsumeRespawn()
        {
            ScriptedInputStep step = Current;
            if (step == null || !step.respawn) return false;
            if (_respawnConsumedForStep == StepIndex) return false;

            _respawnConsumedForStep = StepIndex;
            return true;
        }

        /// <summary>
        /// True the first time it is asked on a step that declares <c>seatToggle</c>, and false
        /// every time after until a different step arrives.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The seat edge, and it is deliberately not <c>step.use</c>.</b> A programme's
        /// <c>use</c> is a LEVEL held for the whole step and packed into
        /// <c>InputButtons.Use</c>; a seat request is a reliable message that must be sent once.
        /// Driving one off the other would send a request per tick for the length of the step.
        /// Ledger X-30.
        /// </para>
        /// <para>
        /// <b>Consuming, exactly like <see cref="TryConsumeRespawn"/>, with a separate counter.</b>
        /// Sharing one counter would let a step declaring both <c>respawn</c> and
        /// <c>seatToggle</c> deliver whichever was asked for first and silently swallow the
        /// other.
        /// </para>
        /// </remarks>
        public bool TryConsumeSeatToggle()
        {
            ScriptedInputStep step = Current;
            if (step == null || !step.seatToggle) return false;
            if (_seatToggleConsumedForStep == StepIndex) return false;

            _seatToggleConsumedForStep = StepIndex;
            return true;
        }

        /// <summary>
        /// Whether the live step holds the minimap open. A level, so it is not consumed.
        /// </summary>
        /// <remarks>
        /// Not a <c>TryConsume</c> like respawn and seat-toggle: those are edges that must be
        /// delivered exactly once, and this is a key held down. <c>MinimapUi</c> polls it every
        /// frame through <c>MinimapUi.HoldSource</c>. Ledger X-61.
        /// </remarks>
        public bool HoldMinimap => Current != null && Current.holdMinimap;

        public bool TryTakeCheckpoint(out ScriptedCheckpoint checkpoint)
        {
            if (_due.Count == 0)
            {
                checkpoint = default;
                return false;
            }

            checkpoint = _due.Dequeue();
            return true;
        }

        private void EnterStepIfNeeded(float dueAtSeconds)
        {
            if (_entered || Finished) return;

            _entered = true;
            ScriptedInputStep step = Current;
            if (step == null) return;

            // The absolute facing the step declares replaces whatever the previous step
            // integrated to. A programme that means "keep turning from here" says so by
            // repeating the yaw it ended on; silently inheriting would make a step's stated
            // yaw a lie in every case but the first.
            Yaw = WrapDegrees(step.yawDegrees);

            if (!string.IsNullOrEmpty(step.checkpoint))
            {
                _due.Enqueue(new ScriptedCheckpoint
                {
                    Name = step.checkpoint,
                    DueAtSeconds = dueAtSeconds,
                });
            }
        }

        private float FirstYaw()
        {
            ScriptedInputStep first = Current;
            return first != null ? WrapDegrees(first.yawDegrees) : 0f;
        }

        private static float YawRate(ScriptedInputStep step)
            => step != null ? step.yawRateDegreesPerSecond : 0f;

        private static float WrapDegrees(float degrees)
        {
            float wrapped = degrees % 360f;
            return wrapped < 0f ? wrapped + 360f : wrapped;
        }
    }
}
#endif
