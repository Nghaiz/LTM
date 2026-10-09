#nullable enable

using System;
using System.Collections.Generic;
using Ironfront.Net.Unity.Client.Menu;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// The full-screen overlay a player opens from the main menu, from the pause menu and from the
    /// deploy screen: one frame and one set of pages (<see cref="OverlayPageView"/>), in every scene.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One implementation everywhere.</b> Settings, the guide, achievements and the ranking have
    /// to read the same in the menu and in a match, so they are not authored into either: the host
    /// builds itself on first use, survives scene loads, and draws over everything (sorting order
    /// <see cref="SortingOrder"/>). In the menu the page sits on the painted backdrop; in a match on
    /// a dimmed view of the battle.
    /// </para>
    /// <para>
    /// <b>Escape</b> goes to the page first (a key being rebound, an unsaved-changes bar), then
    /// closes the overlay; the frame it is used in is published as
    /// <see cref="GameOverlays.EscapeConsumedFrame"/> so the pause menu does not toggle on the same
    /// press. The cursor is freed while the overlay is up and put back as it was after.
    /// </para>
    /// <para>Built in code at runtime: it must exist in every scene without being authored into each.</para>
    /// </remarks>
    public sealed class OverlayHost : MonoBehaviour
    {
        public const int SortingOrder = 500;

        private const float FrameWidth = 1680f;
        private const float FrameHeight = 940f;
        private const float Inset = 64f;
        private const float HeadingBottom = 176f;
        private const float FooterHeight = 92f;

        /// <summary>Every page this build has, in the switcher's order.</summary>
        private static readonly List<(OverlayPage Page, Func<GameObject, OverlayPageView> Attach)> PageTypes =
            new List<(OverlayPage, Func<GameObject, OverlayPageView>)>();

        private static OverlayHost? _instance;

        private readonly Dictionary<OverlayPage, OverlayPageView> _pages = new Dictionary<OverlayPage, OverlayPageView>();
        private readonly Dictionary<OverlayPage, (Button Button, Image Bar)> _switcher =
            new Dictionary<OverlayPage, (Button, Image)>();
        private readonly Dictionary<OverlayPage, RectTransform> _actions = new Dictionary<OverlayPage, RectTransform>();

        private Canvas? _canvas;
        private CanvasGroup? _group;
        private Image? _art;
        private Image? _shade;
        private RectTransform? _frame;
        private Text? _kicker;
        private Text? _title;
        private Text? _subtitle;
        private OverlayPage _current = OverlayPage.None;
        private float _openness;
        private CursorLockMode _savedLock;
        private bool _savedVisible;

        /// <summary>Adds a page kind, in switcher order; each page type registers itself once.</summary>
        public static void RegisterPage<T>(OverlayPage page) where T : OverlayPageView
        {
            foreach ((OverlayPage Page, Func<GameObject, OverlayPageView> Attach) known in PageTypes)
                if (known.Page == page) return;
            PageTypes.Add((page, host => host.AddComponent<T>()));
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (Application.isBatchMode) return;

            GameOverlays.Opener = Open;
            GameOverlays.Closer = () => _instance?.Close();
            GameOverlays.Showing = () => _instance != null ? _instance._current : OverlayPage.None;
            GameOverlays.Available = Known;
        }

        private static bool Known(OverlayPage page)
        {
            foreach ((OverlayPage Page, Func<GameObject, OverlayPageView> Attach) known in PageTypes)
                if (known.Page == page) return true;
            return false;
        }

        /// <summary>Opens <paramref name="page"/>, building the overlay on first use.</summary>
        public static void Open(OverlayPage page)
        {
            if (page == OverlayPage.None) return;
            if (_instance == null) Build();
            _instance!.Show(page);
        }

        private static void Build()
        {
            _instance = Create();
            DontDestroyOnLoad(_instance.gameObject);
        }

        private static OverlayHost Create()
        {
            var root = new GameObject("Ironfront Overlay", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
            OverlayHost host = root.AddComponent<OverlayHost>();
            host.Compose();
            return host;
        }

        /// <summary>
        /// A host that is not the game's: for Editor tools that render a page to a picture. It
        /// survives no scene load and opens nothing by itself; <see cref="ShowForTool"/> shows a page.
        /// </summary>
        public static OverlayHost CreateDetached() => Create();

        /// <summary>Shows <paramref name="page"/> at once, fully faded in. For Editor tools.</summary>
        public void ShowForTool(OverlayPage page)
        {
            Show(page);
            _openness = 1f;
            if (_group != null) _group.alpha = 1f;
            if (_frame != null) _frame.localScale = Vector3.one;
        }

        private void Compose()
        {
            _canvas = GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = SortingOrder;
            _canvas.pixelPerfect = true;

            var scaler = GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            _group = GetComponent<CanvasGroup>();
            var root = (RectTransform)transform;

            _art = Ui.Art(root, "Backdrop", UiSkin.Current.MultiplayerBackground, Color.white);
            _art.preserveAspect = false;
            Ui.Stretch(_art.rectTransform);
            _shade = Ui.Fill(root, "Shade", new Color(0.01f, 0.04f, 0.08f, 0.6f));
            Ui.Stretch(_shade.rectTransform);
            _shade.raycastTarget = true; // swallows clicks meant for the screen behind

            AngularPanel panel = Ui.Panel(root, "Frame", Color.white, UiStyle.CutPanel);
            UiStyle.StyleOperationsPanel(panel);
            panel.raycastTarget = true;
            _frame = Ui.Centre((RectTransform)panel.transform, new Vector2(0f, -8f), new Vector2(FrameWidth, FrameHeight));

            _kicker = Ui.Label(_frame, "Kicker", string.Empty, 13, Ui.Weight.Bold, UiStyle.CyanSoft);
            Ui.TopLeft(_kicker.rectTransform, new Vector2(Inset, 38f), new Vector2(900f, 20f));
            _title = Ui.Label(_frame, "Title", string.Empty, 46, Ui.Weight.Black, UiStyle.Ink);
            Ui.TopLeft(_title.rectTransform, new Vector2(Inset, 60f), new Vector2(900f, 60f));
            _subtitle = Ui.Label(_frame, "Subtitle", string.Empty, 16, Ui.Weight.Regular, UiStyle.Muted);
            Ui.TopLeft(_subtitle.rectTransform, new Vector2(Inset, 122f), new Vector2(1000f, 26f));

            Image rule = Ui.Fill(_frame, "HeadingRule", UiStyle.WithAlpha(UiStyle.Hairline, 0.5f));
            Ui.TopLeft(rule.rectTransform, new Vector2(Inset, HeadingBottom - 14f), new Vector2(FrameWidth - 2f * Inset, 1f));

            BuildPages();
            BuildFooter();
            gameObject.SetActive(false);
        }

        private void BuildPages()
        {
            // One content rect per page, the page component on it, so hiding the rect hides the page.
            var views = new List<OverlayPageView>();
            PageTypes.Sort((x, y) => x.Page.CompareTo(y.Page));
            foreach ((OverlayPage Page, Func<GameObject, OverlayPageView> Attach) entry in PageTypes)
            {
                RectTransform content = Ui.Child(_frame!, entry.Page + " Content");
                Ui.Stretch(content, Inset, Inset, HeadingBottom, FooterHeight);
                RectTransform actions = Ui.Child(_frame!, entry.Page + " Actions");
                actions.anchorMin = new Vector2(1f, 0f);
                actions.anchorMax = new Vector2(1f, 0f);
                actions.pivot = new Vector2(1f, 0f);
                actions.anchoredPosition = new Vector2(-Inset, 22f);
                actions.sizeDelta = new Vector2(1000f, 52f);

                OverlayPageView view = entry.Attach(content.gameObject);
                view.Build(content, actions);
                content.gameObject.SetActive(false);
                actions.gameObject.SetActive(false);
                _pages[entry.Page] = view;
                _actions[entry.Page] = actions;
                views.Add(view);
            }

            // The switcher, right-aligned in the heading band: one tab per page, the open one lit.
            // With a single page there is nothing to switch to, so there is no switcher.
            if (views.Count < 2) return;
            float x = FrameWidth - Inset;
            for (int i = views.Count - 1; i >= 0; i--)
            {
                OverlayPageView view = views[i];
                const float width = 196f;
                x -= width;
                Button tab = Ui.Button(_frame!, "Switch " + view.Page, view.Title, UiStyle.Secondary,
                    new Vector2(width, 46f), view.IconName);
                Ui.TopLeft((RectTransform)tab.transform, new Vector2(x, 70f), new Vector2(width, 46f));
                Image bar = Ui.Fill(tab.transform, "Active", UiStyle.Orange);
                bar.rectTransform.anchorMin = new Vector2(0f, 0f);
                bar.rectTransform.anchorMax = new Vector2(1f, 0f);
                bar.rectTransform.pivot = new Vector2(0.5f, 0f);
                bar.rectTransform.sizeDelta = new Vector2(-12f, 3f);
                bar.rectTransform.anchoredPosition = new Vector2(0f, 3f);
                OverlayPage page = view.Page;
                tab.onClick.AddListener(() => Show(page));
                _switcher[page] = (tab, bar);
                x -= 10f;
            }
        }

        private void BuildFooter()
        {
            Image rule = Ui.Fill(_frame!, "FooterRule", UiStyle.WithAlpha(UiStyle.Hairline, 0.5f));
            rule.rectTransform.anchorMin = new Vector2(0f, 0f);
            rule.rectTransform.anchorMax = new Vector2(1f, 0f);
            rule.rectTransform.pivot = new Vector2(0.5f, 0f);
            rule.rectTransform.sizeDelta = new Vector2(-2f * Inset, 1f);
            rule.rectTransform.anchoredPosition = new Vector2(0f, FooterHeight - 8f);

            RectTransform escape = Ui.KeyCap(_frame!, "EscCap", "ESC");
            escape.anchorMin = escape.anchorMax = escape.pivot = new Vector2(0f, 0f);
            escape.anchoredPosition = new Vector2(Inset, 36f);
            Text hint = Ui.Label(_frame!, "EscHint", "CLOSE", 13, Ui.Weight.Bold, UiStyle.Muted);
            hint.rectTransform.anchorMin = hint.rectTransform.anchorMax = hint.rectTransform.pivot = new Vector2(0f, 0f);
            hint.rectTransform.anchoredPosition = new Vector2(Inset + escape.sizeDelta.x + 10f, 36f);
            hint.rectTransform.sizeDelta = new Vector2(300f, escape.sizeDelta.y);
        }

        private void Show(OverlayPage page)
        {
            if (!_pages.ContainsKey(page)) return;
            if (_current == page && gameObject.activeSelf) return;

            if (_current != OverlayPage.None && _pages.TryGetValue(_current, out OverlayPageView? leaving))
            {
                if (!leaving.TryLeave(() => Show(page))) return;
                SetPageActive(leaving, false);
                leaving.OnHidden();
            }

            bool opening = !gameObject.activeSelf;
            if (opening)
            {
                gameObject.SetActive(true);
                EnsureEventSystem();
                _savedLock = Cursor.lockState;
                _savedVisible = Cursor.visible;
                _openness = 0f;
            }

            bool inMatch = SceneManager.GetActiveScene().buildIndex > 1;
            if (_art != null) _art.enabled = !inMatch && _art.sprite != null;
            if (_shade != null) _shade.color = inMatch ? new Color(0.01f, 0.03f, 0.06f, 0.86f) : new Color(0.01f, 0.04f, 0.08f, 0.55f);

            _current = page;
            OverlayPageView view = _pages[page];
            if (_kicker != null) _kicker.text = view.Kicker;
            if (_title != null) _title.text = view.Title;
            if (_subtitle != null) _subtitle.text = view.Subtitle;
            foreach (KeyValuePair<OverlayPage, (Button Button, Image Bar)> tab in _switcher)
            {
                bool on = tab.Key == page;
                tab.Value.Bar.enabled = on;
                tab.Value.Button.interactable = !on;
            }

            SetPageActive(view, true);
            view.OnShown();
        }

        private void SetPageActive(OverlayPageView view, bool active)
        {
            view.gameObject.SetActive(active);
            if (_actions.TryGetValue(view.Page, out RectTransform? actions)) actions.gameObject.SetActive(active);
        }

        /// <summary>Closes the overlay, if the open page lets it.</summary>
        public void Close()
        {
            if (_current == OverlayPage.None) return;
            OverlayPageView view = _pages[_current];
            if (!view.TryLeave(Close)) return;

            SetPageActive(view, false);
            view.OnHidden();
            _current = OverlayPage.None;
            gameObject.SetActive(false);
            Cursor.lockState = _savedLock;
            Cursor.visible = _savedVisible;
        }

        private void Update()
        {
            if (_current == OverlayPage.None) return;

            // The pause menu or the deploy screen re-locks the cursor on its own schedule; while
            // the overlay is up the player needs the pointer.
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;

            _openness = Mathf.Min(1f, _openness + Time.unscaledDeltaTime / 0.16f);
            if (_group != null) _group.alpha = Hud.HudStyle.EaseOut(_openness);
            if (_frame != null) _frame.localScale = Vector3.one * Mathf.Lerp(0.985f, 1f, _openness);

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                GameOverlays.EscapeConsumedFrame = Time.frameCount;
                if (!_pages[_current].HandleEscape()) Close();
            }
        }

        /// <summary>A scene with no EventSystem still needs the overlay's buttons to answer the mouse.</summary>
        /// <remarks>
        /// Play mode only, and always under this host. An Editor tool renders a detached host in edit
        /// mode (<see cref="ShowForTool"/>); a new GameObject lands in the ACTIVE scene, which there is
        /// whatever the Editor has open, and before this guard every page capture left an EventSystem
        /// in Menu.unity: twenty of them were saved into it (2026-10-09), and the menu logged
        /// "There can be only one active Event System" twenty times at every start.
        /// </remarks>
        private void EnsureEventSystem()
        {
            if (!Application.isPlaying || EventSystem.current != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            go.transform.SetParent(transform, false);
        }
    }
}
