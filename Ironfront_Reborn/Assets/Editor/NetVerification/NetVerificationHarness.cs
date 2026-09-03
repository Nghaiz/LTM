using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Transport;
using Ironfront.Net.Unity;
using Ironfront.Net.Unity.Client;
using Ironfront.Net.Unity.Server;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Editor.Verification
{
    /// <summary>
    /// Drives one Editor play session hard enough to observe the eight Unity-side fixes from
    /// #81/#82 (checklist rows E1-E8) and to time the bot LOD comparison (S5/A9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Verification scaffolding, not shipping code — it lives under
    /// <c>Assets/Editor/</c> so it compiles into <c>Assembly-CSharp-Editor</c> and cannot reach a
    /// player build.
    /// </para>
    /// <para>
    /// <b>Why a file rather than a sequence of injected scripts.</b> Every MCP <c>script-execute</c>
    /// call compiles a fresh Roslyn assembly, so static state does not survive between calls and a
    /// synthetic client created in one call is unreachable from the next. Real statics in a real
    /// assembly do survive, which is the only way to connect a second client in one call and read
    /// its counters in another.
    /// </para>
    /// <para>
    /// <b>Nothing here is saved to an asset.</b> Every mutation (the transport override, the
    /// prediction clock, the player-slot flag, the LOD mode) is applied to the live play-mode
    /// instance and dies with the session. That is deliberate: the scene on disk is the artefact
    /// under test, and a harness that edited it would be measuring itself.
    /// </para>
    /// </remarks>
    public static class NetVerificationHarness
    {
        private const string TransportVar = "IRONFRONT_GAMESERVER_TRANSPORT";
        private const int SendHz = 30;

        // ---------------------------------------------------------------- local player scripting

        private static float _localYaw;
        private static float _localMoveZ;
        private static bool _localScripted;

        // ------------------------------------------------------------------- synthetic 2nd client

        private static ITransportClient _bot;
        private static ClientMessageRouter _botRouter;
        private static bool _pumpHooked;
        private static double _nextSend;
        private static uint _botTick;
        private static float _botYaw;
        private static bool _botFire;
        private static ushort _botActorId;
        private static ushort _botConnectionId;
        private static long _botSpawns, _botDespawns, _botDeaths, _botFires, _botHits;
        private static string _botLastDisconnect = "-";

        private static readonly List<InputFrame> _pending = new List<InputFrame>(ClientInputMessage.MaxFrames);
        private static readonly InputFrame[] _scratch = new InputFrame[ClientInputMessage.MaxFrames];
        private static readonly byte[] _body =
            new byte[ClientInputMessage.HeaderSize + ClientInputMessage.MaxFrames * InputFrame.Size];
        private static readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];

        // ------------------------------------------------------------------------- tick sampling

        private static readonly List<double> _samples = new List<double>(4096);
        private static bool _samplingHooked;
        private static uint _lastSampledTick;

        // =====================================================================================
        // Step 1 — make the two ends able to meet at all.
        // =====================================================================================

        /// <summary>
        /// Points the server at UDP for the next play session.
        /// </summary>
        /// <remarks>
        /// The committed scene has <c>NetServerBootstrap._useLoopbackTransport = true</c> while
        /// <c>NetClientBootstrap</c> unconditionally dials <c>UdpTransportClient</c> at
        /// <c>127.0.0.1:27015</c>, and nothing in <c>Assets/</c> ever assigns
        /// <c>ExternalTransport</c>. So the two ends never meet and the server runs with zero
        /// sessions — which is why every counter row E3/E4/E7 asks about had nothing to count.
        /// <c>GameServerConfig.ApplyEnvironment()</c> reads this variable out of the process, so
        /// setting it here changes the next play session without touching the scene.
        /// </remarks>
        public static string Prepare()
        {
            Environment.SetEnvironmentVariable(TransportVar, "udp");
            return $"{TransportVar}={Environment.GetEnvironmentVariable(TransportVar)}";
        }

        /// <summary>Clears the override so the Editor goes back to the scene's own value.</summary>
        public static string Unprepare()
        {
            Environment.SetEnvironmentVariable(TransportVar, null);
            return $"{TransportVar}=<unset>";
        }

        // =====================================================================================
        // Step 2 — give the local client a hand on the controls.
        // =====================================================================================

        /// <summary>
        /// Enables the local player's prediction clock and replaces its input with a scripted
        /// sweep, so the session actually sends <c>C_INPUT</c>.
        /// </summary>
        /// <remarks>
        /// <c>NetPredictionClock</c> ships disabled (checklist A4 requires that), and while it is
        /// disabled its <c>Update</c> never runs, so <c>OnTickSimulated</c> never fires and
        /// <c>ClientPredictionStage</c> has nothing to send. An honest client that sends nothing
        /// cannot demonstrate E2, E4 or E6.
        /// </remarks>
        public static string ScriptLocalInput(float degreesPerSecond, float moveZ)
        {
            NetPredictionClock clock = NetPredictionClock.Current;
            if (clock == null)
            {
                // Current is only set in OnEnable, so a disabled clock is invisible to it.
                clock = UnityEngine.Object.FindAnyObjectByType<NetPredictionClock>(
                    FindObjectsInactive.Include);
            }

            if (clock == null) return "no NetPredictionClock in the scene";

            _localMoveZ = moveZ;
            _localScripted = true;

            float step = degreesPerSecond / SendHz;
            clock.InputSource = () =>
            {
                _localYaw = Mathf.Repeat(_localYaw + step, 360f);
                return new MoveInput(0f, _localMoveZ, _localYaw, false, false, false);
            };

            bool wasEnabled = clock.enabled;
            clock.enabled = true;

            return $"clock on '{clock.name}' (was enabled={wasEnabled}), "
                   + $"yaw sweep {degreesPerSecond} deg/s, moveZ={moveZ}";
        }

        // =====================================================================================
        // Step 3 — retired. The server builds its own player slots.
        // =====================================================================================
        //
        // OpenSecondSlot() re-badged a live bot by reflecting NetServerActor._availableForPlayers
        // and disabling whatever component was named "AiActorController", because exactly one
        // body in the project was claimable and connection two was refused ServerFull. Phase-3A
        // moved both halves into shipping code: ServerPlayerSlotPool builds Config.MaxConnections
        // claimable bodies at server start, and NetServerActor.Claim() suspends the bot brain
        // through the typed IAiDriver seam. A harness that opened a slot by hand would now be
        // measuring itself against a server that already has sixteen.

        // =====================================================================================
        // Step 4 — the synthetic client.
        // =====================================================================================

        /// <summary>
        /// Connects a second client over the real UDP wire and starts feeding it input.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is not a second <c>NetClientBootstrap</c>: it writes <c>C_INPUT</c> itself so it
        /// can set button bits the shipping client structurally cannot. <c>MoveInput</c> carries
        /// only MoveX/MoveZ/Yaw/Jump/Sprint/Crouch and <c>ClientPredictionStage.ToFrame</c> maps
        /// only those three booleans, so no real client has ever set
        /// <see cref="InputButtons.Fire"/> — and E3's occlusion counter cannot move without it.
        /// </para>
        /// <para>
        /// Pumped from <see cref="EditorApplication.update"/> rather than from a MonoBehaviour so
        /// it survives a domain-safe play session without adding a component to the scene.
        /// </para>
        /// </remarks>
        public static string StartBot(string host, int port, float degreesPerSecond, bool fire)
        {
            if (_bot != null) return "bot already running; call StopBot first";

            _botTick = 0;
            _botYaw = 0f;
            _botFire = fire;
            _botActorId = 0;
            _botConnectionId = 0;
            _botSpawns = _botDespawns = _botDeaths = _botFires = _botHits = 0;
            _botLastDisconnect = "-";
            _pending.Clear();

            _botRouter = new ClientMessageRouter();
            _botRouter.OnSpawnActor += OnBotSpawn;
            _botRouter.OnDespawnActor += _ => _botDespawns++;
            _botRouter.OnDeath += _ => _botDeaths++;
            _botRouter.OnWeaponFire += _ => _botFires++;
            _botRouter.OnHitConfirm += _ => _botHits++;

            var udp = new UdpTransportClient();
            _bot = udp;
            _bot.OnMessage += OnBotMessage;
            _bot.OnConnected += OnBotConnected;
            _bot.OnDisconnected += OnBotDisconnected;

            _degreesPerSecond = degreesPerSecond;
            _bot.Connect(host, port, PendingJoin.CreateUnsignedTicket());

            if (!_pumpHooked)
            {
                EditorApplication.update += Pump;
                _pumpHooked = true;
            }

            return $"bot dialling {host}:{port}, yaw {degreesPerSecond} deg/s, fire={fire}";
        }

        private static float _degreesPerSecond = 90f;

        /// <summary>Drops the bot's link without tearing the pump down — checklist row E5.</summary>
        public static string DropBot()
        {
            if (_bot == null) return "no bot";
            _bot.Disconnect();
            return "bot disconnected";
        }

        /// <summary>Redials with the same object, so the server sees a rejoin.</summary>
        public static string RejoinBot(string host, int port)
        {
            if (_bot == null) return "no bot";
            _pending.Clear();
            _botTick = 0;
            _bot.Connect(host, port, PendingJoin.CreateUnsignedTicket());
            return $"bot redialling {host}:{port}";
        }

        public static string StopBot()
        {
            if (_pumpHooked)
            {
                EditorApplication.update -= Pump;
                _pumpHooked = false;
            }

            if (_bot == null) return "no bot";

            _bot.OnMessage -= OnBotMessage;
            _bot.OnConnected -= OnBotConnected;
            _bot.OnDisconnected -= OnBotDisconnected;
            _bot.Disconnect();

            var disposable = _bot as IDisposable;
            if (disposable != null) disposable.Dispose();

            _bot = null;
            _botRouter = null;
            return "bot stopped";
        }

        /// <summary>Holds or releases the fire button on the synthetic client.</summary>
        public static string SetBotFire(bool fire)
        {
            _botFire = fire;
            return $"bot fire={fire}";
        }

        private static void OnBotSpawn(SpawnActorMessage message)
        {
            _botSpawns++;
            if (message.IsLocalPlayer) _botActorId = message.ActorId;
        }

        private static void OnBotMessage(ReadOnlyMemory<byte> payload)
        {
            if (_botRouter != null) _botRouter.Route(payload.Span);
        }

        private static void OnBotConnected(ConnectResult result)
        {
            _botConnectionId = result.ConnectionId;
            _botTick = result.ServerTick;
        }

        private static void OnBotDisconnected(DisconnectReason reason)
        {
            _botLastDisconnect = reason.ToString();
            _botConnectionId = 0;
            _botActorId = 0;
        }

        private static void Pump()
        {
            if (_bot == null) return;

            _bot.Poll();

            if (_bot.State != ConnectionState.Connected) return;

            double now = EditorApplication.timeSinceStartup;
            if (now < _nextSend) return;
            _nextSend = now + 1.0 / SendHz;

            _botYaw = Mathf.Repeat(_botYaw + _degreesPerSecond / SendHz, 360f);
            _botTick = unchecked(_botTick + 1);

            InputButtons buttons = _botFire ? InputButtons.Fire : InputButtons.None;
            InputFrame frame = InputFrame.FromFloats(0f, 0f, _botYaw, 0f, buttons);

            if (_pending.Count == ClientInputMessage.MaxFrames) _pending.RemoveAt(0);
            _pending.Add(frame);

            uint firstTick = unchecked(_botTick - (uint)(_pending.Count - 1));
            for (int i = 0; i < _pending.Count; i++) _scratch[i] = _pending[i];

            int bodyLength = ClientInputMessage.Write(
                _body, firstTick, new ReadOnlySpan<InputFrame>(_scratch, 0, _pending.Count));
            if (bodyLength < 0) return;

            var writer = new PayloadFrameWriter(_payload, ChannelId.InputSequenced);
            if (!writer.WriteMessage(
                    ClientMessageType.Input, new ReadOnlySpan<byte>(_body, 0, bodyLength))) return;
            if (!writer.TryFinish(out int total)) return;

            _bot.Send(
                (byte)ChannelId.InputSequenced, new ReadOnlySpan<byte>(_payload, 0, total), false);
        }

        // =====================================================================================
        // Step 5 — kill something, so respawn has a reason to run.
        // =====================================================================================

        /// <summary>
        /// Routes a death through the server's own single death path — checklist row E6's setup.
        /// </summary>
        /// <remarks>
        /// The client never sends <c>C_SPAWN_REQUEST</c> (nothing in <c>Assets/</c> writes any
        /// client message but <c>C_INPUT</c>), so a death has to be staged from this side and the
        /// respawn asked for over the wire by <see cref="RequestSpawn"/>.
        /// </remarks>
        public static string KillActor(int victimActorId, int killerActorId)
        {
            ServerTickLoop loop = ServerTickLoop.Current;
            if (loop == null) return "no ServerTickLoop";

            Vector3 before = Vector3.zero;
            NetServerActor victim;
            if (ServerActorRegistry.Instance.TryFind((ushort)victimActorId, out victim))
                before = victim.transform.position;

            loop.EmitDeath(
                (ushort)victimActorId,
                (ushort)killerActorId,
                new Vec3(0f, 0f, 0f),
                (byte)0,
                CauseOfDeath.Bullet);

            return $"killed {victimActorId} at {Fmt(before)}";
        }

        /// <summary>
        /// Sends <c>C_SPAWN_REQUEST</c> from a connected client, which nothing in the game does.
        /// </summary>
        public static string RequestSpawn(bool fromBot)
        {
            // V8, ledger X-11: the body is no longer empty -- ServerMessageRouter now requires
            // SpawnRequestMessage.Size bytes and fails a shorter packet as malformed rather than
            // silently accepting it. This probe asks for no particular loadout (all zero, "left
            // empty") and no particular spawn point (NoSpawnPointPreference, the constructor's
            // own default): it exists to prove the request reaches the server and is granted, not
            // to arm a specific weapon.
            //
            // A heap array rather than a `stackalloc`, for the compiler's reason and not a
            // stylistic one: a stack-allocated span's ref-safe-to-escape scope is narrower than
            // the PayloadFrameWriter ref struct it would be passed to, which is CS8350 and
            // CS8352. NetClientLocalCombatDriver, BaselineAckPolicy and ClientPredictionStage all
            // send their bodies from an array for the same reason.
            var spawnRequest = new SpawnRequestMessage(0, 0, 0, 0, 0);
            byte[] body = new byte[SpawnRequestMessage.Size];
            if (spawnRequest.Write(body) < 0) return "could not encode C_SPAWN_REQUEST";

            var writer = new PayloadFrameWriter(_payload, ChannelId.ReliableOrdered);
            if (!writer.WriteMessage(ClientMessageType.SpawnRequest, new ReadOnlySpan<byte>(body)))
                return "could not frame C_SPAWN_REQUEST";
            if (!writer.TryFinish(out int total)) return "could not finish C_SPAWN_REQUEST";

            var span = new ReadOnlySpan<byte>(_payload, 0, total);

            if (fromBot)
            {
                if (_bot == null || _bot.State != ConnectionState.Connected) return "bot not connected";
                _bot.Send((byte)ChannelId.ReliableOrdered, span, true);
                return "bot sent C_SPAWN_REQUEST";
            }

            NetClientBootstrap client = NetClientBootstrap.Current;
            if (client == null || !client.IsConnected) return "local client not connected";

            client.Send(ChannelId.ReliableOrdered, span, true);
            return "local client sent C_SPAWN_REQUEST";
        }

        // =====================================================================================
        // Step 6 — the bot LOD comparison (S5 / A9).
        // =====================================================================================

        /// <summary>Pins every <c>BotLodGate</c> in the scene to one mode.</summary>
        public static string SetBotLod(string mode)
        {
            BotLodMode parsed;
            if (!Enum.TryParse(mode, true, out parsed)) return $"unknown BotLodMode '{mode}'";

            BotLodGate[] gates = UnityEngine.Object.FindObjectsByType<BotLodGate>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            for (int i = 0; i < gates.Length; i++) gates[i].Mode = parsed;

            return $"{gates.Length} gate(s) set to {parsed}";
        }

        /// <summary>Starts collecting one server-tick duration per tick.</summary>
        public static string StartSampling()
        {
            _samples.Clear();
            _lastSampledTick = 0;

            if (!_samplingHooked)
            {
                EditorApplication.update += Sample;
                _samplingHooked = true;
            }

            return "sampling";
        }

        public static string StopSampling()
        {
            if (_samplingHooked)
            {
                EditorApplication.update -= Sample;
                _samplingHooked = false;
            }

            return Percentiles();
        }

        private static void Sample()
        {
            ServerTickLoop loop = ServerTickLoop.Current;
            if (loop == null) return;

            uint tick = loop.CurrentTick;
            if (tick == _lastSampledTick) return;
            _lastSampledTick = tick;

            object stats = Reflect(loop.Scheduler, "TickTimes");
            object last = stats != null ? Reflect(stats, "LastMs") : null;
            if (last is double) _samples.Add((double)last);
            else if (last is float) _samples.Add((float)last);
        }

        /// <summary>p50/p99/max over the collected samples, plus the count behind them.</summary>
        public static string Percentiles()
        {
            if (_samples.Count == 0) return "{\"samples\":0}";

            var sorted = new List<double>(_samples);
            sorted.Sort();

            return "{\"samples\":" + sorted.Count
                   + ",\"p50\":" + N(sorted[sorted.Count / 2])
                   + ",\"p99\":" + N(sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.99))])
                   + ",\"max\":" + N(sorted[sorted.Count - 1]) + "}";
        }

        // =====================================================================================
        // Step 7 — read everything back.
        // =====================================================================================

        /// <summary>One JSON object holding every number the E rows and S5 ask about.</summary>
        public static string Report()
        {
            var sb = new StringBuilder(4096);
            sb.Append('{');

            sb.Append("\"playing\":").Append(EditorApplication.isPlaying ? "true" : "false");
            sb.Append(",\"transportVar\":\"")
              .Append(Environment.GetEnvironmentVariable(TransportVar) ?? "<unset>").Append('"');

            ServerTickLoop loop = ServerTickLoop.Current;
            if (loop == null)
            {
                sb.Append(",\"server\":null}");
                return sb.ToString();
            }

            sb.Append(",\"serverTick\":").Append(loop.CurrentTick);
            sb.Append(",\"playerCount\":").Append(loop.PlayerCount);
            sb.Append(",\"transport\":\"")
              .Append(loop.Transport != null ? loop.Transport.GetType().Name : "none").Append('"');

            // E3 / E7 — lag compensation.
            sb.Append(",\"shotsOccluded\":").Append(loop.LagCompensator.ShotsOccluded);
            sb.Append(",\"presentFallbacks\":").Append(loop.LagCompensator.PresentFallbacks);
            sb.Append(",\"occlusionAssigned\":")
              .Append(Reflect(loop.LagCompensator, "Occlusion") != null ? "true" : "false");

            // E8 — the audit line carries "ids in-use", which was structurally zero before.
            sb.Append(",\"audit\":\"").Append(Escape(loop.AuditState().ToString())).Append('"');

            // Scheduler diagnostics, whatever it exposes.
            sb.Append(",\"scheduler\":").Append(DumpNumbers(loop.Scheduler));
            sb.Append(",\"tickTimes\":").Append(DumpNumbers(Reflect(loop.Scheduler, "TickTimes")));
            sb.Append(",\"botLod\":").Append(DumpNumbers(loop.BotLod));
            sb.Append(",\"router\":").Append(DumpNumbers(loop.Router));

            // E4 — per-session anti-cheat counters.
            sb.Append(",\"sessions\":[");
            IList players = Reflect(loop, "_players") as IList;
            if (players != null)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    object player = players[i];
                    if (player == null) continue;
                    if (i > 0) sb.Append(',');

                    var session = Reflect(player, "Session") as ClientSession;
                    var actor = Reflect(player, "Actor") as NetServerActor;

                    sb.Append('{');
                    if (session != null)
                    {
                        sb.Append("\"connectionId\":").Append(session.ConnectionId);
                        sb.Append(",\"actorId\":").Append(session.ActorId);
                        sb.Append(",\"speedViolations\":").Append(session.SpeedViolations);
                        sb.Append(",\"inputThrottleEvents\":").Append(session.InputThrottleEvents);
                        sb.Append(",\"inputBudget\":").Append(session.InputBudget);
                        sb.Append(",\"lastProcessedInputTick\":")
                          .Append(Num(Reflect(session, "LastProcessedInputTick")));
                        sb.Append(",\"pendingInputCount\":")
                          .Append(Num(Reflect(session, "PendingInputCount")));
                    }

                    if (actor != null)
                    {
                        sb.Append(",\"actor\":").Append(ActorJson(actor));
                    }
                    else
                    {
                        sb.Append(",\"actor\":null");
                    }

                    sb.Append('}');
                }
            }

            sb.Append(']');

            // E1 / E2 / E5 — every actor a player could be holding.
            sb.Append(",\"playerSlots\":[");
            IReadOnlyList<NetServerActor> actors = ServerActorRegistry.Instance.Actors;
            bool first = true;
            for (int i = 0; i < actors.Count; i++)
            {
                NetServerActor actor = actors[i];
                if (actor == null || !actor.AvailableForPlayers) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(ActorJson(actor));
            }

            sb.Append(']');
            sb.Append(",\"actorTotal\":").Append(actors.Count);

            // What the real client decoded — the other half of E1 and E2.
            NetClientBootstrap client = NetClientBootstrap.Current;
            sb.Append(",\"client\":");
            if (client == null)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append('{');
                sb.Append("\"connected\":").Append(client.IsConnected ? "true" : "false");
                sb.Append(",\"connectionId\":").Append(client.ConnectionId);
                sb.Append(",\"localActorId\":").Append(client.LocalActorId);
                sb.Append(",\"snapshotsApplied\":").Append(client.Router.SnapshotsApplied);
                sb.Append(",\"unknownBaselines\":").Append(client.Router.UnknownBaselines);
                sb.Append(",\"malformed\":").Append(client.Router.MalformedMessages);
                sb.Append(",\"corrections\":").Append(client.Reconciler.CorrectionCount);
                sb.Append(",\"decoded\":").Append(DecodedJson(client, _botActorId));
                sb.Append('}');
            }

            // The synthetic client.
            sb.Append(",\"bot\":");
            if (_bot == null)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append('{');
                sb.Append("\"state\":\"").Append(_bot.State).Append('"');
                sb.Append(",\"connectionId\":").Append(_botConnectionId);
                sb.Append(",\"actorId\":").Append(_botActorId);
                sb.Append(",\"inputTick\":").Append(_botTick);
                sb.Append(",\"yaw\":").Append(N(_botYaw));
                sb.Append(",\"fire\":").Append(_botFire ? "true" : "false");
                sb.Append(",\"spawns\":").Append(_botSpawns);
                sb.Append(",\"despawns\":").Append(_botDespawns);
                sb.Append(",\"deaths\":").Append(_botDeaths);
                sb.Append(",\"weaponFires\":").Append(_botFires);
                sb.Append(",\"hitConfirms\":").Append(_botHits);
                sb.Append(",\"lastDisconnect\":\"").Append(_botLastDisconnect).Append('"');
                sb.Append(",\"snapshotsApplied\":")
                  .Append(_botRouter != null ? _botRouter.SnapshotsApplied : 0);
                sb.Append('}');
            }

            // E6 needs somewhere to respawn to.
            //
            // Read through ISpawnPointDirectory rather than ActorManager.instance.spawnPoints
            // (phase C5b). This harness moved out of Assembly-CSharp-Editor into an assembly that
            // can name the client, and an assembly cannot name ActorManager — so the count comes
            // through the seam the server assembly already declares for exactly this data, and
            // which ActorManagerSpawnPoints answers from that same array.
            //
            // ONE SENTINEL CHANGED, deliberately and harmlessly: the old expression reported -1
            // when ActorManager or its array was absent, the directory reports 0. Both render as
            // "E6 has nowhere to respawn"; -1 survives here only for "nothing registered a
            // directory at all", which is a different fact and worth keeping distinct.
            ISpawnPointDirectory spawnPoints = NetServerBindings.SpawnPoints;
            sb.Append(",\"spawnPoints\":").Append(spawnPoints != null ? spawnPoints.Count : -1);

            sb.Append(",\"localScripted\":").Append(_localScripted ? "true" : "false");
            sb.Append(",\"localYaw\":").Append(N(_localYaw));
            sb.Append('}');
            return sb.ToString();
        }

        private static string ActorJson(NetServerActor actor)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"name\":\"").Append(Escape(actor.name)).Append('"');
            sb.Append(",\"id\":").Append(actor.ActorId);
            sb.Append(",\"team\":").Append(actor.Team);
            sb.Append(",\"claimed\":").Append(actor.IsClaimed ? "true" : "false");
            sb.Append(",\"alive\":").Append(actor.IsAlive ? "true" : "false");
            sb.Append(",\"health\":").Append(N(actor.Health));
            sb.Append(",\"weaponId\":").Append(actor.WeaponId);
            sb.Append(",\"ammoInClip\":").Append(actor.AmmoInClip);
            sb.Append(",\"yawDegrees\":").Append(N(actor.YawDegrees));
            sb.Append(",\"transformYaw\":").Append(N(actor.transform.eulerAngles.y));
            sb.Append(",\"pos\":\"").Append(Fmt(actor.transform.position)).Append('"');
            sb.Append(",\"hasMovement\":").Append(actor.Movement != null ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }

        private static string DecodedJson(NetClientBootstrap client, ushort extraActorId)
        {
            var sb = new StringBuilder(256);
            sb.Append('[');

            bool first = true;
            AppendDecoded(sb, client, client.LocalActorId, ref first);
            if (extraActorId != 0 && extraActorId != client.LocalActorId)
                AppendDecoded(sb, client, extraActorId, ref first);

            sb.Append(']');
            return sb.ToString();
        }

        private static void AppendDecoded(
            StringBuilder sb, NetClientBootstrap client, ushort actorId, ref bool first)
        {
            if (actorId == 0) return;

            ActorSnapshotEntry entry;
            if (!client.Router.Decoder.Current.TryFind(actorId, out entry)) return;

            if (!first) sb.Append(',');
            first = false;

            sb.Append('{');
            sb.Append("\"id\":").Append(entry.ActorId);
            sb.Append(",\"weaponId\":").Append(entry.WeaponId);
            sb.Append(",\"ammoInClip\":").Append(entry.AmmoInClip);
            sb.Append(",\"health\":").Append(entry.Health);
            sb.Append(",\"yawRaw\":").Append(entry.Yaw);
            sb.Append(",\"yawDegrees\":").Append(N(entry.Yaw * 360f / 65536f));
            sb.Append(",\"stateFlags\":\"").Append(entry.StateFlags).Append('"');
            sb.Append(",\"team\":").Append(entry.Team);
            sb.Append('}');
        }

        // =====================================================================================
        // Reflection and formatting helpers.
        // =====================================================================================

        private static object Reflect(object target, string member)
        {
            if (target == null) return null;

            Type type = target.GetType();
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public
                                                             | BindingFlags.NonPublic;

            for (Type t = type; t != null; t = t.BaseType)
            {
                PropertyInfo property = t.GetProperty(member, Flags);
                if (property != null && property.CanRead) return property.GetValue(target);

                FieldInfo field = t.GetField(member, Flags);
                if (field != null) return field.GetValue(target);
            }

            return null;
        }

        /// <summary>
        /// Every readable numeric public property on an object, as a JSON object.
        /// </summary>
        /// <remarks>
        /// Used instead of naming the counters: the diagnostic surfaces on
        /// <c>ServerTickScheduler</c>, <c>TickTimeStats</c> and <c>BotLodScheduler</c> are the replication track's
        /// and change between rounds, and a harness that hardcoded their names would stop
        /// compiling for a reason that has nothing to do with the thing being measured.
        /// </remarks>
        private static string DumpNumbers(object target)
        {
            if (target == null) return "null";

            var sb = new StringBuilder(256);
            sb.Append('{');

            PropertyInfo[] properties = target.GetType().GetProperties(
                BindingFlags.Instance | BindingFlags.Public);

            bool first = true;
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;

                Type t = property.PropertyType;
                bool numeric = t == typeof(int) || t == typeof(uint) || t == typeof(long)
                               || t == typeof(ulong) || t == typeof(short) || t == typeof(ushort)
                               || t == typeof(byte) || t == typeof(sbyte) || t == typeof(float)
                               || t == typeof(double) || t == typeof(bool) || t.IsEnum;
                if (!numeric) continue;

                object value;
                try { value = property.GetValue(target); }
                catch (Exception) { continue; }

                if (!first) sb.Append(',');
                first = false;

                sb.Append('"').Append(property.Name).Append("\":");
                if (t == typeof(bool)) sb.Append((bool)value ? "true" : "false");
                else if (t.IsEnum) sb.Append('"').Append(value).Append('"');
                else if (t == typeof(float)) sb.Append(N((float)value));
                else if (t == typeof(double)) sb.Append(N((double)value));
                else sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            }

            sb.Append('}');
            return sb.ToString();
        }

        private static string N(double value)
        {
            if (double.IsNaN(value)) return "\"NaN\"";
            if (double.IsInfinity(value)) return "\"Inf\"";
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>A reflected value as a JSON number, or <c>null</c> when it was not one.</summary>
        private static string Num(object value)
        {
            if (value == null) return "null";
            if (value is float) return N((float)value);
            if (value is double) return N((double)value);
            if (value is bool) return (bool)value ? "true" : "false";

            try { return Convert.ToString(value, CultureInfo.InvariantCulture); }
            catch (Exception) { return "null"; }
        }

        private static string Fmt(Vector3 v) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.##},{1:0.##},{2:0.##}", v.x, v.y, v.z);

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                        .Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        }
    }
}
