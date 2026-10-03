using System.Collections.Generic;
using System.IO;
using Ironfront.MasterClient;
using Ironfront.Net.Configuration;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client.Menu;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.EditorTools
{
    /// <summary>
    /// Renders one multiplayer menu screen to a PNG with sample rows, so a layout change can be
    /// looked at without play mode, a master server or a second player.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Edit mode, on a copy.</b> The authored <see cref="BuildMenuCanvas.RootName"/> root is
    /// cloned into a preview scene with its own camera, so nothing in the open scene is touched
    /// and nothing is saved. Screens are left as authored except for the sample text written into
    /// their rows, which is exactly what a player would see once the master has answered.
    /// </para>
    /// <para>
    /// <b>The scaler is pinned to the reference resolution</b>, for the reason
    /// <see cref="CaptureMatchHud"/> gives: a batchmode Editor reports a 640x480 screen, and a
    /// capture laid out against it shows a menu no player ever sees.
    /// </para>
    /// </remarks>
    public static class CaptureMenuScreens
    {
        private const string ScenePath = "Assets/Scenes/Menu.unity";
        private const string OutputDirectory = "Temp/menu-capture";

        /// <summary>The <c>-executeMethod</c> / script-execute entry point: every screen it knows.</summary>
        public static string CaptureAll()
        {
            var written = new List<string>
            {
                Capture("Rooms", panel => FillRoomBrowser(panel, 0, 4), "rooms.png"),
                Capture("Rooms", panel => FillRoomBrowser(panel, 1, 4), "rooms-rejoin.png"),
                Capture("Rooms", panel => FillRoomBrowser(panel, 3, 9), "rooms-rejoin-overflow.png"),
                Capture("Create Room", panel => FillCreateRoom(panel, Capacity(100, true, 0, 0, 0), 50, 2), "create-room.png"),
                Capture("Create Room", panel => FillCreateRoom(panel, Capacity(52, true, 3, 48, 198), 64, 1), "create-room-limited.png"),
                Capture("Create Room", panel => FillCreateRoom(panel, Capacity(0, false, 2, 200, 300), 0, 2), "create-room-full.png"),
            };
            return string.Join(", ", written);
        }

        /// <summary>
        /// Renders the screen named <paramref name="panelName"/> after <paramref name="fill"/> has
        /// written sample data into the copy, and returns where the PNG went.
        /// </summary>
        public static string Capture(string panelName, System.Action<GameObject> fill, string fileName)
        {
            Directory.CreateDirectory(OutputDirectory);

            GameObject source = FindAuthoredRoot();
            if (source == null) return $"no '{BuildMenuCanvas.RootName}' in {ScenePath}; run the builder first";

            Scene preview = EditorSceneManager.NewPreviewScene();
            var target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
            Camera camera = null;

            try
            {
                GameObject copy = Object.Instantiate(source);
                SceneManager.MoveGameObjectToScene(copy, preview);

                var cameraObject = new GameObject("Capture Camera", typeof(Camera));
                SceneManager.MoveGameObjectToScene(cameraObject, preview);
                camera = cameraObject.GetComponent<Camera>();
                camera.scene = preview;
                camera.targetTexture = target;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.cullingMask = ~0;

                var canvas = copy.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 1f;

                var scaler = copy.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = 1f;

                GameObject panel = null;
                foreach (Transform child in copy.transform)
                {
                    bool wanted = child.name == panelName;
                    child.gameObject.SetActive(wanted);
                    if (wanted) panel = child.gameObject;
                }

                if (panel == null) return $"no screen '{panelName}' under '{BuildMenuCanvas.RootName}'";

                // A screen hidden by its transition sits at alpha 0 in the authored scene.
                foreach (CanvasGroup group in panel.GetComponentsInChildren<CanvasGroup>(true))
                    group.alpha = 1f;

                fill?.Invoke(panel);

                Canvas.ForceUpdateCanvases();
                camera.Render();

                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = target;
                var pixels = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                pixels.Apply();
                RenderTexture.active = previous;

                string path = Path.Combine(OutputDirectory, fileName);
                File.WriteAllBytes(path, pixels.EncodeToPNG());
                Object.DestroyImmediate(pixels);
                return Path.GetFullPath(path);
            }
            finally
            {
                if (camera != null) camera.targetTexture = null;
                target.Release();
                Object.DestroyImmediate(target);
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        private static GameObject FindAuthoredRoot()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || scene.path != ScenePath) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                    if (root.name == BuildMenuCanvas.RootName) return root;
            }

            return null;
        }

        /// <summary>
        /// <paramref name="rejoins"/> of this player's running matches and <paramref name="open"/>
        /// open rooms, drawn by the screen's own <c>Draw</c>, so the capture is laid out by the code
        /// that lays out the game.
        /// </summary>
        private static void FillRoomBrowser(GameObject panel, int rejoins, int open)
        {
            string[] names = { "Friday night", "Island hop", "Tank rush", "Slow burn", "Dawn raid",
                "Harbour", "Ridge line", "Last stand", "Convoy" };

            var mine = new List<RoomInfo>();
            for (int i = 0; i < rejoins; i++)
            {
                mine.Add(new RoomInfo
                {
                    RoomId = 100 + i, Name = i == 0 ? "Sunday squad" : "Old match " + i, MapId = (ushort)(1 + (i % 2)),
                    Players = 3 - (i % 2), MaxPlayers = 8, State = (byte)Ironfront.Net.Protocol.RoomLifecycleState.InMatch,
                    CanRejoin = true, RejoinTeam = (byte)(i % 2),
                });
            }

            var rooms = new List<RoomInfo>();
            for (int i = 0; i < open; i++)
            {
                rooms.Add(new RoomInfo
                {
                    RoomId = 1 + i, Name = names[i % names.Length], MapId = (ushort)(1 + (i % 2)),
                    Players = 1 + (i % 5), MaxPlayers = 16, BotCount = (byte)(i % 3 == 0 ? 100 : i % 3 == 1 ? 32 : 0),
                    State = (byte)(i == 3 ? Ironfront.Net.Protocol.RoomLifecycleState.Starting : Ironfront.Net.Protocol.RoomLifecycleState.Waiting),
                    IsPrivate = i == 1,
                });
            }

            panel.GetComponent<MenuRoomBrowserScreen>().Draw(mine.ToArray(), rooms.ToArray(), busy: false);
        }

        /// <summary>
        /// The create form as the servers' <paramref name="capacity"/> would draw it, with the slider
        /// asked for <paramref name="bots"/> (and held under the ceiling, as it is in the game).
        /// </summary>
        private static void FillCreateRoom(GameObject panel, RoomCapacity capacity, int bots, ushort mapId)
        {
            // The catalog's display name, as the screen's own dropdown labels it: an id check here
            // called every map that is not Island "Dustbowl", Forest Lake included.
            string mapName = $"map {mapId}";
            foreach (MapCatalog.MapEntry entry in MapCatalog.All)
            {
                if (entry.Id == mapId) mapName = entry.DisplayName;
            }

            MenuBotSlider slider = panel.GetComponentInChildren<MenuBotSlider>(true);
            slider.SetCapacity(capacity);
            slider.Select(bots);

            panel.GetComponentInChildren<MenuHostCapacityCard>(true).Show(capacity, slider.Value, mapId, mapName);

            // Awake does not run on an edit-mode copy, so the fields the screen fills itself are
            // written here with the same words.
            foreach (Text text in panel.GetComponentsInChildren<Text>(true))
            {
                if (text.name == "PreviewTitle") text.text = mapName;
                if (text.name == "Value" && text.transform.parent != null && text.transform.parent.name == "Stat0") text.text = "8";
                if (text.name == "Value" && text.transform.parent != null && text.transform.parent.name == "Stat1") text.text = RoomBotChoice.Preview(slider.Value);
                if (text.name == "Value" && text.transform.parent != null && text.transform.parent.name == "Stat2") text.text = RoomSettingsChoice.Describe(RoomSettings.Default);
                if (text.name == "Value" && text.transform.parent != null && text.transform.parent.name == "Stat3") text.text = "PUBLIC";
            }

            Dropdown map = panel.GetComponentInChildren<Dropdown>(true);
            if (map != null && map.captionText != null) map.captionText.text = mapName;
        }

        private static RoomCapacity Capacity(int maxBots, bool canCreate, int roomsOpen, int botsInPlay, int unitsInUse)
            => new RoomCapacity
            {
                MaxBotsForNewRoom = maxBots,
                CanCreateRoom = canCreate,
                MaxBotsPerMatch = Ironfront.Net.Protocol.ProtocolConstants.MAX_BOTS,
                RoomsOpen = roomsOpen,
                BotsInPlay = botsInPlay,
                BudgetUnits = 300,
                UnitsInUse = unitsInUse,
                MatchCostUnits = 50,
                Maps = new[]
                {
                    new MapAvailability { MapId = 1, Servers = 1, Free = roomsOpen >= 1 ? 0 : 1 },
                    new MapAvailability { MapId = 2, Servers = 1, Free = roomsOpen >= 2 ? 0 : 1 },
                },
            };
    }
}
