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
        /// Where an <c>approachVehicle</c> step aims ON a vehicle, as a height above the
        /// vehicle's own transform origin. Ledger <b>X-44</b>.
        /// </summary>
        /// <remarks>
        /// <b>Not <see cref="TargetAimHeight"/>, and the difference is not cosmetic.</b> That
        /// constant is the torso centre of a STANDING HUMANOID, read from
        /// <see cref="HitboxSet.HumanoidTorsoCenterHeight"/>; a vehicle has no such box and
        /// borrowing the number would be a coincidence rather than a reason. One metre is a
        /// rough hull centre for everything the game drives, and the value does not have to be
        /// exact: an approach step grades on planar DISTANCE
        /// (<see cref="ApproachMoveZ"/>), and the pitch only decides where the client is looking
        /// while it walks. It is named rather than inlined so the next reader does not have to
        /// decide whether a bare 1f meant a hull or a rounding.
        /// </remarks>
        public const float VehicleAimHeight = 1f;

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

        /// <summary>
        /// Index of the nearest candidate within <paramref name="maxMetres"/>, or <c>-1</c>.
        /// What <c>ScriptedTargetSolver.SolveNearestVehicle</c> picks with. Ledger <b>X-44</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Here rather than in the solver, for this file's standing reason.</b> The solver is
        /// the Unity half — two lookups and a transform read — and its own remark says that
        /// "anything that starts computing in this file has left coverage". A nearest-within scan
        /// is arithmetic with real edge cases (nothing in range, an empty candidate set, a tie),
        /// so it belongs on this side of the seam where <c>dotnet test</c> can reach it.
        /// </para>
        /// <para>
        /// <b>Planar, matching <see cref="PlanarDistance"/> and <see cref="ApproachMoveZ"/>.</b> A
        /// vehicle parked at the bottom of a slope is not further away in the sense an approach
        /// step means, and using a 3D distance here would make the hold band disagree with the
        /// one the arbiter measures.
        /// </para>
        /// <para>
        /// <b>Ties go to the LOWER index, deterministically.</b> Two vehicles at equal distance is
        /// the ordinary case at a spawn pad, and picking whichever the comparison happened to see
        /// first would make one run of a programme differ from the next — the one property lane B
        /// is buying.
        /// </para>
        /// <para>
        /// <b>Compared squared, and the caller is handed the index rather than the distance</b>,
        /// so the one square root that is actually needed is taken once by the caller through
        /// <see cref="PlanarDistance"/> rather than once per candidate here.
        /// </para>
        /// </remarks>
        /// <param name="count">
        /// How much of <paramref name="xs"/> / <paramref name="zs"/> is live. The caller reuses
        /// its arrays across frames, so their <c>Length</c> is capacity and not population —
        /// reading Length instead would scan stale entries from a previous frame's vehicle set.
        /// </param>
        public static int NearestIndexWithin(
            float fromX, float fromZ, float[] xs, float[] zs, int count, float maxMetres)
        {
            if (xs == null || zs == null || count <= 0 || maxMetres <= 0f) return -1;

            int limit = count;
            if (limit > xs.Length) limit = xs.Length;
            if (limit > zs.Length) limit = zs.Length;

            // The band is CLOSED at the top -- a candidate exactly at maxMetres is in range --
            // matching ApproachMoveZ's own convention, so a hold distance and a search radius
            // that happen to coincide do not disagree about the same vehicle.
            float maxSquared = maxMetres * maxMetres;
            float bestSquared = float.PositiveInfinity;
            int best = -1;

            for (int i = 0; i < limit; i++)
            {
                float dx = xs[i] - fromX;
                float dz = zs[i] - fromZ;
                float squared = dx * dx + dz * dz;

                if (squared > maxSquared) continue;

                // Strictly less-than, so an equal distance leaves the earlier index standing.
                if (squared >= bestSquared) continue;

                bestSquared = squared;
                best = i;
            }

            return best;
        }

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
