using System.IO;
using System.Reflection;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client.Hud;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.EditorTools
{
    /// <summary>
    /// Renders the in-match readout -- the killfeed and the Tab scoreboard -- to PNGs with sample
    /// data, so a change to either can be looked at without play mode, a match or a second
    /// player. Feature 2, 2026-09-29.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Edit mode, from the prefab.</b> <c>LoadPrefabContents</c> gives an isolated scene; a
    /// temporary camera renders the readout's Canvas in <c>ScreenSpaceCamera</c> into a
    /// 1920x1080 target. The views' <c>Awake</c> does not run outside play mode, so it is invoked
    /// here, and their motion is settled by ticking them rather than by waiting frames.
    /// </para>
    /// <para>
    /// <b>The scaler is pinned to 1:1</b>, or the capture is worthless: batchmode reports a
    /// 640x480 screen, and edge-anchored elements would be laid out against a width the game
    /// never has.
    /// </para>
    /// <para>
    /// Two backgrounds, a bright sky and a dark field, because a readout that reads on one can
    /// vanish on the other. Team colours are the game's own blue and red, stated here because the
    /// palette seam is registered only at runtime. Nothing is saved back to the prefab.
    /// </para>
    /// </remarks>
    public static class CaptureMatchHud
    {
        private const string PrefabPath = "Assets/Prefab/Ingame UI Container.prefab";
        private const string OutputDirectory = "Temp/hud-capture";

        private static readonly Color Blue = new Color(0.2f, 0.45f, 1f);
        private static readonly Color Red = new Color(1f, 0.25f, 0.2f);

        [MenuItem("Ironfront/Net/Capture in-match readout")]
        public static void RunFromMenu() => Debug.Log("[capture-match-hud] " + Capture());

        /// <summary>The <c>-executeMethod</c> entry point.</summary>
        public static void Run() => Debug.Log("[capture-match-hud] " + Capture());

        /// <summary>Renders both captures and returns where they went.</summary>
        public static string Capture()
        {
            Directory.CreateDirectory(OutputDirectory);
            GameObject contents = PrefabUtility.LoadPrefabContents(PrefabPath);

            var cameraObject = new GameObject("Capture Camera", typeof(Camera));
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, contents.scene);
            var target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);

            try
            {
                Transform readout = contents.transform.Find(BuildMatchHud.RootName);
                if (readout == null) return "no '" + BuildMatchHud.RootName + "' in the prefab; run the builder first";

                // Only the readout: the prefab's legacy Canvases would draw over the capture.
                foreach (Transform child in contents.transform)
                    if (child != readout) child.gameObject.SetActive(false);

                // A camera in a preview scene renders the MAIN scenes unless told otherwise: the
                // first capture drew the open Menu scene's helicopter and no UI at all.
                Camera camera = cameraObject.GetComponent<Camera>();
                camera.scene = contents.scene;
                camera.targetTexture = target;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.cullingMask = ~0;

                var canvas = readout.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 1f;

                var scaler = readout.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = 1f;

                var hud = readout.GetComponent<MatchHud>();
                var so = new SerializedObject(hud);

                ShowKillfeed(so);
                string killfeedSky = Render(camera, target, new Color(0.62f, 0.72f, 0.82f), "killfeed-sky.png");
                string killfeedDark = Render(camera, target, new Color(0.1f, 0.12f, 0.1f), "killfeed-dark.png");

                ShowScoreboard(so, 20, 18);
                string board = Render(camera, target, new Color(0.35f, 0.42f, 0.36f), "scoreboard.png");

                ShowScoreboard(so, 32, 32);
                string full = Render(camera, target, new Color(0.35f, 0.42f, 0.36f), "scoreboard-full.png");

                string rect = ((RectTransform)readout).rect.ToString();
                return $"canvas {rect}; wrote {killfeedSky}, {killfeedDark}, {board}, {full}";
            }
            finally
            {
                target.Release();
                Object.DestroyImmediate(target);
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        private static void ShowKillfeed(SerializedObject hud)
        {
            SerializedProperty rows = hud.FindProperty("_killfeedRows");
            Color blue = TeamInk(Blue);
            Color red = TeamInk(Red);

            var lines = new[]
            {
                (new KillfeedLine(5, "Minh", TeamId.Team0, "Bot 12", TeamId.Team1, "RK-44", "", false, true, false), blue, red),
                (new KillfeedLine(4, "Bot 7", TeamId.Team1, "Bot 30", TeamId.Team0, "TANK", "", false, false, false), red, blue),
                (new KillfeedLine(3, "Bot 3", TeamId.Team0, "Hoang", TeamId.Team1, "RECON LRR", "", true, false, false), blue, red),
                (new KillfeedLine(2, "", TeamId.None, "Bot 21", TeamId.Team1, "", "went down with the Helicopter", false, false, false), red, red),
                (new KillfeedLine(1, "Bot 18", TeamId.Team1, "Minh", TeamId.Team0, "DESTROYED JEEP", "", false, false, true), red, blue),
            };

            for (int i = 0; i < rows.arraySize && i < lines.Length; i++)
            {
                var view = (KillfeedRowView)rows.GetArrayElementAtIndex(i).objectReferenceValue;
                view.gameObject.SetActive(true);
                Awake(view);

                (KillfeedLine line, Color killer, Color victim) = lines[i];
                view.Show(in line, i, killer, victim);

                for (int t = 0; t < 40; t++) view.Tick(0.05f);
            }
        }

        private static void ShowScoreboard(SerializedObject hud, int team0Players, int team1Players)
        {
            var view = (ScoreboardView)hud.FindProperty("_scoreboard").objectReferenceValue;
            view.gameObject.SetActive(true);

            foreach (MonoBehaviour behaviour in view.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour is ScoreboardTeamView || behaviour is ScoreboardView) Awake(behaviour);

            // Through the HUD, as the game does, so what the open board hides is hidden here too.
            // The palette seam is empty in edit mode, so the game's own colours are laid on after.
            ((MatchHud)hud.targetObject).SetScoreboardVisible(true);
            view.SetPalette(Blue, Red);

            view.SetMatch(new ScoreboardMatch(
                "DUSTBOWL", "CONQUEST  ·  38 PLAYERS  ·  3 HUMANS", 412, 376, -36f / 200f,
                "TEAM 1 LEADS BY 36  ·  164 MORE TO WIN", "14:32", "TIME LEFT", false, 3, 2,
                TeamId.None,
                "LEAD BY 200 POINTS TO WIN  ·  KILLS SCORE MORE FOR EVERY FLAG YOU HOLD  ·  HOLDING MORE FLAGS SCORES OVER TIME"));

            Column(view, TeamId.Team0, team0Players, humans: 2, firstId: 1, local: 1);
            Column(view, TeamId.Team1, team1Players, humans: 1, firstId: 33, local: 0);
            view.End();

            for (int t = 0; t < 40; t++) view.Tick(0.05f);
        }

        private static void Column(ScoreboardView view, byte team, int players, int humans, ushort firstId, ushort local)
        {
            int kills = 0;
            int deaths = 0;
            var rows = new ScoreboardRow[players];

            for (int i = 0; i < players; i++)
            {
                ushort id = (ushort)(firstId + i);
                int k = Mathf.Max(0, 17 - i + (i % 3));
                int d = 3 + (i * 7) % 9;
                bool human = i < humans;
                string name = !human ? "Bot " + id : team == TeamId.Team0 ? (i == 0 ? "Minh" : "Lan") : "Hoang";
                rows[i] = new ScoreboardRow(id, name, k, d, (d > 0 ? k / (float)d : k).ToString("0.00"), !human, id == local);
                kills += k;
                deaths += d;
            }

            view.BeginColumn(
                team,
                team == TeamId.Team0 ? "TEAM 1" : "TEAM 2",
                players + " PLAYERS  ·  " + humans + (humans == 1 ? " HUMAN" : " HUMANS"),
                kills + " KILLS  ·  " + deaths + " DEATHS");

            foreach (ScoreboardRow row in rows) view.AddRow(team, in row);
        }

        private static Color TeamInk(Color team) => HudStyle.TeamInk(team);

        /// <summary>Runs a component's private Awake, which edit mode never calls.</summary>
        private static void Awake(MonoBehaviour behaviour)
        {
            MethodInfo awake = behaviour.GetType().GetMethod(
                "Awake", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            awake?.Invoke(behaviour, null);
        }

        private static string Render(Camera camera, RenderTexture target, Color background, string file)
        {
            camera.backgroundColor = background;
            Canvas.ForceUpdateCanvases();
            camera.Render();

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;

            var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            image.Apply();
            RenderTexture.active = previous;

            string path = Path.Combine(OutputDirectory, file);
            File.WriteAllBytes(path, image.EncodeToPNG());
            Object.DestroyImmediate(image);

            return Path.GetFullPath(path);
        }
    }
}
