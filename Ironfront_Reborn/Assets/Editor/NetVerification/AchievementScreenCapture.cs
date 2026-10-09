using System.Collections.Generic;
using System.IO;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Unity.Client.Overlay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.EditorTools
{
    /// <summary>
    /// Renders the achievement screens of achievements v2 to PNGs in edit mode, from a sample career:
    /// the page, a card's details, banners mid-animation, and the end-of-round summary. For reviewing
    /// layout, colour and badges without a match or a master.
    /// </summary>
    /// <remarks>An Editor tool: it builds a detached, throwaway copy of each screen in a preview scene.</remarks>
    public static class AchievementScreenCapture
    {
        private const string OutputDirectory = "Temp/overlay-capture";

        [MenuItem("Ironfront/Net/Capture achievement screens")]
        public static void CaptureAll()
        {
            // Pages register themselves on entering play mode; in edit mode the tool does it.
            OverlayHost.RegisterPage<AchievementsPage>(OverlayPage.Achievements);
            Debug.Log("[achievement-capture] " + string.Join(", ", new[]
            {
                CapturePage(),
                CaptureDetail("curvature", "achievement-detail-mythic.png"),
                CaptureDetail("man_overboard", "achievement-detail-hidden.png"),
                CaptureDetail("armourer", "achievement-detail-parts.png"),
                CaptureToast("steady_hand", 1.2f, 1, 3, "toast-bronze.png"),
                CaptureToast("nine_lives", 0.9f, 2, 3, "toast-declassify.png"),
                CaptureToast("curvature", 1.4f, 3, 3, "toast-mythic.png"),
                CaptureSummary(),
            }));
        }

        /// <summary>The page, over a sample career.</summary>
        public static string CapturePage()
            => CaptureOverlays.Capture(OverlayPage.Achievements, host => Page(host).ShowForTool(SampleState(), SampleLocal()),
                "achievements-page.png");

        /// <summary>The details of <paramref name="id"/> over the page.</summary>
        public static string CaptureDetail(string id, string fileName)
            => CaptureOverlays.Capture(OverlayPage.Achievements, host =>
            {
                AchievementsPage page = Page(host);
                page.ShowForTool(SampleState(), SampleLocal());
                page.ShowDetailForTool(id);
            }, fileName);

        /// <summary>The banner for <paramref name="id"/>, <paramref name="seconds"/> into its animation.</summary>
        public static string CaptureToast(string id, float seconds, int place, int of, string fileName)
        {
            Achievement achievement = AchievementCatalog.Find(id);
            return CaptureCanvas(fileName, toast => toast.ShowForTool(achievement, seconds, place, of));
        }

        /// <summary>The end-of-round summary card.</summary>
        public static string CaptureSummary()
        {
            var before = new Dictionary<string, long>
            {
                [CareerStats.Key(CareerStat.Kills)] = 4970, [CareerStats.Key(CareerStat.LongestKillMetres)] = 210,
                [CareerStats.Key(CareerStat.Headshots)] = 31, [CareerStats.Key(CareerStat.Deaths)] = 880,
            };
            var after = new Dictionary<string, long>
            {
                [CareerStats.Key(CareerStat.Kills)] = 5012, [CareerStats.Key(CareerStat.LongestKillMetres)] = 284,
                [CareerStats.Key(CareerStat.Headshots)] = 44, [CareerStats.Key(CareerStat.Deaths)] = 902,
                [CareerStats.Key(CareerStat.RoundsFinished)] = 1,
            };
            List<RoundSummaryLine> lines = AchievementRoundSummary.Build(before, after, new HashSet<string>(),
                new[] { "roll_call", "victory_lap" });
            return CaptureCanvas("round-summary.png", toast => toast.ShowSummaryForTool(lines, 1.5f));
        }

        private static AchievementsPage Page(OverlayHost host) => host.GetComponentInChildren<AchievementsPage>(true);

        private static string CaptureCanvas(string fileName, System.Action<AchievementToast> show)
        {
            Directory.CreateDirectory(OutputDirectory);
            Scene preview = EditorSceneManager.NewPreviewScene();
            var target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
            AchievementToast toast = null;
            Camera camera = null;
            try
            {
                toast = AchievementToast.CreateDetached();
                SceneManager.MoveGameObjectToScene(toast.gameObject, preview);
                var cameraObject = new GameObject("Capture Camera", typeof(Camera));
                SceneManager.MoveGameObjectToScene(cameraObject, preview);
                camera = cameraObject.GetComponent<Camera>();
                camera.scene = preview;
                camera.targetTexture = target;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.18f, 0.22f, 0.2f);

                var canvas = toast.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 1f;
                var scaler = toast.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = 1f;

                show(toast);
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

        private static ICollection<string> SampleLocal() => new HashSet<string> { "cadet", "by_the_book" };

        /// <summary>A believable career a few weeks in: some metals, some progress, a Mythic's first holder.</summary>
        private static AchievementState SampleState()
        {
            var unlocked = new List<AchievementUnlock>();
            long at = 1_760_000_000_000;
            foreach (string id in new[]
                     {
                         "roll_call", "lights_out", "baptism_of_fire", "steady_hand", "flag_runner", "speed_bump",
                         "victory_lap", "three_fronts", "unbroken", "overwatch", "nine_lives", "juggernaut", "rampage",
                     })
                unlocked.Add(new AchievementUnlock { Id = id, At = at += 86_400_000 });

            var earned = new Dictionary<string, long>();
            int i = 0;
            foreach (Achievement achievement in AchievementCatalog.All)
                earned[achievement.Id] = System.Math.Max(0, 180 - (int)achievement.Tier * 40 - (i++ % 13) * 3);
            earned["rampage"] = 1;
            earned["curvature"] = 0;

            return new AchievementState
            {
                Players = 214,
                Unlocked = unlocked.ToArray(),
                Earned = earned,
                Career = new Dictionary<string, long>
                {
                    [CareerStats.Key(CareerStat.Kills)] = 3642, [CareerStats.Key(CareerStat.Headshots)] = 412,
                    [CareerStats.Key(CareerStat.LongestHeadshotMetres)] = 468, [CareerStats.Key(CareerStat.BestStreak)] = 41,
                    [CareerStats.Key(CareerStat.RoundsWon)] = 37, [CareerStats.Key(CareerStat.Deaths)] = 2210,
                    [CareerStats.Key(CareerStat.WeaponKillMask)] = (1L << 1) | (1L << 2) | (1L << 4) | (1L << 7) | (1L << 16),
                    [CareerStats.Key(CareerStat.MapsFinished)] = (1L << 1) | (1L << 2) | (1L << 3),
                },
                Firsts = new Dictionary<string, FirstHolderInfo>
                {
                    ["rampage"] = new FirstHolderInfo { Name = "Nghaiz", At = 1_760_400_000_000 },
                },
            };
        }
    }
}

