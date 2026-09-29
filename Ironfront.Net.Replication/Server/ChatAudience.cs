using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Who hears a chat line: the one rule that keeps a team line on its own side. Owner request
    /// 2026-09-29: team chat, with the other side never able to read it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Decided on the server, from sides the server set.</b> The speaker's side and each
    /// listener's are the actors' own teams, never anything a client sent, so no client can widen
    /// a team line by claiming to stand somewhere else. A player who wants the other side to read
    /// something has <see cref="ChatChannel.All"/> for that.
    /// </para>
    /// <para>
    /// <b>The speaker always hears their own line</b>, on either channel: seeing it come back is
    /// the only proof a player has that it was sent. A speaker with no side yet reaches nobody
    /// else on the team channel -- there is no side to address, and guessing one is exactly how a
    /// line would reach the wrong people.
    /// </para>
    /// </remarks>
    public static class ChatAudience
    {
        /// <summary>Whether one listener receives a line said on <paramref name="channel"/>.</summary>
        public static bool Hears(ChatChannel channel, byte speakerTeam, byte listenerTeam, bool isSpeaker)
        {
            if (isSpeaker) return true;
            if (channel == ChatChannel.All) return true;

            // Anything else this build does not define reaches nobody: failing closed is the
            // only safe reading of a channel whose audience we cannot name.
            if (channel != ChatChannel.Team) return false;

            return speakerTeam != TeamId.None && listenerTeam == speakerTeam;
        }
    }
}
