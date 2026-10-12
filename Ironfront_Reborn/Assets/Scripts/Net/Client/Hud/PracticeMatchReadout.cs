#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Server;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// The practice match's killfeed and Tab board: the online ones, fed by the offline game
    /// instead of the wire. Owner's run of 2026-10-12: the killfeed and the Tab board in practice,
    /// as online.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The online tables, filled from the offline match.</b> A death arrives as a
    /// <see cref="PracticeDeath"/> and is resolved by the server's own rule
    /// (<see cref="DeathAttribution"/>) into the <see cref="DeathMessage"/> a server would have
    /// sent, and counted by the server's tally (<see cref="MatchScoreTally"/>) through its one row
    /// projection. Practice is its own server, so it applies the server's rules. The lines and the
    /// board are then drawn by the code the online presenter draws with
    /// (<see cref="KillfeedLines"/>, <see cref="ScoreboardColumns"/>, <see cref="ScoreboardHeader"/>),
    /// so the two cannot drift apart.
    /// </para>
    /// <para>
    /// <b>Local and offline.</b> Everything here is this process's own match: it opens no
    /// connection and asks nothing of a server or the master, so practice keeps its killfeed and
    /// its board with no internet at all, and no server carries any of it.
    /// </para>
    /// <para>
    /// Clock-free: every call takes the time, so a test drives it frame by frame.
    /// </para>
    /// </remarks>
    internal sealed class PracticeMatchReadout : IKillfeedSubjects
    {
        /// <summary>
        /// How often the board's table is rebuilt while no death moves it: the server's cadence for
        /// the same table, which is what picks up a soldier respawning or taking a seat.
        /// </summary>
        internal const float TableRefreshSeconds = 2f;

        /// <summary>What the player goes by with no account signed in: practice needs none.</summary>
        internal const string UnsignedPlayerName = "You";

        private readonly KillfeedModel _killfeed = new KillfeedModel();
        private readonly KillfeedAccoladeTracker _accolades = new KillfeedAccoladeTracker();
        private readonly PlayerNameTable _names = new PlayerNameTable();
        private readonly BotRoster _bots = new BotRoster();
        private readonly PlayerScoreTable _scores = new PlayerScoreTable();
        private readonly MatchScoreTally _tally = new MatchScoreTally();
        private readonly PracticeActorIds _ids = new PracticeActorIds();
        private readonly ScoreboardColumns _columns = new ScoreboardColumns();

        private readonly PracticeActor[] _actors = new PracticeActor[ProtocolConstants.MAX_ACTORS];
        private readonly PlayerScoreEntry[] _rows = new PlayerScoreEntry[ProtocolConstants.MAX_ACTORS];
        private readonly PlayerListEntry[] _nameRow = new PlayerListEntry[1];

        /// <summary>The flags met so far, by key, and their authored names, by the number the feed uses.</summary>
        private readonly Dictionary<int, int> _flagIndex = new Dictionary<int, int>();
        private readonly List<string> _flagNames = new List<string>();

        private bool _hasPlayer;
        private ushort _playerId;
        private string? _appliedName;

        private bool _started;
        private byte _winner = TeamId.None;
        private bool _boardOpen;
        private bool _tableDirty = true;
        private float _nextTableAt;

        // What was last pushed, so a frame where nothing moved writes nothing: the online
        // presenter's keys, for its reasons.
        private int _pushedCount = -1;
        private long _pushedFeedRevision = -1;
        private int _pushedFeedNames = -1;
        private int _pushedFeedBots = -1;
        private int _pushedFeedScores = -1;
        private int _pushedFeedLocal = -1;
        private bool _pushedBoardVisible;
        private int _pushedTableScores = -1;
        private int _pushedTableNames = -1;
        private int _pushedTableBots = -1;
        private int _pushedTableLocal = -1;
        private ScoreboardHeaderKey _pushedHeader;
        private bool _pushedHeaderValid;

        /// <summary>The signed-in account's name, or null to go by <see cref="UnsignedPlayerName"/>.</summary>
        public string? PlayerName { get; set; }

        /// <summary>Whether the board is open: Tab toggles it, and the player's death closes it.</summary>
        public bool IsBoardOpen => _boardOpen;

        /// <summary>The lines on screen, newest first.</summary>
        public KillfeedModel Killfeed => _killfeed;

        /// <summary>The board's table, as the last refresh left it.</summary>
        public PlayerScoreTable Scores => _scores;

        /// <summary>The number the HUD gave the soldier with this key, if it has met them.</summary>
        public bool TryGetActorId(int key, out ushort actorId) => _ids.TryGet(key, out actorId);

        /// <summary>One death: a killfeed line now, and the board's counts.</summary>
        public void OnDied(in PracticeDeath death, float now)
        {
            if (!Meet(death.VictimKey, death.VictimTeam, death.VictimIsPlayer, out ushort victim)) return;

            ushort killer = DeathMessage.EnvironmentKiller;
            byte killerTeam = TeamId.None;
            if (death.KillerKey == death.VictimKey)
            {
                killer = victim;
                killerTeam = death.VictimTeam;
            }
            else if (death.KillerKey != 0 && Meet(death.KillerKey, death.KillerTeam, death.KillerIsPlayer, out ushort id))
            {
                killer = id;
                killerTeam = death.KillerTeam;
            }

            // Nobody empties a vehicle on record offline, so a crew that dies with one is the
            // world's unless somebody named the kill.
            DeathAttribution attribution = DeathAttribution.Resolve(
                killer, death.WeaponId, death.Detail, death.VehicleType,
                DeathMessage.EnvironmentKiller, death.KillerSeatVehicleType);

            var message = new DeathMessage(
                victim, attribution.KillerActorId, death.Cause, 0, 0, 0,
                (byte)(death.Headshot ? HitboxType.Head : HitboxType.Body),
                attribution.WeaponId, attribution.VehicleType, attribution.Detail, death.DistanceMetres);

            KillfeedEntry entry = KillfeedEntry.From(in message, now);
            entry = entry.WithTeams(entry.KilledByEnvironment ? TeamId.None : killerTeam, death.VictimTeam);
            entry = entry.WithAccolades(_accolades.Observe(in entry, now));
            entry = entry.WithPriority(IsPlayer(victim) || (!entry.KilledByEnvironment && IsPlayer(entry.KillerActorId)));
            _killfeed.Push(in entry);

            // The board counts the deaths the score counts, and a won match counts no more: the
            // server's CountsDeaths. The winning kill itself lands before the win is seen.
            if (_winner == TeamId.None)
            {
                _tally.RecordDeath(victim, entry.KillerActorId);
                if (entry.IsScoredKill && killerTeam != TeamId.None && killerTeam != death.VictimTeam)
                    _tally.CreditKill(entry.KillerActorId, death.Headshot, death.Points);
                _tableDirty = true;
            }

            // The board would sit over the loadout screen a practice death opens; Tab brings it back.
            if (death.VictimIsPlayer) _boardOpen = false;
        }

        /// <summary>A flag changing hands: a killfeed line while the match is being played.</summary>
        public void OnFlagChanged(in PracticeFlagChange change, float now)
        {
            // The online feed posts flags only in a live round (IsRoundLive). Offline, a flip
            // before the HUD's first frame is the scene setting its opening flags, and one after
            // the match is won is the field playing on past the result.
            if (!_started || _winner != TeamId.None) return;

            int point = FlagIndexOf(change.Key, change.Name);
            if (change.Owner != TeamId.None)
                _killfeed.Push(KillfeedEntry.Flag(true, change.Owner, point, now));
            else if (change.PreviousOwner != TeamId.None)
                _killfeed.Push(KillfeedEntry.Flag(false, change.PreviousOwner, point, now));
        }

        /// <summary>
        /// One frame: the round's start and end, the board's table, then whatever moved on screen.
        /// </summary>
        /// <param name="togglePressed">The player pressed the scoreboard key this frame.</param>
        /// <param name="match">How the match was set up, when <paramref name="hasMatch"/>.</param>
        public void Tick(
            float now, IMatchHud? hud, IPracticeMatchView? view, bool togglePressed, string mapName,
            bool hasMatch, in PracticeMatch match)
        {
            if (!_started)
            {
                _started = true;
                _accolades.Reset();
                _killfeed.Push(KillfeedEntry.Round(true, TeamId.None, now));
            }

            PracticeScore score = view != null ? view.Score : default;
            if (view != null && _winner == TeamId.None && score.WinningTeam != TeamId.None)
            {
                _winner = score.WinningTeam;
                _killfeed.Push(KillfeedEntry.Round(false, _winner, now));
            }

            if (view != null && (_tableDirty || now >= _nextTableAt))
            {
                RefreshTable(view);
                _tableDirty = false;
                _nextTableAt = now + TableRefreshSeconds;
            }

            if (togglePressed) _boardOpen = !_boardOpen;

            // KillfeedModel has no clock of its own: expiry and release are the caller's, once a
            // frame, before anything reads it.
            _killfeed.Advance(now);

            if (hud == null)
            {
                // A HUD that arrives later is written in full rather than left empty until the
                // next change.
                _pushedCount = -1;
                _pushedBoardVisible = false;
                _pushedHeaderValid = false;
                return;
            }

            PushKillfeed(hud);
            PushBoard(hud, view != null, in score, mapName, hasMatch, in match);
        }

        /// <summary>Leaves no rows and no board behind: the presenter is going away.</summary>
        public void Clear(IMatchHud? hud)
        {
            hud?.SetKillfeedLineCount(0);
            hud?.SetScoreboardVisible(false);
            _pushedCount = -1;
            _pushedBoardVisible = false;
            _pushedTableScores = -1;
            _pushedHeaderValid = false;
            _boardOpen = false;
        }

        /// <summary>
        /// Numbers the soldier with <paramref name="key"/> and records what it is, the way a
        /// spawn would online. False when the id space is used up.
        /// </summary>
        private bool Meet(int key, byte team, bool isPlayer, out ushort id)
        {
            if (!_ids.TryAssign(key, out id)) return false;

            _bots.Apply(new SpawnActorMessage(
                id, team, isPlayer ? SpawnFlags.IsLocalPlayer : SpawnFlags.IsBot, 0, 0, 0, 0, 0, WeaponIds.NONE));
            if (isPlayer) NamePlayer(id);
            return true;
        }

        /// <summary>The name table holds the player, the one person in a practice match.</summary>
        private void NamePlayer(ushort id)
        {
            string name = string.IsNullOrWhiteSpace(PlayerName) ? UnsignedPlayerName : PlayerName!;
            if (_hasPlayer && _playerId == id && string.Equals(name, _appliedName, StringComparison.Ordinal)) return;

            _hasPlayer = true;
            _playerId = id;
            _appliedName = name;
            _nameRow[0] = new PlayerListEntry { ActorId = (byte)id, Name = Encoding.UTF8.GetBytes(name) };
            _names.Apply(_nameRow, 1);
        }

        private bool IsPlayer(ushort actorId) => _hasPlayer && actorId == _playerId;

        /// <summary>Everybody in the match, with what the tally says they did.</summary>
        private void RefreshTable(IPracticeMatchView view)
        {
            int count = view.FillActors(_actors);
            int rows = 0;

            for (int i = 0; i < count; i++)
            {
                PracticeActor actor = _actors[i];
                if (!Meet(actor.Key, actor.Team, actor.IsPlayer, out ushort id)) continue;

                PlayerStatusFlags status = (actor.IsAlive ? PlayerStatusFlags.Alive : PlayerStatusFlags.None)
                                           | (actor.IsSeated ? PlayerStatusFlags.Seated : PlayerStatusFlags.None);
                _rows[rows++] = _tally.RowFor((byte)id, actor.Team, status, 0);
            }

            _scores.Apply(_rows, rows);
        }

        private int FlagIndexOf(int key, string name)
        {
            if (_flagIndex.TryGetValue(key, out int index)) return index;

            index = _flagNames.Count;
            _flagNames.Add(name);
            _flagIndex.Add(key, index);
            return index;
        }

        private void PushKillfeed(IMatchHud hud)
        {
            int count = _killfeed.Count;
            int localKey = _hasPlayer ? _playerId : -1;

            if (count == _pushedCount
                && _killfeed.Revision == _pushedFeedRevision
                && _names.Revision == _pushedFeedNames
                && _bots.Revision == _pushedFeedBots
                && _scores.Revision == _pushedFeedScores
                && localKey == _pushedFeedLocal)
            {
                return;
            }

            _pushedCount = count;
            _pushedFeedRevision = _killfeed.Revision;
            _pushedFeedNames = _names.Revision;
            _pushedFeedBots = _bots.Revision;
            _pushedFeedScores = _scores.Revision;
            _pushedFeedLocal = localKey;

            hud.SetKillfeedLineCount(count);
            for (int i = 0; i < count; i++)
            {
                KillfeedEntry entry = _killfeed[i];
                KillfeedLine line = KillfeedLines.For(in entry, this, _killfeed.HoldSeconds, _hasPlayer, _playerId);
                hud.SetKillfeedLine(i, in line);
            }
        }

        private void PushBoard(
            IMatchHud hud, bool hasScore, in PracticeScore score, string mapName, bool hasMatch, in PracticeMatch match)
        {
            if (_boardOpen != _pushedBoardVisible)
            {
                _pushedBoardVisible = _boardOpen;
                hud.SetScoreboardVisible(_boardOpen);

                // The HUD clears a board on the way down, so an opened one is written in full.
                _pushedTableScores = -1;
                _pushedHeaderValid = false;
            }

            if (!_boardOpen) return;

            int localKey = _hasPlayer ? _playerId : -1;
            if (_scores.Revision != _pushedTableScores
                || _names.Revision != _pushedTableNames
                || _bots.Revision != _pushedTableBots
                || localKey != _pushedTableLocal)
            {
                _pushedTableScores = _scores.Revision;
                _pushedTableNames = _names.Revision;
                _pushedTableBots = _bots.Revision;
                _pushedTableLocal = localKey;

                _columns.Write(hud, _scores, _names, _bots, _hasPlayer, _playerId);
            }

            // No clock: a practice round runs until a side wins, as an online round does.
            var key = new ScoreboardHeaderKey(
                (int)(_winner == TeamId.None ? MatchPhase.Playing : MatchPhase.Ended),
                score.Score0, score.Score1, -1, score.Flags0, score.Flags1,
                hasMatch ? match.VictoryPoints : 0, _winner, _columns.Players, _columns.Humans);

            if (_pushedHeaderValid && key.Equals(_pushedHeader)) return;

            _pushedHeader = key;
            _pushedHeaderValid = true;

            ScoreboardMatch header = ScoreboardHeader.Build(
                mapName,
                hasMatch && match.Night ? GameMode.Night : GameMode.PointMatch,
                hasMatch ? match.Rule : VictoryRule.Margin,
                hasScore,
                in key);
            hud.SetScoreboardMatch(in header);
        }

        string IKillfeedSubjects.NameOf(ushort actorId) => ActorNames.Display(actorId, _names, _bots);

        byte IKillfeedSubjects.TeamOf(ushort actorId)
        {
            byte scored = _scores.TeamOf(actorId);
            return scored != TeamId.None ? scored : _bots.TeamOf(actorId);
        }

        /// <summary>Nobody leaves a practice match.</summary>
        string? IKillfeedSubjects.DepartedNameOf(ushort actorId) => null;

        string IKillfeedSubjects.FlagNameOf(int pointIndex)
            => KillfeedWording.FlagName(
                pointIndex >= 0 && pointIndex < _flagNames.Count ? _flagNames[pointIndex] : null, pointIndex);
    }
}
