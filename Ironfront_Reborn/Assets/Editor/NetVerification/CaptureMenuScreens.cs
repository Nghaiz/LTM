using System.Collections.Generic;
using System.IO;
using Ironfront.MasterClient;
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
                Capture("Create Room", null, "create-room.png"),
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
                    Players = 1 + (i % 5), MaxPlayers = 16,
                    State = (byte)(i == 3 ? Ironfront.Net.Protocol.RoomLifecycleState.Starting : Ironfront.Net.Protocol.RoomLifecycleState.Waiting),
                    IsPrivate = i == 1,
                });
            }

            panel.GetComponent<MenuRoomBrowserScreen>().Draw(mine.ToArray(), rooms.ToArray(), busy: false);
        }
    }
}
