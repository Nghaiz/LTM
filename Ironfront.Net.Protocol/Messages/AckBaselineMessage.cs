using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// C_ACK_BASELINE (0x27) body codec — the client telling the server "I have snapshot
    /// tick N in full, delta against it".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Spec gap, flagged deliberately.</b> protocol-spec.md section 4.1 lists 0x27 in the
    /// message table but section 4 gives no byte layout for it, unlike C_INPUT (4.2) and
    /// S_SNAPSHOT (4.3). The layout below — a bare <c>u32 baselineTick</c> — is the minimum
    /// the delta encoder needs and matches the width the <c>baselineTick</c> field already
    /// has in the snapshot header. It is implemented here so phase-01 is not blocked, and it
    /// is listed in the phase-01 report as an item still owed a spec section: adding it needs
    /// a PR with 2 approvals under the section 2 change process, but no
    /// <c>PROTOCOL_VERSION</c> bump, because it documents an unspecified message rather than
    /// changing a specified one.
    /// </para>
    /// <para>
    /// Sent on channel 2 (reliable-ordered). Losing an ack is survivable — the server simply
    /// keeps deltaing against the older baseline it still believes in — but a <i>reordered</i>
    /// ack that moved the baseline backwards would not be, which is why the server side must
    /// route every incoming tick through <see cref="SequenceMath.IsNewer32"/> rather than a
    /// raw comparison.
    /// </para>
    /// </remarks>
    public static class AckBaselineMessage
    {
        /// <summary>u32 baselineTick.</summary>
        public const int Size = 4;

        /// <summary>Writes the body. Returns bytes written, or -1 if the buffer is too small.</summary>
        public static int Write(Span<byte> dst, uint baselineTick)
        {
            if (dst.Length < Size) return -1;

            var w = new SpanWriter(dst);
            w.WriteU32(baselineTick);
            return w.Ok ? w.Position : -1;
        }

        /// <summary>Parses the body. False on a truncated packet.</summary>
        public static bool TryParse(ReadOnlySpan<byte> src, out uint baselineTick)
        {
            baselineTick = 0;

            var r = new SpanReader(src);
            uint tick = r.ReadU32();
            if (!r.Ok) return false;

            baselineTick = tick;
            return true;
        }
    }
}
