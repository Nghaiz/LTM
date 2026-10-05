// Diagnostics are compiled OUT of a shipping client build. See LaneBAllocationSampler.cs for why
// the define is inverted.
#if !IRONFRONT_NO_DIAGNOSTICS
using System;
using System.Reflection;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client;
using Ironfront.Net.Unity.Client.Menu;
using UnityEngine;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// Walks a client through the menus into a live match with no mouse or keyboard, for measuring
    /// runs (P31): logs in, creates or joins a room, readies up, and presses Deploy whenever the
    /// loadout screen is up, so a respawn deploys again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> A 100-bot measurement needs two clients in one room, and driving two menus by
    /// screen clicks fights the owner's own desktop for the foreground: a click lands on whatever
    /// window is on top, and a Unity window ignores posted background input. The menus' own
    /// <see cref="MenuScreenController"/> methods are what the buttons call, so calling them is the
    /// same flow with the same checks, minus the pointer.
    /// </para>
    /// <para>
    /// <c>IRONFRONT_AUTOPLAY=user:password</c> turns it on. Then either
    /// <c>IRONFRONT_AUTOPLAY_CREATE=name;mapId;botsPerTeam[;night]</c> creates that room, or
    /// <c>IRONFRONT_AUTOPLAY_JOIN=name</c> waits for a room of that name and joins it.
    /// </para>
    /// <para>
    /// <c>IRONFRONT_AUTOPLAY_UNCAPPED=1</c> runs the match as a focused window would, with no
    /// v-sync and no frame cap, though the window is in the background: the measuring client of
    /// an A/B must not sit under <c>BackgroundFrameCap</c>'s 30 fps, and bringing it to the front
    /// would take the owner's desktop away from them. Menus and loading keep their caps.
    /// </para>
    /// </remarks>
    public sealed class MenuAutopilot : MonoBehaviour
    {
        private const float StepSeconds = 1f;
        private const float DeployDelaySeconds = 2f;
        private const byte MaxPlayers = 4;

        private string _user = string.Empty;
        private string _password = string.Empty;
        private string _createName = string.Empty;
        private ushort _createMap;
        private byte _createBots;
        private bool _createNight;
        private string _joinName = string.Empty;
        private bool _uncapped;

        private float _nextStep;
        private GameFlowState _lastState = (GameFlowState)(-1);
        private float _stateSince;
        private bool _actedInState;
        private float _loadoutSeenAt = -1f;

        private static Type _loadoutUi;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallIfRequested()
        {
            string login = Environment.GetEnvironmentVariable("IRONFRONT_AUTOPLAY");
            if (string.IsNullOrEmpty(login)) return;

            int colon = login.IndexOf(':');
            if (colon <= 0)
            {
                Debug.LogError("[autoplay] IRONFRONT_AUTOPLAY must be user:password; nothing will be driven.");
                return;
            }

            // Built in code like FrameTimeLog's host: a diagnostics tool that exists only when the
            // env var asks, with no scene or prefab of its own to be authored on.
            var host = new GameObject("[MenuAutopilot]");
            DontDestroyOnLoad(host);
            MenuAutopilot pilot = host.AddComponent<MenuAutopilot>();
            pilot._user = login.Substring(0, colon);
            pilot._password = login.Substring(colon + 1);

            string create = Environment.GetEnvironmentVariable("IRONFRONT_AUTOPLAY_CREATE");
            if (!string.IsNullOrEmpty(create))
            {
                string[] parts = create.Split(';');
                pilot._createName = parts[0];
                pilot._createMap = parts.Length > 1 && ushort.TryParse(parts[1], out ushort map) ? map : RoomRules.NightModeMapId;
                pilot._createBots = parts.Length > 2 && byte.TryParse(parts[2], out byte bots) ? bots : (byte)0;
                pilot._createNight = parts.Length > 3 && parts[3] == "night";
            }
            pilot._joinName = Environment.GetEnvironmentVariable("IRONFRONT_AUTOPLAY_JOIN") ?? string.Empty;
            pilot._uncapped = Environment.GetEnvironmentVariable("IRONFRONT_AUTOPLAY_UNCAPPED") == "1";

            Debug.Log($"[autoplay] driving the menus as {pilot._user}: "
                      + (pilot._createName.Length > 0
                          ? $"create '{pilot._createName}' map {pilot._createMap}, {pilot._createBots} bots a side{(pilot._createNight ? ", night" : "")}"
                          : $"join '{pilot._joinName}'"));
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextStep) return;
            _nextStep = Time.unscaledTime + StepSeconds;

            ClientFlowBootstrap flow = ClientFlowBootstrap.Current;
            GameFlowState state = flow != null && flow.Flow != null ? flow.Flow.State : GameFlowState.InMatch;
            if (state != _lastState)
            {
                Debug.Log($"[autoplay] {state}");
                _lastState = state;
                _stateSince = Time.unscaledTime;
                _actedInState = false;
            }

            if (state == GameFlowState.InMatch || flow == null)
            {
                DeployWhenLoadoutIsUp();
                if (_uncapped && flow != null && (QualitySettings.vSyncCount != 0 || Application.targetFrameRate != -1))
                {
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = -1;
                    Debug.Log("[autoplay] uncapped: no v-sync, no frame cap");
                }
                return;
            }

            MenuScreenController menu = FindAnyObjectByType<MenuScreenController>(FindObjectsInactive.Include);
            if (menu == null) return;

            // Re-act in a state that does not move on, as a player would press again.
            bool due = !_actedInState || Time.unscaledTime - _stateSince > 10f;
            if (!due) return;
            _actedInState = true;
            _stateSince = Time.unscaledTime;

            switch (state)
            {
                case GameFlowState.Booting:
                    menu.GoToMultiplayer();
                    break;
                case GameFlowState.LoginScreen:
                    menu.SubmitLogin(_user, _password);
                    break;
                case GameFlowState.Lobby:
                    menu.OpenRoomBrowser();
                    break;
                case GameFlowState.RoomBrowser:
                    BrowseRooms(menu);
                    break;
                case GameFlowState.RoomLobby:
                    menu.SetReady(true);
                    break;
            }
        }

        private void BrowseRooms(MenuScreenController menu)
        {
            if (_createName.Length > 0)
            {
                RoomSettings settings = _createNight
                    ? new RoomSettings(GameMode.Night, RoomSettings.Default.Rule, RoomSettings.Default.VictoryPoints, 60)
                    : RoomSettings.Default;
                menu.SubmitCreateRoom(_createName, _createMap, MaxPlayers, _createBots, null, settings);
                return;
            }

            foreach (Ironfront.MasterClient.RoomInfo room in menu.Rooms)
            {
                if (room != null && room.Name == _joinName)
                {
                    menu.JoinRoom(room.RoomId, null);
                    return;
                }
            }
            menu.RefreshRooms();
            _actedInState = false;
        }

        // LoadoutUi lives in Assembly-CSharp, which this assembly cannot reference; its button
        // handler is reached the way the button reaches it, by name.
        private void DeployWhenLoadoutIsUp()
        {
            if (_loadoutUi == null) _loadoutUi = Type.GetType("LoadoutUi, Assembly-CSharp");
            if (_loadoutUi == null) return;

            MethodInfo isOpen = _loadoutUi.GetMethod("IsOpen", BindingFlags.Public | BindingFlags.Static);
            bool open = isOpen != null && (bool)isOpen.Invoke(null, null);
            if (!open)
            {
                _loadoutSeenAt = -1f;
                return;
            }

            if (_loadoutSeenAt < 0f) _loadoutSeenAt = Time.unscaledTime;
            if (Time.unscaledTime - _loadoutSeenAt < DeployDelaySeconds) return;

            object instance = _loadoutUi.GetField("instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (instance is Component loadout)
            {
                loadout.SendMessage("OnDeployClick", SendMessageOptions.DontRequireReceiver);
                Debug.Log("[autoplay] deploy");
                _loadoutSeenAt = -1f;
            }
        }
    }
}
#endif
