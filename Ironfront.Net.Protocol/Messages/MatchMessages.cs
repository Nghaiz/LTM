using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// S_MATCH_STATE (0x45). protocol-spec.md section 4.1 lists the message; section 4 never
    /// gives it a byte layout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This layout is not in the frozen spec.</b> Same gap, same handling, and same reason
    /// as <see cref="SpawnActorMessage"/>: defining a layout for a message the spec declares
    /// but does not describe <i>documents</i> an unspecified message rather than <i>changes</i>
    /// a specified one, so it does not bump
    /// <see cref="ProtocolConstants.PROTOCOL_VERSION"/>. It still needs the section 2 review
    /// to become normative, and is flagged in code, in the phase-03 report, and on the client track
    /// checklist.
    /// </para>
    /// <para>
    /// <b>What is deliberately absent.</b> There is no <c>winningTeam</c> field. The winner is
    /// whichever team still has tickets when the phase becomes
    /// <see cref="MatchPhase.Ended"/>, which the client can read off the two ticket counts it
    /// is already being sent — a stored copy is a derived field that can disagree with its own
    /// source (conventions: "No Derived Fields"). <see cref="WinningTeam"/> computes it.
    /// </para>
    /// <para>
    /// Sent on any phase change and otherwise at most once a second, so 8 bytes on channel 2
    /// is not a bandwidth line item.
    /// </para>
    /// </remarks>
    public readonly struct MatchStateMessage
    {
        /// <summary>u8 + u16 + u16 + u16 + u8 = 8 bytes.</summary>
        public const int Size = 8;

        public readonly MatchPhase Phase;

        /// <summary>Tickets remaining, team 0. Clamped to 0.</summary>
        public readonly ushort Tickets0;

        /// <summary>Tickets remaining, team 1. Clamped to 0.</summary>
        public readonly ushort Tickets1;

        /// <summary>
        /// Whole seconds left in the current phase, rounded up. 0 in
        /// <see cref="MatchPhase.Playing"/>, which has no timer — it ends on tickets.
        /// </summary>
        public readonly ushort PhaseSecondsRemaining;

        /// <summary>Humans connected, for the "waiting for N more players" line.</summary>
        public readonly byte HumanPlayerCount;

        public MatchStateMessage(
            MatchPhase phase, ushort tickets0, ushort tickets1,
            ushort phaseSecondsRemaining, byte humanPlayerCount)
        {
            Phase                 = phase;
            Tickets0              = tickets0;
            Tickets1              = tickets1;
            PhaseSecondsRemaining = phaseSecondsRemaining;
            HumanPlayerCount      = humanPlayerCount;
        }

        /// <summary>
        /// Who won, once the match is over: <see cref="TeamId.Team0"/>,
        /// <see cref="TeamId.Team1"/>, or <see cref="TeamId.None"/> while it is undecided or
        /// genuinely drawn.
        /// </summary>
        public byte WinningTeam
        {
            get
            {
                if (Phase != MatchPhase.Ended && Phase != MatchPhase.Resetting)
                    return TeamId.None;
                if (Tickets0 == Tickets1) return TeamId.None;
                return Tickets0 > Tickets1 ? TeamId.Team0 : TeamId.Team1;
            }
        }

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU8((byte)Phase);
            w.WriteU16(Tickets0);
            w.WriteU16(Tickets1);
            w.WriteU16(PhaseSecondsRemaining);
            w.WriteU8(HumanPlayerCount);
            return w.Ok ? w.Position : -1;
        }

        /// <summary>
        /// Parses a body. Rejects a phase byte outside <see cref="MatchPhase"/> rather than
        /// casting it through — an undefined phase reaches the client's <c>switch</c> as a
        /// value no case handles, and a HUD stuck in a state that does not exist is far harder
        /// to diagnose than a rejected message.
        /// </summary>
        public static bool TryParse(ReadOnlySpan<byte> src, out MatchStateMessage message)
        {
            message = default;

            var r = new SpanReader(src);
            byte phase       = r.ReadU8();
            ushort tickets0  = r.ReadU16();
            ushort tickets1  = r.ReadU16();
            ushort seconds   = r.ReadU16();
            byte humans      = r.ReadU8();
            if (!r.Ok) return false;
            if (phase > (byte)MatchPhase.Resetting) return false;

            message = new MatchStateMessage(
                (MatchPhase)phase, tickets0, tickets1, seconds, humans);
            return true;
        }
    }

    /// <summary>
    /// S_CAPTURE_POINT (0x46). Layout defined here, not in the spec — see
    /// <see cref="MatchStateMessage"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ownership is one signed byte covering -1..+1 at 1% resolution, which is finer than the
    /// capture bar can display and two bytes cheaper than a float. -100 is fully team 0,
    /// +100 fully team 1, 0 neutral.
    /// </para>
    /// <para>
    /// There is no <c>owningTeam</c> field for the same reason
    /// <see cref="MatchStateMessage"/> has no winner field: it is a threshold test on a value
    /// already in the message. <see cref="OwningTeam"/> applies it, once, here, so the two
    /// sides cannot disagree about where the boundary is.
    /// </para>
    /// </remarks>
    public readonly struct CapturePointMessage
    {
        /// <summary>u8 + i8 + u8 = 3 bytes.</summary>
        public const int Size = 3;

        /// <summary>
        /// Ownership at or beyond this fraction counts as captured. Matches the
        /// <c>&gt; 0.9</c> test the phase-03 ticket-bleed sketch uses.
        /// </summary>
        public const float OwnedThreshold = 0.9f;

        /// <summary>Index into the map's capture-point list. Stable for a map.</summary>
        public readonly byte PointId;

        /// <summary>Ownership x100, -100 (team 0) .. +100 (team 1).</summary>
        public readonly sbyte OwnerQ;

        public readonly CaptureFlags Flags;

        public CapturePointMessage(byte pointId, sbyte ownerQ, CaptureFlags flags)
        {
            PointId = pointId;
            OwnerQ  = ownerQ;
            Flags   = flags;
        }

        /// <summary>Ownership as -1..+1.</summary>
        public float Owner => OwnerQ * 0.01f;

        public bool IsContested => (Flags & CaptureFlags.Contested) != 0;

        /// <summary>
        /// Which team holds the point, or <see cref="TeamId.None"/> while it is still being
        /// fought over.
        /// </summary>
        public byte OwningTeam
        {
            get
            {
                float owner = Owner;
                if (owner <= -OwnedThreshold) return TeamId.Team0;
                if (owner >= OwnedThreshold) return TeamId.Team1;
                return TeamId.None;
            }
        }

        /// <summary>Quantizes a -1..+1 ownership value, clamping out-of-range input.</summary>
        public static sbyte PackOwner(float owner)
        {
            if (float.IsNaN(owner)) return 0;
            float scaled = owner * 100f;
            if (scaled <= -100f) return -100;
            if (scaled >= 100f) return 100;
            return (sbyte)(scaled < 0f ? -(int)(-scaled + 0.5f) : (int)(scaled + 0.5f));
        }

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU8(PointId);
            w.WriteI8(OwnerQ);
            w.WriteU8((byte)Flags);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out CapturePointMessage message)
        {
            message = default;

            var r = new SpanReader(src);
            byte pointId = r.ReadU8();
            sbyte ownerQ = r.ReadI8();
            byte flags   = r.ReadU8();
            if (!r.Ok) return false;

            // Reject rather than cast: an unknown bit here is either a newer server or a
            // corrupted byte, and both are better surfaced than silently masked off.
            if ((flags & ~(byte)CaptureFlags.Contested) != 0) return false;
            if (ownerQ < -100 || ownerQ > 100) return false;

            message = new CapturePointMessage(pointId, ownerQ, (CaptureFlags)flags);
            return true;
        }
    }
}
