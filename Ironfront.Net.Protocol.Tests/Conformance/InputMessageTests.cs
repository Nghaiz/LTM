using System;
using Xunit;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// protocol-spec.md section 14, checklist item 8:
    /// "C_INPUT with frameCount = 3 is exactly 29 bytes".
    /// </summary>
    public class InputMessageTests
    {
        // startTick 300, frameCount 3, three identical frames:
        //   moveX +127, moveZ -127, yaw 0x8000 (180 degrees), pitch 4096, buttons Fire|Aim
        private const string InputHex =
            "2C 01 00 00 03 " +
            "7F 81 00 80 00 10 03 00 " +
            "7F 81 00 80 00 10 03 00 " +
            "7F 81 00 80 00 10 03 00";

        private static InputFrame SampleFrame() => new InputFrame(
            moveX: 127, moveZ: -127, yaw: 0x8000, pitch: 4096,
            buttons: InputButtons.Fire | InputButtons.Aim);

        [Fact]
        public void FrameCountOfThree_IsExactly29Bytes()
        {
            Assert.Equal(29, ClientInputMessage.SizeFor(3));
            Assert.Equal(29, ClientInputMessage.SizeFor(ProtocolConstants.INPUT_REDUNDANCY));
            Assert.Equal(8, InputFrame.Size);

            // 4 (startTick) + 1 (frameCount) + 3 * 8 = 29, per the spec's arithmetic.
            Assert.Equal(4 + 1 + 3 * 8, ClientInputMessage.SizeFor(3));
        }

        [Fact]
        public void UpstreamBudget_IsUnder1KbPerSecond()
        {
            // 29 bytes * 30 Hz = 870 B/s, which the spec calls negligible. If this ever
            // fails, the input packet grew and the claim in section 4.2 needs revisiting.
            int bytesPerSecond =
                ClientInputMessage.SizeFor(ProtocolConstants.INPUT_REDUNDANCY)
                * ProtocolConstants.INPUT_SEND_RATE;

            Assert.Equal(870, bytesPerSecond);
        }

        [Fact]
        public void Write_ProducesTheExpectedBytes()
        {
            Span<InputFrame> frames = stackalloc InputFrame[3];
            frames[0] = SampleFrame();
            frames[1] = SampleFrame();
            frames[2] = SampleFrame();

            Span<byte> buffer = stackalloc byte[ClientInputMessage.SizeFor(3)];
            int written = ClientInputMessage.Write(buffer, startTick: 300, frames);

            Assert.Equal(29, written);
            Assert.Equal(InputHex, Hex.ToHex(buffer));
        }

        [Fact]
        public void TryParse_OfTheExpectedBytes_ProducesTheRightFrames()
        {
            Span<InputFrame> frames = stackalloc InputFrame[ClientInputMessage.MaxFrames];

            Assert.True(ClientInputMessage.TryParse(
                Hex.FromHex(InputHex), frames, out uint startTick, out int frameCount));

            Assert.Equal(300u, startTick);
            Assert.Equal(3, frameCount);

            for (int i = 0; i < frameCount; i++)
            {
                Assert.Equal(127, frames[i].MoveX);
                Assert.Equal(-127, frames[i].MoveZ);
                Assert.Equal(0x8000, frames[i].Yaw);
                Assert.Equal(4096, frames[i].Pitch);
                Assert.True(frames[i].IsPressed(InputButtons.Fire));
                Assert.True(frames[i].IsPressed(InputButtons.Aim));
                Assert.False(frames[i].IsPressed(InputButtons.Jump));
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(8)]
        public void RoundTrip_AtEveryLegalFrameCount(int count)
        {
            Span<InputFrame> source = stackalloc InputFrame[count];
            for (int i = 0; i < count; i++)
                source[i] = new InputFrame(
                    (sbyte)i, (sbyte)-i, (ushort)(i * 1000), (short)(i * 100),
                    InputButtons.Sprint);

            Span<byte> buffer = stackalloc byte[ClientInputMessage.SizeFor(count)];
            Assert.Equal(ClientInputMessage.SizeFor(count),
                         ClientInputMessage.Write(buffer, 42, source));

            Span<InputFrame> parsed = stackalloc InputFrame[ClientInputMessage.MaxFrames];
            Assert.True(ClientInputMessage.TryParse(buffer, parsed, out uint tick, out int n));

            Assert.Equal(42u, tick);
            Assert.Equal(count, n);
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(source[i].MoveX, parsed[i].MoveX);
                Assert.Equal(source[i].MoveZ, parsed[i].MoveZ);
                Assert.Equal(source[i].Yaw, parsed[i].Yaw);
                Assert.Equal(source[i].Pitch, parsed[i].Pitch);
                Assert.Equal(source[i].Buttons, parsed[i].Buttons);
            }
        }

        [Fact]
        public void Write_RejectsIllegalFrameCounts()
        {
            Span<byte> buffer = stackalloc byte[128];

            Assert.Equal(-1, ClientInputMessage.Write(buffer, 0, ReadOnlySpan<InputFrame>.Empty));

            Span<InputFrame> tooMany = stackalloc InputFrame[ClientInputMessage.MaxFrames + 1];
            Assert.Equal(-1, ClientInputMessage.Write(buffer, 0, tooMany));
        }

        [Fact]
        public void Write_RejectsAnUndersizedBuffer()
        {
            Span<InputFrame> frames = stackalloc InputFrame[3];
            Span<byte> tooSmall = stackalloc byte[28];
            Assert.Equal(-1, ClientInputMessage.Write(tooSmall, 0, frames));
        }

        [Fact]
        public void TryParse_RejectsATruncatedMessage()
        {
            // Header says 3 frames, but only 2 frames of data follow. A malformed packet
            // must fail cleanly rather than read whatever is next in the buffer.
            byte[] truncated = Hex.FromHex(InputHex).AsSpan(0, 5 + 16).ToArray();
            Span<InputFrame> frames = stackalloc InputFrame[ClientInputMessage.MaxFrames];

            Assert.False(ClientInputMessage.TryParse(truncated, frames, out _, out _));
        }

        [Fact]
        public void TryParse_RejectsAFrameCountOutsideOneToEight()
        {
            byte[] bytes = Hex.FromHex(InputHex);
            Span<InputFrame> frames = stackalloc InputFrame[ClientInputMessage.MaxFrames];

            bytes[4] = 0;
            Assert.False(ClientInputMessage.TryParse(bytes, frames, out _, out _));

            bytes[4] = 9;
            Assert.False(ClientInputMessage.TryParse(bytes, frames, out _, out _));

            bytes[4] = 255;
            Assert.False(ClientInputMessage.TryParse(bytes, frames, out _, out _));
        }

        [Fact]
        public void ButtonBits_MatchSpecSection42()
        {
            Assert.Equal(1 << 0, (ushort)InputButtons.Fire);
            Assert.Equal(1 << 1, (ushort)InputButtons.Aim);
            Assert.Equal(1 << 2, (ushort)InputButtons.Reload);
            Assert.Equal(1 << 3, (ushort)InputButtons.Jump);
            Assert.Equal(1 << 4, (ushort)InputButtons.Crouch);
            Assert.Equal(1 << 5, (ushort)InputButtons.Sprint);
            Assert.Equal(1 << 6, (ushort)InputButtons.Prone);
            // V7-D10 retired ThrowGrenade to Reserved7 rather than implementing it. The bit
            // is still pinned at 7 because the neighbours must not renumber -- a rename moves
            // no byte, so this row is the proof the retirement was not a wire change.
            Assert.Equal(1 << 7, (ushort)InputButtons.Reserved7);
            Assert.Equal(1 << 8, (ushort)InputButtons.LeanLeft);
            Assert.Equal(1 << 9, (ushort)InputButtons.LeanRight);
            Assert.Equal(1 << 10, (ushort)InputButtons.Use);
            Assert.Equal(1 << 11, (ushort)InputButtons.SwitchWeapon0);
            Assert.Equal(1 << 12, (ushort)InputButtons.SwitchWeapon1);
            Assert.Equal(1 << 13, (ushort)InputButtons.SwitchWeapon2);
            Assert.Equal(1 << 14, (ushort)InputButtons.SwitchWeapon3);
        }

        [Fact]
        public void FromFloats_QuantizesThroughTheSharedConstants()
        {
            InputFrame frame = InputFrame.FromFloats(
                moveX: 1f, moveZ: -1f, yawDegrees: 180f, pitchDegrees: 45f,
                buttons: InputButtons.Fire);

            Assert.Equal(127, frame.MoveX);
            Assert.Equal(-127, frame.MoveZ);
            Assert.Equal(Quantize.PackYaw(180f), frame.Yaw);
            Assert.Equal(Quantize.PackPitch(45f), frame.Pitch);

            Assert.Equal(1f, frame.MoveXFloat, 3);
            Assert.Equal(-1f, frame.MoveZFloat, 3);
            Assert.Equal(180f, frame.YawDegrees, 2);
            Assert.Equal(45f, frame.PitchDegrees, 2);
        }

        /// <summary>
        /// <c>InputFrame.WeaponSlot</c> decodes bits 11-14, and -1 when none is set.
        /// </summary>
        /// <remarks>
        /// These four bits sat on the wire from the freeze until 2026-08-21 with no producer and
        /// no consumer. The decoder is shared so the two halves cannot transcribe them
        /// differently; these are its pins.
        /// </remarks>
        [Theory]
        [InlineData(InputButtons.SwitchWeapon0, 0)]
        [InlineData(InputButtons.SwitchWeapon1, 1)]
        [InlineData(InputButtons.SwitchWeapon2, 2)]
        [InlineData(InputButtons.SwitchWeapon3, 3)]
        public void EachSwitchBitDecodesToItsOwnSlot(InputButtons bit, int expected)
        {
            var frame = new InputFrame(0, 0, 0, 0, bit);

            Assert.Equal(expected, frame.WeaponSlot);
        }

        [Fact]
        public void NoSwitchBitDecodesToNoSlot()
        {
            var held = new InputFrame(0, 0, 0, 0, InputButtons.Fire | InputButtons.Sprint);

            Assert.Equal(-1, held.WeaponSlot);
            Assert.Equal(-1, new InputFrame(0, 0, 0, 0, InputButtons.None).WeaponSlot);
        }

        /// <summary>Two bits at once resolves to the LOWEST, and never throws.</summary>
        /// <remarks>
        /// A producer should not send this. Rejecting the frame would drop the movement with it,
        /// and taking the highest would make a stuck low bit invisible.
        /// </remarks>
        [Fact]
        public void MoreThanOneSwitchBitTakesTheLowest()
        {
            var frame = new InputFrame(0, 0, 0, 0,
                InputButtons.SwitchWeapon3 | InputButtons.SwitchWeapon1);

            Assert.Equal(1, frame.WeaponSlot);
        }
    }
}
