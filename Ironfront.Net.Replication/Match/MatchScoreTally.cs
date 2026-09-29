using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Match
{
    /// <summary>
    /// Per-actor kill and death accounting for one match. Phase P6 task 3.1, checklist A13.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fed from where a kill is RESOLVED, never from where <c>S_DEATH</c> is serialised.</b>
    /// The wire message is a broadcast on a reliable channel and a retransmit of it is an
    /// ordinary event, so a tally reading the wire would double-count one death per lost ack —
    /// and would miss any death resolved on a path that does not frame a message. The single
    /// resolution point is <c>ServerTickLoop.EmitDeath</c>, which is also where the ticket comes
    /// off the dying team, so the two counts cannot disagree about what happened.
    /// </para>
    /// <para>
    /// <b>Engine-free, for <c>ServerMessageRouter</c>'s reason.</b> The only caller is a
    /// <c>MonoBehaviour</c> and therefore unreachable from CI; the arithmetic that decides
    /// whether a suicide scores a kill belongs where a test can watch it get that wrong.
    /// </para>
    /// <para>
    /// <b>Fixed arrays, not a dictionary.</b> Actor ids are allocated from
    /// <c>0..MAX_ACTORS - 1</c> (protocol-spec.md § 4.3.1), so the whole id space is 64 slots
    /// and indexing it directly costs nothing and allocates nothing after construction. A death
    /// is not a hot path, but the class it is called from is, and a table that grows during a
    /// tick is the kind of thing that only shows up in the fifth round of a long server.
    /// </para>
    /// </remarks>
    public sealed class MatchScoreTally
    {
        private readonly int[] _kills  = new int[ProtocolConstants.MAX_ACTORS];
        private readonly int[] _deaths = new int[ProtocolConstants.MAX_ACTORS];

        // The Tab board's extra columns (owner's report of 2026-09-30), kept beside the counts
        // they extend so one Forget and one Clear cover every per-actor number.
        private readonly int[] _headshots  = new int[ProtocolConstants.MAX_ACTORS];
        private readonly int[] _streak     = new int[ProtocolConstants.MAX_ACTORS];
        private readonly int[] _bestStreak = new int[ProtocolConstants.MAX_ACTORS];
        private readonly int[] _points     = new int[ProtocolConstants.MAX_ACTORS];

        /// <summary>Deaths recorded this match, across every actor.</summary>
        /// <remarks>
        /// Cumulative and un-attributed, so "did anything reach the tally at all" is answerable
        /// without walking the id space — which is the question asked when a match ends and the
        /// report is empty.
        /// </remarks>
        public long DeathsRecorded { get; private set; }

        /// <summary>
        /// Deaths whose killer was the world, or the victim itself. Counted, never scored.
        /// </summary>
        public long UnattributedDeaths { get; private set; }

        /// <summary>
        /// Deaths naming an id outside the actor id space. Non-zero means a caller is passing
        /// something that is not an actor id.
        /// </summary>
        /// <remarks>
        /// A counter rather than a throw, for <c>ServerMessageRouter.MalformedMessages</c>'s
        /// reason: a bad id from one damage path must not take the tick loop down for everyone
        /// else. <c>DeathMessage.EnvironmentKiller</c> (0xFFFF) is NOT counted here — it is a
        /// documented value meaning "nobody", and it lands in
        /// <see cref="UnattributedDeaths"/> instead.
        /// </remarks>
        public long OutOfRangeIds { get; private set; }

        /// <summary>
        /// Records one death against its victim, and the kill against its killer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A suicide is a death and not a kill.</b> Killer equal to victim is what a fall, a
        /// player's own grenade, or a vehicle rolled onto its roof produces, and crediting it
        /// would let a scoreboard be climbed by dying. It is counted in
        /// <see cref="UnattributedDeaths"/> so the case stays visible rather than looking like a
        /// tally that missed one.
        /// </para>
        /// <para>
        /// <b>A team kill still scores.</b> Deliberately: this class records what happened, and
        /// whether a team kill should cost the killer is a match rule, which lives in
        /// <c>MatchStateMachine</c> with every other rule. Encoding it here would put half a
        /// scoring policy in a counter, where nobody would look for it.
        /// </para>
        /// </remarks>
        public void RecordDeath(ushort victimActorId, ushort killerActorId)
        {
            if (victimActorId >= ProtocolConstants.MAX_ACTORS)
            {
                OutOfRangeIds++;
                return;
            }

            _deaths[victimActorId]++;
            _streak[victimActorId] = 0;
            DeathsRecorded++;

            if (killerActorId == DeathMessage.EnvironmentKiller || killerActorId == victimActorId)
            {
                UnattributedDeaths++;
                return;
            }

            if (killerActorId >= ProtocolConstants.MAX_ACTORS)
            {
                OutOfRangeIds++;
                return;
            }

            _kills[killerActorId]++;
        }

        /// <summary>
        /// Credits one ENEMY kill's extras to its killer: the streak it extends, whether it was a
        /// headshot, and the points it put on the killer's side. Call after <see cref="RecordDeath"/>
        /// for the same death, and only for a kill of the other side.
        /// </summary>
        /// <remarks>
        /// <b>A team kill earns none of these</b>, although <see cref="RecordDeath"/> still counts
        /// it as a kill: that count records what happened, and these record what the kill was
        /// worth to the side, which for friendly fire is nothing -- the award goes to the enemy.
        /// </remarks>
        public void CreditKill(ushort killerActorId, bool headshot, int points)
        {
            if (killerActorId >= ProtocolConstants.MAX_ACTORS) return;

            if (headshot) _headshots[killerActorId]++;

            int streak = ++_streak[killerActorId];
            if (streak > _bestStreak[killerActorId]) _bestStreak[killerActorId] = streak;

            if (points > 0) _points[killerActorId] += points;
        }

        /// <summary>Enemy kills this actor made with a headshot, or 0.</summary>
        public int HeadshotsOf(ushort actorId)
            => actorId < ProtocolConstants.MAX_ACTORS ? _headshots[actorId] : 0;

        /// <summary>Enemy kills since this actor last died, or 0.</summary>
        public int StreakOf(ushort actorId)
            => actorId < ProtocolConstants.MAX_ACTORS ? _streak[actorId] : 0;

        /// <summary>The longest streak this actor has had this match, or 0.</summary>
        public int BestStreakOf(ushort actorId)
            => actorId < ProtocolConstants.MAX_ACTORS ? _bestStreak[actorId] : 0;

        /// <summary>The points this actor's kills put on its side's score, or 0.</summary>
        public int PointsOf(ushort actorId)
            => actorId < ProtocolConstants.MAX_ACTORS ? _points[actorId] : 0;

        /// <summary>Kills credited to this actor, or 0.</summary>
        public int KillsOf(ushort actorId)
            => actorId < ProtocolConstants.MAX_ACTORS ? _kills[actorId] : 0;

        /// <summary>Deaths recorded against this actor, or 0.</summary>
        public int DeathsOf(ushort actorId)
            => actorId < ProtocolConstants.MAX_ACTORS ? _deaths[actorId] : 0;

        /// <summary>True when this actor has neither killed nor died this match.</summary>
        /// <remarks>
        /// The predicate the end-of-match report filters on, named here rather than spelled as
        /// <c>Kills == 0 &amp;&amp; Deaths == 0</c> at the call site — see
        /// <c>ServerMasterReporter.CollectScores</c> for why a scoreless player is omitted
        /// rather than reported as a row of zeroes.
        /// </remarks>
        public bool IsUntouched(ushort actorId) => KillsOf(actorId) == 0 && DeathsOf(actorId) == 0;

        /// <summary>Zeroes one actor's kills and deaths, for a body handed to a new occupant.</summary>
        /// <remarks>
        /// <para>
        /// The tally is keyed by actor id, and a player slot keeps its id from one occupant to
        /// the next. Without this, a player who joins into a leaver's slot inherits the leaver's
        /// row: on every scoreboard, and in the end-of-match report, which reads this tally by
        /// the actor ids of the players still connected.
        /// </para>
        /// <para>
        /// The match-wide counters are left alone. They describe the deaths the round has
        /// resolved, and a player leaving does not un-resolve any of them.
        /// </para>
        /// </remarks>
        public void Forget(ushort actorId)
        {
            if (actorId >= ProtocolConstants.MAX_ACTORS) return;

            _kills[actorId] = 0;
            _deaths[actorId] = 0;
            _headshots[actorId] = 0;
            _streak[actorId] = 0;
            _bestStreak[actorId] = 0;
            _points[actorId] = 0;
        }

        /// <summary>Empties the tally for a new round.</summary>
        public void Clear()
        {
            Array.Clear(_kills, 0, _kills.Length);
            Array.Clear(_deaths, 0, _deaths.Length);
            Array.Clear(_headshots, 0, _headshots.Length);
            Array.Clear(_streak, 0, _streak.Length);
            Array.Clear(_bestStreak, 0, _bestStreak.Length);
            Array.Clear(_points, 0, _points.Length);

            DeathsRecorded     = 0;
            UnattributedDeaths = 0;
            OutOfRangeIds      = 0;
        }
    }
}
