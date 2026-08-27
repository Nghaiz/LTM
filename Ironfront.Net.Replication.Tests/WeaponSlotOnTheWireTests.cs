using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// verdict-closure R1 task R1.1 — the pins for debt-ledger row <b>X-31</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>X-31 is row X-3 happening a second time, to the same struct, one bit-group over.</b>
    /// <c>InputButtons</c> declared <c>SwitchWeapon0..3</c> and <c>Use</c>;
    /// <c>ServerCombatBridge</c> read <c>frame.WeaponSlot</c> and called
    /// <c>ApplyWeaponSwitchIntent</c>; and <see cref="MoveInput.ToButtons"/> — the one place a
    /// <see cref="MoveInput"/> becomes buttons — had never heard of either. Five bits with a
    /// live reader on the far end were permanently zero.
    /// </para>
    /// <para>
    /// <b>Why the packer being right was not enough, and why that misled the investigation for
    /// two days.</b> <c>InputButtonPacker.Pack</c> HAS produced bits 11-14 since 2026-08-21, and
    /// <c>ScriptedInputSource.Buttons</c> HAS passed <c>step.switchWeaponSlot</c> into it — both
    /// were read back from source and both are correct. But that packer reaches the wire only
    /// through <c>NetPredictionClock.DefaultInput</c>, and a lane-B client assigns
    /// <c>clock.InputSource = BuildMoveInput</c>, replacing <c>DefaultInput</c> wholesale. So on
    /// a scripted client the packer's answer was computed and discarded every tick, and the
    /// frame that actually went out was built from a <see cref="MoveInput"/> with no slot on it.
    /// <c>artifacts/lane-b/x31-diag-04</c> is that: <c>buttons=0x0001 slot=-1</c> on 60 of 60
    /// frames, from a step object carrying <c>"switchWeaponSlot": 2</c> and <c>"fire": true</c>,
    /// with only the second surviving.
    /// </para>
    /// <para>
    /// <b>Structured the way <c>ClientInputSenderTests</c> is, for the reason that file gives.</b>
    /// The reachable half runs through the real <c>C_INPUT</c> codec; the Unity half — the
    /// harness's <c>BuildMoveInput</c> — is graded by Roslyn over the real file, because no gate
    /// in this repository compiles it and X-31 lived in exactly that half.
    /// </para>
    /// </remarks>
    public sealed class WeaponSlotOnTheWireTests
    {
        // -------------------------------------------------------- the wire, end to end

        /// <summary>
        /// <b>Pin 1</b> — a <see cref="MoveInput"/> asking for slot 2 arrives as slot 2. This is
        /// the assertion that was RED on the day X-31 was filed.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void AMoveInputCarryingASlotPutsThatSlotOnTheWire(int slot)
        {
            InputFrame received = RoundTrip(Intent(weaponSlot: slot));

            Assert.Equal(slot, received.WeaponSlot);
        }

        /// <summary>
        /// <b>Pin 2</b> — the exact shape of the x31-diag-04 artifact, as a test. Fire and slot
        /// come off the same intent and BOTH survive; before the fix the first did and the
        /// second did not.
        /// </summary>
        /// <remarks>
        /// Named for the artifact rather than for the mechanism, because the next person to read
        /// a run with <c>buttons=0x0001</c> in it should be able to find this test from the log
        /// line.
        /// </remarks>
        [Fact]
        public void FireAndSlotFromOneIntentBothSurvive_TheX31DiagShape()
        {
            InputFrame received = RoundTrip(Intent(fire: true, weaponSlot: 2));

            Assert.True(received.IsPressed(InputButtons.Fire), "fire was lost");
            Assert.Equal(2, received.WeaponSlot);
            Assert.Equal(
                InputButtons.Fire | InputButtons.SwitchWeapon2,
                received.Buttons);
        }

        /// <summary>
        /// <b>Pin 3</b> — <see cref="InputButtons.Use"/> travels too. It was declared, unread by
        /// the client's mask builder, and dropped by the same omission.
        /// </summary>
        [Fact]
        public void UseTravels()
        {
            Assert.True(RoundTrip(Intent(use: true)).IsPressed(InputButtons.Use));
        }

        /// <summary>
        /// <b>Pin 4</b> — a negative slot selects nothing, and sets no other bit while doing it.
        /// </summary>
        /// <remarks>
        /// The second half matters more than the first: a slot encoder that reached for a bit on
        /// out-of-range input would put a weapon switch on every frame of every programme that
        /// never asked for one, which is a worse bug than the one being fixed and would not fail
        /// any check that only asserted "slot 2 arrives".
        /// </remarks>
        [Theory]
        [InlineData(-1)]
        [InlineData(4)]
        [InlineData(int.MinValue)]
        public void AnOutOfRangeSlotSelectsNothing(int slot)
        {
            InputFrame received = RoundTrip(Intent(weaponSlot: slot));

            Assert.Equal(-1, received.WeaponSlot);
            Assert.Equal(InputButtons.None, received.Buttons);
        }

        /// <summary>
        /// <b>Pin 5</b> — the dequantize path is symmetric: a frame read off the wire rebuilds an
        /// intent carrying the same slot and use.
        /// </summary>
        /// <remarks>
        /// This is what makes a replayed frame on the server identical to the frame the client
        /// predicted with. An encoder that grew a field while <see cref="MoveInput.FromFrame"/>
        /// did not would desynchronise prediction from replay for exactly the ticks a switch was
        /// requested — visible as a correction, blamed on the reconciler.
        /// </remarks>
        [Fact]
        public void TheDequantizePathCarriesSlotAndUseBack()
        {
            MoveInput replayed = MoveInput.FromFrame(RoundTrip(Intent(use: true, weaponSlot: 3)));

            Assert.Equal(3, replayed.WeaponSlot);
            Assert.True(replayed.Use);
        }

        /// <summary>
        /// <b>Pin 6</b> — <see cref="MoveInput.WithAxes"/> keeps the slot. The server's speed
        /// check rebuilds an intent through it, and a slot lost there would be a switch that the
        /// authority silently declined to replay.
        /// </summary>
        [Fact]
        public void WithAxesKeepsTheSlotAndUse()
        {
            MoveInput clamped = Intent(use: true, weaponSlot: 1).WithAxes(0f, 0f);

            Assert.Equal(1, clamped.WeaponSlot);
            Assert.True(clamped.Use);
        }

        // -------------------------------------------------------- one codec, not four

        /// <summary>
        /// <b>Pin 7</b> — the two encoders agree, because they are the same encoder.
        /// </summary>
        /// <remarks>
        /// <c>InputButtonPacker</c> (Ironfront.Net.Unity) and <see cref="MoveInput.ToButtons"/>
        /// (Ironfront.Net.Replication) live in assemblies that cannot reference each other, so
        /// before <see cref="InputFrame.SlotBit"/> the only way for both to speak bits 11-14 was
        /// to transcribe them twice — and X-3 and X-31 are both what happens when one
        /// transcription learns a bit and the other does not. The packer is not reachable from
        /// here (it is Unity source), so this grades the shared half both now call.
        /// </remarks>
        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(9)]
        public void SlotBitAndSlotOfAreInverses(int slot)
        {
            InputButtons bit = InputFrame.SlotBit(slot);
            int expected = slot >= 0 && slot <= 3 ? slot : -1;

            Assert.Equal(expected, InputFrame.SlotOf(bit));
        }

        /// <summary>
        /// <b>Pin 8</b> — the lowest set bit wins, and it is decided in one place.
        /// </summary>
        /// <remarks>
        /// More than one slot bit is not a state a producer should send. The decode rule is
        /// documented on <see cref="InputFrame.WeaponSlot"/>; this pins it so a second producer
        /// cannot quietly adopt a different one.
        /// </remarks>
        [Fact]
        public void MoreThanOneSlotBitResolvesToTheLowest()
        {
            Assert.Equal(
                1,
                InputFrame.SlotOf(InputButtons.SwitchWeapon1 | InputButtons.SwitchWeapon3));
        }

        // -------------------------------------------------------- the Unity half, by source scan

        /// <summary>
        /// <b>Pin 9</b> — the lane-B harness hands its step's slot to the intent it builds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the assertion that would have caught X-31 on the day the seam was written,
        /// and the one <c>ScriptedInputProgrammeTests</c> could not: that file pins
        /// <c>ScriptedInputSource.cs</c> passing <c>weaponSlot: step.switchWeaponSlot</c> into
        /// the packer, which was true throughout and graded a path a lane-B client does not use.
        /// </para>
        /// <para>
        /// Roslyn over the real file, for the reason <c>ClientInputSenderTests</c> states: no
        /// gate here compiles Unity source, and a comment mentioning the field is not a call.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheLaneBHarnessPassesItsStepSlotIntoTheIntentItBuilds()
        {
            IReadOnlyList<ArgumentSyntax> arguments = MoveInputConstructionIn(
                "Net/Diagnostics/LaneBHarness.cs");

            string[] rendered = arguments.Select(a => a.ToString()).ToArray();

            Assert.True(
                rendered.Contains("step.switchWeaponSlot"),
                "LaneBHarness builds a MoveInput without step.switchWeaponSlot: "
                + string.Join(", ", rendered)
                + " — that omission IS row X-31. clock.InputSource is replaced wholesale, so a "
                + "slot that is not on this MoveInput cannot reach the wire at all.");

            Assert.True(
                rendered.Contains("step.use"),
                "LaneBHarness builds a MoveInput without step.use: " + string.Join(", ", rendered));
        }

        /// <summary>
        /// <b>Pin 10</b> — the shared Unity conversion carries the slot through as well.
        /// </summary>
        /// <remarks>
        /// The keyboard client reaches the wire through <c>MovementSimulation.FromUnityInput</c>
        /// rather than through the harness. It does not PRODUCE a slot today (a human switches
        /// weapons locally and the server is never told — a separate, recorded gap), but the
        /// conversion dropping one it was handed would make that gap unfixable without finding
        /// this all over again.
        /// </remarks>
        [Fact]
        public void TheSharedUnityConversionCarriesTheSlotThrough()
        {
            string[] rendered = MoveInputConstructionIn("Net/Shared/MovementSimulation.cs")
                .Select(a => a.ToString())
                .ToArray();

            Assert.True(
                rendered.Any(a => a.Contains("SlotOf", StringComparison.Ordinal)),
                "MovementSimulation.FromUnityInput builds a MoveInput with no slot argument: "
                + string.Join(", ", rendered));
        }

        // -------------------------------------------------------- helpers

        /// <summary>
        /// The arguments of the widest <c>new MoveInput(...)</c> in a Unity source file.
        /// </summary>
        /// <remarks>
        /// Widest rather than first: both files also construct the movement-only overload for a
        /// null step or a shadow sample, and asserting against that one would report a missing
        /// slot on a correct file.
        /// </remarks>
        private static IReadOnlyList<ArgumentSyntax> MoveInputConstructionIn(string relativePath)
        {
            ArgumentListSyntax? widest = UnitySource(relativePath)
                .DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
                .Where(o => o.Type.ToString() == "MoveInput")
                .Select(o => o.ArgumentList)
                .Where(a => a != null)
                .OrderByDescending(a => a!.Arguments.Count)
                .FirstOrDefault();

            Assert.True(widest != null, $"no `new MoveInput(...)` in {relativePath}");

            return widest!.Arguments;
        }

        private static MoveInput Intent(
            bool fire = false, bool use = false, int weaponSlot = -1)
            => new MoveInput(
                0f, 0f, 0f,
                jump: false, sprint: false, crouch: false,
                fire: fire, aim: false, reload: false,
                use: use, weaponSlot: weaponSlot);

        /// <summary>
        /// Quantizes intent the way the client does, frames it as a <c>C_INPUT</c> body, and
        /// reads it back out.
        /// </summary>
        /// <remarks>
        /// Through the real codec rather than by reading <see cref="MoveInput.ToButtons"/>
        /// directly, for the reason <c>ClientInputSenderTests.RoundTrip</c> gives: the mask being
        /// right and the mask reaching the far end are two different claims, and X-31 is a case
        /// where a correct mask builder existed and its answer never left the process.
        /// </remarks>
        private static InputFrame RoundTrip(in MoveInput input)
        {
            InputFrame sent = InputFrame.FromFloats(
                input.MoveX, input.MoveZ, input.YawDegrees, 0f, input.ToButtons());

            var body = new byte[ClientInputMessage.SizeFor(1)];
            Assert.Equal(body.Length, ClientInputMessage.Write(body, 1u, new[] { sent }));

            var frames = new InputFrame[1];
            Assert.True(ClientInputMessage.TryParse(body, frames, out uint _, out int count));
            Assert.Equal(1, count);

            return frames[0];
        }

        private static SyntaxNode UnitySource(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing Unity source: {path}");

            return CSharpSyntaxTree
                .ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp9))
                .GetRoot();
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null;
                 d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException("no Ironfront.sln above the working directory");
        }
    }
}
