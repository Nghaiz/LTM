#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol.Achievements;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// What this player has earned, as the game knows it (owner's list of 2026-10-09, item 4):
    /// the master's answer for the signed-in account, and the practice achievements this machine
    /// saw. Feeds the achievement page and the unlock toast.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Practice achievements are kept here first and claimed after.</b> An offline match has
    /// no master to tell, so <see cref="PracticeFeats"/>' ids are stored in PlayerPrefs, toasted at
    /// once, and claimed from the master the next time an account is signed in. The whole set is
    /// claimed for each account that signs in on this machine: the master records only what the
    /// account lacks, so a second claim costs one answer and loses nothing.
    /// </para>
    /// <para>
    /// <b>One toast per achievement per machine.</b> The master pushes what it records -- after a
    /// round, and after a claim. A claimed practice achievement was toasted when it was earned,
    /// so its echo is not toasted again; nor is anything already toasted in this run.
    /// </para>
    /// <para>
    /// Driven by <see cref="Tick"/> from the toast's host, every frame, on the main thread. The
    /// master's push arrives on the link's thread and is queued until then.
    /// </para>
    /// </remarks>
    public static class AchievementLedger
    {
        /// <summary>The practice achievements earned on this machine, comma-separated.</summary>
        public const string EarnedKey = "ironfront.achievements.earned";

        /// <summary>Seconds before a claim that failed is tried again.</summary>
        private const float RetrySeconds = 30f;

        private static readonly HashSet<string> Local = new HashSet<string>(StringComparer.Ordinal);
        private static readonly ConcurrentQueue<string> Pushed = new ConcurrentQueue<string>();
        private static readonly Queue<Achievement> Toasts = new Queue<Achievement>();
        private static readonly HashSet<string> Toasted = new HashSet<string>(StringComparer.Ordinal);

        private static MasterSession? _session;
        private static bool _loaded;
        private static bool _busy;
        private static int _claimedFor;
        private static float _nextClaimAt;

        /// <summary>The master's last answer for <see cref="StatePlayerId"/>, or null.</summary>
        public static AchievementState? State { get; private set; }

        /// <summary>The account <see cref="State"/> belongs to.</summary>
        public static int StatePlayerId { get; private set; }

        /// <summary>Something earned or loaded; pages redraw. Main thread.</summary>
        public static event Action? Changed;

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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            Local.Clear();
            while (Pushed.TryDequeue(out _)) { }
            Toasts.Clear();
            Toasted.Clear();
            _session = null;
            _loaded = false;
            _busy = false;
            _claimedFor = 0;
            _nextClaimAt = 0f;
            State = null;
            StatePlayerId = 0;
            Changed = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (Application.isBatchMode) return;
            PracticeFeats.Earned += OnPracticeEarned;
        }

        /// <summary>Follows the session, takes the master's pushes, and claims practice achievements. Every frame.</summary>
        public static void Tick()
        {
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
        }

        /// <summary>The next achievement to toast, if one is waiting.</summary>
        public static bool TryNextToast(out Achievement achievement)
        {
            if (Toasts.Count > 0)
            {
                achievement = Toasts.Dequeue();
                return true;
            }
            achievement = null!;
            return false;
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

        private static async Task ClaimAsync(MasterSession session)
        {
            _busy = true;
            int player = session.PlayerId;
            try
            {
                AchievementState? state = await session.ClaimAchievementsAsync(new List<string>(LocalEarned));
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

            // The echo of a claim: toasted when it was earned, in this run or an earlier one.
            if (fromMaster && LocalEarned.Contains(id)) return;
            if (Toasted.Add(id)) Toasts.Enqueue(achievement);
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

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            string stored = PlayerPrefs.GetString(EarnedKey, string.Empty);
            foreach (string id in stored.Split(','))
                if (id.Length > 0 && AchievementCatalog.Find(id) != null) Local.Add(id);
        }
    }
}
