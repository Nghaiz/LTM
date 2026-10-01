// Bot soak -- each map played offline by bots alone, and everything they log or get wrong
// written down.
//
// WHAT IT IS FOR
//     A dedicated server's log is where most bot defects show, and it shows them without a stack
//     and without saying which bot or where. This plays each map in the Editor with bots only, at
//     a raised time scale, and writes per map:
//       - every warning, error, exception and assert, counted, with its first full message and
//         stack trace;
//       - every failed path search (not the ones a seeker cancelled on purpose): its reason, where
//         it started and where it was going, the bot asking and the nearest flag to the goal;
//       - bots stuck (a path, and under StuckRadius of travel for StuckAfter), idle (no path, no
//         target, no cover for IdleAfter), swimming for LongSwim, or under the terrain;
//       - flag captures, and the commander's own [bots] lines.
//
// HOW A MAP IS PLAYED
//     Offline: the GameObjects carrying NetServerBootstrap and NetClientBootstrap are deactivated
//     in memory before Play, so the role stays Offline and nothing binds a port (the scene is
//     never saved; see AstarHitchProbe.PinToServerRole for why it is the GameObject and not the
//     component). Bots are released through the real gate, NetBotRelease.NotifyPlayerSpawned,
//     and the roster comes from the master's room plan (AssignRoom + HostedRoom), the same path a
//     live room takes. The match then runs for the asked game seconds at the asked time scale.
//
// Results land in tmp/bot-soak/<label>/<map>.json (tmp/ is gitignored). The state machine
// survives Play Mode's domain reloads through SessionState, as AstarHitchProbe's does.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Ironfront.Net.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Ironfront.Editor.Verification
{
    [InitializeOnLoad]
    public static class BotSoakProbe
    {
        public static readonly string[] Maps = { "Dustbowl", "Island", "ForestLake" };

        const string QueueKey = "Ironfront.BotSoak.Queue";
        const string LabelKey = "Ironfront.BotSoak.Label";
        const string SceneKey = "Ironfront.BotSoak.Scene";
        const string SecondsKey = "Ironfront.BotSoak.Seconds";
        const string ScaleKey = "Ironfront.BotSoak.TimeScale";
        const string BotsKey = "Ironfront.BotSoak.BotsPerTeam";
        const string ReturnKey = "Ironfront.BotSoak.ReturnScene";

        /// <summary>Frames the scene gets to come up before the bots are released.</summary>
        const int SettleFrames = 60;

        /// <summary>Any room id will do: offline, the plan answers for whatever HostedRoom names.</summary>
        const ushort SoakRoom = 1;

        const float SampleEvery = 5f;
        const float StuckAfter = 30f;
        const float StuckRadius = 1.5f;
        const float IdleAfter = 60f;
        const float LongSwim = 20f;
        const float UnderTerrainDepth = 3f;
        const int SamplesKept = 60;

        enum SoakPhase { Settle, Releasing, Released }

        static SoakPhase _phase;
        static int _phaseFrame;
        static float _releasedAt;
        static float _nextSample;
        static BotSoakMapResult _result;
        static string _map;

        static readonly object LogGate = new object();
        static readonly Dictionary<string, BotSoakLogEntry> LogsByKey = new Dictionary<string, BotSoakLogEntry>();
        static readonly ConcurrentQueue<BotSoakPathFailure> PathFailures = new ConcurrentQueue<BotSoakPathFailure>();
        static int _pathsDone;
        static int _pathsCanceled;
        static readonly Dictionary<Actor, BotTrack> Tracks = new Dictionary<Actor, BotTrack>();
        static int[] _lastOwners;

        static readonly FieldInfo HasPath = AiField("hasPath");
        static readonly FieldInfo CalculatingPath = AiField("calculatingPath");
        static readonly FieldInfo InCover = AiField("inCover");
        static readonly FieldInfo LastGoto = AiField("lastGotoPoint");
        static readonly FieldInfo CurrentPath = AiField("path");
        static readonly FieldInfo Waypoint = AiField("waypoint");

        /// <summary>
        /// <c>Path.canceled</c>, read by name so the probe also runs on a tree from before seekers
        /// could cancel quietly; there every cancel shows up as a logged failure instead.
        /// </summary>
        static readonly PropertyInfo Canceled = typeof(Pathfinding.Path).GetProperty("canceled");

        sealed class BotTrack
        {
            public Vector3 Anchor;
            public float AnchoredAt;
            public float IdleSince = -1f;
            public float SwimSince = -1f;
            public bool StuckNoted, IdleNoted, SwimNoted, UnderNoted;
        }

        static BotSoakProbe()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.delayCall += ResumeIfPending;
        }

        // ---------------------------------------------------------------------------- entries

        [MenuItem("Ironfront/Verification/Bot soak (all maps, 10 min each)")]
        public static void RunAllMenu()
        {
            Run(Maps, 600f, 4f, 25, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        }

        /// <summary>Plays <paramref name="maps"/> in turn for <paramref name="gameSeconds"/> of match each.</summary>
        public static void Run(string[] maps, float gameSeconds, float timeScale, int botsPerTeam, string label)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("[soak] leave Play Mode first.");
            }
            SessionState.SetString(LabelKey, label);
            SessionState.SetString(QueueKey, string.Join(",", maps));
            SessionState.SetFloat(SecondsKey, gameSeconds);
            SessionState.SetFloat(ScaleKey, timeScale);
            SessionState.SetInt(BotsKey, botsPerTeam);
            SessionState.SetString(ReturnKey, EditorSceneManager.GetActiveScene().path);
            Advance();
        }

        public static bool IsRunning => !string.IsNullOrEmpty(SessionState.GetString(LabelKey, ""));

        /// <summary>Where the current or last run writes.</summary>
        public static string OutputFolder(string label) =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "tmp", "bot-soak", label));

        public static void Abort()
        {
            Detach();
            SessionState.EraseString(QueueKey);
            SessionState.EraseString(SceneKey);
            SessionState.EraseString(LabelKey);
            if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
        }

        static void ResumeIfPending()
        {
            if (!IsRunning) return;
            if (!string.IsNullOrEmpty(SessionState.GetString(SceneKey, ""))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            Advance();
        }

        // ------------------------------------------------------------------------ state machine

        static void Advance()
        {
            string queue = SessionState.GetString(QueueKey, "");
            if (string.IsNullOrEmpty(queue))
            {
                Finish();
                return;
            }

            string[] parts = queue.Split(',');
            string next = parts[0];
            SessionState.SetString(QueueKey, string.Join(",", parts.Skip(1)));
            SessionState.SetString(SceneKey, next);

            EditorSceneManager.OpenScene("Assets/Scenes/" + next + ".unity", OpenSceneMode.Single);
            int off = DeactivateNetRoots();
            Debug.Log("[soak] " + next + ": deactivated " + off + " net bootstrap object(s) in memory; role stays Offline");
            EditorApplication.EnterPlaymode();
        }

        static void Finish()
        {
            string label = SessionState.GetString(LabelKey, "");
            string back = SessionState.GetString(ReturnKey, "");
            SessionState.EraseString(LabelKey);
            SessionState.EraseString(SceneKey);
            SessionState.EraseString(ReturnKey);
            if (!string.IsNullOrEmpty(back) && File.Exists(back))
            {
                EditorSceneManager.OpenScene(back, OpenSceneMode.Single);
            }
            Debug.Log("[soak] done: " + OutputFolder(label));
        }

        static int DeactivateNetRoots()
        {
            int off = 0;
            foreach (MonoBehaviour behaviour in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (behaviour == null) continue;
                string type = behaviour.GetType().Name;
                if (type != "NetServerBootstrap" && type != "NetClientBootstrap") continue;
                if (!behaviour.gameObject.activeSelf) continue;
                behaviour.gameObject.SetActive(false);
                off++;
            }
            return off;
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            _map = SessionState.GetString(SceneKey, "");
            if (string.IsNullOrEmpty(_map)) return;

            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                Attach();
            }
            else if (change == PlayModeStateChange.EnteredEditMode)
            {
                Detach();
                SessionState.EraseString(SceneKey);
                // On the next editor tick, not through delayCall: delayCall does not drain while the
                // Editor sits in the background, and a soak is left to run in the background.
                EditorApplication.update -= AdvanceOnce;
                EditorApplication.update += AdvanceOnce;
            }
        }

        static void AdvanceOnce()
        {
            EditorApplication.update -= AdvanceOnce;
            Advance();
        }

        static void Attach()
        {
            _phase = SoakPhase.Settle;
            _phaseFrame = Time.frameCount;
            _result = new BotSoakMapResult { map = _map, role = NetContext.Role.ToString() };
            lock (LogGate) LogsByKey.Clear();
            while (PathFailures.TryDequeue(out _)) { }
            _pathsDone = 0;
            _pathsCanceled = 0;
            Tracks.Clear();
            _lastOwners = null;

            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
            AstarPath.OnPathPostSearch = (OnPathDelegate)Delegate.Remove(AstarPath.OnPathPostSearch, new OnPathDelegate(OnPathDone));
            AstarPath.OnPathPostSearch = (OnPathDelegate)Delegate.Combine(AstarPath.OnPathPostSearch, new OnPathDelegate(OnPathDone));
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Detach()
        {
            EditorApplication.update -= Tick;
            Application.logMessageReceivedThreaded -= OnLog;
            AstarPath.OnPathPostSearch = (OnPathDelegate)Delegate.Remove(AstarPath.OnPathPostSearch, new OnPathDelegate(OnPathDone));
            NetBotRelease.HostedRoom = null;
            Time.timeScale = 1f;
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying || _result == null) return;

            if (_phase == SoakPhase.Settle)
            {
                if (Time.frameCount - _phaseFrame < SettleFrames) return;
                if (ActorManager.instance == null) return;

                if (GameManager.instance != null && !GameManager.instance.ingame)
                {
                    typeof(GameManager).GetMethod("StartGame", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(GameManager.instance, null);
                    _result.startedByProbe = true;
                }

                int bots = SessionState.GetInt(BotsKey, 25);
                NetBotRelease.HostedRoom = () => SoakRoom;
                NetBotRelease.AssignRoom(SoakRoom, bots);
                NetBotRelease.NotifyPlayerSpawned();
                Time.timeScale = SessionState.GetFloat(ScaleKey, 4f);

                _result.botsPerTeam = bots;
                _result.timeScale = Time.timeScale;
                _result.role = NetContext.Role.ToString();
                _phase = SoakPhase.Releasing;
                return;
            }

            if (_phase == SoakPhase.Releasing)
            {
                // The match is measured from the moment bots may exist, not from the gate opening:
                // NetBotRelease holds them for DelaySeconds after the first player spawn.
                if (!NetBotRelease.IsReleased) return;
                _releasedAt = Time.time;
                _nextSample = Time.time + SampleEvery;
                _phase = SoakPhase.Released;
                return;
            }

            DrainPathFailures();

            if (Time.time >= _nextSample)
            {
                _nextSample = Time.time + SampleEvery;
                Sample();
            }

            if (Time.time - _releasedAt >= SessionState.GetFloat(SecondsKey, 600f))
            {
                Write();
                EditorApplication.ExitPlaymode();
                _result = null;
            }
        }

        // ------------------------------------------------------------------------------ capture

        static void OnLog(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Log)
            {
                if (message.StartsWith("[bots]", StringComparison.Ordinal))
                {
                    lock (LogGate)
                    {
                        _result?.commanderLines.Add(Stamp() + message);
                        if (_result != null && _result.commanderLines.Count > SamplesKept) _result.commanderLines.RemoveAt(0);
                    }
                }
                return;
            }

            string firstLine = message.Split('\n')[0];
            string key = type + "|" + Regex.Replace(firstLine, @"[-+]?\d+(\.\d+)?", "#") + "|" + FirstFrame(stackTrace);
            lock (LogGate)
            {
                if (!LogsByKey.TryGetValue(key, out BotSoakLogEntry entry))
                {
                    entry = new BotSoakLogEntry
                    {
                        type = type.ToString(),
                        message = message.Length > 2000 ? message.Substring(0, 2000) : message,
                        stack = stackTrace.Length > 4000 ? stackTrace.Substring(0, 4000) : stackTrace,
                        firstAt = Stamp(),
                    };
                    LogsByKey.Add(key, entry);
                }
                entry.count++;
            }
        }

        static void OnPathDone(Pathfinding.Path p)
        {
            Interlocked.Increment(ref _pathsDone);
            if (Canceled != null && (bool)Canceled.GetValue(p))
            {
                Interlocked.Increment(ref _pathsCanceled);
                return;
            }
            if (!p.error) return;

            var failure = new BotSoakPathFailure { reason = p.errorLog.Split('\n')[0] };
            if (p is Pathfinding.ABPath ab)
            {
                failure.from = ab.originalStartPoint;
                failure.to = ab.originalEndPoint;
            }
            failure.graphMask = p.nnConstraint != null ? p.nnConstraint.graphMask : -1;
            PathFailures.Enqueue(failure);
        }

        /// <summary>Main thread: names the bot asking and what lies at the goal.</summary>
        static void DrainPathFailures()
        {
            while (PathFailures.TryDequeue(out BotSoakPathFailure failure))
            {
                failure.at = Stamp();
                Actor asker = NearestAiActor(failure.from, 3f);
                if (asker != null)
                {
                    var ai = asker.controller as AiActorController;
                    failure.bot = asker.name;
                    failure.team = asker.team;
                    failure.seated = asker.IsSeated();
                    failure.squadState = ai != null && ai.squad != null ? ai.squad.state.ToString() : "none";
                }
                SpawnPoint flag = NearestFlag(failure.to, out float distance);
                failure.nearestFlag = flag != null ? flag.name : "";
                failure.nearestFlagDistance = distance;

                _result.pathFailureCount++;
                _result.pathFailureReasons.Add(failure.reason);
                if (_result.pathFailures.Count < SamplesKept) _result.pathFailures.Add(failure);
            }
        }

        static void Sample()
        {
            float now = Time.time;
            List<Actor> actors = ActorManager.instance != null ? ActorManager.instance.actors : null;
            if (actors == null) return;

            int alive = 0, swimming = 0, seated = 0;
            foreach (Actor actor in actors)
            {
                if (actor == null || !actor.aiControlled || actor.dead)
                {
                    if (actor != null) Tracks.Remove(actor);
                    continue;
                }
                var ai = actor.controller as AiActorController;
                if (ai == null) continue;
                alive++;

                Vector3 position = actor.Position();
                if (!Tracks.TryGetValue(actor, out BotTrack track))
                {
                    Tracks.Add(actor, new BotTrack { Anchor = position, AnchoredAt = now });
                    continue;
                }

                bool isSeated = actor.IsSeated();
                bool hasPath = (bool)HasPath.GetValue(ai);
                bool calculating = (bool)CalculatingPath.GetValue(ai);
                bool inCover = (bool)InCover.GetValue(ai);
                bool fighting = ai.target != null;
                if (isSeated) seated++;

                if (Vector3.Distance(position, track.Anchor) > StuckRadius)
                {
                    track.Anchor = position;
                    track.AnchoredAt = now;
                    track.StuckNoted = false;
                }
                else if (!track.StuckNoted && hasPath && !isSeated && !inCover && !fighting && !actor.fallenOver
                         && now - track.AnchoredAt >= StuckAfter)
                {
                    track.StuckNoted = true;
                    Note(_result.stuck, ref _result.stuckCount, actor, ai, now - track.AnchoredAt);
                }

                bool idle = !hasPath && !calculating && !fighting && !inCover && !isSeated;
                if (!idle)
                {
                    track.IdleSince = -1f;
                    track.IdleNoted = false;
                }
                else if (track.IdleSince < 0f)
                {
                    track.IdleSince = now;
                }
                else if (!track.IdleNoted && now - track.IdleSince >= IdleAfter)
                {
                    track.IdleNoted = true;
                    Note(_result.idle, ref _result.idleCount, actor, ai, now - track.IdleSince);
                }

                if (!actor.inWater || isSeated)
                {
                    track.SwimSince = -1f;
                    track.SwimNoted = false;
                }
                else
                {
                    swimming++;
                    if (track.SwimSince < 0f) track.SwimSince = now;
                    else if (!track.SwimNoted && now - track.SwimSince >= LongSwim)
                    {
                        track.SwimNoted = true;
                        Note(_result.longSwims, ref _result.longSwimCount, actor, ai, now - track.SwimSince);
                    }
                }

                if (!track.UnderNoted && !isSeated && TerrainSurface.IsUnder(position, UnderTerrainDepth))
                {
                    track.UnderNoted = true;
                    Note(_result.underTerrain, ref _result.underTerrainCount, actor, ai, 0f);
                }
            }

            _result.gameSeconds = now - _releasedAt;
            _result.peakAlive = Mathf.Max(_result.peakAlive, alive);
            _result.peakSwimming = Mathf.Max(_result.peakSwimming, swimming);
            _result.peakSeated = Mathf.Max(_result.peakSeated, seated);
            SampleFlags();
        }

        static void SampleFlags()
        {
            SpawnPoint[] flags = ActorManager.instance.spawnPoints;
            if (flags == null) return;
            if (_lastOwners == null || _lastOwners.Length != flags.Length)
            {
                _lastOwners = flags.Select(f => f != null ? f.owner : -2).ToArray();
                return;
            }
            for (int i = 0; i < flags.Length; i++)
            {
                if (flags[i] == null || flags[i].owner == _lastOwners[i]) continue;
                _result.flagChanges.Add(Stamp() + flags[i].name + ": " + _lastOwners[i] + " -> " + flags[i].owner);
                _lastOwners[i] = flags[i].owner;
            }
            _result.finalOwners = string.Join(", ", flags.Select(f => f == null ? "?" : f.name + "=" + f.owner));
        }

        static void Note(List<BotSoakBotEvent> into, ref int count, Actor actor, AiActorController ai, float seconds)
        {
            count++;
            if (into.Count >= SamplesKept) return;
            SpawnPoint flag = NearestFlag(actor.Position(), out float distance);
            into.Add(new BotSoakBotEvent
            {
                at = Stamp(),
                bot = actor.name,
                team = actor.team,
                position = actor.Position(),
                seconds = seconds,
                goal = (Vector3)LastGoto.GetValue(ai),
                squadState = ai.squad != null ? ai.squad.state.ToString() : "none",
                squadSize = ai.squad != null ? ai.squad.members.Count : 0,
                inWater = actor.inWater,
                nearestFlag = flag != null ? flag.name : "",
                nearestFlagDistance = distance,
                path = DescribePath(ai, actor.Position()),
                vehicle = DescribeSquadVehicle(ai, actor.Position()),
            });
        }

        /// <summary>Where the bot is along its path: waypoints left and the distance to the next.</summary>
        static string DescribePath(AiActorController ai, Vector3 position)
        {
            if (!(CurrentPath.GetValue(ai) is Pathfinding.Path path) || path.vectorPath == null || path.vectorPath.Count == 0)
            {
                return "";
            }
            int next = Mathf.Clamp((int)Waypoint.GetValue(ai), 0, path.vectorPath.Count - 1);
            Vector3 waypoint = path.vectorPath[next];
            return "waypoint " + next + " of " + path.vectorPath.Count + ", "
                   + Vector3.Distance(position, waypoint).ToString("F1", CultureInfo.InvariantCulture) + " m to it at "
                   + waypoint.ToString("F1");
        }

        /// <summary>The squad's vehicle as the bot sees it: where it is now and whether it can be boarded.</summary>
        static string DescribeSquadVehicle(AiActorController ai, Vector3 position)
        {
            Squad squad = ai.squad;
            if (squad == null || squad.squadVehicle == null) return "";
            Vehicle vehicle = squad.squadVehicle;
            Actor driver = vehicle.HasDriver() ? vehicle.Driver() : null;
            string driverNote = driver == null ? "none"
                : driver.controller is AiActorController driving && driving.squad == squad ? "own squad"
                : driver.aiControlled ? "another squad" : "a player";
            return vehicle.name + " " + Vector3.Distance(position, vehicle.transform.position).ToString("F1", CultureInfo.InvariantCulture)
                   + " m away" + (vehicle.dead ? ", dead" : "") + (vehicle.burning ? ", burning" : "")
                   + (vehicle.stuck ? ", stuck" : "") + (vehicle.IsFull() ? ", full" : ", " + vehicle.EmptySeats() + " empty seat(s)")
                   + ", " + vehicle.ClaimedSeatCount + "/" + vehicle.seats.Length + " claimed, driver " + driverNote;
        }

        // ------------------------------------------------------------------------------ output

        static void Write()
        {
            DrainPathFailures();
            Sample();
            _result.pathsDone = _pathsDone;
            _result.pathsCanceled = _pathsCanceled;
            lock (LogGate)
            {
                _result.logs = LogsByKey.Values.OrderByDescending(e => e.count).ToList();
            }

            string folder = OutputFolder(SessionState.GetString(LabelKey, "unlabelled"));
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, _map + ".json");
            File.WriteAllText(file, JsonUtility.ToJson(_result, true));
            Debug.Log("[soak] " + _map + ": " + _result.gameSeconds.ToString("F0", CultureInfo.InvariantCulture)
                      + " game s, " + _result.logs.Count + " distinct warning/error line(s), "
                      + _result.pathFailureCount + " failed path(s), " + _result.pathsCanceled + " cancelled; " + file);
        }

        // ----------------------------------------------------------------------------- helpers

        static string Stamp() => _result == null
            ? ""
            : "[" + (Time.time - _releasedAt).ToString("F0", CultureInfo.InvariantCulture) + "s] ";

        static string FirstFrame(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return "";
            foreach (string line in stackTrace.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("UnityEngine.Debug", StringComparison.Ordinal)) continue;
                return trimmed;
            }
            return "";
        }

        static Actor NearestAiActor(Vector3 point, float within)
        {
            Actor best = null;
            float bestDistance = within;
            List<Actor> actors = ActorManager.instance != null ? ActorManager.instance.actors : null;
            if (actors == null) return null;
            foreach (Actor actor in actors)
            {
                if (actor == null || !actor.aiControlled) continue;
                float distance = Vector3.Distance(actor.Position(), point);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = actor;
                }
            }
            return best;
        }

        static SpawnPoint NearestFlag(Vector3 point, out float distance)
        {
            distance = float.PositiveInfinity;
            SpawnPoint best = null;
            SpawnPoint[] flags = ActorManager.instance != null ? ActorManager.instance.spawnPoints : null;
            if (flags == null) return null;
            foreach (SpawnPoint flag in flags)
            {
                if (flag == null) continue;
                float d = Vector3.Distance(flag.transform.position, point);
                if (d < distance)
                {
                    distance = d;
                    best = flag;
                }
            }
            return best;
        }

        static FieldInfo AiField(string name) =>
            typeof(AiActorController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException(nameof(AiActorController), name);
    }

    [Serializable]
    public sealed class BotSoakMapResult
    {
        public string map;
        public string role;
        public bool startedByProbe;
        public int botsPerTeam;
        public float timeScale;
        public float gameSeconds;
        public int peakAlive;
        public int peakSeated;
        public int peakSwimming;
        public int pathsDone;
        public int pathsCanceled;
        public int pathFailureCount;
        public List<string> pathFailureReasons = new List<string>();
        public List<BotSoakPathFailure> pathFailures = new List<BotSoakPathFailure>();
        public int stuckCount;
        public List<BotSoakBotEvent> stuck = new List<BotSoakBotEvent>();
        public int idleCount;
        public List<BotSoakBotEvent> idle = new List<BotSoakBotEvent>();
        public int longSwimCount;
        public List<BotSoakBotEvent> longSwims = new List<BotSoakBotEvent>();
        public int underTerrainCount;
        public List<BotSoakBotEvent> underTerrain = new List<BotSoakBotEvent>();
        public List<string> flagChanges = new List<string>();
        public string finalOwners;
        public List<string> commanderLines = new List<string>();
        public List<BotSoakLogEntry> logs = new List<BotSoakLogEntry>();
    }

    [Serializable]
    public sealed class BotSoakLogEntry
    {
        public string type;
        public int count;
        public string firstAt;
        public string message;
        public string stack;
    }

    [Serializable]
    public sealed class BotSoakPathFailure
    {
        public string at;
        public string reason;
        public Vector3 from;
        public Vector3 to;
        public int graphMask;
        public string bot;
        public int team;
        public bool seated;
        public string squadState;
        public string nearestFlag;
        public float nearestFlagDistance;
    }

    [Serializable]
    public sealed class BotSoakBotEvent
    {
        public string at;
        public string bot;
        public int team;
        public Vector3 position;
        public float seconds;
        public Vector3 goal;
        public string squadState;
        public int squadSize;
        public bool inWater;
        public string nearestFlag;
        public float nearestFlagDistance;
        public string path;
        public string vehicle;
    }
}
