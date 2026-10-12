using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Everything the board's header is drawn from, compared as integers so an unchanged header
    /// costs nothing and a clock tick costs one push.
    /// </summary>
    internal readonly struct ScoreboardHeaderKey : IEquatable<ScoreboardHeaderKey>
    {
        public ScoreboardHeaderKey(
            int phase, int score0, int score1, int seconds, int flags0, int flags1,
            int victoryPoints, byte winningTeam, int players, int humans)
        {
            Phase = phase;
            Score0 = score0;
            Score1 = score1;
            Seconds = seconds;
            Flags0 = flags0;
            Flags1 = flags1;
            VictoryPoints = victoryPoints;
            WinningTeam = winningTeam;
            Players = players;
            Humans = humans;
        }

        /// <summary>The round's <see cref="MatchPhase"/>, -1 before the match has said.</summary>
        public int Phase { get; }
        public int Score0 { get; }
        public int Score1 { get; }

        /// <summary>The clock in whole seconds, -1 for a phase with none.</summary>
        public int Seconds { get; }
        public int Flags0 { get; }
        public int Flags1 { get; }
        public int VictoryPoints { get; }
        public byte WinningTeam { get; }
        public int Players { get; }
        public int Humans { get; }

        public bool Equals(ScoreboardHeaderKey other)
            => Phase == other.Phase && Score0 == other.Score0 && Score1 == other.Score1
               && Seconds == other.Seconds && Flags0 == other.Flags0 && Flags1 == other.Flags1
               && VictoryPoints == other.VictoryPoints && WinningTeam == other.WinningTeam
               && Players == other.Players && Humans == other.Humans;
    }

    /// <summary>
    /// The match at the top of the Tab board, worded: the online board's header and the practice
    /// board's, from one implementation (owner's run of 2026-10-12).
    /// </summary>
    internal static class ScoreboardHeader
    {
        /// <param name="hasState">
        /// Whether the match has said anything yet; until it has, the header states the map and
        /// the head count and leaves the lead, the phase and the rules blank.
        /// </param>
        public static ScoreboardMatch Build(
            string mapName, GameMode mode, VictoryRule rule, bool hasState, in ScoreboardHeaderKey key)
        {
            MatchPhase phase = hasState ? (MatchPhase)key.Phase : MatchPhase.WaitingForPlayers;

            return new ScoreboardMatch(
                mapName,
                ScoreboardWording.SummaryLine(mode, key.Players, key.Humans),
                key.Score0,
                key.Score1,
                ScoreboardWording.Lead(key.Score0, key.Score1, key.VictoryPoints),
                hasState
                    ? ScoreboardWording.LeadLine(
                        phase, key.Score0, key.Score1, key.VictoryPoints, key.WinningTeam, rule)
                    : string.Empty,
                ScoreboardWording.Clock(key.Seconds),
                hasState ? ScoreboardWording.PhaseLabel(phase, key.Seconds >= 0) : string.Empty,
                clockUrgent: phase == MatchPhase.Playing && key.Seconds >= 0 && key.Seconds <= 60,
                key.Flags0,
                key.Flags1,
                key.WinningTeam,
                hasState ? ScoreboardWording.Rules(key.VictoryPoints, rule) : string.Empty);
        }
    }
}
