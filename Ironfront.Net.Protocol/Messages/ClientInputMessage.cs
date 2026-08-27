using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// One quantized input frame, as it appears on the wire. protocol-spec.md section 4.2.
    /// </summary>
    /// <remarks>
    /// Fields are stored in their quantized form, not as floats, so that a frame read off
    /// the wire and a frame about to be written are byte-identical. Convert with
    /// <see cref="Quantize"/> at the edges.
    /// </remarks>
    public readonly struct InputFrame
    {
        /// <summary>i8 + i8 + u16 + i16 + u16 = 8 bytes.</summary>
        public const int Size = 8;

        /// <summary>-127..127, divide by 127 for -1.0..1.0.</summary>
        public readonly sbyte MoveX;
        /// <summary>-127..127, divide by 127 for -1.0..1.0.</summary>
        public readonly sbyte MoveZ;
        /// <summary>0..65535 mapping to 0..360 degrees.</summary>
        public readonly ushort Yaw;
        /// <summary>-16384..16384 mapping to -90..90 degrees.</summary>
        public readonly short Pitch;
        public readonly InputButtons Buttons;

        public InputFrame(sbyte moveX, sbyte moveZ, ushort yaw, short pitch, InputButtons buttons)
        {
            MoveX   = moveX;
            MoveZ   = moveZ;
            Yaw     = yaw;
            Pitch   = pitch;
            Buttons = buttons;
        }

        /// <summary>Builds a frame from unquantized gameplay values.</summary>
        public static InputFrame FromFloats(
            float moveX, float moveZ, float yawDegrees, float pitchDegrees, InputButtons buttons)
            => new InputFrame(
                Quantize.PackMoveAxis(moveX),
                Quantize.PackMoveAxis(moveZ),
                Quantize.PackYaw(yawDegrees),
                Quantize.PackPitch(pitchDegrees),
                buttons);

        public float MoveXFloat => Quantize.UnpackMoveAxis(MoveX);
        public float MoveZFloat => Quantize.UnpackMoveAxis(MoveZ);
        public float YawDegrees => Quantize.UnpackYaw(Yaw);
        public float PitchDegrees => Quantize.UnpackPitch(Pitch);

        public bool IsPressed(InputButtons button) => (Buttons & button) != 0;

        /// <summary>
        /// The weapon slot this frame selects, or <c>-1</c> when it selects none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>SwitchWeapon0..3</c> are bits 11-14 (protocol-spec § 4.2). They were declared at
        /// the freeze and had zero producers and zero consumers until 2026-08-21, which is why
        /// this decoder lives here rather than in either peer: two transcriptions of the same
        /// four bits is how the halves drift.
        /// </para>
        /// <para>
        /// <b>Lowest set bit wins.</b> More than one is not a state a producer should send, and
        /// the alternatives on receiving it are worse: rejecting the frame drops the movement
        /// with it, and taking the highest would make a stuck low bit invisible.
        /// </para>
        /// </remarks>
        public int WeaponSlot => SlotOf(Buttons);

        /// <summary>
        /// The weapon slot a button mask selects, or <c>-1</c> when it selects none.
        /// </summary>
        /// <remarks>
        /// The instance property above is the usual way in; this static form exists so a
        /// producer holding a bare mask can ask the same question without framing a throwaway
        /// frame first.
        /// </remarks>
        public static int SlotOf(InputButtons buttons)
        {
            if ((buttons & InputButtons.SwitchWeapon0) != 0) return 0;
            if ((buttons & InputButtons.SwitchWeapon1) != 0) return 1;
            if ((buttons & InputButtons.SwitchWeapon2) != 0) return 2;
            if ((buttons & InputButtons.SwitchWeapon3) != 0) return 3;

            return -1;
        }

        /// <summary>
        /// The single bit a slot selects, or <see cref="InputButtons.None"/> when the slot is out
        /// of range. The inverse of <see cref="SlotOf"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The one encoder for bits 11-14, and it lives in Protocol because there are two
        /// producers in two assemblies that cannot see each other.</b> <c>InputButtonPacker</c>
        /// is in <c>Ironfront.Net.Unity</c> and <c>MoveInput.ToButtons</c> is in
        /// <c>Ironfront.Net.Replication</c>; neither may reference the other, so before this
        /// existed the only way for both to speak bits 11-14 was to transcribe them twice. Rows
        /// X-3 and X-31 are both what happens when one transcription learns a bit and the other
        /// does not — X-31 because <c>MoveInput.ToButtons</c> had never heard of a slot at all.
        /// </para>
        /// <para>
        /// Out of range is <see cref="InputButtons.None"/> rather than an exception: this is
        /// called once per input frame on a hot path, and a scripted programme with a typo'd
        /// slot should produce a run that visibly does not switch, not one that dies at frame 1.
        /// </para>
        /// </remarks>
        public static InputButtons SlotBit(int slot)
        {
            switch (slot)
            {
                case 0:  return InputButtons.SwitchWeapon0;
                case 1:  return InputButtons.SwitchWeapon1;
                case 2:  return InputButtons.SwitchWeapon2;
                case 3:  return InputButtons.SwitchWeapon3;
                default: return InputButtons.None;
            }
        }
    }

    /// <summary>
    /// C_INPUT (0x20) body codec. protocol-spec.md section 4.2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Input is critical data sent unreliably, so each packet repeats the
    /// <see cref="ProtocolConstants.INPUT_REDUNDANCY"/> most recent frames. Without that,
    /// one lost packet costs the server a whole tick of input and the character stalls;
    /// with a redundancy of 3, three consecutive packets must be lost before anything is
    /// missed, at a cost of 16 extra bytes. Far cheaper than sending reliably, which would
    /// retransmit input that is already stale by the time it lands.
    /// </para>
    /// <para>
    /// Server-side: keep <c>lastProcessedInputTick</c> per connection and discard any
    /// frame whose tick is not newer — those are the redundant copies.
    /// </para>
    /// </remarks>
    public static class ClientInputMessage
    {
        /// <summary>u32 startTick + u8 frameCount.</summary>
        public const int HeaderSize = 5;

        /// <summary>frameCount is a u8 but the spec constrains it to 1..8.</summary>
        public const int MaxFrames = 8;

        /// <summary>
        /// Encoded size for a given frame count. At the standard redundancy of 3 this is
        /// 4 + 1 + 3 * 8 = 29 bytes, or 870 B/s upstream at 30 Hz.
        /// </summary>
        public static int SizeFor(int frameCount) => HeaderSize + frameCount * InputFrame.Size;

        /// <summary>
        /// Writes the message body. Returns bytes written, or -1 when the frame count is
        /// out of range or the buffer is too small.
        /// </summary>
        public static int Write(Span<byte> dst, uint startTick, ReadOnlySpan<InputFrame> frames)
        {
            if (frames.Length < 1 || frames.Length > MaxFrames) return -1;

            int size = SizeFor(frames.Length);
            if (dst.Length < size) return -1;

            var w = new SpanWriter(dst);
            w.WriteU32(startTick);
            w.WriteU8((byte)frames.Length);

            for (int i = 0; i < frames.Length; i++)
            {
                InputFrame f = frames[i];
                w.WriteI8(f.MoveX);
                w.WriteI8(f.MoveZ);
                w.WriteU16(f.Yaw);
                w.WriteI16(f.Pitch);
                w.WriteU16((ushort)f.Buttons);
            }

            return w.Ok ? w.Position : -1;
        }

        /// <summary>
        /// Parses a message body into <paramref name="frames"/>, which must have room for
        /// at least the encoded frame count (use <see cref="MaxFrames"/> to be safe).
        /// </summary>
        public static bool TryParse(
            ReadOnlySpan<byte> src, Span<InputFrame> frames, out uint startTick, out int frameCount)
        {
            startTick  = 0;
            frameCount = 0;

            var r = new SpanReader(src);
            uint tick = r.ReadU32();
            byte count = r.ReadU8();
            if (!r.Ok) return false;

            if (count < 1 || count > MaxFrames) return false;
            if (frames.Length < count) return false;
            if (src.Length < SizeFor(count)) return false;

            for (int i = 0; i < count; i++)
            {
                sbyte moveX = r.ReadI8();
                sbyte moveZ = r.ReadI8();
                ushort yaw  = r.ReadU16();
                short pitch = r.ReadI16();
                ushort btns = r.ReadU16();
                frames[i] = new InputFrame(moveX, moveZ, yaw, pitch, (InputButtons)btns);
            }

            if (!r.Ok) return false;

            startTick  = tick;
            frameCount = count;
            return true;
        }
    }
}
