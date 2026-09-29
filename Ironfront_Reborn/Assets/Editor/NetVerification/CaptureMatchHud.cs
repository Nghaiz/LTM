using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
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

                ShowNameplates(so);
                string plates = Render(camera, target, new Color(0.45f, 0.5f, 0.42f), "nameplates.png");

                ShowScoreboard(so, 20, 18);
                string board = Render(camera, target, new Color(0.35f, 0.42f, 0.36f), "scoreboard.png");

                ShowScoreboard(so, 32, 32);
                string full = Render(camera, target, new Color(0.35f, 0.42f, 0.36f), "scoreboard-full.png");

                string rect = ((RectTransform)readout).rect.ToString();
                return $"canvas {rect}; wrote {killfeedSky}, {killfeedDark}, {plates}, {board}, {full}";
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

            // The weapon pictures come from the game's own WeaponManager entries, read off the
            // prefab by name: this assembly cannot name the type, and edit mode has no instance.
            System.Func<byte, Sprite> previousIcons = NetClientBindings.WeaponIcon;
            NetClientBindings.WeaponIcon = WeaponIcons();

            const float hold = KillfeedModel.DefaultHoldSeconds;

            // One of each shape the feed draws (owner's report of 2026-09-30): your triple kill
            // with a headshot, a roadkill that killed you, a long shot, a revenge with a vehicle's
            // gun, a drowning, and two match events.
            var lines = new[]
            {
                (new KillfeedLine(7, "Minh", TeamId.Team0, BotCallsigns.For(36), TeamId.Team1, "RK-44", "", true, true, false, WeaponIds.RK44, "", badge: KillfeedWording.MultiKillName(3), badgeTone: (int)KillfeedTone.MultiKill, holdSeconds: hold), blue, red),
                (new KillfeedLine(6, BotCallsigns.For(41), TeamId.Team1, "Minh", TeamId.Team0, "QUAD BIKE  ·  ROADKILL", "", false, false, true, glyph: (int)KillfeedGlyph.QuadBike, restAfterGlyph: "ROADKILL", holdSeconds: hold), red, blue),
                (new KillfeedLine(5, "Hoang", TeamId.Team1, BotCallsigns.For(5), TeamId.Team0, "RECON LRR", "", true, false, false, WeaponIds.RECON_LRR, "", badge: "LONG SHOT", badgeTone: (int)KillfeedTone.LongShot, distance: KillfeedWording.Distance(312), holdSeconds: hold), red, blue),
                (new KillfeedLine(4, BotCallsigns.For(9), TeamId.Team0, BotCallsigns.For(34), TeamId.Team1, "TANK", "", false, false, false, glyph: (int)KillfeedGlyph.Tank, badge: KillfeedWording.StreakName(10), badgeTone: (int)KillfeedTone.Streak, holdSeconds: hold), blue, red),
                (new KillfeedLine(3, "", TeamId.None, BotCallsigns.For(44), TeamId.Team1, "", "drowned", false, false, false, glyph: (int)KillfeedGlyph.Drowned, holdSeconds: hold), red, red),
                (new KillfeedLine(2, "BLUE TEAM", TeamId.Team0, "FORTRESS", TeamId.Team0, "", "", false, false, false, glyph: (int)KillfeedGlyph.Flag, verb: KillfeedWording.FlagVerb(true), isEvent: true, holdSeconds: hold), blue, blue),
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

            NetClientBindings.WeaponIcon = previousIcons;
        }

        /// <summary>The loadout screen's weapon silhouettes by network id, read off <c>_Managers.prefab</c>.</summary>
        private static System.Func<byte, Sprite> WeaponIcons()
        {
            var icons = new Dictionary<byte, Sprite>();

            GameObject managers = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/_Managers.prefab");
            System.Type managerType = System.Type.GetType("WeaponManager, Assembly-CSharp");
            Component manager = managers != null && managerType != null
                ? managers.GetComponentInChildren(managerType, true)
                : null;

            if (manager != null)
            {
                SerializedProperty weapons = new SerializedObject(manager).FindProperty("weapons");
                for (int i = 0; weapons != null && i < weapons.arraySize; i++)
                {
                    SerializedProperty entry = weapons.GetArrayElementAtIndex(i);
                    int id = entry.FindPropertyRelative("NetworkId").intValue;
                    if (id > 0 && id <= byte.MaxValue && entry.FindPropertyRelative("image").objectReferenceValue is Sprite sprite)
                        icons[(byte)id] = sprite;
                }
            }

            return id => icons.TryGetValue(id, out Sprite sprite) ? sprite : null;
        }

        /// <summary>
        /// Five plates, all people -- bots carry none since 2026-09-29 -- named with Vietnamese
        /// marks, which the plate keeps as written: a teammate just hit who tops their side (the
        /// white trail, the star), an enemy on low health (the pulse), an enemy at the wheel who
        /// tops theirs, one far off in the water with a long name, and a teammate behind cover.
        /// </summary>
        private static void ShowNameplates(SerializedObject hud)
        {
            var layer = (NameplateLayer)hud.FindProperty("_nameplates").objectReferenceValue;
            layer.gameObject.SetActive(true);
            layer.Initialize();

            Color blue = TeamInk(Blue);
            Color red = TeamInk(Red);

            var plates = new (ushort Id, float X, float Y, float Scale, float Opacity, string Name, byte Team, float Before, float After, float Metres, byte Weapon, bool Seated, bool Water, bool Leader, Color Ink)[]
            {
                (1, 700f, 660f, 1f, 1f, "Minh", TeamId.Team0, 1f, 0.55f, 11f, WeaponIds.RK44, false, false, true, blue),
                (33, 1260f, 700f, 0.95f, 1f, "Hoàng", TeamId.Team1, 0.2f, 0.2f, 18f, WeaponIds.RECON_LRR, false, false, false, red),
                (34, 990f, 590f, 0.86f, 1f, "Tuấn", TeamId.Team1, 1f, 1f, 33f, WeaponIds.SIND7, true, false, true, red),
                (35, 1520f, 540f, 0.66f, 0.9f, "Nguyễn Văn Khoa", TeamId.Team1, 0.7f, 0.7f, 63f, WeaponIds.SL_DEFENDER, false, true, false, red),
                (2, 430f, 520f, 0.66f, 0.5f, "Lan", TeamId.Team0, 0.9f, 0.9f, 118f, WeaponIds.EAGLE_76, false, false, false, blue),
            };

            // The plates draw the weapon in hand from the same loadout art the killfeed does.
            System.Func<byte, Sprite> previousIcons = NetClientBindings.WeaponIcon;
            NetClientBindings.WeaponIcon = WeaponIcons();

            // Twenty frames at the old health, then ten after the hit: the trail is still holding.
            for (int frame = 0; frame < 30; frame++)
            {
                layer.Begin();

                foreach (var p in plates)
                {
                    float health = frame < 20 ? p.Before : p.After;
                    var plate = new Nameplate(
                        p.Id, p.X, p.Y, p.Scale, p.Opacity, p.Name, p.Team, health, p.Team == TeamId.Team0,
                        p.Metres, p.Weapon, p.Seated, p.Water, p.Leader);
                    layer.Set(in plate, p.Ink, 0.02f);
                }

                layer.End();
            }

            NetClientBindings.WeaponIcon = previousIcons;
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

            // Worded by the same statics the presenter calls, so the capture shows what a match does.
            view.SetMatch(new ScoreboardMatch(
                "DUSTBOWL", ScoreboardWording.SummaryLine(team0Players + team1Players, 3),
                412, 376, ScoreboardWording.Lead(412, 376, 200),
                ScoreboardWording.LeadLine(MatchPhase.Playing, 412, 376, 200, TeamId.None),
                ScoreboardWording.Clock(-1), ScoreboardWording.PhaseLabel(MatchPhase.Playing, false), false,
                3, 2, TeamId.None, ScoreboardWording.Rules(200)));

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
            var order = new int[players];

            for (int i = 0; i < players; i++)
            {
                ushort id = (ushort)(firstId + i);
                int k = Mathf.Max(0, 17 - i + (i % 3));
                int d = 3 + (i * 7) % 9;
                bool human = i < humans;
                string name = !human ? BotCallsigns.For(id) : team == TeamId.Team0 ? (i == 0 ? "Minh" : "Lan") : "Hoàng";

                // A spread of every state the board draws: dead, in a vehicle, a live streak, a
                // slow connection.
                bool alive = i % 5 != 3;
                bool seated = i % 7 == 2;
                int streak = i % 4 == 0 ? 3 + i % 5 : i % 3;
                int ping = human ? (i == 0 ? 38 : 124 + i * 40) : 0;
                float ratio = d > 0 ? k / (float)d : k;

                rows[i] = new ScoreboardRow(
                    id, name, k, d, ratio.ToString("0.00"), !human, id == local,
                    rank: 0, hasStats: true, isAlive: alive, isSeated: seated,
                    headshots: k / 3, streak: streak, bestStreak: streak + i % 6, points: k * 2 + i % 4,
                    pingMs: ping, isLeader: i == 0 && k > 0, ratioValue: ratio);
                order[i] = i;
                kills += k;
                deaths += d;
            }

            // Ranks by the board's order (kills, then fewer deaths), players listed first.
            System.Array.Sort(order, (a, b) => rows[b].Kills != rows[a].Kills
                ? rows[b].Kills.CompareTo(rows[a].Kills)
                : rows[a].Deaths.CompareTo(rows[b].Deaths));
            var ranks = new int[players];
            for (int place = 0; place < players; place++) ranks[order[place]] = place + 1;

            view.BeginColumn(
                team,
                ScoreboardWording.TeamName(team),
                ScoreboardWording.PlayersLine(players, humans),
                ScoreboardWording.TotalsLine(kills, deaths));

            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < players; i++)
                {
                    ScoreboardRow r = rows[i];
                    if (r.IsBot != (pass == 1)) continue;

                    var ranked = new ScoreboardRow(
                        r.ActorId, r.Name, r.Kills, r.Deaths, r.Ratio, r.IsBot, r.IsLocal, ranks[i],
                        r.HasStats, r.IsAlive, r.IsSeated, r.Headshots, r.Streak, r.BestStreak, r.Points,
                        r.PingMs, r.IsLeader, r.RatioValue);
                    view.AddRow(team, in ranked);
                }
            }
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
