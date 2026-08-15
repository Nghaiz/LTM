using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>Why a join ticket was turned away.</summary>
    /// <remarks>
    /// For the server's own logs and counters only. The transport reports every one of these
    /// to the client as <see cref="ConnectDenyReason.InvalidTicket"/>, because a handshake
    /// that says <i>which</i> check failed is an oracle for forging one
    /// (<see cref="JoinTicket.Verify"/>).
    /// </remarks>
    public enum TicketRejection
    {
        None = 0,
        /// <summary>Wrong length, or the server has no shared secret configured.</summary>
        Malformed = 1,
        /// <summary>HMAC mismatch.</summary>
        BadSignature = 2,
        /// <summary>Signed correctly, but past its 60-second window.</summary>
        Expired = 3,
        /// <summary>Signed for a different game server.</summary>
        WrongServer = 4,
        /// <summary>That player is already connected, or has a live reservation.</summary>
        AlreadyConnected = 5,
    }

    /// <summary>
    /// The game server's half of the joinTicket handshake. Phase-03 task 4,
    /// protocol-spec.md section 12.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev C.
    /// </para>
    /// <para>
    /// <b>The HMAC comparison is not written here.</b> It is
    /// <see cref="JoinTicket.Verify"/>, which lives beside
    /// <see cref="JoinTicket.Issue"/> in the protocol library precisely so the two halves of
    /// one algorithm cannot disagree about which bytes are signed. That also means trap 4 —
    /// comparing HMACs with an early-exit <c>SequenceEqual</c> and leaking the signature one
    /// byte at a time to a timing attack — is closed at the only place a comparison happens,
    /// rather than being a rule this class has to remember.
    /// </para>
    /// <para>
    /// What this class adds on top is the two checks a shared verifier cannot make, because
    /// they depend on <i>this</i> server's state: that the ticket was issued for this server,
    /// and that the player named by it is not already here. Without the second, one leaked
    /// ticket can be replayed by every client that has it for the whole 60-second window.
    /// </para>
    /// <para>
    /// <b>Reservation, not just a lookup.</b> The transport asks this question during the
    /// handshake, before there is a connection to associate the player with. A validator that
    /// only consulted a list of connected players would pass two simultaneous replays of the
    /// same ticket, because neither has finished connecting when the other is checked. So a
    /// successful validation <i>reserves</i> the playerId then and there. A reservation whose
    /// handshake never completes expires with the ticket that created it — no timer, no
    /// sweep, and nothing that can leak a slot for longer than the ticket was valid for.
    /// </para>
    /// </remarks>
    public sealed class TicketValidator
    {
        private readonly byte[] _sharedSecret;
        private readonly ushort _serverId;

        // playerId -> the Unix ms at which this claim lapses. Never larger than the number of
        // tickets issued inside one 60 s window, so a Dictionary is the right shape.
        private readonly Dictionary<uint, long> _claims = new Dictionary<uint, long>();

        // Players admitted whose connection has not been reported yet. See
        // TryTakePendingAdmission. A List rather than a Queue because the identity-matched
        // overload has to remove from the middle; index 0 is still the head.
        private readonly List<uint> _pendingAdmissions = new List<uint>();

        /// <param name="sharedSecret">
        /// The HMAC key, shared with the master server. An empty secret makes every ticket
        /// <see cref="TicketRejection.Malformed"/> — fail-closed, matching
        /// <see cref="JoinTicket.Verify"/>.
        /// </param>
        /// <param name="serverId">
        /// This server's id as assigned by GS_REGISTER. 0 disables the check, which is the
        /// correct behaviour before the master has answered: refusing every ticket because we
        /// have not been told our own id yet would make the server unjoinable in standalone
        /// mode, which phase-03's risk table lists as a supported configuration.
        /// </param>
        public TicketValidator(ReadOnlySpan<byte> sharedSecret, ushort serverId = 0)
        {
            _sharedSecret = sharedSecret.ToArray();
            _serverId     = serverId;
        }

        public long Accepted { get; private set; }

        public long Rejected { get; private set; }

        /// <summary>Rejections by reason, indexed by <see cref="TicketRejection"/>.</summary>
        public long[] RejectionsByReason { get; } = new long[6];

        /// <summary>Live claims: players connected or mid-handshake.</summary>
        public int ClaimCount => _claims.Count;

        /// <summary>
        /// Validates a ticket and, on success, claims the player slot it names.
        /// </summary>
        /// <param name="nowUnixMs">Wall clock, in Unix milliseconds.</param>
        public bool TryAdmit(
            ReadOnlySpan<byte> ticket, long nowUnixMs, out uint playerId, out TicketRejection reason)
        {
            playerId = 0;

            TicketVerifyResult verified = JoinTicket.Verify(ticket, _sharedSecret, nowUnixMs);
            if (verified != TicketVerifyResult.Valid)
            {
                reason = verified switch
                {
                    TicketVerifyResult.BadSignature => TicketRejection.BadSignature,
                    TicketVerifyResult.Expired      => TicketRejection.Expired,
                    _                               => TicketRejection.Malformed,
                };
                return Reject(reason);
            }

            // Only now are the fields trustworthy. Reading them before Verify would be reading
            // attacker-controlled bytes, which is what TryReadFields' own remarks warn about.
            if (!JoinTicket.TryReadFields(
                    ticket, out uint id, out ushort ticketServerId, out _,
                    out long expiresAtUnixMs, out _))
                return Reject(TicketRejection.Malformed, out reason);

            if (_serverId != 0 && ticketServerId != _serverId)
                return Reject(TicketRejection.WrongServer, out reason);

            ExpireClaims(nowUnixMs);

            if (_claims.ContainsKey(id))
                return Reject(TicketRejection.AlreadyConnected, out reason);

            _claims[id] = expiresAtUnixMs;
            _pendingAdmissions.Add(id);
            playerId    = id;
            reason      = TicketRejection.None;
            Accepted++;
            return true;
        }

        /// <summary>Admissions whose connection has not been reported yet.</summary>
        public int PendingAdmissionCount => _pendingAdmissions.Count;

        /// <summary>
        /// Takes the admission belonging to a known player id.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Prefer this over the positional overload.</b> <c>ConnectionInfo.PlayerId</c> now
        /// carries the identity the transport read out of the signed ticket (checklist B7), so
        /// a caller holding a <c>ConnectionInfo</c> can pair on the identity itself and never
        /// has to assume the queue head is the connection being reported.
        /// </para>
        /// </remarks>
        /// <returns>False when this player has no admission outstanding.</returns>
        public bool TryTakePendingAdmission(uint playerId)
        {
            int index = _pendingAdmissions.IndexOf(playerId);
            if (index < 0) return false;

            _pendingAdmissions.RemoveAt(index);
            return true;
        }

        /// <summary>
        /// Takes the oldest admission that has not yet been paired with a connection.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Positional, and therefore the fallback.</b> It exists for a transport that reports
        /// no player identity on connect — the loopback, which has no ticket to read one out of.
        /// Admission and connection happen in the same <c>Poll</c>, in order, one immediately
        /// after the other, so on that transport the head is the connection being reported.
        /// </para>
        /// <para>
        /// <b>What it costs when that assumption breaks.</b> A handshake that is admitted and
        /// then fails before connecting leaves its admission at the head, and the next
        /// connection is paired with the wrong player. The consequence is worse than it looks:
        /// the mis-paired claim is then passed to <see cref="ConfirmConnected"/>, which sets it
        /// to <see cref="long.MaxValue"/> — so it does <b>not</b> lapse with the ticket, and the
        /// real owner cannot rejoin until whoever was mis-paired disconnects, or the server
        /// restarts. Nobody is admitted who should not have been.
        /// </para>
        /// <para>
        /// On the UDP transport, use <see cref="TryTakePendingAdmission(uint)"/> instead.
        /// </para>
        /// </remarks>
        public bool TryTakePendingAdmission(out uint playerId)
        {
            if (_pendingAdmissions.Count == 0)
            {
                playerId = 0;
                return false;
            }

            playerId = _pendingAdmissions[0];
            _pendingAdmissions.RemoveAt(0);
            return true;
        }

        /// <summary>
        /// Converts a claim made during the handshake into one that lasts as long as the
        /// connection does. Call from the transport's connected callback.
        /// </summary>
        /// <remarks>
        /// Without this the claim lapses when the ticket does — 60 seconds into a session that
        /// may run for an hour — and a replay of the same ticket would then be admitted
        /// alongside the player already using it.
        /// </remarks>
        public void ConfirmConnected(uint playerId) => _claims[playerId] = long.MaxValue;

        /// <summary>Releases a player's claim. Call on disconnect.</summary>
        public bool Release(uint playerId) => _claims.Remove(playerId);

        /// <summary>Drops every claim. Used when the server tears the world down.</summary>
        public void Clear()
        {
            _claims.Clear();
            _pendingAdmissions.Clear();
        }

        public bool IsClaimed(uint playerId) => _claims.ContainsKey(playerId);

        /// <summary>
        /// Drops claims whose tickets have expired without the handshake completing.
        /// </summary>
        /// <remarks>
        /// A linear pass over a table that holds at most one entry per connected player plus
        /// whatever handshakes are in flight, run once per join attempt. Sweeping on join
        /// rather than on a timer means the cost is paid by the thing that creates the garbage.
        /// </remarks>
        private void ExpireClaims(long nowUnixMs)
        {
            if (_claims.Count == 0) return;

            List<uint>? doomed = null;
            foreach (KeyValuePair<uint, long> claim in _claims)
            {
                if (claim.Value > nowUnixMs) continue;
                (doomed ??= new List<uint>()).Add(claim.Key);
            }

            if (doomed == null) return;
            for (int i = 0; i < doomed.Count; i++) _claims.Remove(doomed[i]);
        }

        private bool Reject(TicketRejection reason)
        {
            Rejected++;
            RejectionsByReason[(int)reason]++;
            return false;
        }

        private bool Reject(TicketRejection value, out TicketRejection reason)
        {
            reason = value;
            return Reject(value);
        }
    }
}
