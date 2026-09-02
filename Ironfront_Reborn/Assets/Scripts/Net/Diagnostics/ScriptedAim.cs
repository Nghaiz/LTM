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
        /// Strafe and forward axes for a client walking toward a route corner while its body
        /// faces <paramref name="facingYawDegrees"/> — never the bearing to the corner itself.
        /// Ledger <b>X-66</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this exists at all.</b> <see cref="ApproachMoveZ"/> only ever points a client
        /// straight at its target and holds forward, which is a straight line. Dustbowl's ridge
        /// is not, so an infantry approach along it stalls on the terrain — the failure this
        /// method is here to route around, literally.
        /// </para>
        /// <para>
        /// <b>Quantized to eight octants, not proportional — same reasoning as
        /// <see cref="ApproachMoveZ"/>'s own remark.</b> A proportional axis creeps for the last
        /// few metres of a leg at a speed the movement model does not reproduce identically
        /// under loss, so two runs of the same route would end at different positions — the one
        /// property lane B is buying. Snapping to the nearest of eight held-key combinations
        /// (the same vocabulary a keyboard offers: forward, forward-right, right, ...) keeps the
        /// output exactly as reproducible as a straight approach.
        /// </para>
        /// <para>
        /// <b>Facing-relative, not world-relative — this is the whole point of the parameter.</b>
        /// <c>BuildMoveInput</c>'s own remark forbids sending a yaw that disagrees with the
        /// controller's solved aim: the body keeps looking at whatever <c>aimAtPlayer</c>
        /// resolved to, and the legs walk toward the corner independently, exactly as a human
        /// strafing while keeping a target in their sights would. So the bearing to the corner is
        /// rotated into the facing's own frame before it is quantized — walking due east while
        /// facing north is a pure strafe (moveX = 1, moveZ = 0), not a diagonal.
        /// </para>
        /// <para>
        /// <b>The frame matches <see cref="YawDegrees"/></b>: 0 faces +Z and grows toward +X, and
        /// a relative bearing of 0 (straight ahead) is octant boundary-centred rather than
        /// boundary-edged, so a corner dead ahead reads as pure forward rather than landing
        /// exactly between two octants and depending on floating-point rounding to pick one.
        /// </para>
        /// </remarks>
        /// <param name="fromX">The walker's current X.</param>
        /// <param name="fromZ">The walker's current Z.</param>
        /// <param name="toX">The corner's X.</param>
        /// <param name="toZ">The corner's Z.</param>
        /// <param name="facingYawDegrees">
        /// The body's facing — the solved aim yaw when one is resolved, exactly as
        /// <c>BuildMoveInput</c> already sends on the wire.
        /// </param>
        /// <param name="moveX">Strafe axis, one of -1, 0, 1.</param>
        /// <param name="moveZ">Forward axis, one of -1, 0, 1.</param>
        public static void SteerToward(
            float fromX, float fromZ, float toX, float toZ, float facingYawDegrees,
            out float moveX, out float moveZ)
        {
            float dx = toX - fromX;
            float dz = toZ - fromZ;

            // Already at the corner: no direction to quantize, and holding still is the correct
            // answer rather than an arbitrary octant.
            if (dx == 0f && dz == 0f) { moveX = 0f; moveZ = 0f; return; }

            float bearing = WrapDegrees(RadiansToDegrees((float)Math.Atan2(dx, dz)));
            float relative = WrapDegrees(bearing - facingYawDegrees);

            // Eight 45-degree wedges, centred on 0/45/90/.../315 rather than edged there, so a
            // corner exactly ahead (relative == 0) lands in the middle of the "forward" wedge
            // instead of on the boundary between "forward" and "forward-left".
            int octant = ((int)Math.Floor((relative + 22.5f) / 45f)) % 8;
            if (octant < 0) octant += 8;

            (moveX, moveZ) = octant switch
            {
                0 => (0f, 1f),   // forward
                1 => (1f, 1f),   // forward-right
                2 => (1f, 0f),   // right
                3 => (1f, -1f),  // back-right
                4 => (0f, -1f),  // back
                5 => (-1f, -1f), // back-left
                6 => (-1f, 0f),  // left
                _ => (-1f, 1f),  // forward-left (octant 7)
            };
        }

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

    /// <summary>
    /// Walks a <see cref="ScriptedInputStep.route"/> corner by corner, advancing past any corner
    /// already within its hold radius. Ledger <b>X-66</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Pure, like the rest of <see cref="ScriptedAim"/>.</b> This holds no Unity type and no
    /// reference to the harness that owns a walker's live position — <see cref="Advance"/> is
    /// handed that position every call rather than reading it, so the class stays reachable by
    /// <c>dotnet test</c> through the same <c>&lt;Compile Include&gt;</c> arrangement as its
    /// file.
    /// </para>
    /// <para>
    /// <b>Parallel <c>float[]</c> arrays, not <c>ScriptedRouteWaypoint[]</c> — the same choice
    /// <see cref="ScriptedAim.NearestIndexWithin"/> makes, and for the same reason.</b>
    /// <c>ScriptedRouteWaypoint</c> is declared in <c>ScriptedInputProgramme.cs</c>, and
    /// <c>Ironfront.Net.LoadHarness</c> links THIS file alone, without that one, to keep its own
    /// scripted-aim seam free of the programme model. A route type here would have made that
    /// standalone link fail to compile — this class stays reachable from both link sites by
    /// staying engine- and model-free, exactly like the rest of the file.
    /// </para>
    /// <para>
    /// <b>Only ever advances.</b> <see cref="CornerIndex"/> is monotonic: a corner already
    /// passed is never re-entered, even if the walker is later blown back within its radius —
    /// re-triggering a corner that way would let one leg of a route repeat depending on how a
    /// run's timing happened to land, which is exactly the kind of run-to-run divergence the
    /// rest of lane B's arithmetic is built to avoid.
    /// </para>
    /// </remarks>
    public sealed class ScriptedRouteCursor
    {
        private readonly float[] _xs;
        private readonly float[] _zs;
        private readonly float _cornerRadiusMeters;
        private int _index;

        /// <param name="xs">Each corner's X, in order. Must hold at least one corner.</param>
        /// <param name="zs">Each corner's Z, in order. Same length as <paramref name="xs"/>.</param>
        /// <param name="cornerRadiusMeters">
        /// How close <see cref="Advance"/> must read before it moves past a corner.
        /// </param>
        public ScriptedRouteCursor(float[] xs, float[] zs, float cornerRadiusMeters)
        {
            if (xs == null || xs.Length == 0)
            {
                throw new ArgumentException("a route needs at least one corner", nameof(xs));
            }

            if (zs == null || zs.Length != xs.Length)
            {
                throw new ArgumentException(
                    $"zs must hold exactly as many corners as xs ({xs.Length})", nameof(zs));
            }

            _xs = xs;
            _zs = zs;
            _cornerRadiusMeters = cornerRadiusMeters;
            _index = 0;
        }

        /// <summary>True once every corner has been passed.</summary>
        public bool Finished => _index >= _xs.Length;

        /// <summary>The corner currently being steered toward. 0-based, saturates at the route's length.</summary>
        public int CornerIndex => _index;

        /// <summary>The live corner's X, or 0 once <see cref="Finished"/>.</summary>
        public float CurrentCornerX => Finished ? 0f : _xs[_index];

        /// <summary>The live corner's Z, or 0 once <see cref="Finished"/>.</summary>
        public float CurrentCornerZ => Finished ? 0f : _zs[_index];

        /// <summary>
        /// Advances past every corner already within radius of <paramref name="fromX"/>,
        /// <paramref name="fromZ"/>.
        /// </summary>
        /// <returns>
        /// True while a corner remains to steer toward; false once the route is
        /// <see cref="Finished"/>, so the caller can hand over to <c>approach</c> in the same
        /// call that discovers the route is spent.
        /// </returns>
        public bool Advance(float fromX, float fromZ)
        {
            while (!Finished)
            {
                float distance = ScriptedAim.PlanarDistance(fromX, fromZ, _xs[_index], _zs[_index]);
                if (distance > _cornerRadiusMeters) return true;

                _index++;
            }

            return false;
        }
    }
}
#endif
