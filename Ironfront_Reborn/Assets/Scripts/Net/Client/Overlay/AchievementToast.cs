#nullable enable

using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Unity.Client.Hud;
using Ironfront.Net.Unity.Client.Menu;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// The unlock banner (owner's list of 2026-10-09, item 4): top-middle, over the menu, the
    /// match and the overlay alike, one achievement at a time with a chime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How it moves.</b> The banner drops in from above the screen's edge with a little
    /// overshoot, the badge pops in just after it, a sheen crosses the panel once, and a bar in
    /// the metal's colour runs down while it holds. Then it rises out and the next one, if any,
    /// follows. Timed on unscaled time, so a paused match still shows it.
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

        /// <summary>The chime, in <c>Resources</c>; written by <c>tools/ui/make_achievement_sound.py</c>.</summary>
        public const string SoundPath = "IronfrontUi/achievement-unlocked";

        private const float Width = 640f;
        private const float Height = 116f;
        private const float RestY = -28f;
        private const float HiddenY = 150f;
        private const float InSeconds = 0.5f;
        private const float HoldSeconds = 5f;
        private const float OutSeconds = 0.35f;
        private const float GapSeconds = 0.25f;

        private static AchievementToast? _instance;

        /// <summary>A golden wrench found on the menu, waiting for its banner (<see cref="GoldenWrench"/>).</summary>
        private static bool _wrenchNoticePending;
        private static Sprite? _wrenchPicture;

        private RectTransform? _panel;
        private CanvasGroup? _group;
        private AngularPanel? _face;
        private Image? _glow;
        private Image? _badge;
        private Image? _trophy;
        private Text? _kicker;
        private Text? _title;
        private Text? _line;
        private Text? _tier;
        private Image? _timer;
        private RectTransform? _sheen;
        private AudioSource? _audio;

        private bool _showing;
        private float _age;
        private float _idle;

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
        public void ShowForTool(Achievement achievement, float seconds)
        {
            Begin(achievement, playSound: false);
            _age = seconds;
            Animate();
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

            // The sheen: a pale slanted band that crosses the face once, clipped to it.
            RectTransform clip = Ui.Child(_panel, "Sheen Clip");
            Ui.Stretch(clip, 3f, 3f, 3f, 3f);
            clip.gameObject.AddComponent<RectMask2D>();
            Image sheen = Ui.Fill(clip, "Sheen", new Color(1f, 1f, 1f, 0.1f));
            _sheen = sheen.rectTransform;
            _sheen.anchorMin = _sheen.anchorMax = new Vector2(0.5f, 0.5f);
            _sheen.sizeDelta = new Vector2(70f, Height * 2f);
            _sheen.localRotation = Quaternion.Euler(0f, 0f, -22f);

            _glow = Ui.Fill(_panel, "Glow", Color.white);
            _glow.sprite = HudSprites.Glow();
            Ui.TopLeft(_glow.rectTransform, new Vector2(-6f, -12f), new Vector2(140f, 140f));

            _badge = Ui.Art(_panel, "Badge", null, Color.white);
            Ui.TopLeft(_badge.rectTransform, new Vector2(20f, 14f), new Vector2(88f, 88f));

            _trophy = Ui.Icon(_panel, "Trophy", "trophy", Color.white);
            Ui.TopLeft(_trophy.rectTransform, new Vector2(126f, 17f), new Vector2(16f, 16f));
            _kicker = Ui.Label(_panel, "Kicker", "ACHIEVEMENT UNLOCKED", 13, Ui.Weight.Bold, Color.white);
            Ui.TopLeft(_kicker.rectTransform, new Vector2(148f, 14f), new Vector2(300f, 22f));

            _tier = Ui.Label(_panel, "Tier", string.Empty, 12, Ui.Weight.Bold, Color.white, TextAnchor.MiddleRight);
            Ui.TopLeft(_tier.rectTransform, new Vector2(Width - 236f, 14f), new Vector2(214f, 22f));

            _title = Ui.Label(_panel, "Title", string.Empty, 26, Ui.Weight.Black, UiStyle.Ink);
            Ui.TopLeft(_title.rectTransform, new Vector2(126f, 36f), new Vector2(Width - 150f, 36f));
            _title.horizontalOverflow = HorizontalWrapMode.Overflow;

            _line = Ui.Label(_panel, "Line", string.Empty, 15, Ui.Weight.Regular, UiStyle.Muted);
            Ui.TopLeft(_line.rectTransform, new Vector2(126f, 72f), new Vector2(Width - 150f, 24f));

            _timer = Ui.Fill(_panel, "Timer", Color.white);
            _timer.rectTransform.anchorMin = new Vector2(0f, 0f);
            _timer.rectTransform.anchorMax = new Vector2(0f, 0f);
            _timer.rectTransform.pivot = new Vector2(0f, 0f);
            _timer.rectTransform.anchoredPosition = new Vector2(14f, 6f);
            _timer.rectTransform.sizeDelta = new Vector2(Width - 28f, 3f);

            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 0f;
            _audio.ignoreListenerPause = true;
            _audio.volume = 0.8f;
            _audio.clip = Resources.Load<AudioClip>(SoundPath);

            _group.alpha = 0f;
        }

        private void Update()
        {
            AchievementLedger.Tick();

            if (_showing)
            {
                _age += Time.unscaledDeltaTime;
                Animate();
                if (_age >= InSeconds + HoldSeconds + OutSeconds)
                {
                    _showing = false;
                    _idle = 0f;
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
            else if (AchievementLedger.TryNextToast(out Achievement next)) Begin(next, playSound: true);
        }

        /// <summary>
        /// The golden wrench's banner: the secret the original game kept behind <c>ISEEGOLD</c>.
        /// Gold, like the wrench; no chime, because the original's own unlock sound plays with it.
        /// </summary>
        private void BeginWrenchNotice()
        {
            Color gold = AchievementArt.TierColour(AchievementTier.Gold);
            if (_face != null) _face.Configure(14f, AngularEdge.All, 2f, UiStyle.WithAlpha(gold, 0.9f));
            if (_glow != null) _glow.color = UiStyle.WithAlpha(gold, 0.5f);
            if (_badge != null) _badge.sprite = _wrenchPicture;
            if (_trophy != null) _trophy.color = gold;
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
            if (_title != null) _title.text = "THE GOLDEN WRENCH IS YOURS";
            if (_line != null) _line.text = "Pick it from GEAR on the loadout screen of a practice match.";
            if (_timer != null) _timer.color = gold;

            _showing = true;
            _age = 0f;
            Animate();
        }

        private void Begin(Achievement achievement, bool playSound)
        {
            Color metal = AchievementArt.TierColour(achievement.Tier);
            if (_face != null) _face.Configure(14f, AngularEdge.All, 2f, UiStyle.WithAlpha(metal, 0.9f));
            if (_glow != null) _glow.color = UiStyle.WithAlpha(metal, 0.5f);
            if (_badge != null) _badge.sprite = AchievementArt.Badge(achievement, revealed: true);
            if (_trophy != null) _trophy.color = metal;
            if (_kicker != null)
            {
                _kicker.text = "ACHIEVEMENT UNLOCKED";
                _kicker.color = metal;
            }
            if (_tier != null)
            {
                _tier.text = AchievementBoard.TierName(achievement.Tier) + "  //  +" + achievement.Points + " PTS";
                _tier.color = UiStyle.WithAlpha(metal, 0.85f);
            }
            if (_title != null) _title.text = achievement.Title;
            if (_line != null) _line.text = achievement.Description;
            if (_timer != null) _timer.color = metal;

            _showing = true;
            _age = 0f;
            Animate();

            if (playSound && _audio != null && _audio.clip != null) _audio.Play();
        }

        private void Animate()
        {
            if (_panel == null || _group == null) return;

            float outStart = InSeconds + HoldSeconds;
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
                _sheen.anchoredPosition = new Vector2(Mathf.Lerp(-Width * 0.62f, Width * 0.62f, sweep), 0f);
                _sheen.gameObject.SetActive(sweep > 0f && sweep < 1f);
            }

            if (_timer != null)
            {
                float left = 1f - Mathf.Clamp01((_age - InSeconds) / HoldSeconds);
                _timer.rectTransform.sizeDelta = new Vector2((Width - 28f) * left, 3f);
            }
        }
    }
}
