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
// #nullable disable, for the reason ScriptedInputProgramme.cs states: this file is compiled
// twice, once by Unity's Assembly-CSharp (no nullable context) and once by
// Ironfront.Net.Replication.Tests through a <Compile Include> link (nullable warnings are
// errors). Annotating for the second emits CS8632 in the first.
#nullable disable

using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// The arithmetic a scripted client uses to point at another player and to close the
    /// distance to one. Phase-3D lane B, check 1 (E7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a programme cannot just state a yaw for this.</b> Checks 1 and 13 need one client
    /// to shoot another one, and where a body spawns is the server's choice out of a spawn-point
    /// set — a recorded absolute facing would be aiming at whatever happened to be there on the
    /// run it was recorded from. So the step names a PLAYER and this computes the facing, every
    /// frame, from where that player currently is.
    /// </para>
    /// <para>
    /// <b>That is still the same seam.</b> The numbers this returns are handed to
    /// <c>IInputSource.Yaw</c>/<c>Pitch</c> and to <c>MoveInput</c>, which is exactly where a
    /// human's mouse and keyboard land. Nothing under test is told a script computed them —
    /// aiming by looking up a position is what an aimbot does through the ordinary input path,
    /// not a test-only path through the netcode.
    /// </para>
    /// <para>
    /// <b>Pitch sign is the shipped one, taken from the shipped source.</b>
    /// <c>ServerCombatAuthority.AimDirection</c> negates pitch because the client packs Unity's
    /// euler X, where looking DOWN is positive; its own remark records that getting this
    /// backwards mirrors every shot vertically and still hits at short range, so it still looks
    /// like it works. A second transcription of that convention here would be free to drift, so
    /// <see cref="PitchDegrees"/> is written against that file and pinned by a test that
    /// round-trips through <c>AimDirection</c> itself.
    /// </para>
    /// <para>
    /// <b>No UnityEngine, no Vector3</b> — same <c>&lt;Compile Include&gt;</c> arrangement as
    /// <see cref="ScriptedInputCursor"/>. A <c>using UnityEngine;</c> here silently drops this
    /// out of <c>dotnet test</c> coverage, which is the only coverage anything under
    /// <c>Assets/</c> gets.
    /// </para>
    /// </remarks>
    public static class ScriptedAim
    {
        /// <summary>
        /// How far above its own feet a scripted shooter's eye sits — the same constant
        /// <c>ServerCombatAuthority.EyePosition</c> raises the shooter by.
        /// </summary>
        public const float ShooterEyeHeight = ProtocolConstants.EYE_HEIGHT;

        /// <summary>
        /// Where a scripted shooter aims ON a standing body, as a height above that body's
        /// feet: the torso box's centre.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Aiming at the target's ORIGIN is wrong</b>, and that is the reason this constant
        /// is not zero: <c>ServerCombatAuthority.EyePosition</c> raises the SHOOTER by 1.6 m,
        /// so a shot at the target's feet is a downward shot that grazes the hitbox at contact
        /// range and misses entirely at any distance worth calling a range test.
        /// </para>
        /// <para>
        /// <b>Raising BOTH ends by the eye height is also wrong, and ledger X-25 is what it
        /// cost.</b> That reads as "aim level", and it is level — at 1.6 m, which on
        /// <see cref="HitboxSet.Humanoid"/> is 0.020 m inside the head box's lower edge
        /// (1.580..1.820), with the torso's 0.35 m of margin never used. Both endpoints move
        /// together, so the aim point was 1.6 m at EVERY range rather than only up close: it is
        /// why no lane-B combat run had ever scored a hit, and it put every shot through the
        /// 1.550..1.580 gap ledger X-24 names.
        /// </para>
        /// <para>
        /// The torso centre is the one aim point on a standing body with margin on every side.
        /// It is read from <see cref="HitboxSet.HumanoidTorsoCenterHeight"/> rather than
        /// restated here, so moving the box moves the shooter with it.
        /// </para>
        /// </remarks>
        public const float TargetAimHeight = HitboxSet.HumanoidTorsoCenterHeight;

        /// <summary>
        /// Compass yaw in degrees from one point to another, in the engine's left-handed Y-up
        /// frame — the same frame <c>ServerCombatAuthority.AimDirection</c> reads.
        /// </summary>
        /// <remarks>
        /// <c>Atan2(x, z)</c>, not the usual <c>Atan2(y, x)</c>: yaw 0 faces +Z and grows toward
        /// +X, which is what <c>AimDirection</c>'s <c>(Sin(yaw), _, Cos(yaw))</c> means.
        /// </remarks>
        public static float YawDegrees(float fromX, float fromZ, float toX, float toZ)
        {
            float dx = toX - fromX;
            float dz = toZ - fromZ;

            // Directly on top of the target: any yaw is equally right, and 0 is the one a
            // reader can predict. Returning NaN here would poison MoveInput for the rest of
            // the run instead of costing one frame of aim.
            if (dx == 0f && dz == 0f) return 0f;

            return WrapDegrees(RadiansToDegrees((float)Math.Atan2(dx, dz)));
        }

        /// <summary>
        /// Aim pitch in degrees from one point to another. Positive looks DOWN — see the class
        /// remark.
        /// </summary>
        public static float PitchDegrees(
            float fromX, float fromY, float fromZ,
            float toX, float toY, float toZ)
        {
            float dy = toY - fromY;
            float horizontal = Horizontal(toX - fromX, toZ - fromZ);

            if (horizontal == 0f && dy == 0f) return 0f;

            // Negated so that a target ABOVE the shooter (dy > 0) yields a NEGATIVE pitch,
            // which AimDirection then re-negates into a +Y component.
            return -RadiansToDegrees((float)Math.Atan2(dy, horizontal));
        }

        /// <summary>
        /// Aim pitch in degrees from a shooter to the torso of a standing body, given the FEET
        /// position of each. Positive looks DOWN — see the class remark.
        /// </summary>
        /// <remarks>
        /// The two ends are raised by different heights on purpose, and that asymmetry is the
        /// whole of the method: <see cref="ShooterEyeHeight"/> is where the shot leaves from
        /// and <see cref="TargetAimHeight"/> is where it should arrive. Calling
        /// <see cref="PitchDegrees"/> directly with the same height on both ends is ledger
        /// X-25, so the harness calls this instead.
        /// </remarks>
        public static float PitchAtBody(
            float fromX, float fromFeetY, float fromZ,
            float toX, float toFeetY, float toZ)
            => PitchDegrees(
                fromX, fromFeetY + ShooterEyeHeight, fromZ,
                toX, toFeetY + TargetAimHeight, toZ);

        /// <summary>
        /// Forward axis for a client told to close on a target: full ahead until it is inside
        /// <paramref name="holdDistanceMeters"/>, then nothing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Binary rather than proportional, and it does not back up.</b> A proportional
        /// approach would have the client creep for the last few metres at a speed the movement
        /// model does not reproduce identically under loss, and a client that reversed when it
        /// overshot would oscillate around the hold distance for the rest of the step —
        /// producing a position that differs between two runs of the same programme, which is
        /// the one property lane B is buying.
        /// </para>
        /// <para>
        /// <b>The band is closed at the top and open at the bottom</b>: at exactly the hold
        /// distance the client stops. A step that wants contact says so with a small hold
        /// distance, not with a sign.
        /// </para>
        /// </remarks>
        public static float ApproachMoveZ(float distanceMeters, float holdDistanceMeters)
            => distanceMeters > holdDistanceMeters ? 1f : 0f;

        /// <summary>Planar distance, ignoring height. What <see cref="ApproachMoveZ"/> grades.</summary>
        public static float PlanarDistance(float fromX, float fromZ, float toX, float toZ)
            => Horizontal(toX - fromX, toZ - fromZ);

        private static float Horizontal(float dx, float dz)
            => (float)Math.Sqrt((double)dx * dx + (double)dz * dz);

        private static float RadiansToDegrees(float radians)
            => (float)(radians * (180.0 / Math.PI));

        private static float WrapDegrees(float degrees)
        {
            float wrapped = degrees % 360f;
            return wrapped < 0f ? wrapped + 360f : wrapped;
        }
    }
}
#endif
