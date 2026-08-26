using System;
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
        private readonly Func<bool> _aiming;

        /// <param name="lookTransform">
        /// The transform whose rotation IS the player's aim — the first-person camera or its
        /// parent. Null is tolerated: <see cref="Yaw"/> and <see cref="Pitch"/> then report 0,
        /// which is wrong but harmless, since nothing in single-player reads them.
        /// </param>
        /// <param name="aiming">
        /// Whether the player is aiming. Helicopter cyclic is suppressed while aiming, and that
        /// state is the controller's — <c>FpsActorController.Aiming()</c> folds in
        /// <c>toggleAim</c> and a latch this class cannot see. A delegate rather than a latched
        /// bool, so the read stays live like every other member here; null means never aiming,
        /// which is what a source with no controller behind it should report.
        /// </param>
        public LocalInputSource(Transform lookTransform, Func<bool> aiming = null)
        {
            _lookTransform = lookTransform;
            _aiming = aiming;
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
        /// in <c>FpsActorController</c> — including the loadout-screen terms, which are part of
        /// the button's meaning and not a caller's concern. Those now come from
        /// <see cref="ILocalInputEnvironment.LoadoutScreenOpen"/> rather than from the UI class
        /// directly, which is a change of route and not of meaning (C2).
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
                bool loadoutOpen = NetInputBindings.Environment.LoadoutScreenOpen;

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

        /// <summary>Helicopter tail rotor. See <see cref="HelicopterControls"/>.</summary>
        public float HeliYaw => HelicopterControls.Yaw;

        /// <summary>Helicopter lift. See <see cref="HelicopterControls"/>.</summary>
        public float HeliCollective => HelicopterControls.Collective;

        /// <summary>Helicopter bank. See <see cref="HelicopterControls"/>.</summary>
        public float HeliRoll => HelicopterControls.Roll;

        /// <summary>Helicopter nose pitch. See <see cref="HelicopterControls"/>.</summary>
        public float HeliPitch => HelicopterControls.Pitch;

        /// <summary>
        /// Always false: the keyboard path still lives in <c>NetClientLocalCombatDriver</c>,
        /// which owns the serialized key and reads it directly.
        /// </summary>
        /// <remarks>
        /// Moving that read here would be the right home for it -- every other keyboard read
        /// in the client is in this class -- but it is a rebind change, and rebinding is not
        /// what check 13 is blocked on. Left as named debt rather than half-done.
        /// </remarks>
        public bool RespawnPressed => false;

        /// <summary>
        /// The four helicopter controls, scaled and inverted per this user's options (V5-D9).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A transcription of <c>FpsActorController.HelicopterInput()</c>, moved here
        /// unchanged</b> — including the <c>helicopterType == 2</c> branch, whose raw
        /// <c>Input.GetAxis</c> reads were booked as accepted debt by an in-file comment there
        /// because <c>IInputSource</c> had no member for them. It does now, and this is the
        /// place UnityEngine.Input is allowed to be read.
        /// </para>
        /// <para>
        /// <b>The scaling happens here, on the sender, and that is the decision.</b>
        /// <see cref="ILocalInputEnvironment.HelicopterOptions"/> is a client-local setting the
        /// server does not have —
        /// reaching it at server role is an authority hole and a headless
        /// <c>NullReferenceException</c> at once. So a finished control vector crosses the wire
        /// and the server treats it as opaque, bounded by <c>Vehicle.Clamp4</c> exactly as it
        /// already is offline. Nothing is lost by that bound: the offline path has always run
        /// <c>Clamp4</c> over these same values, so deflection past full stick has never had an
        /// effect.
        /// </para>
        /// <para>
        /// Evaluated live on every read, like everything else here. Four reads per frame of the
        /// same axes costs nothing measurable, and latching would reintroduce the one-frame
        /// staleness the whole class is shaped to avoid.
        /// </para>
        /// </remarks>
        private HelicopterAxes HelicopterControls
        {
            get
            {
                HelicopterControlOptions options = NetInputBindings.Environment.HelicopterOptions;
                float sensitivity = options.MouseSensitivity * options.HelicopterSensitivity;

                if (options.Style == HelicopterControlStyle.Custom)
                {
                    float stickPitch = Input.GetAxis("Helicopter Pitch") * (options.InvertPitch ? -1f : 1f);
                    float stickYaw = Input.GetAxis("Helicopter Yaw") * (options.InvertYaw ? -1f : 1f);
                    float stickRoll = Input.GetAxis("Helicopter Roll") * (options.InvertRoll ? -1f : 1f);
                    float stickCollective = Input.GetAxis("Helicopter Throttle") * (options.InvertThrottle ? -1f : 1f);

                    return new HelicopterAxes(
                        stickYaw * 30f * sensitivity,
                        stickCollective,
                        stickRoll * 20f * sensitivity,
                        stickPitch * 30f * sensitivity);
                }

                // LookDelta*, not Yaw/Pitch: helicopter control integrates a per-frame mouse
                // delta, and an absolute angle is a different quantity. Substituting one for the
                // other is a silent handling change, which is why IInputSource carries both.
                float mouseX = sensitivity * LookDeltaX;
                float mouseY = sensitivity * LookDeltaY;

                if (!options.InvertPitch) mouseY = -mouseY;

                if (_aiming != null && _aiming())
                {
                    mouseX = 0f;
                    mouseY = 0f;
                }

                if (options.Style == HelicopterControlStyle.Battlefield)
                    return new HelicopterAxes(MoveX, MoveZ, mouseX * 20f, mouseY * 30f);

                return new HelicopterAxes(mouseX * 30f, MoveZ, MoveX * 20f, mouseY * 30f);
            }
        }
    }
}
