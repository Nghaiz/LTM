#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// What this player has earned, as the game knows it (achievements v2): the master's answer for
    /// the signed-in account and the practice achievements this machine saw. Feeds the achievement
    /// page, the unlock banners and the end-of-round summary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Practice achievements are kept here first and claimed after.</b> An offline match has no
    /// master to tell, so <see cref="PracticeFeats"/>' ids are stored in PlayerPrefs, shown at once,
    /// and claimed from the master the next time an account is signed in, with the practice numbers
    /// behind their progress. The master records only what the account lacks.
    /// </para>
    /// <para>
    /// <b>One queue, and nothing in it is lost</b> (section 5.2): every unlock is one banner, shown
    /// in turn; the queue is written to PlayerPrefs until a banner has played, so a banner a scene
    /// change or a quit cut short plays at the next start. A claimed practice achievement's echo
    /// from the master is not shown twice.
    /// </para>
    /// <para>
    /// <b>The round summary.</b> When a round starts the career is snapshotted; when it ends the
    /// master is asked again and <see cref="AchievementRoundSummary"/> says what changed. Practice
    /// rounds do the same with this machine's practice numbers.
    /// </para>
    /// <para>Driven by <see cref="Tick"/> from the banner's host, every frame, on the main thread.</para>
    /// </remarks>
    public static class AchievementLedger
    {
        /// <summary>The practice achievements earned on this machine, comma-separated.</summary>
        public const string EarnedKey = "ironfront.achievements.earned";

        /// <summary>Banners not yet played, comma-separated ids, oldest first.</summary>
        public const string QueueKey = "ironfront.achievements.toast-queue";

        /// <summary>Seconds before a claim that failed is tried again.</summary>
        private const float RetrySeconds = 30f;

        /// <summary>Seconds after a round ends before the master is asked for its final numbers.</summary>
        private const float SummaryDelaySeconds = 4f;

        private static readonly HashSet<string> Local = new HashSet<string>(StringComparer.Ordinal);
        private static readonly ConcurrentQueue<string> Pushed = new ConcurrentQueue<string>();
        private static readonly List<string> Queue = new List<string>();
        private static readonly HashSet<string> Toasted = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<string> RoundUnlocks = new List<string>();

        private static MasterSession? _session;
        private static bool _loaded;
        private static bool _busy;
        private static int _claimedFor;
        private static float _nextClaimAt;
        private static int _burstShown;

        /// <summary>The banner on screen: out of the queue, still saved until it has played.</summary>
        private static string? _inFlight;

        private static Dictionary<string, long>? _roundBefore;
        private static HashSet<string>? _heldBefore;
        private static bool _roundRunning;
        private static bool _roundIsPractice;
        private static float _summaryDueAt = -1f;

        /// <summary>The master's last answer for <see cref="StatePlayerId"/>, or null.</summary>
        public static AchievementState? State { get; private set; }

        /// <summary>The account <see cref="State"/> belongs to.</summary>
        public static int StatePlayerId { get; private set; }

        /// <summary>The last round's summary, or null before one has ended.</summary>
        public static List<RoundSummaryLine>? RoundSummary { get; private set; }

        /// <summary>Something earned or loaded; pages redraw. Main thread.</summary>
        public static event Action? Changed;

        /// <summary>A round's summary is ready (<see cref="RoundSummary"/>). Main thread.</summary>
        public static event Action? RoundSummaryReady;

        /// <summary>The practice achievements earned on this machine.</summary>
        public static ICollection<string> LocalEarned
        {
            get
            {
                Load();
                return Local;
            }
        }

        /// <summary>The live master session, or null on a server or before the menu has made one.</summary>
        public static MasterSession? Session => ClientFlowBootstrap.Current != null ? ClientFlowBootstrap.Current.Session : null;

        /// <summary>Whether an account is signed in to ask the master about.</summary>
        public static bool SignedIn => Session != null && Session.IsLoggedIn;

        /// <summary>The master's answer when it belongs to the account signed in now; null otherwise.</summary>
        public static AchievementState? CurrentState
            => State != null && Session != null && Session.IsLoggedIn && Session.PlayerId == StatePlayerId ? State : null;

        /// <summary>Banners waiting, the one on screen excluded.</summary>
        public static int Waiting => Queue.Count;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            Local.Clear();
            while (Pushed.TryDequeue(out _)) { }
            Queue.Clear();
            Toasted.Clear();
            RoundUnlocks.Clear();
            _session = null;
            _loaded = false;
            _busy = false;
            _claimedFor = 0;
            _nextClaimAt = 0f;
            _burstShown = 0;
            _inFlight = null;
            _roundBefore = null;
            _heldBefore = null;
            _roundRunning = false;
            _roundIsPractice = false;
            _summaryDueAt = -1f;
            State = null;
            StatePlayerId = 0;
            RoundSummary = null;
            Changed = null;
            RoundSummaryReady = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (Application.isBatchMode) return;
            PracticeFeats.Earned += OnPracticeEarned;
            PracticeFeats.RoundBegan += () => BeginRound(practice: true);
            PracticeFeats.RoundOver += () => EndRound();
        }

        /// <summary>Follows the session, takes the master's pushes, claims practice achievements, ends rounds. Every frame.</summary>
        public static void Tick()
        {
            Load();
            MasterSession? session = Session;
            if (!ReferenceEquals(session, _session))
            {
                if (_session != null) _session.OnAchievementsUnlocked -= OnPushed;
                _session = session;
                if (_session != null) _session.OnAchievementsUnlocked += OnPushed;
            }

            while (Pushed.TryDequeue(out string id)) Note(id, fromMaster: true);

            if (session != null && session.IsLoggedIn && !_busy && _claimedFor != session.PlayerId
                && Time.realtimeSinceStartup >= _nextClaimAt)
                _ = ClaimAsync(session);

            if (_summaryDueAt >= 0f && Time.realtimeSinceStartup >= _summaryDueAt)
            {
                _summaryDueAt = -1f;
                _ = SummariseAsync();
            }
        }

        /// <summary>
        /// The next banner, if one is waiting, with its place in the run of banners shown back to
        /// back: "2 of 3". Call <see cref="Played"/> once it has been shown in full.
        /// </summary>
        public static bool TryNextToast(out Achievement achievement, out int place, out int of)
        {
            Load();
            while (Queue.Count > 0)
            {
                string id = Queue[0];
                Queue.RemoveAt(0);
                Achievement? next = AchievementCatalog.Find(id);
                if (next == null)
                {
                    SaveQueue();
                    continue;
                }
                _inFlight = id;
                _burstShown++;
                achievement = next;
                place = _burstShown;
                of = _burstShown + Queue.Count;
                return true;
            }
            _burstShown = 0;
            achievement = null!;
            place = 0;
            of = 0;
            return false;
        }

        /// <summary>The banner for <paramref name="id"/> played in full: it leaves the saved queue.</summary>
        public static void Played(string id)
        {
            if (_inFlight == id) _inFlight = null;
            SaveQueue();
        }

        /// <summary>
        /// The online round moved to <paramref name="phase"/> (the client's view of S_MATCH_STATE):
        /// Playing snapshots the career; Ended asks the master for the round's outcome.
        /// </summary>
        public static void NoteRoundPhase(MatchPhase phase)
        {
            if (phase == MatchPhase.Playing) BeginRound(practice: false);
            else if (phase == MatchPhase.Ended) EndRound();
        }

        /// <summary>Asks the master again, for a page being opened. False with the reason in <paramref name="error"/>.</summary>
        public static async Task<(bool Ok, string Error)> RefreshAsync()
        {
            MasterSession? session = Session;
            if (session == null || !session.IsLoggedIn) return (false, "Sign in to multiplayer to see how rare each one is.");

            int player = session.PlayerId;
            AchievementState? state = await session.GetAchievementsAsync();
            if (state == null) return (false, session.CareerError);
            Keep(state, player);
            return (true, string.Empty);
        }

        private static void BeginRound(bool practice)
        {
            _roundRunning = true;
            _roundIsPractice = practice;
            _summaryDueAt = -1f;
            RoundUnlocks.Clear();
            _roundBefore = practice ? PracticeFeats.Progress() : Copy(CurrentState?.Career);
            _heldBefore = Held();
        }

        private static void EndRound()
        {
            if (!_roundRunning) return;
            _roundRunning = false;
            if (_roundIsPractice)
            {
                Summarise(PracticeFeats.Progress());
                return;
            }
            _summaryDueAt = Time.realtimeSinceStartup + SummaryDelaySeconds;
        }

        private static async Task SummariseAsync()
        {
            if (SignedIn) await RefreshAsync();
            Summarise(CurrentState?.Career);
        }

        private static void Summarise(IReadOnlyDictionary<string, long>? after)
        {
            RoundSummary = AchievementRoundSummary.Build(_roundBefore, after, _heldBefore ?? Held(), RoundUnlocks);
            RoundSummaryReady?.Invoke();
        }

        private static HashSet<string> Held()
        {
            var held = new HashSet<string>(LocalEarned, StringComparer.Ordinal);
            AchievementState? state = CurrentState;
            if (state?.Unlocked != null)
                foreach (AchievementUnlock unlock in state.Unlocked)
                    if (unlock?.Id != null) held.Add(unlock.Id);
            return held;
        }

        private static async Task ClaimAsync(MasterSession session)
        {
            _busy = true;
            int player = session.PlayerId;
            try
            {
                AchievementState? state = await session.ClaimAchievementsAsync(new List<string>(LocalEarned), PracticeFeats.Progress());
                if (state != null && session.IsLoggedIn && session.PlayerId == player)
                {
                    _claimedFor = player;
                    Keep(state, player);
                }
                else
                {
                    _nextClaimAt = Time.realtimeSinceStartup + RetrySeconds;
                    if (session.CareerError.Length > 0) Debug.LogWarning("[achievements] claim failed: " + session.CareerError);
                }
            }
            finally
            {
                _busy = false;
            }
        }

        private static void Keep(AchievementState state, int player)
        {
            State = state;
            StatePlayerId = player;
            Changed?.Invoke();
        }

        private static void OnPushed(string[] ids)
        {
            foreach (string id in ids)
                if (!string.IsNullOrEmpty(id)) Pushed.Enqueue(id);
        }

        private static void OnPracticeEarned(string id)
        {
            if (AchievementCatalog.Find(id) == null) return;
            Load();
            if (!Local.Add(id)) return;

            PlayerPrefs.SetString(EarnedKey, string.Join(",", Local));
            PlayerPrefs.Save();
            Debug.Log("[achievements] earned in practice: " + id);

            _claimedFor = 0;
            _nextClaimAt = 0f;
            Note(id, fromMaster: false);
        }

        private static void Note(string id, bool fromMaster)
        {
            Achievement? achievement = AchievementCatalog.Find(id);
            if (achievement == null) return;

            if (fromMaster && State != null && _session != null && _session.PlayerId == StatePlayerId)
                State.Unlocked = Append(State.Unlocked, id);

            Changed?.Invoke();

            // The echo of a claim: shown when it was earned, in this run or an earlier one.
            if (fromMaster && LocalEarned.Contains(id)) return;
            if (!Toasted.Add(id)) return;

            if (_roundRunning || _summaryDueAt >= 0f) RoundUnlocks.Add(id);
            Queue.Add(id);
            SaveQueue();
        }

        private static AchievementUnlock[] Append(AchievementUnlock[]? unlocked, string id)
        {
            unlocked ??= Array.Empty<AchievementUnlock>();
            foreach (AchievementUnlock unlock in unlocked)
                if (unlock.Id == id) return unlocked;

            var grown = new AchievementUnlock[unlocked.Length + 1];
            Array.Copy(unlocked, grown, unlocked.Length);
            grown[unlocked.Length] = new AchievementUnlock { Id = id, At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            return grown;
        }

        private static Dictionary<string, long>? Copy(Dictionary<string, long>? career)
            => career == null ? null : new Dictionary<string, long>(career, StringComparer.Ordinal);

        private static void SaveQueue()
        {
            string queued = string.Join(",", Queue);
            PlayerPrefs.SetString(QueueKey, _inFlight == null ? queued : queued.Length == 0 ? _inFlight : _inFlight + "," + queued);
            PlayerPrefs.Save();
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            // Ids of the retired list are dropped here and the key rewritten without them.
            string stored = PlayerPrefs.GetString(EarnedKey, string.Empty);
            foreach (string id in stored.Split(','))
                if (id.Length > 0 && AchievementCatalog.Find(id) != null) Local.Add(id);
            string kept = string.Join(",", Local);
            if (kept != stored) PlayerPrefs.SetString(EarnedKey, kept);

            // Banners a quit or a crash cut short play now.
            foreach (string id in PlayerPrefs.GetString(QueueKey, string.Empty).Split(','))
                if (id.Length > 0 && AchievementCatalog.Find(id) != null && Toasted.Add(id)) Queue.Add(id);
        }
    }
}
