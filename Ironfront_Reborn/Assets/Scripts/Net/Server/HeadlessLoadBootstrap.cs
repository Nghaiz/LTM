using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ironfront.Net.Transport;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Process A of the debt-closure phase-3 harness: the measurement sink on the authoritative
    /// server. Adds a reproducible RNG seed, a tick-time histogram, and a JSONL record per
    /// stepped tick — and adds nothing a player would ever see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a writer, not a measurement.</b> Every number it emits is read off state the
    /// server already keeps: <see cref="Ironfront.Net.Replication.Server.TickTimeStats.Last"/>
    /// for the step time, <see cref="Ironfront.Net.Replication.Interest.InterestManager"/>'s
    /// counters for the interest accounting, and <see cref="ConnectionInfo.Stats"/> for the
    /// bytes. Nothing here re-derives a figure the server computes for itself, because two
    /// implementations of one number is how a harness ends up grading its own arithmetic
    /// (`plans/debt-closure/phases/phase-3-harness.md` § 4 makes the same point about decoders).
    /// </para>
    /// <para>
    /// <b>Opt-in by environment variable, self-installing, and absent otherwise.</b> With
    /// <see cref="OutputVariable"/> unset this type installs nothing, subscribes to nothing and
    /// allocates nothing — an ordinary server run is unchanged down to the frame. It is
    /// installed from a <see cref="RuntimeInitializeOnLoadMethod"/> rather than authored onto a
    /// scene object for the reason phase 2 recorded when it added
    /// <c>NetClientLocalCombatDriver</c> in code: a component that must be dragged onto a
    /// GameObject on every map is a component that is missing on one of them, and here the
    /// symptom would be a measurement run that silently produced no measurements.
    /// </para>
    /// <para>
    /// <b>Why the whole thing is one file.</b> Phase 3 § 7 lists exactly one writable path under
    /// <c>Assets/Scripts/Net/Server/</c>, and the neighbouring <c>Net/Diagnostics/</c> folder
    /// carries no asmdef, so it compiles into <c>Assembly-CSharp</c> — which
    /// <c>Ironfront.Net.Unity.Server</c> may not reference. Splitting the histogram or the
    /// writer out of this file would not compile, whatever the file-length convention prefers.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(300)]
    [DisallowMultipleComponent]
    public sealed class HeadlessLoadBootstrap : MonoBehaviour
    {
        /// <summary>
        /// Path of the per-tick JSONL sink. Presence of this variable is what enables the whole
        /// component; there is deliberately no separate on/off switch to disagree with it.
        /// </summary>
        public const string OutputVariable = "IRONFRONT_LOAD_JSONL";

        /// <summary>
        /// Path of the end-of-run summary. Defaults to <see cref="OutputVariable"/> with
        /// <c>.summary.json</c> appended.
        /// </summary>
        public const string SummaryVariable = "IRONFRONT_LOAD_SUMMARY";

        /// <summary>
        /// Seed handed to <see cref="UnityEngine.Random.InitState"/>. Defaults to
        /// <see cref="DefaultSeed"/>.
        /// </summary>
        /// <remarks>
        /// <b>What this actually pins.</b> <c>ServerCombatBridge.ChooseSpawnIndex</c> picks a
        /// spawn point by reservoir sampling over <see cref="UnityEngine.Random"/>, so seeding
        /// the shared generator is what makes two runs put the same bots in the same places.
        /// It pins nothing about the network, which is <c>NetworkSimulator</c>'s own seed and is
        /// reported separately by process B — two seeds, because they are two generators, and a
        /// report that printed one of them would be claiming reproducibility it does not have.
        /// </remarks>
        public const string SeedVariable = "IRONFRONT_LOAD_SEED";

        /// <summary>
        /// Path of the log-entry sink: one JSONL record per <see cref="LogType.Error"/>,
        /// <see cref="LogType.Exception"/> or <see cref="LogType.Assert"/> the process emits.
        /// Defaults to <see cref="OutputVariable"/> with <c>.errors.jsonl</c> appended.
        /// </summary>
        /// <remarks>
        /// <b>Criterion 11 grades the log, not the exit code</b>, and a Unity player survives
        /// many exceptions by logging them and carrying on with a broken object. That much was
        /// already known. What was not covered is that <c>-logFile</c> writes an
        /// <c>Debug.LogError</c> message with the same shape as a <c>Debug.Log</c> one -- no
        /// level marker anywhere on the line -- so <c>run-lane-a.ps1</c>'s type tally could only
        /// ever find entries whose TEXT began with an exception type name. An error raised by
        /// the game's own code (<c>"[net] match reset left state behind"</c>) was invisible to
        /// it. Subscribing to <see cref="Application.logMessageReceived"/> is the only place the
        /// <see cref="LogType"/> still exists, so the tally is taken here and the log is left to
        /// be the human-readable copy.
        /// </remarks>
        public const string ErrorsVariable = "IRONFRONT_LOAD_ERRORS";

        /// <summary>Seed used when <see cref="SeedVariable"/> is unset.</summary>
        /// <remarks>12345 to match <c>SimulatorConfig.RandomSeed</c>'s default, so a run
        /// described only as "the defaults" is one configuration rather than two.</remarks>
        public const int DefaultSeed = 12345;

        /// <summary>Records buffered before the writer is flushed to disk.</summary>
        /// <remarks>
        /// A crash loses at most this many records. It is deliberately small: the run this
        /// serves is minutes long, and a harness whose evidence is lost by the failure it was
        /// watching for is worth nothing.
        /// </remarks>
        private const int FlushEveryRecords = 60;

        /// <summary>
        /// Upper edges of the step-time histogram, in milliseconds, plus an implicit overflow
        /// bucket above the last one.
        /// </summary>
        /// <remarks>
        /// Clustered around the 33.3 ms tick budget because that is the only threshold the
        /// number is judged against — evenly spaced buckets would spend most of their resolution
        /// on times nobody has a question about. The exact per-step microseconds are in the
        /// JSONL regardless, so a percentile is computed from the records, not from these
        /// buckets; the histogram is the cheap in-process view, not the evidence.
        /// </remarks>
        private static readonly double[] BucketEdgesMs =
            { 1, 2, 4, 8, 12, 16, 20, 25, 33.3, 50, 100 };

        /// <summary>UTF-8 without a byte-order mark, for both sinks.</summary>
        /// <remarks>
        /// <b><see cref="Encoding.UTF8"/> emits a BOM</b>, and the first line of a JSONL file is
        /// a whole record: the leading <c>U+FEFF</c> lands immediately before <c>{</c>, so a
        /// reader doing the obvious thing gets a parse error on line 1 and none thereafter.
        /// Observed on the first Editor run of this component. The summary shares the encoding
        /// for the same reason — <c>File.WriteAllText(path, text, Encoding.UTF8)</c> also
        /// writes one.
        /// </remarks>
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly List<ushort> _connections = new List<ushort>();
        private readonly long[] _bucketCounts = new long[BucketEdgesMs.Length + 1];
        private readonly StringBuilder _line = new StringBuilder(512);
        private readonly Dictionary<ushort, ConnectionBytes> _lastPerConnection =
            new Dictionary<ushort, ConnectionBytes>();

        private ServerTickLoop _loop;
        private MatchController _match;
        private ITransportServer _transport;
        private StreamWriter _writer;
        private StreamWriter _errorWriter;

        private string _outputPath;
        private string _summaryPath;
        private string _errorsPath;
        private int _seed;

        // Counted by LogType, which is the only axis on which criterion 11 is stated. Indexed by
        // (int)LogType, so the array is as long as the enum and no entry can be forgotten.
        private readonly long[] _logCounts = new long[5];
        private long _errorEntriesWritten;

        private uint _lastTick;
        private int _recordsSinceFlush;

        private long _records;
        private long _resets;
        private long _ticksCovered;
        private long _connectionMismatchRecords;
        private double _maxStepMs;
        private double _totalStepMs;

        private long _lastEntriesConsidered, _lastEntriesRefreshed, _lastEntriesHeld;
        private long _lastEntriesCulled, _lastEntriesShed;

        /// <summary>
        /// Error records written to disk before the sink stops writing bodies and only counts.
        /// </summary>
        /// <remarks>
        /// X-69 put 534 NullReferenceExceptions in one run's log, each with a stack trace. A cap
        /// keeps a storm from turning the evidence file into hundreds of megabytes, and the
        /// TALLY is uncapped -- so a run that trips the cap still reports its true count, which
        /// is the number criterion 11 is graded on. A truncated file that under-reported the
        /// count would be the exact failure this sink exists to prevent.
        /// </remarks>
        private const int MaxErrorRecords = 500;

        private struct ConnectionBytes
        {
            public long Sent;
            public long Received;
        }

        /// <summary>
        /// Creates the component when <see cref="OutputVariable"/> is set, and does nothing at
        /// all when it is not.
        /// </summary>
        /// <remarks>
        /// <b>After scene load, not before.</b> The seed has to be applied before anything picks
        /// a spawn point, and <c>BeforeSceneLoad</c> would be earlier still — but the component
        /// also has to find <see cref="ServerTickLoop.Current"/>, which does not exist until a
        /// map scene is up. So it is installed here, seeds immediately, and then waits for the
        /// loop; the wait is what handles the Splash → Menu → map sequence, where the first
        /// scene to load is not the one with a server in it.
        /// </remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            string output = Environment.GetEnvironmentVariable(OutputVariable);
            if (string.IsNullOrWhiteSpace(output)) return;

            // prefab-only-construction-unity.md does not apply: this is a diagnostics harness
            // host, not scene construction — one empty object carrying one component, wiring no
            // references and shipping in no scene, created only when the run asked for it.
            var host = new GameObject("Headless Load Bootstrap");
            DontDestroyOnLoad(host);
            host.AddComponent<HeadlessLoadBootstrap>();
        }

        /// <summary>Whether the sink is open and recording.</summary>
        public bool Recording => _writer != null && _loop != null;

        /// <summary>Records written so far. Zero after a run means the sink never attached.</summary>
        public long RecordCount => _records;

        private void Awake()
        {
            _outputPath = Environment.GetEnvironmentVariable(OutputVariable);
            if (string.IsNullOrWhiteSpace(_outputPath))
            {
                // Unreachable through Install, reachable if somebody adds the component by hand.
                Debug.LogError(
                    $"[load] {nameof(HeadlessLoadBootstrap)} needs {OutputVariable} to name a "
                    + "JSONL path. Nothing will be recorded.");
                enabled = false;
                return;
            }

            _summaryPath = Environment.GetEnvironmentVariable(SummaryVariable);
            if (string.IsNullOrWhiteSpace(_summaryPath)) _summaryPath = _outputPath + ".summary.json";

            _errorsPath = Environment.GetEnvironmentVariable(ErrorsVariable);
            if (string.IsNullOrWhiteSpace(_errorsPath)) _errorsPath = _outputPath + ".errors.jsonl";

            _seed = ReadSeed();
            UnityEngine.Random.InitState(_seed);

            if (!TryOpenWriter()) { enabled = false; return; }

            // Before anything else can throw. A sink that starts counting after the first
            // subsystem has already booted would report a clean run whose first exception it
            // simply was not there for -- and Awake is the earliest point at which this
            // component exists at all.
            _errorWriter = TryOpenErrors();
            Application.logMessageReceived += OnLogMessage;

            Debug.Log(
                $"[load] recording to {_outputPath} (summary {_summaryPath}, "
                + $"errors {_errorsPath}), {SeedVariable}={_seed}");
        }

        /// <summary>
        /// Opens the error sink, or returns null having said why.
        /// </summary>
        /// <remarks>
        /// A null writer does NOT disable the tally: <see cref="OnLogMessage"/> still counts by
        /// <see cref="LogType"/>, and the summary still carries those counts. Losing the bodies
        /// costs triage detail; losing the counts would cost the criterion.
        /// </remarks>
        private StreamWriter TryOpenErrors()
        {
            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(_errorsPath));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                return new StreamWriter(_errorsPath, append: false, Utf8NoBom) { AutoFlush = true };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[load] could not open '{_errorsPath}': {ex.Message}. "
                                 + "The per-type tally is still taken; the bodies are not kept.");
                return null;
            }
        }

        /// <summary>
        /// Tallies every log entry by type and writes the bodies of the ones criterion 11 grades.
        /// </summary>
        /// <remarks>
        /// <b><see cref="LogType.Warning"/> and <see cref="LogType.Log"/> are counted and not
        /// written.</b> The count is what distinguishes "the sink was attached and the run was
        /// clean" from "the sink was never attached" — a zero beside a zero says nothing, and a
        /// zero beside 40,000 Log entries says the subscription was live.
        /// <b>AutoFlush is on</b> because the failure being recorded is exactly the one that may
        /// take the process down before <see cref="Close"/> runs.
        /// </remarks>
        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            int index = (int)type;
            if ((uint)index < (uint)_logCounts.Length) _logCounts[index]++;

            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
                return;

            if (_errorWriter == null || _errorEntriesWritten >= MaxErrorRecords) return;

            try
            {
                var record = new StringBuilder(256);
                record.Append("{\"type\":\"").Append(type).Append('"')
                      .Append(",\"tick\":").Append(_loop == null ? 0u : _loop.CurrentTick)
                      .Append(",\"atSec\":")
                      .Append(Time.realtimeSinceStartupAsDouble.ToString(
                          "0.###", CultureInfo.InvariantCulture))
                      .Append(",\"condition\":").Append(Quote(condition))
                      .Append(",\"site\":").Append(Quote(FirstStackLine(stackTrace)))
                      .Append('}');

                _errorWriter.WriteLine(record.ToString());
                _errorEntriesWritten++;
            }
            catch (Exception)
            {
                // Deliberately silent: logging from inside a log handler is how a single error
                // becomes an unbounded recursion, and the tally above has already recorded it.
                _errorWriter = null;
            }
        }

        /// <summary>The first frame of a stack trace, which is the site worth triaging to.</summary>
        private static string FirstStackLine(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return string.Empty;

            int end = stackTrace.IndexOf('\n');
            string line = end < 0 ? stackTrace : stackTrace.Substring(0, end);
            return line.TrimEnd('\r');
        }

        /// <summary>Minimal JSON string escaping — enough for a log line and a stack frame.</summary>
        private static string Quote(string value)
        {
            var quoted = new StringBuilder(64);
            quoted.Append('"');

            for (int i = 0; value != null && i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': quoted.Append("\\\""); break;
                    case '\\': quoted.Append("\\\\"); break;
                    case '\n': quoted.Append("\\n"); break;
                    case '\r': quoted.Append("\\r"); break;
                    case '\t': quoted.Append("\\t"); break;
                    default:
                        if (c < ' ') quoted.Append("\\u").Append(((int)c).ToString("x4"));
                        else quoted.Append(c);
                        break;
                }
            }

            quoted.Append('"');
            return quoted.ToString();
        }

        private static int ReadSeed()
        {
            string raw = Environment.GetEnvironmentVariable(SeedVariable);
            if (string.IsNullOrWhiteSpace(raw)) return DefaultSeed;

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
                return seed;

            // Loud, and then the default: a run whose seed was a typo must not quietly report
            // itself as reproducible at a seed nobody asked for.
            Debug.LogError(
                $"[load] {SeedVariable}='{raw}' is not an integer. Falling back to {DefaultSeed}.");
            return DefaultSeed;
        }

        private bool TryOpenWriter()
        {
            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(_outputPath));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                _writer = new StreamWriter(_outputPath, append: false, Utf8NoBom)
                {
                    AutoFlush = false,
                };
                return true;
            }
            catch (Exception ex)
            {
                // Throwing here would take the server down with it, which is a worse outcome
                // than an unmeasured run — but it is reported as a failure, not skipped.
                Debug.LogError($"[load] could not open '{_outputPath}': {ex.Message}");
                _writer = null;
                return false;
            }
        }

        /// <summary>
        /// One record per fixed step that advanced at least one tick.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Execution order 300, after <c>ServerSnapshotStage</c> at 200.</b> The snapshot
        /// stage is what records the step time and sends the bytes, so sampling before it would
        /// read the previous step's numbers under this step's tick number — an off-by-one that
        /// nothing downstream could detect.
        /// </para>
        /// <para>
        /// <b>"Per tick" is per stepped tick, and the record says how many.</b> A catch-up step
        /// simulates several ticks and sends at most one snapshot, so a record can cover more
        /// than one tick; <c>nTicks</c> carries the count rather than the record pretending each
        /// tick got its own. The tick number is the last of them, matching what the snapshot
        /// stage itself uses.
        /// </para>
        /// </remarks>
        private void FixedUpdate()
        {
            if (_writer == null) return;

            if (_loop == null)
            {
                if (!TryAttach()) return;
            }

            uint tick = _loop.CurrentTick;
            if (tick == _lastTick) return;

            uint ticks = tick - _lastTick;
            _lastTick = tick;

            WriteRecord(tick, ticks);
        }

        private bool TryAttach()
        {
            ServerTickLoop loop = ServerTickLoop.Current;
            if (loop == null || loop.Transport == null) return false;

            _loop = loop;
            _match = loop.GetComponent<MatchController>();
            if (_match != null) _match.MatchResetCompleted += OnMatchResetCompleted;
            _transport = loop.Transport;
            _transport.OnClientConnected += OnClientConnected;
            _transport.OnClientDisconnected += OnClientDisconnected;

            // Anchor on the tick the loop is ALREADY at, so the first record describes a real
            // advance. Starting from 0 instead emitted a phantom record for tick 0 carrying
            // stepMicros 0 — the loop had not stepped yet, so TickTimes.Last was still its
            // initial value — and that zero then sat in the ≤1 ms bucket and pulled the mean
            // down. A measurement whose first sample is of nothing is worse than one sample
            // short: it is indistinguishable from a genuinely instant step. Observed on the
            // first Editor run of this component, which is what running it was for.
            _lastTick = loop.CurrentTick;

            // Said out loud when absent, because a null one costs the phase field and the
            // phase field is what separates "mid-round" from "at reset" in the soak grading.
            // Silent, the records would simply all read phase 0 and the audit rows below would
            // be ungradeable for a reason nothing in the artifact explains.
            if (_match == null)
                Debug.LogWarning(
                    "[load] no MatchController beside the tick loop: every record will carry "
                    + "phase 0, and the five-match soak cannot be graded from this run.");

            Debug.Log(
                $"[load] attached to {_loop.GetType().Name} over "
                + $"{_transport.GetType().Name} at tick {_loop.CurrentTick}");
            return true;
        }

        /// <summary>
        /// Writes one <c>"reset"</c> record per completed reset, carrying the post-reset audit.
        /// </summary>
        /// <remarks>
        /// This is the ONLY trustworthy count of resets in the file, and the only place the
        /// audit is sampled at the moment criterion 13 is about. The per-tick records still
        /// carry an audit — that is what proves each pool ROSE during the round — but their
        /// phase field cannot locate a reset (see <c>MatchController.MatchResetCompleted</c>).
        /// <para>
        /// <c>liveActors</c> is recorded beside the audit because <c>ActorIdsInUse</c> is NOT
        /// expected to be zero on a map whose actors outlive the round: Dustbowl retains all 56
        /// deliberately (<c>ServerTickLoop.ResetForNewMatch</c> passes them so the pool cannot
        /// re-offer an id still held). Without the live count beside it, a correct retention and
        /// a genuine leak are the same number.
        /// </para>
        /// </remarks>
        private void OnMatchResetCompleted(
            Ironfront.Net.Replication.Server.ServerStateSnapshot audit)
        {
            if (_writer == null) return;

            _resets++;

            _line.Length = 0;
            _line.Append("{\"reset\":").Append(_resets)
                 .Append(",\"t\":").Append(_loop == null ? 0u : _loop.CurrentTick)
                 .Append(",\"liveActors\":").Append(ServerActorRegistry.Instance.Count)
                 .Append(",\"liveVehicles\":").Append(ServerVehicleRegistry.Instance.Count);

            AppendAuditOf(audit);

            _line.Append('}');
            _writer.WriteLine(_line.ToString());

            // Flushed at once: a reset record is the evidence for criterion 13, and the run it
            // belongs to may not survive to the next scheduled flush.
            _writer.Flush();
            _recordsSinceFlush = 0;
        }

        private void OnClientConnected(ushort connectionId, ConnectionInfo info)
        {
            if (!_connections.Contains(connectionId)) _connections.Add(connectionId);
        }

        private void OnClientDisconnected(ushort connectionId, DisconnectReason reason)
        {
            _connections.Remove(connectionId);
            _lastPerConnection.Remove(connectionId);
        }

        private void WriteRecord(uint tick, uint ticks)
        {
            double stepMs = _loop.Scheduler.TickTimes.Last;
            RecordHistogram(stepMs);

            Ironfront.Net.Replication.Interest.InterestManager interest = _loop.Interest;

            long consideredDelta = interest.EntriesConsidered - _lastEntriesConsidered;
            long sentDelta = interest.EntriesRefreshed - _lastEntriesRefreshed;
            long heldDelta = interest.EntriesHeld - _lastEntriesHeld;
            long culledDelta = interest.EntriesCulled - _lastEntriesCulled;
            long shedDelta = interest.EntriesShed - _lastEntriesShed;

            _lastEntriesConsidered = interest.EntriesConsidered;
            _lastEntriesRefreshed = interest.EntriesRefreshed;
            _lastEntriesHeld = interest.EntriesHeld;
            _lastEntriesCulled = interest.EntriesCulled;
            _lastEntriesShed = interest.EntriesShed;

            // The stage split, and the frame the stages live in.
            //
            // stepMicros is the SCRIPT span -- input stage, the gameplay and AI between the two
            // stages, and the snapshot build. Every one of those is a FixedUpdate, and Unity
            // steps PhysX after the last of them, so none of the three includes physics.
            // frameMicros is Time.unscaledDeltaTime for the frame this fixed step belongs to,
            // which does. P7 task 4.2 asks for exactly this distinction: "the netcode is 300 us
            // and the frame is 28 ms" and "the snapshot stage is 20 ms" have different remedies,
            // and one total cannot tell them apart.
            double inputMs = _loop.LastInputStageMs;
            double snapshotMs = _loop.LastSnapshotStageMs;
            double gameplayMs = stepMs - inputMs - snapshotMs;
            if (gameplayMs < 0.0) gameplayMs = 0.0;

            _line.Length = 0;
            _line.Append("{\"t\":").Append(tick)
                 .Append(",\"nTicks\":").Append(ticks)
                 .Append(",\"stepMicros\":").Append((long)Math.Round(stepMs * 1000.0))
                 .Append(",\"inputMicros\":").Append((long)Math.Round(inputMs * 1000.0))
                 .Append(",\"gameplayMicros\":").Append((long)Math.Round(gameplayMs * 1000.0))
                 .Append(",\"snapshotMicros\":").Append((long)Math.Round(snapshotMs * 1000.0))
                 .Append(",\"frameMicros\":")
                 .Append((long)Math.Round(Time.unscaledDeltaTime * 1000000.0))
                 .Append(",\"phase\":").Append(_match == null ? 0 : (int)_match.Match.Phase)
                 .Append(",\"actors\":").Append(ServerActorRegistry.Instance.Count)
                 .Append(",\"vehicles\":").Append(ServerVehicleRegistry.Instance.Count)
                 .Append(",\"players\":").Append(_loop.PlayerCount)
                 .Append(",\"conns\":").Append(_connections.Count)
                 .Append(",\"entriesConsidered\":").Append(consideredDelta)
                 .Append(",\"entriesSent\":").Append(sentDelta)
                 .Append(",\"entriesHeld\":").Append(heldDelta)
                 .Append(",\"entriesCulled\":").Append(culledDelta)
                 .Append(",\"entriesShed\":").Append(shedDelta);

            AppendAudit();
            AppendConnectionBytes();

            // The one place this component can lie is by counting fewer connections than the
            // transport holds, which happens if a client connected before the loop existed and
            // so before the subscription. Saying so per record is cheaper than a run whose
            // bandwidth figure is quietly short by one client.
            if (_transport.ConnectionCount != _connections.Count)
            {
                _connectionMismatchRecords++;
                _line.Append(",\"connMismatch\":").Append(_transport.ConnectionCount);
            }

            _line.Append('}');

            _writer.WriteLine(_line.ToString());
            _records++;
            _ticksCovered += ticks;

            if (++_recordsSinceFlush >= FlushEveryRecords)
            {
                _writer.Flush();
                _recordsSinceFlush = 0;
            }
        }

        /// <summary>
        /// Appends <c>"audit":[…]</c> — the twelve counts a match reset is supposed to have
        /// emptied — and <c>"auditClean"</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why per record and not only at the reset.</b> The soak's risk is that its new
        /// fields read zero from pools that were never populated, and a counter that cannot rise
        /// cannot fall meaningfully (`green-that-proves-nothing.md`). Grading that needs the
        /// mid-round value as well as the post-reset one, so the sink writes it every record and
        /// the grader asserts non-zero DURING a round before asserting zero after it. Sampling
        /// only at the boundary would produce exactly the vacuous pass the risk names.
        /// </para>
        /// <para>
        /// <b>Positional, in the order below</b>, for the reason <c>perConn</c> is: twelve
        /// key names at 30 Hz is most of the file. The order is
        /// <c>[actorIdsInUse, actorIdsFree, actorIdsQuarantined, hitboxHistoryActors,
        /// interestPairs, spawnAckPairs, sessions, vehicleIdsInUse, vehicleIdsQuarantined,
        /// vehicleInterestPairs, vehiclesRegistered, mountedWeapons, turrets, projectileIds]</c>
        /// and <c>tools/grade-v9.py</c> names the same order in one place.
        /// </para>
        /// <para>
        /// <b><c>auditClean</c> is the loop's own predicate</b>, not a re-derivation: it is
        /// <c>IsCleanOfActorState</c>, the same one <c>MatchController</c> logs against, so a
        /// grader reading this file and a reader reading the log cannot reach different verdicts.
        /// </para>
        /// </remarks>
        private void AppendAudit() => AppendAuditOf(_loop.AuditState());

        /// <summary>The positional audit array, for a snapshot from either sampling point.</summary>
        private void AppendAuditOf(Ironfront.Net.Replication.Server.ServerStateSnapshot audit)
        {
            _line.Append(",\"audit\":[").Append(audit.ActorIdsInUse)
                 .Append(',').Append(audit.ActorIdsFree)
                 .Append(',').Append(audit.ActorIdsQuarantined)
                 .Append(',').Append(audit.HitboxHistoryActors)
                 .Append(',').Append(audit.InterestPairs)
                 .Append(',').Append(audit.SpawnAckPairs)
                 .Append(',').Append(audit.Sessions)
                 .Append(',').Append(audit.VehicleIdsInUse)
                 .Append(',').Append(audit.VehicleIdsQuarantined)
                 .Append(',').Append(audit.VehicleInterestPairs)
                 .Append(',').Append(audit.VehiclesRegistered)
                 .Append(',').Append(audit.MountedWeaponsTracked)
                 .Append(',').Append(audit.TurretsTracked)
                 .Append(',').Append(audit.ProjectileIdsInUse)
                 .Append("],\"auditClean\":").Append(audit.IsCleanOfActorState ? 1 : 0);
        }

        /// <summary>
        /// Appends <c>"perConn":[[id,sentDelta,recvDelta,rttMs],…]</c> and the run totals.
        /// </summary>
        /// <remarks>
        /// Per connection rather than only in aggregate because phase 4 grades <i>bandwidth per
        /// client</i>, and a mean over connections cannot answer that: one client standing in a
        /// crowd and one alone on the far side of the map are the two numbers worth having, and
        /// their average describes neither. Positional arrays rather than objects to keep a
        /// minutes-long run's JSONL readable at a glance and small on disk.
        /// </remarks>
        private void AppendConnectionBytes()
        {
            long sentTotal = 0, recvTotal = 0;

            _line.Append(",\"perConn\":[");
            for (int i = 0; i < _connections.Count; i++)
            {
                ushort id = _connections[i];
                ConnectionInfo info = _transport.GetInfo(id);

                _lastPerConnection.TryGetValue(id, out ConnectionBytes previous);
                long sentDelta = info.Stats.BytesSent - previous.Sent;
                long recvDelta = info.Stats.BytesReceived - previous.Received;
                _lastPerConnection[id] = new ConnectionBytes
                {
                    Sent = info.Stats.BytesSent,
                    Received = info.Stats.BytesReceived,
                };

                sentTotal += sentDelta;
                recvTotal += recvDelta;

                if (i > 0) _line.Append(',');
                _line.Append('[').Append(id).Append(',').Append(sentDelta).Append(',')
                     .Append(recvDelta).Append(',')
                     .Append(info.SmoothedRttMs.ToString("0.##", CultureInfo.InvariantCulture))
                     .Append(']');
            }

            _line.Append("],\"bytesSent\":").Append(sentTotal)
                 .Append(",\"bytesRecv\":").Append(recvTotal);
        }

        private void RecordHistogram(double stepMs)
        {
            _totalStepMs += stepMs;
            if (stepMs > _maxStepMs) _maxStepMs = stepMs;

            for (int i = 0; i < BucketEdgesMs.Length; i++)
            {
                if (stepMs <= BucketEdgesMs[i]) { _bucketCounts[i]++; return; }
            }

            _bucketCounts[BucketEdgesMs.Length]++;
        }

        private void OnApplicationQuit() => Close();

        private void OnDestroy() => Close();

        /// <summary>
        /// Flushes the JSONL, writes the summary, and unsubscribes. Safe to call twice.
        /// </summary>
        private void Close()
        {
            Application.logMessageReceived -= OnLogMessage;

            if (_errorWriter != null)
            {
                try { _errorWriter.Flush(); _errorWriter.Dispose(); }
                catch (Exception) { /* the tally in the summary is the evidence, not this file */ }
                _errorWriter = null;
            }

            if (_match != null)
            {
                _match.MatchResetCompleted -= OnMatchResetCompleted;
                _match = null;
            }

            if (_transport != null)
            {
                _transport.OnClientConnected -= OnClientConnected;
                _transport.OnClientDisconnected -= OnClientDisconnected;
                _transport = null;
            }

            if (_writer == null) return;

            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[load] closing '{_outputPath}' failed: {ex.Message}");
            }

            _writer = null;
            WriteSummary();
        }

        private void WriteSummary()
        {
            var summary = new StringBuilder(1024);
            summary.Append("{\n  \"seed\": ").Append(_seed)
                   .Append(",\n  \"seedVariable\": \"").Append(SeedVariable).Append('"')
                   .Append(",\n  \"records\": ").Append(_records)
                   .Append(",\n  \"ticksCovered\": ").Append(_ticksCovered)
                   .Append(",\n  \"resets\": ").Append(_resets)
                   .Append(",\n  \"connectionMismatchRecords\": ").Append(_connectionMismatchRecords)
                   .Append(",\n  \"stepMs\": {")
                   .Append("\"mean\": ")
                   .Append((_records == 0 ? 0.0 : _totalStepMs / _records)
                           .ToString("0.###", CultureInfo.InvariantCulture))
                   .Append(", \"max\": ")
                   .Append(_maxStepMs.ToString("0.###", CultureInfo.InvariantCulture))
                   .Append('}')
                   .Append(",\n  \"histogramMs\": [");

            for (int i = 0; i < _bucketCounts.Length; i++)
            {
                string edge = i < BucketEdgesMs.Length
                    ? BucketEdgesMs[i].ToString("0.#", CultureInfo.InvariantCulture)
                    : "Infinity";

                if (i > 0) summary.Append(',');
                summary.Append("\n    {\"leMs\": \"").Append(edge).Append("\", \"count\": ")
                       .Append(_bucketCounts[i]).Append('}');
            }

            // No percentile here on purpose. TickTimeStats keeps a 256-sample ring, so its p99
            // describes the last eight seconds rather than the run, and a summary that printed
            // one would be answering a different question from the one phase 4 asks. The exact
            // per-step microseconds are in the JSONL; that is what the run p99 is computed from.
            // The tally criterion 11 is graded on, by LogType, every type printed even at
            // zero. An absent line reads as "not measured"; "Exception: 0" reads as measured and
            // clean, and those must never look alike -- the same rule run-lane-a.ps1 states for
            // its own two named types.
            summary.Append("\n  ],\n  \"logByType\": {")
                   .Append("\"Error\": ").Append(_logCounts[(int)LogType.Error])
                   .Append(", \"Assert\": ").Append(_logCounts[(int)LogType.Assert])
                   .Append(", \"Warning\": ").Append(_logCounts[(int)LogType.Warning])
                   .Append(", \"Log\": ").Append(_logCounts[(int)LogType.Log])
                   .Append(", \"Exception\": ").Append(_logCounts[(int)LogType.Exception])
                   .Append('}')
                   .Append(",\n  \"errorEntriesWritten\": ").Append(_errorEntriesWritten)
                   .Append(",\n  \"errorRecordCap\": ").Append(MaxErrorRecords)
                   .Append(",\n  \"errorsPath\": \"")
                   .Append(_errorsPath == null ? "" : _errorsPath.Replace("\\", "/"))
                   .Append('"')
                   .Append(",\n  \"note\": ")
                   .Append("\"stepMicros per record is the p99 input; the histogram is indicative\"")
                   .Append("\n}\n");

            try
            {
                File.WriteAllText(_summaryPath, summary.ToString(), Utf8NoBom);
                Debug.Log($"[load] {_records} records, summary at {_summaryPath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[load] could not write '{_summaryPath}': {ex.Message}");
            }
        }
    }
}
