using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Turns the three combat events the server has been sending all along — death, weapon fire
    /// and hit confirm — into a corpse, a muzzle flash and a hitmarker. phase-V10 tasks 5 and 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>All three had zero production subscribers before this phase.</b> The server side of
    /// combat shipped and the client half was never built: <c>ServerActorDamageSink</c> already
    /// documents that <c>Actor.Die()</c> is deliberately not called because "each client runs its
    /// own ragdoll off <c>S_DEATH</c>" — and no client ran anything off it.
    /// <c>ServerEventWriter.WeaponFireAudibleRadius</c> has been filtering by earshot for an
    /// audience that did not exist.
    /// </para>
    /// <para>
    /// <b>Cosmetics only, and the boundary is mechanical.</b> Nothing here spawns a projectile
    /// or applies recoil: <c>Weapon.SpawnProjectile</c> sets <c>source = user</c> and would do
    /// real damage from a client, and <c>Actor.ApplyRecoil</c> chains to the LOCAL camera rig, so
    /// running it for a remote shooter kicks your own view. Both are outside
    /// <c>Weapon.PlayFireCosmetics</c> by construction (V10 D7) and a CI gate asserts that no
    /// file in this folder names either.
    /// </para>
    /// <para>
    /// <b>No handler throws.</b> <c>ClientMessageRouter.Route</c> counts malformed input rather
    /// than throwing, and an exception raised from a subscriber would propagate straight into
    /// the transport pump (V10 D22).
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class NetClientCombatPresenter : MonoBehaviour
    {
        [Tooltip("Draws the streak. Optional — without it a shot still flashes and reports.")]
        [SerializeField] private CosmeticTracerPool _tracers;

        [Tooltip("Resolves an actor id to the body drawing it. Found on this object if unset.")]
        [SerializeField] private RemoteActorRegistry _registry;

        private NetClientBootstrap _client;

        [Tooltip("Push the killfeed to the HUD. Off leaves the rows blank for a clean capture.")]
        [SerializeField] private bool _drawKillfeed = true;

        // What was last pushed to the HUD, so Update writes strings only when the visible feed
        // has actually changed. -1 is "nothing pushed yet", which is also what a HUD arriving
        // late resets this to -- see PushKillfeed.
        private int _pushedCount = -1;
        private long _pushedTotalKills = -1;
        private int _pushedNameRevision = -1;
        private int _pushedLocalActorId = -1;

        private readonly KillfeedModel _killfeed = new KillfeedModel();
        private readonly HitmarkerModel _hitmarker = new HitmarkerModel();
        private readonly PlayerNameTable _names = new PlayerNameTable();
        private readonly PlayerScoreTable _scores = new PlayerScoreTable();

        [Tooltip("The key that opens and closes the scoreboard. P18 3.3; a toggle since 2026-09-29.")]
        [SerializeField] private KeyCode _scoreboardKey = KeyCode.Tab;

        /// <summary>
        /// An extra way to hold the board open, for a run with no keyboard. P18 3.3.
        /// </summary>
        /// <remarks>
        /// <b><c>MinimapUi.HoldSource</c>'s pattern, and it exists for the identical reason</b>
        /// (ledger X-61): a scripted lane-B client presses no keys, so a board that only ever
        /// read <see cref="Input"/> could never appear in a captured artifact -- and P18's
        /// criteria 2, 3, 4 and 7 are all graded on one. A LEVEL rather than an edge, because
        /// that is what a held key is. The board is up while this says so OR the player has
        /// toggled it open, so a scripted run and a person at the keyboard cannot fight.
        /// </remarks>
        public static System.Func<bool> ScoreboardHoldSource;

        /// <summary>
        /// Whether the player has toggled the board open with Tab. Press to open, press again to
        /// close: the owner's call on 2026-09-29, replacing hold-to-show.
        /// </summary>
        private bool _scoreboardOpen;

        /// <summary>The match state and the flags the board's header states.</summary>
        private NetClientObjectivePresenter _objectives;

        /// <summary>Which actors are bots: the wire names humans only.</summary>
        private readonly BotRoster _bots = new BotRoster();

        /// <summary>The map the board is titled with, read once: a presenter lives in one map.</summary>
        private string _mapName = string.Empty;

        /// <summary>What was last pushed to the board, so an unchanged board writes nothing.</summary>
        private int _pushedScoreRevision = -1;
        private int _pushedScoreNameRevision = -1;
        private int _pushedScoreBotRevision = -1;
        private int _pushedScoreLocalActorId = -1;
        private bool _pushedScoreboardVisible;
        private ScoreboardHeaderKey _pushedHeader;
        private bool _pushedHeaderValid;

        /// <summary>The roster size the rows last counted, for the header's summary line.</summary>
        private int _rosterPlayers;
        private int _rosterHumans;

        /// <summary>Reused across pushes; a scoreboard that allocated a list per frame would be
        /// the IMGUI killfeed's mistake with more rows.</summary>
        private readonly System.Collections.Generic.List<ushort> _scoreboardOrder =
            new System.Collections.Generic.List<ushort>(ProtocolConstants.MAX_ACTORS);

        /// <summary>The last few kills, newest first. Drawn by the HUD; pruned here.</summary>
        public KillfeedModel Killfeed => _killfeed;

        /// <summary>
        /// Actor id to display name, rebuilt from every S_PLAYER_LIST.
        /// debt-closure phase 2 task 2a.
        /// </summary>
        public PlayerNameTable Names => _names;

        /// <summary>
        /// Per-actor kills and deaths, rebuilt from every S_PLAYER_SCORES. P18 task 3.1.
        /// </summary>
        public PlayerScoreTable Scores => _scores;

        /// <summary>The newest confirmed hit and how long it stays up.</summary>
        public HitmarkerModel Hitmarker => _hitmarker;

        private void Awake()
        {
            if (!NetClientPresenterGuard.IsPresentable)
            {
                enabled = false;
                return;
            }

            if (!NetClientPresenterGuard.TryResolveClient(nameof(NetClientCombatPresenter), out _client))
            {
                enabled = false;
                return;
            }

            if (_registry == null) _registry = GetComponent<RemoteActorRegistry>();
            if (_registry == null) _registry = FindObjectOfType<RemoteActorRegistry>();

            // Optional, like the registry: without it the board still lists every player and
            // only its header goes quiet.
            _objectives = GetComponent<NetClientObjectivePresenter>();
            if (_objectives == null) _objectives = FindObjectOfType<NetClientObjectivePresenter>();

            _mapName = gameObject.scene.name.ToUpperInvariant();

            if (_registry == null)
            {
                NetClientPresenterGuard.WarnOnce(
                    "combat-no-registry",
                    "[net] NetClientCombatPresenter found no RemoteActorRegistry, so it cannot "
                    + "resolve who died or who fired. Killfeed and hitmarker still work; corpses "
                    + "and muzzle flashes do not.");
            }
        }

        private void OnEnable()
        {
            if (_client == null) return;
            _client.Router.OnDeath += OnDeath;
            _client.Router.OnWeaponFire += OnWeaponFire;
            _client.Router.OnHitConfirm += OnHitConfirm;

            // debt-closure phase 2 task 2a. This subscription is what retires OnPlayerList from
            // ClientWiringGate's KnownUnwiredEvents: the exemption retires on SUBSCRIPTION, and
            // this presenter is the killfeed's owner, so the name table belongs beside it rather
            // than on a component of its own that the scene would then have to carry.
            _client.Router.OnPlayerList += _names.Apply;

            // P18 task 3.1. The subscriber ClientWiringGate demands for 0x51 -- the gate reads
            // the router's events by reflection, so OnPlayerScores is a blocker from the moment
            // it exists until this line does. It sits beside the name table because the two are
            // the two halves of one board and nothing else reads either.
            _client.Router.OnPlayerScores += _scores.Apply;

            // Feature 2: bots have no name on the wire, and the spawn is what says who they are.
            _client.Router.OnSpawnActor += _bots.Apply;
        }

        private void OnDisable()
        {
            if (_client == null) return;
            _client.Router.OnDeath -= OnDeath;
            _client.Router.OnWeaponFire -= OnWeaponFire;
            _client.Router.OnHitConfirm -= OnHitConfirm;
            _client.Router.OnPlayerList -= _names.Apply;
            _client.Router.OnPlayerScores -= _scores.Apply;
            _client.Router.OnSpawnActor -= _bots.Apply;
            _names.Reset();
            _scores.Reset();
            _bots.Reset();
            _scoreboardOpen = false;

            // A presenter going away leaves no rows behind. Without this, disconnecting mid-match
            // freezes the last five kills on screen with nothing left to prune them.
            NetClientBindings.MatchHud?.SetKillfeedLineCount(0);
            NetClientBindings.MatchHud?.SetScoreboardVisible(false);
            _pushedCount = -1;
            _pushedScoreRevision = -1;
            _pushedScoreNameRevision = -1;
            _pushedScoreBotRevision = -1;
            _pushedScoreboardVisible = false;
            _pushedHeaderValid = false;
        }

        private void Update()
        {
            // KillfeedModel deliberately has no clock of its own, so expiry is the caller's to
            // run. Once a frame, before anything reads it.
            _killfeed.Prune(Time.time);

            PushKillfeed();
            PushScoreboard();
        }

        /// <summary>
        /// Rewrites the HUD's killfeed when the visible feed has changed. P17 3.3.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This replaces an IMGUI drawer, and the replacement was specified by the thing it
        /// replaces.</b> That drawer's remark said "delete it when a HUD element reads
        /// <see cref="Killfeed"/> and <see cref="Names"/> instead", and also that it allocated
        /// its strings every frame — "the honest cost of the stopgap ... the replacement does
        /// not have this problem". So this writes only on a change, and the change key is four
        /// cheap integers rather than a comparison of the rendered text.
        /// </para>
        /// <para>
        /// <b>Why the name revision is in the key.</b> S_PLAYER_LIST arrives on join and on
        /// change, routinely AFTER the first kills of a match. Without it a feed whose lines
        /// read "actor 7" would keep reading "actor 7" for the rest of those lines' lives, and
        /// the one message that could have fixed them would have been ignored because the count
        /// did not move.
        /// </para>
        /// <para>
        /// <b>A team is NOT in the key, and that is a stated limit.</b> Teams are resolved when
        /// a line is written, from the decoded snapshot; an actor whose team changed after its
        /// line was drawn keeps the old colour until the feed next changes. A team changes at
        /// most once a life and a line lives five seconds, so the window is narrow — and closing
        /// it would mean re-resolving 64 actors every frame to detect a change that a kill will
        /// push out of the feed anyway.
        /// </para>
        /// <para>
        /// <b><c>_drawKillfeed</c> off is a count of zero, not a skipped push.</b> Returning
        /// early would leave whatever was on screen when it was switched off, which is worse
        /// than either state: a lane-B run turning it off wants the rows blank.
        /// </para>
        /// </remarks>
        private void PushKillfeed()
        {
            IMatchHud hud = NetClientBindings.MatchHud;

            if (hud == null)
            {
                // The HUD prefab is instantiated by GameManager.StartGame, which can land after
                // the first kills. Forgetting what was pushed is what makes the feed appear in
                // full the frame a HUD registers, rather than staying empty until the next kill.
                _pushedCount = -1;
                return;
            }

            int count = _drawKillfeed ? _killfeed.Count : 0;

            // The local actor is in the key because a line that names you is drawn differently,
            // and the id can be assigned after the first kills of a match have already landed.
            bool hasLocal = NetClientPresenterGuard.TryResolveLocalActorId(out ushort localActorId);
            int localKey = hasLocal ? localActorId : -1;

            if (count == _pushedCount
                && _killfeed.TotalKills == _pushedTotalKills
                && _names.Revision == _pushedNameRevision
                && localKey == _pushedLocalActorId)
            {
                return;
            }

            _pushedCount = count;
            _pushedTotalKills = _killfeed.TotalKills;
            _pushedNameRevision = _names.Revision;
            _pushedLocalActorId = localKey;

            hud.SetKillfeedLineCount(count);

            for (int i = 0; i < count; i++)
            {
                KillfeedEntry entry = _killfeed[i];
                KillfeedWording wording = KillfeedWording.For(in entry);

                // A death nobody scored has no killer to name or colour: the sentence says what
                // happened to the victim instead of "The world" killing them (feature 2).
                bool scored = !wording.IsSentence;

                var line = new KillfeedLine(
                    entry.Sequence,
                    scored ? NameFor(entry.KillerActorId) : string.Empty,
                    scored ? TeamOf(entry.KillerActorId) : TeamId.None,
                    NameFor(entry.VictimActorId),
                    TeamOf(entry.VictimActorId),
                    wording.Label,
                    wording.Sentence,
                    entry.Headshot,
                    localIsKiller: scored && hasLocal && entry.KillerActorId == localActorId,
                    localIsVictim: hasLocal && entry.VictimActorId == localActorId);

                hud.SetKillfeedLine(i, in line);
            }
        }

        /// <summary>
        /// Toggles the Tab board, and rewrites it when something on it has moved. P18 3.3,
        /// rebuilt for feature 2 (2026-09-29).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Tab toggles.</b> Press to open, press again to close (the owner's call), and a death
        /// closes it so it never sits over the deploy screen. <see cref="ScoreboardHoldSource"/>
        /// still forces it open for a scripted run.
        /// </para>
        /// <para>
        /// <b>The sides come from <c>PlayerScoreTable</c>, not from <see cref="TeamOf"/>.</b>
        /// That resolver reads the decoded snapshot, which <c>InterestManager</c> sheds under a
        /// per-snapshot ceiling — fine for a killfeed, where a miss is one neutral-coloured name,
        /// and useless for a scoreboard, where it would put most of a 41-bot roster on no side at
        /// all. <c>S_PLAYER_SCORES</c> carries the team for exactly this reason.
        /// </para>
        /// <para>
        /// <b>Rows are keyed on the ACTOR ID, and the name is looked up afterwards.</b> The two
        /// tables fill from two messages that arrive independently and scores routinely land
        /// first; a row keyed on the name table would make a player appear only once their name
        /// did. Criterion 5 is that case.
        /// </para>
        /// <para>
        /// <b>Sorted by kills, then by fewer deaths, then by id.</b> The last term is what makes
        /// the order STABLE — without it two players on the same K/D swap places whenever a
        /// broadcast arrives, and a board that reshuffles under a reader is unreadable. The id is
        /// also what makes the two clients' boards agree, which criterion 4 grades.
        /// </para>
        /// <para>
        /// <b>Written only when something moved.</b> The rows are keyed on the three tables'
        /// revisions and the local actor; the header on its own handful of integers, because the
        /// clock moves every second while nobody scores. An open board must not rebuild 42 rows
        /// of string every frame.
        /// </para>
        /// <para>
        /// <b>Synchronised with the killfeed by construction.</b> The kills on this board are the
        /// server's tally of the same credited killer <c>S_DEATH</c> names, and every name here
        /// comes from <see cref="NameFor"/>, which the killfeed also uses.
        /// </para>
        /// </remarks>
        private void PushScoreboard()
        {
            if (Input.GetKeyDown(_scoreboardKey) && !LocalTextEntry.OwnsKeyboard)
                _scoreboardOpen = !_scoreboardOpen;

            IMatchHud hud = NetClientBindings.MatchHud;

            if (hud == null)
            {
                // Forget what was pushed, for PushKillfeed's reason: the HUD prefab is
                // instantiated by GameManager.StartGame and can register after the first
                // broadcast, and a board that remembered would then stay empty until the next.
                _pushedScoreRevision = -1;
                _pushedScoreboardVisible = false;
                _pushedHeaderValid = false;
                return;
            }

            bool visible = _scoreboardOpen
                           || (ScoreboardHoldSource != null && ScoreboardHoldSource());

            if (visible != _pushedScoreboardVisible)
            {
                _pushedScoreboardVisible = visible;
                hud.SetScoreboardVisible(visible);

                // The HUD clears a board on the way down, so an opened one is written in full.
                _pushedScoreRevision = -1;
                _pushedHeaderValid = false;
            }

            if (!visible) return;

            // False before the server has assigned one, and the out value is then the unassigned
            // sentinel -- which is a real actor id nobody holds, so it must not be compared
            // against. No local row is highlighted until there is a local actor to highlight.
            bool hasLocal = NetClientPresenterGuard.TryResolveLocalActorId(out ushort localActorId);
            int localKey = hasLocal ? localActorId : -1;

            if (_scores.Revision != _pushedScoreRevision
                || _names.Revision != _pushedScoreNameRevision
                || _bots.Revision != _pushedScoreBotRevision
                || localKey != _pushedScoreLocalActorId)
            {
                _pushedScoreRevision = _scores.Revision;
                _pushedScoreNameRevision = _names.Revision;
                _pushedScoreBotRevision = _bots.Revision;
                _pushedScoreLocalActorId = localKey;

                PushScoreboardColumns(hud, hasLocal, localActorId);
            }

            PushScoreboardHeader(hud);
        }

        private void PushScoreboardColumns(IMatchHud hud, bool hasLocal, ushort localActorId)
        {
            _rosterPlayers = 0;
            _rosterHumans = 0;

            for (int team = TeamId.Team0; team <= TeamId.Team1; team++)
            {
                _scoreboardOrder.Clear();

                int humans = 0;
                int totalKills = 0;
                int totalDeaths = 0;

                for (ushort actorId = 0; actorId < ProtocolConstants.MAX_ACTORS; actorId++)
                {
                    if (!_scores.Has(actorId)) continue;
                    if (_scores.TeamOf(actorId) != team) continue;

                    _scoreboardOrder.Add(actorId);
                    totalKills += _scores.KillsOf(actorId);
                    totalDeaths += _scores.DeathsOf(actorId);

                    // A human is who S_PLAYER_LIST names; the spawn flag says who is a bot, and
                    // an actor neither has described yet is counted as neither.
                    if (_names.NameOf(actorId) != null) humans++;
                }

                _scoreboardOrder.Sort(CompareScoreRows);

                _rosterPlayers += _scoreboardOrder.Count;
                _rosterHumans += humans;

                hud.BeginScoreboardColumn(team, _scoreboardOrder.Count, humans, totalKills, totalDeaths);

                for (int i = 0; i < _scoreboardOrder.Count; i++)
                {
                    ushort actorId = _scoreboardOrder[i];
                    int kills = _scores.KillsOf(actorId);
                    int deaths = _scores.DeathsOf(actorId);

                    var row = new ScoreboardRow(
                        actorId,
                        NameFor(actorId),
                        kills,
                        deaths,
                        ScoreboardWording.Ratio(kills, deaths),
                        isBot: _names.NameOf(actorId) == null && _bots.IsBot(actorId),
                        isLocal: hasLocal && actorId == localActorId);

                    hud.AddScoreboardRow(team, in row);
                }
            }

            hud.EndScoreboard();
        }

        /// <summary>
        /// States the match at the top of the board, when any of it has changed.
        /// </summary>
        /// <remarks>
        /// The numbers come from the same latched <c>S_MATCH_STATE</c> and the same capture-point
        /// count the objective HUD shows (<see cref="NetClientObjectivePresenter.Match"/>), so the
        /// board and the score bar cannot tell two stories.
        /// </remarks>
        private void PushScoreboardHeader(IMatchHud hud)
        {
            MatchStateModel match = _objectives != null ? _objectives.Match : null;
            bool hasState = match != null && match.HasState;
            MatchStateMessage state = hasState ? match.Current : default;

            float now = Time.time;
            int seconds = hasState && match.HasTimer ? Mathf.CeilToInt(match.SecondsRemaining(now)) : -1;
            if (seconds < -1) seconds = -1;

            var key = new ScoreboardHeaderKey(
                hasState ? (int)state.Phase : -1,
                state.Score0, state.Score1, seconds,
                _objectives != null ? _objectives.CapturePointsHeldBy(TeamId.Team0) : 0,
                _objectives != null ? _objectives.CapturePointsHeldBy(TeamId.Team1) : 0,
                state.VictoryPoints,
                hasState ? match.WinningTeam : TeamId.None,
                _rosterPlayers, _rosterHumans);

            if (_pushedHeaderValid && key.Equals(_pushedHeader)) return;

            _pushedHeader = key;
            _pushedHeaderValid = true;

            MatchPhase phase = hasState ? state.Phase : MatchPhase.WaitingForPlayers;

            var header = new ScoreboardMatch(
                _mapName,
                "CONQUEST  ·  " + ScoreboardWording.PlayersLine(_rosterPlayers, _rosterHumans),
                state.Score0,
                state.Score1,
                ScoreboardWording.Lead(state.Score0, state.Score1, state.VictoryPoints),
                hasState
                    ? ScoreboardWording.LeadLine(
                        phase, state.Score0, state.Score1, state.VictoryPoints, key.WinningTeam)
                    : string.Empty,
                ScoreboardWording.Clock(seconds),
                hasState ? ScoreboardWording.PhaseLabel(phase, seconds >= 0) : string.Empty,
                clockUrgent: phase == MatchPhase.Playing && seconds >= 0 && seconds <= 60,
                key.Flags0,
                key.Flags1,
                key.WinningTeam,
                hasState ? ScoreboardWording.Rules(state.VictoryPoints) : string.Empty);

            hud.SetScoreboardMatch(in header);
        }

        /// <summary>
        /// Everything the board's header is drawn from, compared as integers so an unchanged
        /// header costs nothing and a clock tick costs one push.
        /// </summary>
        private readonly struct ScoreboardHeaderKey : System.IEquatable<ScoreboardHeaderKey>
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

            public int Phase { get; }
            public int Score0 { get; }
            public int Score1 { get; }
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

        private int CompareScoreRows(ushort left, ushort right)
        {
            int byKills = _scores.KillsOf(right).CompareTo(_scores.KillsOf(left));
            if (byKills != 0) return byKills;

            int byDeaths = _scores.DeathsOf(left).CompareTo(_scores.DeathsOf(right));
            if (byDeaths != 0) return byDeaths;

            return left.CompareTo(right);
        }

        /// <summary>
        /// The team the snapshot gives this actor, or <c>TeamId.None</c> when it does not carry
        /// one.
        /// </summary>
        /// <remarks>
        /// A miss is a NORMAL outcome and not a defect — a kill outside this client's interest
        /// radius names two actors this client never spawned. The resolver is
        /// <c>NetClientPresenterGuard</c>'s, the same one the local readout and the minimap read
        /// through, so there is one answer to "what team is that actor on" rather than a second
        /// one growing inside the killfeed.
        /// </remarks>
        private static int TeamOf(ushort actorId)
            => NetClientPresenterGuard.TryResolveActorTeam(actorId, out byte team)
                ? team
                : TeamId.None;

        /// <summary>
        /// The name S_PLAYER_LIST gave this actor, or the id when no broadcast named it.
        /// </summary>
        /// <remarks>
        /// The fallback is HERE and not in <c>PlayerNameTable</c>, which returns null: only the
        /// caller knows what an unnamed actor should read as, and manufacturing it in the table
        /// would make a genuinely missing name indistinguishable from a real one. "actor 7" is
        /// exactly what this feed rendered for every line before this phase.
        /// </remarks>
        private string NameFor(ushort actorId) => ActorNames.Display(actorId, _names, _bots);

        /// <summary>
        /// What an actor is called on screen: a player's name, "Bot 23", or the id while nobody
        /// has said. The one answer the killfeed, the scoreboard and the deploy screen share.
        /// </summary>
        public string DisplayNameOf(ushort actorId) => NameFor(actorId);

        /// <summary>
        /// One death message, two consumers: the feed takes the line, the ragdoll takes the
        /// impulse (V10 D19).
        /// </summary>
        /// <remarks>
        /// <c>KillfeedEntry.From</c> drops <c>ForceX/Y/Z</c> — correctly, a text line has no use
        /// for a vector — so the corpse cannot be driven from the feed. <see cref="DeathImpulse"/>
        /// carries it instead. Neither shipped type changes.
        /// </remarks>
        private void OnDeath(DeathMessage message)
        {
            _killfeed.Push(in message, Time.time);

            DeathImpulse impulse = DeathImpulse.From(in message);
            Vector3 force = new Vector3(impulse.Force.X, impulse.Force.Y, impulse.Force.Z);

            // The local player is deliberately absent from the remote registry — it is
            // predicted, not interpolated — so its own death must be resolved first or it
            // looks like an actor this client never heard of.
            var hitbox = (HitboxType)message.HitboxHit;

            if (NetClientPresenterGuard.IsLocalActor(message.VictimActorId))
            {
                // The board would cover the deploy screen, and the player just died with it open
                // rather than asking to read it. Tab brings it back.
                _scoreboardOpen = false;

                KnockOverLocalActor(force, hitbox);
                return;
            }

            if (_registry == null) return;

            // A miss is a NORMAL outcome: the victim died outside this client's interest
            // radius and was never spawned here. The killfeed line is still correct, there is
            // simply no body to fell. This must not log.
            if (!_registry.TryFindView(message.VictimActorId, out RemoteActorView view)) return;

            FellBody(view, force, hitbox);
        }

        /// <summary>
        /// Drops a remote body using the ready-made public pair on <c>Actor</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>Actor.KnockOver</c> enables the ragdoll via <c>FallOver()</c> and applies the
        /// impulse in one call, behind its own <c>if (!ragdoll.IsRagdoll())</c> re-entrancy
        /// guard — so a snapshot confirming the same death cannot throw the corpse twice.
        /// </para>
        /// <para>
        /// <b><c>Actor.Die</c> is not called and not widened.</b> It is private with one caller,
        /// and it calls <c>ScoreUi.AddScore</c> — which on a client would double-count against
        /// the server's authoritative <c>S_MATCH_STATE</c>. <c>ServerActorDamageSink</c> already
        /// documents that the netcode deliberately does not call it.
        /// </para>
        /// <para>
        /// <b>The impulse goes to the bone that was hit.</b> debt-closure phase 2 task 2d closed
        /// ledger C-8: <c>ActiveRaggy.RigidbodyForBone</c> resolves through the animator's
        /// humanoid bone map, so it does not depend on rig NAMING the way V10 assumed when it
        /// left this open — a humanoid avatar has already normalised that. A bone the rig does
        /// not simulate falls back to the main body, which is what every corpse used to get.
        /// </para>
        /// </remarks>
        private static void FellBody(RemoteActorView view, Vector3 force, HitboxType hitbox)
        {
            if (view == null) return;

            if (view.TryFellBody(force, BoneFor(hitbox))) return;

            // Degraded, and loudly. A silent no-op here is indistinguishable from the bug this
            // whole phase exists to close.
            NetClientPresenterGuard.WarnOnce(
                "death-no-rig",
                "[net] a remote actor died but its prefab carries no Actor with a ragdoll rig, "
                + "so the corpse cannot be felled. Hiding its renderers until the respawn snapshot. "
                + "Client-track item E1.");
            view.HideForDeathFallback();
        }

        /// <summary>
        /// Which bone an impulse lands on, from the hitbox S_DEATH carries.
        /// </summary>
        /// <remarks>
        /// <b>Three hitboxes, not a skeleton.</b> The wire carries Body/Head/Limb and nothing
        /// finer, so this maps to three bones and stops. <c>Limb</c> resolves to the right upper
        /// leg rather than to the limb that was actually hit — the byte does not say which — and
        /// that is a visible improvement over the pelvis without pretending to information the
        /// protocol does not carry. Widening it means widening <c>HitboxType</c>, which is a
        /// PROTOCOL_VERSION decision.
        /// </remarks>
        private static HumanBodyBones BoneFor(HitboxType hitbox)
        {
            switch (hitbox)
            {
                case HitboxType.Head: return HumanBodyBones.Head;
                case HitboxType.Limb: return HumanBodyBones.RightUpperLeg;
                default: return HumanBodyBones.Hips;
            }
        }

        private static void KnockOverLocalActor(Vector3 force, HitboxType hitbox)
        {
            // ClientCombatState owns the local player's death STATE — respawn timer, ammo,
            // health — and V10 does not duplicate it. NetClientLocalCombatDriver declares itself
            // the one production owner and holds one, at its own :50. Until 2026-08-30 this
            // remark still called that a recorded gap — the last surviving copy of a sentence
            // that stopped being true when the driver landed, which is precisely the decay
            // ledger X-29 was filed against. What is this presenter's is that the body falls
            // over: at the client role Actor.Damage never reaches Die() (ownsHealth is false),
            // so without this the local player takes hits, staggers, and stands there dead.
            ILocalPlayerRig local = NetClientBindings.LocalPlayer;
            if (!local.HasFellableBody) return;

            local.FellBody(force, BoneFor(hitbox));
        }

        /// <summary>
        /// Somebody else's shot: a flash at the right height, one report, and a streak.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Stateless, by decision (V10 D9).</b> Weapon fire rides the cosmetic channel —
        /// unreliable-sequenced, documented safe to drop — so nothing here may read or advance
        /// <c>currentMuzzle</c>. Driving that counter from received events would desynchronise
        /// permanently on the first dropped packet, and would not reproduce on a clean network.
        /// </para>
        /// <para>
        /// <b>No distance test.</b> Earshot filtering is already done server-side by
        /// <c>ServerEventWriter.WeaponFireAudibleRadius</c>. A second filter here would be a
        /// second thing to keep in agreement with the first.
        /// </para>
        /// </remarks>
        private void OnWeaponFire(WeaponFireMessage message)
        {
            // The local player's own shot was already drawn by Weapon.Shoot when the trigger was
            // pulled. Playing the echo would double the flash and the report.
            if (NetClientPresenterGuard.IsLocalActor(message.ShooterActorId)) return;
            if (_registry == null) return;
            if (!_registry.TryFindView(message.ShooterActorId, out RemoteActorView view)) return;

            // A corpse fires nothing. A fire event that crossed a death in flight would
            // otherwise flash a muzzle on a ragdoll.
            if (!view.CanPlayCosmetics) return;

            ShotEvent shot = ShotEvent.From(in message);
            Vector3 direction = new Vector3(shot.Direction.X, shot.Direction.Y, shot.Direction.Z);

            // One report per message, never the Fire() loop: each S_WEAPON_FIRE is one shot, and
            // Shoot alone is SILENT on an automatic weapon (V10 D8). The liveness check the
            // weapon needs lives with the view, which is the only thing holding one.
            view.PlayActiveWeaponFireCosmetics();

            if (_tracers != null) _tracers.Fire(view.MuzzlePosition, direction);
        }

        /// <summary>
        /// A hit this client landed. Shooter-only, and that is a security property.
        /// </summary>
        /// <remarks>
        /// The server sends <c>S_HIT_CONFIRM</c> to the shooter alone. Rendering it for anybody
        /// else would tell a player that someone, somewhere, hit something — a server-served
        /// wallhack. Recorded because "why does only one client get this event" is exactly the
        /// question a future reader answers by broadcasting it (V10 D18).
        /// </remarks>
        private void OnHitConfirm(HitConfirmMessage message)
        {
            uint tick = _client != null ? _client.Router.Decoder.Current.ServerTick : 0u;

            _hitmarker.Push(in message, tick, Time.time);

            // The newest hit wins, including a quieter one — that is HitmarkerModel's documented
            // semantics, and the severity travels as an int so Assembly-CSharp takes no
            // dependency on the replication library for a cosmetic. Silent when this build
            // registered no HUD, which is what a headless client and an EditMode test are.
            NetClientBindings.ShowHit((int)_hitmarker.Current.Severity);
        }
    }
}
