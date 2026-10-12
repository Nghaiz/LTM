using System.Collections.Generic;
using System.Linq;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Unity.Client.Hud;
using NUnit.Framework;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The practice match's killfeed and Tab board (owner's run of 2026-10-12): the online ones,
    /// fed by the offline game's deaths, flags and score, with no server and no network behind them.
    /// </summary>
    public sealed class PracticeMatchReadoutTests
    {
        private const int Player = 1001, BlueBot = 2001, RedBotA = 3001, RedBotB = 3002;
        private const int Fortress = 5001;

        private static readonly PracticeMatch Dustbowl = new PracticeMatch(
            mapId: 1, night: false, VictoryRule.Margin, victoryPoints: 200, vehicles: true,
            team0Bots: 1, team1Bots: 2, playerTeam: 0, capturePoints: 5);

        private PracticeMatchReadout _readout;
        private RecordingHud _hud;
        private FakeView _view;
        private float _now;

        [SetUp]
        public void SetUp()
        {
            _readout = new PracticeMatchReadout { PlayerName = "Nghaiz" };
            _hud = new RecordingHud();
            _view = new FakeView();
            _view.Actors.Add(new PracticeActor(Player, TeamId.Team0, isPlayer: true, isAlive: true, isSeated: false));
            _view.Actors.Add(new PracticeActor(BlueBot, TeamId.Team0, isPlayer: false, isAlive: true, isSeated: true));
            _view.Actors.Add(new PracticeActor(RedBotA, TeamId.Team1, isPlayer: false, isAlive: true, isSeated: false));
            _view.Actors.Add(new PracticeActor(RedBotB, TeamId.Team1, isPlayer: false, isAlive: true, isSeated: false));
            _now = 10f;
            Tick();
        }

        [Test]
        public void TheMatchOpensWithTheRoundLine()
        {
            Assert.AreEqual(1, _hud.Lines.Count);
            Assert.IsTrue(_hud.Lines[0].IsEvent);
            Assert.AreEqual(KillfeedWording.RoundWords(true, TeamId.None), _hud.Lines[0].Sentence);
        }

        [Test]
        public void AKillReadsAsTheOnlineLine_NamesSidesWeaponHeadshotAndRange()
        {
            _readout.OnDied(Kill(RedBotA, TeamId.Team1, Player, TeamId.Team0, killerIsPlayer: true,
                weapon: WeaponIds.RK44, headshot: true, metres: 120, points: 2), _now);
            Tick();

            KillfeedLine line = _hud.Lines[0];
            Assert.AreEqual("Nghaiz", line.KillerName);
            Assert.AreEqual(TeamId.Team0, line.KillerTeam);
            Assert.AreEqual(BotCallsigns.For(IdOf(RedBotA)), line.VictimName);
            Assert.AreEqual(TeamId.Team1, line.VictimTeam);
            Assert.AreEqual(WeaponIds.RK44, line.WeaponId);
            Assert.IsTrue(line.Headshot);
            Assert.IsTrue(line.LocalIsKiller);
            Assert.IsFalse(line.LocalIsVictim);
            Assert.AreEqual(KillfeedWording.Distance(120), line.Distance);
        }

        [Test]
        public void TheBoardListsEverySoldier_PlayersFirst_WithTheTallysCountsAndTheMatch()
        {
            _readout.OnDied(Kill(RedBotA, TeamId.Team1, Player, TeamId.Team0, killerIsPlayer: true,
                weapon: WeaponIds.RK44, headshot: true, metres: 30, points: 3), _now);
            _readout.OnDied(Kill(BlueBot, TeamId.Team0, RedBotB, TeamId.Team1, killerIsPlayer: false,
                weapon: WeaponIds.SIND7, headshot: false, metres: 50, points: 1), _now);
            _view.Score = new PracticeScore(3, 1, 2, 1, TeamId.None);

            Tick(toggle: true);

            Assert.IsTrue(_hud.BoardVisible);
            Assert.AreEqual((2, 1, 1, 1), _hud.Columns[TeamId.Team0], "blue: two soldiers, one person, a kill and a death");
            Assert.AreEqual((2, 0, 1, 1), _hud.Columns[TeamId.Team1], "red: two bots, a kill and a death");

            ScoreboardRow[] blue = _hud.RowsOf(TeamId.Team0);
            Assert.AreEqual("Nghaiz", blue[0].Name, "the person heads the side's list");
            Assert.IsTrue(blue[0].IsLocal);
            Assert.IsFalse(blue[0].IsBot);
            Assert.AreEqual(1, blue[0].Kills);
            Assert.AreEqual(1, blue[0].Headshots);
            Assert.AreEqual(3, blue[0].Points);
            Assert.AreEqual(1, blue[0].Streak);
            Assert.IsTrue(blue[0].IsLeader);
            Assert.AreEqual(BotCallsigns.For(IdOf(BlueBot)), blue[1].Name);
            Assert.IsTrue(blue[1].IsBot);
            Assert.AreEqual(1, blue[1].Deaths);
            Assert.IsTrue(blue[1].IsSeated);

            ScoreboardMatch header = _hud.Header;
            Assert.AreEqual("DUSTBOWL", header.MapName);
            Assert.AreEqual(3, header.Score0);
            Assert.AreEqual(1, header.Score1);
            Assert.AreEqual(2, header.Flags0);
            Assert.AreEqual(1, header.Flags1);
            StringAssert.Contains("4 PLAYERS", header.Summary);
            StringAssert.Contains("1 HUMAN", header.Summary);
            StringAssert.Contains("LEAD BY 200", header.Rules);
            Assert.AreEqual(ScoreboardWording.PhaseLabel(MatchPhase.Playing, false), header.PhaseLabel);
        }

        [Test]
        public void ATeamKillCountsAsAKill_ButEarnsTheKillerNothing()
        {
            _readout.OnDied(Kill(RedBotB, TeamId.Team1, RedBotA, TeamId.Team1, killerIsPlayer: false,
                weapon: WeaponIds.FRAG, headshot: false, metres: 5, points: 0), _now);
            Tick(toggle: true);

            Assert.AreEqual("TEAMKILL", _hud.Lines[0].Badge);
            ScoreboardRow killer = _hud.RowsOf(TeamId.Team1).Single(row => row.ActorId == IdOf(RedBotA));
            Assert.AreEqual(1, killer.Kills);
            Assert.AreEqual(0, killer.Streak);
            Assert.AreEqual(0, killer.Points);
        }

        [Test]
        public void ADeathNobodyCausedIsASentence_AndScoresNobody()
        {
            _readout.OnDied(new PracticeDeath(
                Player, TeamId.Team0, victimIsPlayer: true, 0, TeamId.None, killerIsPlayer: false,
                CauseOfDeath.Fall, WeaponIds.NONE, VehicleIds.NONE, DeathDetail.None, VehicleIds.NONE,
                headshot: false, distanceMetres: 0, points: 0), _now);
            Tick(toggle: true);

            Assert.IsTrue(_hud.Lines[0].IsSentence);
            Assert.AreEqual(string.Empty, _hud.Lines[0].KillerName);
            Assert.IsTrue(_hud.Lines[0].LocalIsVictim);
            Assert.AreEqual(1, _hud.RowsOf(TeamId.Team0).Single(row => row.IsLocal).Deaths);
            Assert.AreEqual(0, _hud.Columns[TeamId.Team0].Kills + _hud.Columns[TeamId.Team1].Kills);
        }

        [Test]
        public void AFlagLineNamesTheFlag_AndTheScenesOpeningFlipsAreNotNews()
        {
            var opening = new PracticeReadoutHarness();
            opening.Readout.OnFlagChanged(new PracticeFlagChange(Fortress, "Fortress Capture Point", TeamId.Team1, TeamId.None), 0f);
            Assert.AreEqual(0, opening.Readout.Killfeed.Count, "a flip before the first frame is the scene setting itself up");

            _readout.OnFlagChanged(new PracticeFlagChange(Fortress, "Fortress Capture Point", TeamId.Team1, TeamId.Team0), _now);
            Tick();

            KillfeedLine line = _hud.Lines[0];
            Assert.IsTrue(line.IsEvent);
            Assert.AreEqual(ScoreboardWording.TeamName(TeamId.Team1), line.KillerName);
            Assert.AreEqual(KillfeedWording.FlagVerb(true), line.Verb);
            Assert.AreEqual("FORTRESS", line.VictimName);
        }

        [Test]
        public void TheWinIsPostedOnce_AndTheBoardCountsNoDeathAfterIt()
        {
            _readout.OnDied(Kill(RedBotA, TeamId.Team1, Player, TeamId.Team0, killerIsPlayer: true,
                weapon: WeaponIds.RK44, headshot: false, metres: 20, points: 4), _now);
            _view.Score = new PracticeScore(200, 0, 3, 0, TeamId.Team0);
            Tick();
            Tick();

            Assert.AreEqual(1, _hud.Lines.Count(line => line.IsEvent && line.Verb == KillfeedWording.RoundWords(false, TeamId.Team0)));

            _readout.OnDied(Kill(RedBotB, TeamId.Team1, Player, TeamId.Team0, killerIsPlayer: true,
                weapon: WeaponIds.RK44, headshot: false, metres: 20, points: 4), _now);
            Tick(toggle: true);

            Assert.AreEqual(1, _hud.RowsOf(TeamId.Team0).Single(row => row.IsLocal).Kills, "the winning kill counts, a later one does not");
            Assert.AreEqual(ScoreboardWording.TeamName(TeamId.Team0) + " WINS", _hud.Header.LeadLine);
            Assert.AreEqual(ScoreboardWording.PhaseLabel(MatchPhase.Ended, false), _hud.Header.PhaseLabel);
        }

        [Test]
        public void TabTogglesTheBoard_AndThePlayersDeathPutsItAway()
        {
            Tick(toggle: true);
            Assert.IsTrue(_hud.BoardVisible);

            Tick(toggle: true);
            Assert.IsFalse(_hud.BoardVisible);

            Tick(toggle: true);
            _readout.OnDied(Kill(Player, TeamId.Team0, RedBotA, TeamId.Team1, killerIsPlayer: false,
                weapon: WeaponIds.RK44, headshot: false, metres: 20, points: 1), _now);
            Tick();
            Assert.IsFalse(_hud.BoardVisible, "the board would cover the loadout screen a practice death opens");
        }

        [Test]
        public void WithNoAccountThePlayerIsYou()
        {
            var unsigned = new PracticeReadoutHarness(playerName: null);
            unsigned.Tick(1f);
            unsigned.Readout.OnDied(Kill(RedBotA, TeamId.Team1, Player, TeamId.Team0, killerIsPlayer: true,
                weapon: WeaponIds.RK44, headshot: false, metres: 20, points: 1), 1.05f);
            unsigned.Tick(1.1f);

            Assert.AreEqual(PracticeMatchReadout.UnsignedPlayerName, unsigned.Hud.Lines[0].KillerName);
        }

        [Test]
        public void SoldiersPastTheIdSpaceAreLeftOut_WithoutThrowing()
        {
            var crowded = new PracticeReadoutHarness();
            for (int i = 0; i < ProtocolConstants.MAX_ACTORS + 12; i++)
                crowded.View.Actors.Add(new PracticeActor(9000 + i, (byte)(i % 2), false, true, false));

            crowded.Tick(1f, toggle: true);
            crowded.Readout.OnDied(Kill(9000 + ProtocolConstants.MAX_ACTORS + 5, TeamId.Team1, 9000, TeamId.Team0,
                killerIsPlayer: false, weapon: WeaponIds.RK44, headshot: false, metres: 20, points: 1), 1f);
            crowded.Tick(1.1f);

            Assert.AreEqual(ProtocolConstants.MAX_ACTORS - 1, crowded.Readout.Scores.Count);
            Assert.AreEqual(1, crowded.Readout.Killfeed.Count, "only the round line: the unnumbered soldier's death has no line");
        }

        private void Tick(bool toggle = false)
        {
            _now += 0.05f;
            _readout.Tick(_now, _hud, _view, toggle, "DUSTBOWL", true, in Dustbowl);
        }

        private ushort IdOf(int key)
        {
            Assert.IsTrue(_readout.TryGetActorId(key, out ushort id), "the HUD has not met soldier " + key);
            return id;
        }

        private static PracticeDeath Kill(
            int victim, byte victimTeam, int killer, byte killerTeam, bool killerIsPlayer,
            byte weapon, bool headshot, ushort metres, int points)
            => new PracticeDeath(
                victim, victimTeam, victimIsPlayer: victim == Player, killer, killerTeam, killerIsPlayer,
                CauseOfDeath.Bullet, weapon, VehicleIds.NONE, DeathDetail.None, VehicleIds.NONE,
                headshot, metres, points);

        /// <summary>A readout with its own HUD and view, for the cases the shared one is not set up for.</summary>
        private sealed class PracticeReadoutHarness
        {
            public readonly PracticeMatchReadout Readout;
            public readonly RecordingHud Hud = new RecordingHud();
            public readonly FakeView View = new FakeView();

            public PracticeReadoutHarness(string playerName = "Nghaiz")
            {
                Readout = new PracticeMatchReadout { PlayerName = playerName };
            }

            public void Tick(float now, bool toggle = false)
                => Readout.Tick(now, Hud, View, toggle, "DUSTBOWL", true, in Dustbowl);
        }

        private sealed class FakeView : IPracticeMatchView
        {
            public readonly List<PracticeActor> Actors = new List<PracticeActor>();

            public PracticeScore Score { get; set; } = new PracticeScore(0, 0, 1, 1, TeamId.None);

            public int FillActors(PracticeActor[] actors)
            {
                int count = System.Math.Min(actors.Length, Actors.Count);
                for (int i = 0; i < count; i++) actors[i] = Actors[i];
                return count;
            }
        }

        /// <summary>The HUD as the readout drives it: what it was last told, in the order it was told.</summary>
        private sealed class RecordingHud : IMatchHud
        {
            public readonly List<KillfeedLine> Lines = new List<KillfeedLine>();
            public readonly Dictionary<int, (int Players, int Humans, int Kills, int Deaths)> Columns =
                new Dictionary<int, (int, int, int, int)>();
            private readonly List<(int Team, ScoreboardRow Row)> _rows = new List<(int, ScoreboardRow)>();

            public bool BoardVisible { get; private set; }
            public ScoreboardMatch Header { get; private set; }

            public ScoreboardRow[] RowsOf(int team) => _rows.Where(row => row.Team == team).Select(row => row.Row).ToArray();

            public void SetLocalTeam(int team) { }

            public void SetKillfeedLineCount(int count) => Lines.Clear();

            public void SetKillfeedLine(int index, in KillfeedLine line) => Lines.Insert(System.Math.Min(index, Lines.Count), line);

            public void ShowDeploy(string caption, int killerTeam) { }

            public void TickDeploy(float secondsUntilRespawn, bool canDeploy) { }

            public void HideDeploy() { }

            public void SetScoreboardVisible(bool visible) => BoardVisible = visible;

            public void SetScoreboardMatch(in ScoreboardMatch match) => Header = match;

            public void BeginScoreboardColumn(int team, int playerCount, int humanCount, int totalKills, int totalDeaths)
            {
                Columns[team] = (playerCount, humanCount, totalKills, totalDeaths);
                _rows.RemoveAll(row => row.Team == team);
            }

            public void AddScoreboardRow(int team, in ScoreboardRow row) => _rows.Add((team, row));

            public void EndScoreboard() { }

            public void BeginNameplates() { }

            public void SetNameplate(in Nameplate plate) { }

            public void EndNameplates() { }

            public bool ConsumeDeployPressed() => false;
        }
    }
}
