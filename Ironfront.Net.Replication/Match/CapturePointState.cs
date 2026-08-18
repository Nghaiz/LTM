using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Match
{
    /// <summary>
    /// One capture point's authoritative state: where it is, who is taking it, and what the
    /// clients were last told.
    /// </summary>
    /// <remarks>
    /// Engine-free. The Unity wrapper reads position and radius off the existing
    /// <c>CapturePoint</c> component in the original codebase and copies them in — the
    /// gameplay object stays the client track's, and the authority over its value stays here.
    /// </remarks>
    public sealed class CapturePointState
    {
        public CapturePointState(byte pointId, in Vec3 position, float radius, float captureSpeed = 0.2f)
        {
            if (radius <= 0f) throw new ArgumentOutOfRangeException(nameof(radius));

            PointId      = pointId;
            Position     = position;
            Radius       = radius;
            CaptureSpeed = captureSpeed;
            LastSentQ    = CapturePointMessage.PackOwner(0f);
        }

        public byte PointId { get; }

        public Vec3 Position { get; }

        public float Radius { get; }

        /// <summary>Ownership gained per second by a single attacker standing on the point.</summary>
        public float CaptureSpeed { get; }

        /// <summary>-1 fully team 0, +1 fully team 1, 0 neutral.</summary>
        public float Owner { get; private set; }

        /// <summary>Both teams present inside the radius this tick.</summary>
        public bool IsContested { get; private set; }

        /// <summary>The quantized value the clients were last sent.</summary>
        public sbyte LastSentQ { get; private set; }

        /// <summary>
        /// Which team holds it, applying the same threshold the wire message applies, so the
        /// server's ticket bleed and the client's flag colour cannot disagree.
        /// </summary>
        public byte OwningTeam => ToMessage().OwningTeam;

        /// <summary>
        /// Advances ownership toward whichever team has more bodies inside the radius.
        /// </summary>
        /// <returns>True when the change is worth a message, per the rules' send threshold.</returns>
        /// <remarks>
        /// <para>
        /// Rate scales with the headcount advantage and is capped
        /// (<see cref="MatchRules.MaxCaptureHeadcount"/>), so bringing a squad helps and
        /// bringing the whole team does not trivialise it.
        /// </para>
        /// <para>
        /// <b>Trap 3, and the two halves of the send test.</b> Sending on every tick would be
        /// 5 points x 30 Hz x 16 clients = 2400 messages a second for a bar that moves too
        /// slowly to watch. So a message goes out only when the bar has moved by
        /// <see cref="MatchRules.CaptureSendThreshold"/>, <b>or</b> when ownership has actually
        /// changed hands. The second half is not optional: crossing
        /// <see cref="CapturePointMessage.OwnedThreshold"/> is a 1% move that flips a flag's
        /// colour and starts the enemy's tickets draining, and a threshold test alone would
        /// let clients sit on the wrong side of it for several seconds.
        /// </para>
        /// <para>
        /// Both halves compare <i>quantized</i> values, not floats. The wire carries one signed
        /// byte, so a float that has moved but still packs to the same byte is a message that
        /// changes nothing on the client — the same reasoning that made <c>WorldSnapshot</c>
        /// store quantized entries in phase 01.
        /// </para>
        /// </remarks>
        public bool Tick(int team0Count, int team1Count, float deltaSeconds, MatchRules rules)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));

            IsContested = team0Count > 0 && team1Count > 0;

            int diff = team0Count - team1Count;
            if (diff == 0 || deltaSeconds <= 0f) return false;

            byte ownerBefore = OwningTeam;

            int effective = Math.Min(Math.Abs(diff), rules.MaxCaptureHeadcount);
            float rate = effective * CaptureSpeed * deltaSeconds;

            // Team 0 pushes ownership negative, team 1 positive.
            float target = Owner + (diff > 0 ? -rate : rate);
            Owner = target < -1f ? -1f : target > 1f ? 1f : target;

            if (OwningTeam != ownerBefore) return true;

            sbyte packed = CapturePointMessage.PackOwner(Owner);
            int threshold = (int)(rules.CaptureSendThreshold * 100f);
            return Math.Abs(packed - LastSentQ) >= Math.Max(1, threshold);
        }

        /// <summary>The message describing the current state.</summary>
        public CapturePointMessage ToMessage()
            => new CapturePointMessage(
                PointId,
                CapturePointMessage.PackOwner(Owner),
                IsContested ? CaptureFlags.Contested : CaptureFlags.None);

        /// <summary>
        /// Records that clients have been told about the current value. Call this only after
        /// the message has actually been handed to the transport — marking it sent first and
        /// failing to send leaves the point frozen on every client until it next crosses the
        /// threshold from wherever it drifted to.
        /// </summary>
        public void MarkSent() => LastSentQ = CapturePointMessage.PackOwner(Owner);

        /// <summary>
        /// Returns the point to neutral for a new match.
        /// </summary>
        /// <remarks>
        /// <see cref="LastSentQ"/> is deliberately reset too. Leaving it at the old value would
        /// mean a point that ended the last match at 0 and starts the next at 0 never sends its
        /// opening state to the clients that joined in between.
        /// </remarks>
        public void Reset()
        {
            Owner       = 0f;
            IsContested = false;
            LastSentQ   = CapturePointMessage.PackOwner(0f);
        }

        /// <summary>Squared distance test, so the caller never needs a square root.</summary>
        public bool Contains(in Vec3 point)
        {
            Vec3 d = point - Position;
            return d.SqrMagnitude <= Radius * Radius;
        }
    }
}
