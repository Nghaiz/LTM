using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// Actor id to display name, rebuilt from every <c>S_PLAYER_LIST</c>. The half of the
    /// killfeed that turns "actor 7 killed actor 3" into two names. debt-closure phase 2, C-3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the subscriber the opcode never had.</b> V3 gave <c>S_PLAYER_LIST</c> its
    /// struct, its writer and its router case and stopped there, on purpose — the phase added no
    /// MonoBehaviour, so <c>ClientMessageRouter.OnPlayerList</c> shipped raised-but-unsubscribed
    /// and sat in <c>ClientWiringGate</c>'s exemption list. This type is what retires that entry.
    /// </para>
    /// <para>
    /// <b>Strings are decoded here and nowhere else.</b> The router hands out
    /// <see cref="PlayerListEntry"/> rows whose <c>Name</c> points into a buffer that is recycled
    /// the moment the callback returns, so anything that keeps a name past the call has to copy
    /// it. Doing that once, here, is why <see cref="PlayerListMessage.NameOf"/>'s allocation is
    /// paid per broadcast rather than per killfeed line per frame.
    /// </para>
    /// <para>
    /// <b>Replace, never merge.</b> <c>S_PLAYER_LIST</c> is a whole table, not a delta — the
    /// server sends it on join and on change. Merging would leave a player who disconnected
    /// named forever, which is the failure that looks like a bug in the killfeed rather than in
    /// the table.
    /// </para>
    /// </remarks>
    public sealed class PlayerNameTable
    {
        /// <summary>
        /// One slot per possible actor id, so a lookup is an index rather than a hash. 64 entries
        /// of <c>string?</c> is 512 bytes and it never rehashes on the frame a player joins.
        /// </summary>
        private readonly string?[] _names = new string?[ProtocolConstants.MAX_ACTORS];

        /// <summary>How many rows the last broadcast carried. Zero before the first one.</summary>
        public int Count { get; private set; }

        /// <summary>Bumped on every applied broadcast, so a HUD can cache and invalidate.</summary>
        public int Revision { get; private set; }

        /// <summary>
        /// Replaces the whole table from one <c>S_PLAYER_LIST</c>.
        /// </summary>
        /// <remarks>
        /// Wired straight to <c>ClientMessageRouter.OnPlayerList</c>: the router already parsed
        /// into its own reusable row buffer and passes the live count beside it, because the
        /// buffer is <see cref="ProtocolConstants.MAX_ACTORS"/> long regardless of how many rows
        /// arrived. Reading <c>entries.Length</c> instead of <paramref name="count"/> would name
        /// every actor after the last real row with the previous broadcast's leftovers.
        /// </remarks>
        public void Apply(PlayerListEntry[] entries, int count)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));

            if (count < 0 || count > entries.Length)
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    $"S_PLAYER_LIST reported {count} rows in a {entries.Length}-row buffer.");

            Array.Clear(_names, 0, _names.Length);

            for (int i = 0; i < count; i++)
            {
                byte actorId = entries[i].ActorId;

                // A row naming an actor id this build cannot hold is dropped rather than thrown
                // on: the router counts malformed input instead of throwing (V10 D22) and a
                // subscriber that threw would propagate into the transport pump. The id is a u8
                // and MAX_ACTORS is 64, so this is reachable only from a server that raised the
                // ceiling without raising ours.
                if (actorId >= _names.Length) continue;

                // SANITIZED HERE, on the client's own ingress, even though the server sanitized
                // its copy at the transport (ledger X-36). That is not redundant work: the
                // server's pass protects the SERVER from the ticket, and this one protects THIS
                // client from the server. A client cannot verify a game server it connected to
                // — a modified or hostile one can put any bytes it likes in S_PLAYER_LIST, and
                // they land in a killfeed label with rich text on. Trusting the far end because
                // the near end is careful is how the near end's care stops meaning anything.
                //
                // A name that sanitizes to nothing is left NULL rather than stored as "": null
                // is this table's existing word for "no broadcast has named this actor", and
                // NameOr's fallback is what the caller wants in both cases. Storing an empty
                // string would render a blank feed line, which reads as a rendering fault.
                string sanitized = PlayerNameSanitizer.Sanitize(
                    PlayerListMessage.NameOf(in entries[i]));

                _names[actorId] = sanitized.Length == 0 ? null : sanitized;
            }

            Count = count;
            Revision++;
        }

        /// <summary>
        /// The name for an actor id, or null when no broadcast has named it.
        /// </summary>
        /// <remarks>
        /// Null rather than a manufactured "Player 7": the caller is a killfeed line and it is
        /// the one that knows what an unnamed actor should read as. Inventing the fallback here
        /// would make a genuinely missing name indistinguishable from a real one.
        /// </remarks>
        public string? NameOf(ushort actorId)
            => actorId < _names.Length ? _names[actorId] : null;

        /// <summary>The name, or <paramref name="fallback"/> when the table has none.</summary>
        public string NameOr(ushort actorId, string fallback)
            => NameOf(actorId) ?? fallback;

        /// <summary>Drops every name. Call on disconnect or when leaving a match.</summary>
        public void Reset()
        {
            Array.Clear(_names, 0, _names.Length);
            Count = 0;
            Revision++;
        }
    }
}
