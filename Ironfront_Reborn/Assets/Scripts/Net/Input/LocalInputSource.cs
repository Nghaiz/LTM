using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Keyboard and mouse. After phase-00 task 3 this is the only place in
    /// <c>FpsActorController</c>'s gameplay path that touches <c>UnityEngine.Input</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every read is live, and that is what makes the refactor safe.</b> The obvious design
    /// is to latch a frame's input in a <c>Sample()</c> and serve it from fields. It is also the
    /// design that changes behaviour: <c>Actor.FixedUpdate</c> reads <c>SwimInput</c> and
    /// <c>Actor.Update</c> reads <c>Fire</c>/<c>Aiming</c>/<c>Crouch</c>, so the two run at
    /// different rates against the same source, and a latch makes one of them see a value one
    /// frame stale. The original code called <c>Input.GetButton</c> at the point of use; so does
    /// this. Phase-00 criterion 5 is "single-player still plays exactly as before", and a
    /// property that forwards straight to the same call is the only version of this class that
    /// can claim it.
    /// </para>
    /// <para>
    /// <b>Yaw and Pitch are read from the camera, not accumulated.</b> Mouse-look is owned by
    /// <c>FirstPersonController.MouseLook</c>, not by this class (docs/codebase-map.md § 4).
    /// Integrating a second yaw here would produce a number that drifts away from where the
    /// player is actually looking within seconds. Reporting the camera's real angle cannot
    /// drift, because it is not a copy.
    /// </para>
    /// <para>
    /// <b>Not compiled into the test project</b> — it uses <c>UnityEngine</c>. The parts worth
    /// testing were split out into <see cref="InputButtonPacker"/> deliberately; keep this class
    /// a wiring layer with no arithmetic in it.
    /// </para>
    /// </remarks>
    public sealed class LocalInputSource : IInputSource
    {
        private readonly Transform _lookTransform;

        /// <param name="lookTransform">
        /// The transform whose rotation IS the player's aim — the first-person camera or its
        /// parent. Null is tolerated: <see cref="Yaw"/> and <see cref="Pitch"/> then report 0,
        /// which is wrong but harmless, since nothing in single-player reads them.
        /// </param>
        public LocalInputSource(Transform lookTransform)
        {
            _lookTransform = lookTransform;
        }

        public float MoveX => Input.GetAxis("Horizontal");

        public float MoveZ => Input.GetAxis("Vertical");

        public float Lean => Input.GetAxis("Lean");

        public float LookDeltaX => Input.GetAxis("Mouse X");

        public float LookDeltaY => Input.GetAxis("Mouse Y");

        public float Yaw => _lookTransform != null ? _lookTransform.eulerAngles.y : 0f;

        /// <summary>
        /// Aim pitch in -90..90. Unity reports euler angles in 0..360, where looking up is 350
        /// rather than -10, and the protocol's i16 pitch field is signed.
        /// </summary>
        public float Pitch
        {
            get
            {
                if (_lookTransform == null) return 0f;

                float x = _lookTransform.eulerAngles.x;
                return x > 180f ? x - 360f : x;
            }
        }

        /// <summary>
        /// The button bitfield. Each expression here is a transcription of the line it replaced
        /// in <c>FpsActorController</c> — including the <c>LoadoutUi.IsOpen()</c> terms, which
        /// are part of the button's meaning and not a caller's concern.
        /// </summary>
        /// <remarks>
        /// <c>InputShadowCompare</c> re-evaluates those original expressions beside these and
        /// says so in the Console when the two disagree. That is the only check that exists for
        /// this transcription, because no gate in this repository compiles Unity code.
        /// </remarks>
        public ushort Buttons
        {
            get
            {
                bool loadoutOpen = LoadoutUi.IsOpen();

                return InputButtonPacker.Pack(
                    fire:   (Input.GetButton("Fire1") || Input.GetMouseButton(0)) && !loadoutOpen,
                    aim:    (Input.GetButton("Fire2") || Input.GetMouseButton(1)) && !loadoutOpen,
                    reload: Input.GetButton("Reload") && !loadoutOpen,
                    jump:   Input.GetButton("Jump"),
                    crouch: Input.GetButton("Crouch"),
                    sprint: Input.GetButton("Sprint"),
                    use:    Input.GetButton("Use"));
            }
        }
    }
}
