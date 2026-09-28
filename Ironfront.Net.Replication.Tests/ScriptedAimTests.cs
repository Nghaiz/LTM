using System;
using System.IO;
using System.Linq;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Unity.Diagnostics;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// debt-closure phase 3D lane B — the pins for scripted aiming, which is what lets one
    /// client shoot another one on purpose (checks 1 and 13).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The load-bearing test in this file is <see cref="AimingAtAPointProducesADirectionThatReachesIt"/>.</b>
    /// Everything else pins a quadrant or a boundary; that one round-trips through the SHIPPED
    /// <c>ServerCombatAuthority.AimDirection</c> and asserts the resulting unit vector actually
    /// points at the target. A yaw or pitch convention transcribed backwards passes every
    /// hand-written quadrant assertion you would think to write — and produces shots mirrored
    /// vertically, which at the short ranges a scripted approach ends at still hit, and
    /// therefore still look like they work. <c>AimDirection</c>'s own remark says exactly this;
    /// this is the test that holds it.
    /// </para>
    /// <para>
    /// <b>Why aiming is computed at all.</b> Checks 1 and 13 need one client to kill another,
    /// and where the server spawns a body is its own choice out of a spawn-point set. A recorded
    /// absolute yaw would be aiming at whatever stood there on the run it was recorded from, so
    /// the programme names a PLAYER and the harness computes the facing every frame. The numbers
    /// still leave through <c>IInputSource</c> and <c>MoveInput</c> — the same seam a mouse
    /// lands on — so nothing under test acquires a test-only path.
    /// </para>
    /// </remarks>
    public sealed class ScriptedAimTests
    {
        // --------------------------------------------------------------------- yaw, by quadrant

        /// <summary>Yaw 0 faces +Z and grows toward +X, which is what <c>AimDirection</c> means.</summary>
        [Theory]
        [InlineData(0f, 10f, 0f)]     // due +Z
        [InlineData(10f, 0f, 90f)]    // due +X
        [InlineData(0f, -10f, 180f)]  // due -Z
        [InlineData(-10f, 0f, 270f)]  // due -X
        public void YawFacesTheTargetInTheEnginesFrame(float toX, float toZ, float expected)
            => Assert.Equal(expected, ScriptedAim.YawDegrees(0f, 0f, toX, toZ), 3);

        /// <summary>Every answer lands in [0, 360), so two records are comparable as numbers.</summary>
        [Theory]
        [InlineData(-1f, -1f)]
        [InlineData(-1f, 1f)]
        [InlineData(1f, -1f)]
        [InlineData(1f, 1f)]
        public void YawIsAlwaysWrappedIntoOneTurn(float toX, float toZ)
            => Assert.InRange(ScriptedAim.YawDegrees(0f, 0f, toX, toZ), 0f, 359.9999f);

        /// <summary>
        /// A target at the shooter's own position yields 0, not NaN.
        /// </summary>
        /// <remarks>
        /// <c>Atan2(0, 0)</c> is 0 rather than NaN, so this is belt-and-braces — but the value
        /// feeds <c>MoveInput</c>'s yaw, and a NaN there does not stay local: it goes on the
        /// wire, and every later frame's smoothing that touches it returns NaN too. One frame
        /// of arbitrary facing is a cost; a poisoned facing for the rest of the run is not.
        /// </remarks>
        [Fact]
        public void YawOnTopOfTheTargetIsZeroRatherThanNotANumber()
        {
            float yaw = ScriptedAim.YawDegrees(5f, 5f, 5f, 5f);

            Assert.False(float.IsNaN(yaw));
            Assert.Equal(0f, yaw, 3);
        }

        // ------------------------------------------------------------------- pitch, by direction

        /// <summary>Level with the target is zero pitch.</summary>
        [Fact]
        public void PitchAtTheSameHeightIsZero()
            => Assert.Equal(0f, ScriptedAim.PitchDegrees(0f, 1.6f, 0f, 0f, 1.6f, 10f), 3);

        /// <summary>
        /// A target ABOVE the shooter yields a NEGATIVE pitch, because the client packs Unity's
        /// euler X where looking down is positive.
        /// </summary>
        /// <remarks>
        /// The sign that <c>ServerCombatAuthority.AimDirection</c> then re-negates. Getting it
        /// backwards is the mirrored-shot bug that keeps working at close range.
        /// </remarks>
        [Fact]
        public void PitchIsNegativeLookingUp()
            => Assert.Equal(-45f, ScriptedAim.PitchDegrees(0f, 0f, 0f, 0f, 10f, 10f), 3);

        /// <summary>And positive looking down.</summary>
        [Fact]
        public void PitchIsPositiveLookingDown()
            => Assert.Equal(45f, ScriptedAim.PitchDegrees(0f, 10f, 0f, 0f, 0f, 10f), 3);

        /// <summary>Straight up is -90, the end of the range <c>IInputSource.Pitch</c> declares.</summary>
        [Fact]
        public void PitchStraightUpIsMinusNinety()
            => Assert.Equal(-90f, ScriptedAim.PitchDegrees(0f, 0f, 0f, 0f, 10f, 0f), 3);

        // ------------------------------------------------- the round-trip through shipped code

        /// <summary>
        /// A yaw/pitch pair from <see cref="ScriptedAim"/>, fed to the shipped
        /// <c>ServerCombatAuthority.AimDirection</c>, produces a unit vector pointing at the
        /// target.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the only test here that would catch a swapped convention.</b> It does not
        /// assert a number this file chose; it asserts that the shipped resolver, given these
        /// angles, walks from the shooter to the target. Flip either sign, swap the
        /// <c>Atan2</c> arguments, or drop the negation, and the dot product falls away from 1
        /// immediately — including in the offset cases below, which is where a single mirrored
        /// axis hides.
        /// </para>
        /// <para>
        /// Tolerance is 1e-4 rather than exact: both sides are float trig, and the composition
        /// of two <c>Atan2</c>s with a <c>Sin</c>/<c>Cos</c> pair does not round-trip bit for
        /// bit. A tolerance loose enough to accept a mirrored axis would be about 2.0 — this is
        /// four orders of magnitude tighter than the failure it grades.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData(0f, 0f, 0f, 0f, 0f, 10f)]        // dead ahead
        [InlineData(0f, 0f, 0f, 10f, 0f, 0f)]        // hard right
        [InlineData(0f, 0f, 0f, -7f, 0f, -3f)]       // behind and left
        [InlineData(0f, 0f, 0f, 4f, 6f, 8f)]         // above and to the right
        [InlineData(0f, 10f, 0f, -4f, 0f, 8f)]       // below and to the left
        [InlineData(3f, 2f, -5f, -8f, 9f, 12f)]      // both ends off the origin
        public void AimingAtAPointProducesADirectionThatReachesIt(
            float fromX, float fromY, float fromZ, float toX, float toY, float toZ)
        {
            float yaw = ScriptedAim.YawDegrees(fromX, fromZ, toX, toZ);
            float pitch = ScriptedAim.PitchDegrees(fromX, fromY, fromZ, toX, toY, toZ);

            Vec3 aim = ServerCombatAuthority.AimDirection(yaw, pitch);

            float dx = toX - fromX;
            float dy = toY - fromY;
            float dz = toZ - fromZ;
            float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

            // Unit-vector dot product: 1 means the shipped resolver is pointing exactly along
            // the line to the target. Anything else is an angle, and the size of the gap is
            // the size of the miss.
            float dot = (aim.X * dx + aim.Y * dy + aim.Z * dz) / length;

            Assert.Equal(1f, dot, 4);
        }

        // ------------------------------------------------------- where on the body it aims

        /// <summary>
        /// The pitch a scripted shooter sends puts the shot inside the target's TORSO box,
        /// with margin, at every range — not through the 1.550..1.580 gap between torso and
        /// head that ledger X-24 names.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the pin ledger X-25 needed and did not have.</b> The harness used to
        /// raise both endpoints by <c>EYE_HEIGHT</c>, which is level aim at 1.6 m — 0.020 m
        /// inside the head box's lower edge, with the torso's 0.35 m of margin unused. Every
        /// quadrant assertion in this file passed the whole time, because none of them ever
        /// asked WHERE on a body the ray lands. That is why no lane-B combat run had scored a
        /// hit: the harness aimed at the one height where X-24's seam lives.
        /// </para>
        /// <para>
        /// <b>What it grades, and what it deliberately does not.</b> It asserts the ray enters
        /// the torso with at least <see cref="MinimumTorsoMarginMetres"/> of vertical room to
        /// the nearest edge, which fails for an aim point that is merely inside the box by a
        /// hair. It does NOT assert the seam is gone — X-24 owns that, keeps its own row, and
        /// closing it is a hitbox change this file must not pre-empt. A shooter that aims at
        /// centre of mass stops being a coin toss against the seam; it does not repair it.
        /// </para>
        /// <para>
        /// The ray is built by the SHIPPED resolver (<c>AimDirection</c>) and tested against
        /// the SHIPPED boxes (<c>HitboxSet.Humanoid</c>, <c>Aabb.Raycast</c>), so a convention
        /// this file transcribed wrongly cannot make it pass.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData(0f, 1.5f)]      // contact range
        [InlineData(0f, 5f)]        // the lane-B hold distance
        [InlineData(0f, 12f)]
        [InlineData(0f, 40f)]
        [InlineData(0f, 120f)]      // both ends move together, so range must not matter
        [InlineData(3.5f, 18f)]     // shooter uphill
        [InlineData(-2.5f, 18f)]    // shooter downhill
        public void AimingAtAStandingBodyEntersTheTorsoWithMargin(float shooterFeetY, float range)
        {
            var shooterFeet = new Vec3(0f, shooterFeetY, 0f);
            var targetFeet = new Vec3(0f, 0f, range);

            float yaw = ScriptedAim.YawDegrees(
                shooterFeet.X, shooterFeet.Z, targetFeet.X, targetFeet.Z);
            float pitch = ScriptedAim.PitchAtBody(
                shooterFeet.X, shooterFeet.Y, shooterFeet.Z,
                targetFeet.X, targetFeet.Y, targetFeet.Z);

            Vec3 direction = ServerCombatAuthority.AimDirection(yaw, pitch);
            var eye = new Vec3(
                shooterFeet.X, shooterFeet.Y + ScriptedAim.ShooterEyeHeight, shooterFeet.Z);

            HitboxSet boxes = HitboxSet.Humanoid(in targetFeet);
            Aabb torso = boxes.Torso;

            Assert.True(
                torso.Raycast(in eye, in direction, range * 2f + 10f, out float distance),
                $"the shot missed the torso entirely at {range} m");

            float entryY = eye.Y + direction.Y * distance;
            float marginBelow = entryY - torso.Min.Y;
            float marginAbove = torso.Max.Y - entryY;

            Assert.True(
                MathF.Min(marginBelow, marginAbove) >= MinimumTorsoMarginMetres,
                $"entered the torso at y={entryY:F4} with only "
                + $"{MathF.Min(marginBelow, marginAbove):F4} m to the nearest edge "
                + $"({torso.Min.Y:F3}..{torso.Max.Y:F3}) at {range} m");
        }

        /// <summary>
        /// Level ground does NOT mean level aim: the shooter's eye is 1.6 m up and the torso
        /// centre it aims at is 1.2 m up, so the shot looks slightly DOWN.
        /// </summary>
        /// <remarks>
        /// The asymmetry between the two ends is the entire content of
        /// <c>ScriptedAim.PitchAtBody</c>. A pitch of exactly zero here is the X-25 bug
        /// restored, and this states that in one line without a slab test.
        /// </remarks>
        [Fact]
        public void PitchAtABodyOnLevelGroundLooksDownAtTheTorsoRatherThanLevel()
        {
            const float range = 20f;

            float pitch = ScriptedAim.PitchAtBody(0f, 0f, 0f, 0f, 0f, range);

            float expected = -RadiansToDegrees(
                MathF.Atan2(ScriptedAim.TargetAimHeight - ScriptedAim.ShooterEyeHeight, range));

            Assert.Equal(expected, pitch, 4);
            Assert.True(pitch > 0f, "aiming at the torso from eye height looks down, not level");
        }

        /// <summary>
        /// The height the harness aims at is the shipped torso box's centre, not a second
        /// transcription of it.
        /// </summary>
        /// <remarks>
        /// Move <c>HitboxSet</c>'s torso and this stays true; restate 1.20 at the aim site and
        /// the two drift the first time the box moves, which is the failure this asserts
        /// against rather than the value.
        /// </remarks>
        [Fact]
        public void TheAimHeightIsTheShippedTorsoCentre()
        {
            HitboxSet boxes = HitboxSet.Humanoid(new Vec3(0f, 0f, 0f));

            Assert.Equal(ScriptedAim.TargetAimHeight, boxes.Torso.Center.Y, 5);
            Assert.Equal(Ironfront.Net.Protocol.ProtocolConstants.EYE_HEIGHT, ScriptedAim.ShooterEyeHeight, 5);
        }

        /// <summary>
        /// Half the torso's height, less a margin for the float trig on both sides — the
        /// smallest room-to-edge that distinguishes "aimed at centre of mass" from "landed
        /// inside the box by luck".
        /// </summary>
        /// <remarks>
        /// The torso is 0.70 m tall, so a perfect centre shot has 0.35 m. 0.30 m accepts the
        /// float error of two <c>Atan2</c>s and a <c>Sin</c>/<c>Cos</c> pair and rejects
        /// everything else: the X-25 aim point sat 0.05 m ABOVE the box entirely.
        /// </remarks>
        private const float MinimumTorsoMarginMetres = 0.30f;

        private static float RadiansToDegrees(float radians)
            => (float)(radians * (180.0 / Math.PI));

        // ---------------------------------------------------------------------------- approach

        /// <summary>Full ahead while outside the hold distance.</summary>
        [Fact]
        public void ApproachDrivesForwardWhileFarAway()
            => Assert.Equal(1f, ScriptedAim.ApproachMoveZ(40f, 8f), 3);

        /// <summary>
        /// Stopped at and inside the hold distance — never reversed.
        /// </summary>
        /// <remarks>
        /// A client that backed up when it overshot would oscillate around the hold distance for
        /// the rest of the step, so the position at the next checkpoint would differ between two
        /// runs of the same programme. Repeatability is the one property lane B is buying;
        /// an approach that ends somewhere slightly different every time spends it.
        /// </remarks>
        [Theory]
        [InlineData(8f)]
        [InlineData(3f)]
        [InlineData(0f)]
        public void ApproachStopsAndNeverReverses(float distance)
            => Assert.Equal(0f, ScriptedAim.ApproachMoveZ(distance, 8f), 3);

        /// <summary>Distance is planar — a target on a roof is not "far" because it is high.</summary>
        [Fact]
        public void PlanarDistanceIgnoresHeight()
            => Assert.Equal(5f, ScriptedAim.PlanarDistance(0f, 0f, 3f, 4f), 3);

        // --------------------------------------------------------------- the Unity half, by Roslyn

        /// <summary>
        /// The harness sends the SOLVED yaw on the wire, not the programme's declared one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The two differ exactly when a step names a target, which is exactly the steps checks
        /// 1 and 13 are made of. Reading the cursor in <c>BuildMoveInput</c> would send a
        /// <c>C_INPUT</c> facing one way while <c>FpsActorController</c> aimed another: the
        /// client would appear to every observer to be shooting sideways, while its OWN screen
        /// — and therefore its own screenshot — looked entirely correct.
        /// </para>
        /// <para>
        /// Graded as text because nothing in this repository compiles Unity code. Weaker than
        /// executing it, and stated rather than hidden.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheHarnessSendsTheSolvedYawNotTheCursorYaw()
        {
            string harness = UnitySource("Net/Diagnostics/LaneBHarness.cs");
            string builder = Between(harness, "private MoveInput BuildMoveInput()", "\n        }");

            Assert.Contains("_source.Yaw", builder);
            Assert.Contains("ScriptedAim.ApproachMoveZ", builder);

            // The fallback when there is no source at all is still allowed to read the cursor;
            // what must not appear is the cursor's yaw being handed to a MoveInput while a
            // source exists. Both MoveInput constructions take the local `yaw`.
            Assert.DoesNotContain("new MoveInput(0f, 0f, _cursor.Yaw", builder);
            Assert.DoesNotContain("step.moveZ, _cursor.Yaw", builder);
        }

        /// <summary>
        /// The solver answers once per frame, so three callers cannot get three answers.
        /// </summary>
        /// <remarks>
        /// <c>IInputSource.Yaw</c>, <c>IInputSource.Pitch</c> and the harness's
        /// <c>MoveInput</c> builder all ask in the same frame, and Unity orders none of them
        /// against each other. Without the memo the yaw a client turns to can differ from the
        /// yaw it shoots along by however far the target walked between two reads — small,
        /// present only while the target moves, and therefore present only during check 1.
        /// </remarks>
        [Fact]
        public void TheSolverMemoizesOneAnswerPerFrame()
        {
            string solver = UnitySource("Net/Diagnostics/ScriptedTargetSolver.cs");

            Assert.Contains("_solvedFrame == Time.frameCount", solver);
            Assert.Contains("_solvedFrame = Time.frameCount", solver);
        }

        /// <summary>
        /// The aim arithmetic stays engine-free, or it silently leaves this suite.
        /// </summary>
        /// <remarks>
        /// <see cref="ScriptedAim"/> is linked into this project by <c>&lt;Compile Include&gt;</c>,
        /// which is the only way anything under <c>Assets/</c> is reachable by <c>dotnet test</c>.
        /// A <c>using UnityEngine;</c> would drop the round-trip pin above out of coverage while
        /// leaving a green suite behind — the file would simply stop being compiled here.
        /// </remarks>
        [Fact]
        public void TheAimArithmeticNamesNoUnityEngine()
        {
            // The DIRECTIVE, not the substring: the file's own remarks discuss Unity's euler
            // convention at length, and a naive Contains would go red for being documented.
            bool imports = UnitySource("Net/Diagnostics/ScriptedAim.cs")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.TrimStart().StartsWith("using UnityEngine",
                                                         StringComparison.Ordinal));

            Assert.False(imports,
                "ScriptedAim.cs names UnityEngine, so it can no longer be linked into this "
                + "suite and the AimDirection round-trip stops being graded. Move whatever "
                + "needed it into ScriptedTargetSolver.");
        }

        /// <summary>
        /// The recorder writes what the step was aiming at, and whether the name resolved.
        /// </summary>
        /// <remarks>
        /// A check-1 run where the shooter never resolved "OBS-A" produces the same artifact as
        /// one where it resolved the target and missed — no killfeed line, full health on both
        /// sides, and a screenshot that cannot tell them apart. This field is the only thing in
        /// the run that distinguishes "the check failed" from "the check never ran".
        /// </remarks>
        [Fact]
        public void TheRecorderWritesWhetherTheTargetResolved()
        {
            string recorder = UnitySource("Net/Diagnostics/LaneBCheckpointRecorder.cs");

            Assert.Contains("\\\"aim\\\":", recorder);
            Assert.Contains("Str(\"requested\"", recorder);
            Assert.Contains("s.Resolved ? \"true\" : \"false\"", recorder);
        }

        // ------------------------------------------------------------------------------ helpers

        /// <summary>The text between two markers, for pinning one method rather than a file.</summary>
        private static string Between(string text, string start, string end)
        {
            int from = text.IndexOf(start, StringComparison.Ordinal);
            Assert.True(from >= 0, $"marker not found: {start}");

            int to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            Assert.True(to >= 0, $"closing marker not found after: {start}");

            return text.Substring(from, to - from);
        }

        private static string UnitySource(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null;
                 d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
