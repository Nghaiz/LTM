#nullable enable

using System.Collections.Generic;
using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Unity.Client.Hud;
using Ironfront.Net.Unity.Client.Menu;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// The unlock banner (achievements v2, section 5.2): top-middle, over the menu, the match and the
    /// overlay alike, one achievement at a time, in the order they were earned, with a counter when
    /// several arrive together. Also the end-of-round summary card.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How it moves.</b> The banner drops in with a little overshoot, the badge pops, a sheen
    /// crosses once and a bar in the metal's colour runs down while it holds; then it rises out and
    /// the next follows 0.3 s later. A hidden achievement's silhouette fills with colour from the
    /// bottom ("declassified"). A Mythic one is twice as wide, obsidian, with embers, and holds for
    /// 8 s. In a match, with the mouse captured, a banner is a little smaller so it does not cover
    /// the fight (Mythic excepted). Timed on unscaled time, so a paused match still shows it.
    /// </para>
    /// <para>
    /// <b>Each metal has its own sound</b>, and the three disasters (BULLET SPONGE, PARTICIPATION
    /// TROPHY, CANNON FODDER) a sad trumpet; all written by <c>tools/ui/make_achievement_sound.py</c>.
    /// </para>
    /// <para>
    /// <b>Also the ledger's clock.</b> It exists in every scene for the whole run, so it is what
    /// calls <see cref="AchievementLedger.Tick"/>; nothing else would.
    /// </para>
    /// <para>Built in code at runtime, like <see cref="OverlayHost"/>, for the same reason.</para>
    /// </remarks>
    public sealed class AchievementToast : MonoBehaviour
    {
        public const int SortingOrder = 650;

        /// <summary>The fallback chime, in <c>Resources</c>.</summary>
        public const string SoundPath = "IronfrontUi/achievement-unlocked";

        /// <summary>Per-metal sounds: <c>IronfrontUi/achievement-bronze</c> and so on, and <c>-disaster</c>.</summary>
        public const string SoundPrefix = "IronfrontUi/achievement-";

        private const float Width = 640f;
        private const float MythicWidth = 1280f;
        private const float Height = 116f;
        private const float MythicHeight = 140f;
        private const float RestY = -28f;
        private const float HiddenY = 170f;
        private const float InSeconds = 0.5f;
        private const float HoldSeconds = 5f;
        private const float MythicHoldSeconds = 8f;
        private const float OutSeconds = 0.35f;
        private const float GapSeconds = 0.3f;
        private const float CombatScale = 0.8f;
        private const int EmberCount = 16;
        private const float SummarySeconds = 20f;

        private static AchievementToast? _instance;

        /// <summary>A golden wrench found on the menu, waiting for its banner (<see cref="GoldenWrench"/>).</summary>
        private static bool _wrenchNoticePending;
        private static Sprite? _wrenchPicture;

        private RectTransform? _panel;
        private CanvasGroup? _group;
        private AngularPanel? _face;
        private Image? _glow;
        private Image? _shadow;
        private Image? _badge;
        private Image? _trophy;
        private Text? _kicker;
        private Text? _title;
        private Text? _line;
        private Text? _tier;
        private Text? _counter;
        private Image? _timer;
        private RectTransform? _sheen;
        private RectTransform? _embers;
        private readonly List<Image> _emberDots = new List<Image>(EmberCount);
        private AudioSource? _audio;
        private readonly Dictionary<string, AudioClip?> _clips = new Dictionary<string, AudioClip?>();

        private RectTransform? _summary;
        private CanvasGroup? _summaryGroup;
        private readonly List<Text> _summaryLines = new List<Text>();
        private float _summaryAge = -1f;
        private float _summaryShown = 1f;

        private bool _showing;
        private float _age;
        private float _idle;
        private float _hold = HoldSeconds;
        private float _width = Width;
        private bool _mythic;
        private bool _declassify;
        private string? _current;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (Application.isBatchMode || _instance != null) return;
            _instance = Create();
            GoldenWrench.Revealed += picture =>
            {
                _wrenchPicture = picture;
                _wrenchNoticePending = true;
            };
            AchievementLedger.RoundSummaryReady += () => { if (_instance != null) _instance.ShowSummary(); };
            DontDestroyOnLoad(_instance.gameObject);
        }

        private static AchievementToast Create()
        {
            var root = new GameObject("Ironfront Achievement Toast", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler));
            AchievementToast toast = root.AddComponent<AchievementToast>();
            toast.Compose();
            return toast;
        }

        /// <summary>A banner that is not the game's: for Editor tools that render one to a picture.</summary>
        public static AchievementToast CreateDetached() => Create();

        /// <summary>Shows <paramref name="achievement"/> as it looks <paramref name="seconds"/> after it began. For Editor tools.</summary>
        public void ShowForTool(Achievement achievement, float seconds, int place = 1, int of = 1)
        {
            Begin(achievement, place, of, playSound: false);
            _age = seconds;
            Animate();
        }

        /// <summary>Shows a round summary card. For Editor tools.</summary>
        public void ShowSummaryForTool(List<RoundSummaryLine> lines, float seconds)
        {
            ShowSummary(lines);
            _summaryAge = seconds;
            AnimateSummary();
        }

        private void Compose()
        {
            var canvas = GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            canvas.pixelPerfect = true;

            var scaler = GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var root = (RectTransform)transform;
            _panel = Ui.Child(root, "Banner");
            _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 1f);
            _panel.pivot = new Vector2(0.5f, 1f);
            _panel.sizeDelta = new Vector2(Width, Height);
            _panel.anchoredPosition = new Vector2(0f, HiddenY);
            _group = _panel.gameObject.AddComponent<CanvasGroup>();
            _group.blocksRaycasts = false;
            _group.interactable = false;

            _face = Ui.Panel(_panel, "Face", new Color(0.02f, 0.06f, 0.1f, 0.94f), 14f, Color.white,
                AngularEdge.All, 2f);
            Ui.Stretch((RectTransform)_face.transform);

            // Embers for a Mythic banner, clipped to it, under the text.
            _embers = Ui.Child(_panel, "Embers");
            Ui.Stretch(_embers, 3f, 3f, 3f, 3f);
            _embers.gameObject.AddComponent<RectMask2D>();
            for (int i = 0; i < EmberCount; i++)
            {
                Image dot = Ui.Fill(_embers, "Ember", new Color(1f, 0.45f, 0.18f, 0f));
                dot.rectTransform.anchorMin = dot.rectTransform.anchorMax = new Vector2(0f, 0f);
                dot.rectTransform.sizeDelta = new Vector2(4f, 4f);
                dot.raycastTarget = false;
                _emberDots.Add(dot);
            }

            // The sheen: a pale slanted band that crosses the face once, clipped to it.
            RectTransform clip = Ui.Child(_panel, "Sheen Clip");
            Ui.Stretch(clip, 3f, 3f, 3f, 3f);
            clip.gameObject.AddComponent<RectMask2D>();
            Image sheen = Ui.Fill(clip, "Sheen", new Color(1f, 1f, 1f, 0.1f));
            _sheen = sheen.rectTransform;
            _sheen.anchorMin = _sheen.anchorMax = new Vector2(0.5f, 0.5f);
            _sheen.sizeDelta = new Vector2(70f, MythicHeight * 2f);
            _sheen.localRotation = Quaternion.Euler(0f, 0f, -22f);

            _glow = Ui.Fill(_panel, "Glow", Color.white);
            _glow.sprite = HudSprites.Glow();
            Ui.TopLeft(_glow.rectTransform, new Vector2(-6f, -12f), new Vector2(140f, 140f));

            _shadow = Ui.Art(_panel, "Silhouette", null, Color.white);
            Ui.TopLeft(_shadow.rectTransform, new Vector2(20f, 14f), new Vector2(88f, 88f));
            _badge = Ui.Art(_panel, "Badge", null, Color.white);
            Ui.TopLeft(_badge.rectTransform, new Vector2(20f, 14f), new Vector2(88f, 88f));
            _badge.type = Image.Type.Filled;
            _badge.fillMethod = Image.FillMethod.Vertical;
            _badge.fillOrigin = (int)Image.OriginVertical.Bottom;

            _trophy = Ui.Icon(_panel, "Trophy", "trophy", Color.white);
            Ui.TopLeft(_trophy.rectTransform, new Vector2(126f, 17f), new Vector2(16f, 16f));
            _kicker = Ui.Label(_panel, "Kicker", "ACHIEVEMENT UNLOCKED", 13, Ui.Weight.Bold, Color.white);
            Ui.TopLeft(_kicker.rectTransform, new Vector2(148f, 14f), new Vector2(380f, 22f));
            _kicker.horizontalOverflow = HorizontalWrapMode.Overflow;

            _tier = Ui.Label(_panel, "Tier", string.Empty, 12, Ui.Weight.Bold, Color.white, TextAnchor.MiddleRight);
            _counter = Ui.Label(_panel, "Counter", string.Empty, 12, Ui.Weight.Bold, UiStyle.Muted, TextAnchor.MiddleRight);

            _title = Ui.Label(_panel, "Title", string.Empty, 26, Ui.Weight.Black, UiStyle.Ink);
            _title.horizontalOverflow = HorizontalWrapMode.Overflow;
            _line = Ui.Label(_panel, "Line", string.Empty, 15, Ui.Weight.Regular, UiStyle.Muted, TextAnchor.UpperLeft);

            _timer = Ui.Fill(_panel, "Timer", Color.white);
            _timer.rectTransform.anchorMin = new Vector2(0f, 0f);
            _timer.rectTransform.anchorMax = new Vector2(0f, 0f);
            _timer.rectTransform.pivot = new Vector2(0f, 0f);
            _timer.rectTransform.anchoredPosition = new Vector2(14f, 6f);

            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 0f;
            _audio.ignoreListenerPause = true;
            _audio.volume = 0.8f;

            _group.alpha = 0f;
            Layout(Width, Height);
            ComposeSummary(root);
        }

        /// <summary>Places the banner's text for a banner <paramref name="width"/> wide.</summary>
        private void Layout(float width, float height)
        {
            _width = width;
            float size = height - 28f;
            float text = 20f + size + 18f;
            float drop = (height - Height) * 0.5f;
            if (_panel != null) _panel.sizeDelta = new Vector2(width, height);
            if (_badge != null) Ui.TopLeft(_badge.rectTransform, new Vector2(20f, 14f), new Vector2(size, size));
            if (_shadow != null) Ui.TopLeft(_shadow.rectTransform, new Vector2(20f, 14f), new Vector2(size, size));
            if (_glow != null) Ui.TopLeft(_glow.rectTransform, new Vector2(-6f, -12f), new Vector2(size + 52f, size + 52f));
            if (_trophy != null) Ui.TopLeft(_trophy.rectTransform, new Vector2(text, 17f + drop * 0.5f), new Vector2(16f, 16f));
            if (_kicker != null) Ui.TopLeft(_kicker.rectTransform, new Vector2(text + 22f, 14f + drop * 0.5f), new Vector2(420f, 22f));
            if (_tier != null) Ui.TopLeft(_tier.rectTransform, new Vector2(width - 236f, 14f), new Vector2(214f, 22f));
            if (_counter != null) Ui.TopLeft(_counter.rectTransform, new Vector2(width - 236f, height - 30f), new Vector2(214f, 18f));
            if (_title != null)
            {
                _title.fontSize = height > Height ? 32 : 26;
                Ui.TopLeft(_title.rectTransform, new Vector2(text, 36f + drop * 0.6f), new Vector2(width - text - 24f, 40f));
            }
            if (_line != null)
            {
                _line.fontSize = height > Height ? 17 : 15;
                Ui.TopLeft(_line.rectTransform, new Vector2(text, 72f + drop), new Vector2(width - text - 134f, height - 80f - drop));
            }
            if (_timer != null) _timer.rectTransform.sizeDelta = new Vector2(width - 28f, 3f);
        }

        private void Update()
        {
            AchievementLedger.Tick();
            if (_summaryAge >= 0f)
            {
                _summaryAge += Time.unscaledDeltaTime;
                AnimateSummary();
            }

            if (_showing)
            {
                _age += Time.unscaledDeltaTime;
                Animate();
                if (_age >= InSeconds + _hold + OutSeconds)
                {
                    _showing = false;
                    _idle = 0f;
                    if (_current != null) AchievementLedger.Played(_current);
                    _current = null;
                }
                return;
            }

            _idle += Time.unscaledDeltaTime;
            if (_idle < GapSeconds) return;
            if (_wrenchNoticePending)
            {
                _wrenchNoticePending = false;
                BeginWrenchNotice();
            }
            else if (AchievementLedger.TryNextToast(out Achievement next, out int place, out int of))
            {
                Begin(next, place, of, playSound: true);
            }
        }

        /// <summary>
        /// The golden wrench's banner: the secret the original game kept behind <c>ISEEGOLD</c>.
        /// Gold, like the wrench; no chime, because the original's own unlock sound plays with it.
        /// </summary>
        private void BeginWrenchNotice()
        {
            Color gold = AchievementArt.TierColour(AchievementTier.Gold);
            Style(gold, mythic: false);
            if (_badge != null)
            {
                _badge.sprite = _wrenchPicture;
                _badge.fillAmount = 1f;
            }
            if (_shadow != null) _shadow.enabled = false;
            if (_kicker != null)
            {
                _kicker.text = "SECRET FOUND";
                _kicker.color = gold;
            }
            if (_tier != null)
            {
                _tier.text = "PRACTICE ONLY";
                _tier.color = UiStyle.WithAlpha(gold, 0.85f);
            }
            if (_counter != null) _counter.text = string.Empty;
            if (_title != null) _title.text = "THE GOLDEN WRENCH IS YOURS";
            if (_line != null) _line.text = "Pick it from GEAR on the loadout screen of a practice match.";

            _declassify = false;
            _current = null;
            _hold = HoldSeconds;
            _showing = true;
            _age = 0f;
            Animate();
        }

        private void Begin(Achievement achievement, int place, int of, bool playSound)
        {
            Color metal = AchievementArt.TierColour(achievement.Tier);
            bool mythic = achievement.Tier == AchievementTier.Mythic;
            Style(metal, mythic);

            _declassify = achievement.Hidden;
            if (_shadow != null)
            {
                _shadow.enabled = _declassify;
                _shadow.sprite = AchievementArt.Badge(achievement, revealed: false);
            }
            if (_badge != null)
            {
                _badge.sprite = AchievementArt.Badge(achievement, revealed: true);
                _badge.fillAmount = _declassify ? 0f : 1f;
            }
            if (_kicker != null)
            {
                _kicker.text = achievement.Hidden ? "CLASSIFIED ACHIEVEMENT DECLASSIFIED" : "ACHIEVEMENT UNLOCKED";
                _kicker.color = metal;
            }
            if (_tier != null)
            {
                _tier.text = AchievementBoard.TierName(achievement.Tier) + "  //  +" + achievement.Points + " PTS";
                _tier.color = UiStyle.WithAlpha(metal, 0.85f);
            }
            if (_counter != null) _counter.text = of > 1 ? place + " / " + of : string.Empty;
            if (_title != null) _title.text = achievement.Title;
            if (_line != null) _line.text = achievement.Description;

            _current = AchievementCatalog.Find(achievement.Id) != null ? achievement.Id : null;
            _hold = mythic ? MythicHoldSeconds : HoldSeconds;
            _showing = true;
            _age = 0f;
            Animate();

            if (playSound) PlaySound(achievement);
        }

        private void Style(Color metal, bool mythic)
        {
            _mythic = mythic;
            Layout(mythic ? MythicWidth : Width, mythic ? MythicHeight : Height);
            if (_face != null)
            {
                _face.color = mythic ? new Color(0.08f, 0.03f, 0.04f, 0.96f) : new Color(0.02f, 0.06f, 0.1f, 0.94f);
                _face.Configure(14f, AngularEdge.All, mythic ? 3f : 2f, UiStyle.WithAlpha(metal, 0.9f));
            }
            if (_glow != null) _glow.color = UiStyle.WithAlpha(metal, 0.5f);
            if (_trophy != null) _trophy.color = metal;
            if (_timer != null) _timer.color = metal;
            if (_embers != null) _embers.gameObject.SetActive(mythic);
            if (_panel != null)
            {
                bool fighting = !mythic && Cursor.lockState == CursorLockMode.Locked;
                _panel.localScale = Vector3.one * (fighting ? CombatScale : 1f);
            }
        }

        private void PlaySound(Achievement achievement)
        {
            if (_audio == null) return;
            string name = (achievement.Tags & AchievementTags.Disaster) != 0
                ? "disaster"
                : AchievementBoard.TierName(achievement.Tier).ToLowerInvariant();
            AudioClip? clip = Clip(SoundPrefix + name) ?? Clip(SoundPath);
            if (clip == null) return;
            _audio.clip = clip;
            _audio.Play();
        }

        private AudioClip? Clip(string path)
        {
            if (!_clips.TryGetValue(path, out AudioClip? clip))
            {
                clip = Resources.Load<AudioClip>(path);
                _clips[path] = clip;
            }
            return clip;
        }

        private void Animate()
        {
            if (_panel == null || _group == null) return;

            float outStart = InSeconds + _hold;
            float y;
            float alpha;
            if (_age < InSeconds)
            {
                float t = _age / InSeconds;
                y = Mathf.LerpUnclamped(HiddenY, RestY, HudStyle.EaseOutBack(t));
                alpha = HudStyle.EaseOut(t * 1.6f);
            }
            else if (_age < outStart)
            {
                y = RestY;
                alpha = 1f;
            }
            else
            {
                float t = Mathf.Clamp01((_age - outStart) / OutSeconds);
                y = Mathf.Lerp(RestY, HiddenY, t * t);
                alpha = 1f - t;
            }
            _panel.anchoredPosition = new Vector2(0f, y);
            _group.alpha = alpha;

            if (_badge != null)
            {
                float pop = HudStyle.EaseOutBack((_age - 0.12f) / 0.4f);
                _badge.rectTransform.localScale = Vector3.one * Mathf.Lerp(0.35f, 1f, pop);
                if (_shadow != null) _shadow.rectTransform.localScale = _badge.rectTransform.localScale;
                if (_declassify) _badge.fillAmount = Mathf.Clamp01((_age - 0.5f) / 1.1f);
            }

            if (_glow != null)
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin(_age * 4f);
                Color glow = _glow.color;
                glow.a = Mathf.Lerp(0.3f, 0.6f, pulse) * Mathf.Clamp01(_age / 0.3f);
                _glow.color = glow;
            }

            if (_sheen != null)
            {
                float sweep = Mathf.Clamp01((_age - 0.3f) / 0.7f);
                _sheen.anchoredPosition = new Vector2(Mathf.Lerp(-_width * 0.62f, _width * 0.62f, sweep), 0f);
                _sheen.gameObject.SetActive(sweep > 0f && sweep < 1f);
            }

            if (_timer != null)
            {
                float left = 1f - Mathf.Clamp01((_age - InSeconds) / _hold);
                _timer.rectTransform.sizeDelta = new Vector2((_width - 28f) * left, 3f);
            }

            if (_mythic) AnimateEmbers();
        }

        private void AnimateEmbers()
        {
            for (int i = 0; i < _emberDots.Count; i++)
            {
                float speed = 0.22f + 0.05f * (i % 5);
                float phase = (_age * speed + i * 0.137f) % 1f;
                float x = 30f + (i * 173f) % (MythicWidth - 60f) + Mathf.Sin(_age * 2f + i) * 10f;
                Image dot = _emberDots[i];
                dot.rectTransform.anchoredPosition = new Vector2(x, 4f + phase * (MythicHeight - 8f));
                Color c = dot.color;
                c.a = Mathf.Sin(phase * Mathf.PI) * 0.85f;
                dot.color = c;
            }
        }

        // ------------------------------------------------------------------ round summary

        private void ComposeSummary(RectTransform root)
        {
            _summary = Ui.Child(root, "Round Summary");
            _summary.anchorMin = _summary.anchorMax = new Vector2(0.5f, 0f);
            _summary.pivot = new Vector2(0.5f, 0f);
            _summary.sizeDelta = new Vector2(760f, 40f);
            _summary.anchoredPosition = new Vector2(0f, 120f);
            _summaryGroup = _summary.gameObject.AddComponent<CanvasGroup>();
            _summaryGroup.blocksRaycasts = false;
            _summaryGroup.interactable = false;
            _summaryGroup.alpha = 0f;

            AngularPanel face = Ui.Panel(_summary, "Face", new Color(0.02f, 0.06f, 0.1f, 0.92f), 12f,
                UiStyle.WithAlpha(UiStyle.Amber, 0.7f), AngularEdge.All, 1.5f);
            Ui.Stretch((RectTransform)face.transform);
            Image icon = Ui.Icon(_summary, "Trophy", "trophy", UiStyle.Amber);
            Ui.TopLeft(icon.rectTransform, new Vector2(18f, 12f), new Vector2(18f, 18f));
            Text heading = Ui.Label(_summary, "Heading", "ROUND ACHIEVEMENTS", 13, Ui.Weight.Bold, UiStyle.Amber);
            Ui.TopLeft(heading.rectTransform, new Vector2(44f, 10f), new Vector2(400f, 22f));
            for (int i = 0; i < 8; i++)
            {
                Text line = Ui.Label(_summary, "Line " + i, string.Empty, 15, Ui.Weight.Regular, UiStyle.Ink);
                Ui.TopLeft(line.rectTransform, new Vector2(44f, 38f + i * 24f), new Vector2(700f, 22f));
                line.horizontalOverflow = HorizontalWrapMode.Overflow;
                _summaryLines.Add(line);
            }
        }

        private void ShowSummary() => ShowSummary(AchievementLedger.RoundSummary);

        private void ShowSummary(List<RoundSummaryLine>? lines)
        {
            if (_summary == null || lines == null || lines.Count == 0) return;
            int shown = Mathf.Min(lines.Count, _summaryLines.Count);
            for (int i = 0; i < _summaryLines.Count; i++)
            {
                bool on = i < shown;
                _summaryLines[i].gameObject.SetActive(on);
                if (!on) continue;
                RoundSummaryLine line = lines[i];
                Color metal = AchievementArt.TierColour(line.Achievement.Tier);
                _summaryLines[i].text = line.Text;
                _summaryLines[i].color = line.Kind == RoundSummaryKind.Unlocked ? metal
                    : line.Kind == RoundSummaryKind.Milestone ? UiStyle.Amber
                    : UiStyle.Ink;
                _summaryLines[i].fontStyle = line.Kind == RoundSummaryKind.Progress ? FontStyle.Normal : FontStyle.Bold;
            }
            _summary.sizeDelta = new Vector2(760f, 46f + shown * 24f);
            _summaryAge = 0f;
            AnimateSummary();
        }

        private void AnimateSummary()
        {
            if (_summaryGroup == null || _summary == null) return;
            float fadeIn = Mathf.Clamp01(_summaryAge / 0.4f);
            float fadeOut = Mathf.Clamp01((SummarySeconds - _summaryAge) / 0.6f);

            // The Tab board fills the screen; the card fades out while it is open, not over it.
            bool boardOpen = NetClientBindings.MatchHud is MatchHud hud && hud.IsScoreboardVisible;
            _summaryShown = Mathf.MoveTowards(_summaryShown, boardOpen ? 0f : 1f, Time.unscaledDeltaTime * 8f);
            _summaryGroup.alpha = Mathf.Min(fadeIn, fadeOut) * _summaryShown;
            _summary.anchoredPosition = new Vector2(0f, Mathf.Lerp(90f, 120f, HudStyle.EaseOut(fadeIn)));
            if (_summaryAge >= SummarySeconds) _summaryAge = -1f;
        }
    }
}
