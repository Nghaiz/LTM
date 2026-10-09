#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Ironfront.Net.Unity.Client.Menu;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// SETTINGS: display, graphics, audio, gameplay, vehicles and controls, in the menu and in a
    /// match alike. Edits are a draft (<see cref="SettingsDraft"/>) until APPLY; leaving with unsaved
    /// changes asks first.
    /// </summary>
    /// <remarks>
    /// It replaces two screens that disagreed: the menu's settings page, which half of its rows said
    /// IN DEVELOPMENT and whose sensitivity reached the game only after a restart, and the match's
    /// own options panel, which held the gameplay options the menu did not. Every row here is a real
    /// setting, with a line on what it does in the panel on the right.
    /// </remarks>
    public sealed class SettingsPage : OverlayPageView
    {
        private const float TabWidth = 236f;
        private const float RowsLeft = 262f;
        private const float RowsWidth = 860f;
        private const float InfoWidth = 400f;
        private const float ControlWidth = 380f;

        private static readonly (SettingsCategory Category, string Caption, string Icon)[] Categories =
        {
            (SettingsCategory.Display, "DISPLAY", "monitor"),
            (SettingsCategory.Graphics, "GRAPHICS", "eye"),
            (SettingsCategory.Audio, "AUDIO", "speaker"),
            (SettingsCategory.Gameplay, "GAMEPLAY", "crosshair"),
            (SettingsCategory.Vehicles, "VEHICLES", "helicopter"),
            (SettingsCategory.Controls, "CONTROLS", "keyboard"),
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() => OverlayHost.RegisterPage<SettingsPage>(OverlayPage.Settings);

        private readonly Dictionary<SettingsCategory, GameObject> _groups = new Dictionary<SettingsCategory, GameObject>();
        private readonly Dictionary<SettingsCategory, (Button Button, Image Bar)> _tabs =
            new Dictionary<SettingsCategory, (Button, Image)>();
        private readonly List<Action> _refreshers = new List<Action>();
        private readonly List<DisplayResolutionOption> _resolutions = new List<DisplayResolutionOption>();

        private SettingsDraft _draft = new SettingsDraft();
        private SettingsDraft _saved = new SettingsDraft();
        private SettingsCategory _category = SettingsCategory.Display;
        private Text? _infoTitle;
        private Text? _infoBody;
        private Text? _notice;
        private Text? _status;
        private GameObject? _confirm;
        private Action? _pendingLeave;
        private KeyBindingList? _keys;
        private bool _refreshing;

        public override OverlayPage Page => OverlayPage.Settings;
        public override string Title => "SETTINGS";
        public override string Kicker => "SYSTEM CONTROL // CLIENT CONFIGURATION";
        public override string Subtitle => "Display, graphics, audio and gameplay options, and your own key bindings.";
        public override string IconName => "gear";

        public override void Build(RectTransform content, RectTransform actions)
        {
            BuildTabs(content);
            BuildInfo(content);
            foreach (var (category, caption, _) in Categories)
                _groups[category] = BuildGroup(content, category, caption);
            BuildActions(actions);
            BuildConfirm(content);
            SettingRow.Focused += ShowInfo;
        }

        private void OnDestroy() => SettingRow.Focused -= ShowInfo;

        private static SettingsCategory? _requested;

        /// <summary>Opens on <paramref name="category"/> the next time the page is shown (the guide's CHANGE KEYS).</summary>
        public static void RequestCategory(SettingsCategory category) => _requested = category;

        public override void OnShown()
        {
            if (_requested.HasValue)
            {
                _category = _requested.Value;
                _requested = null;
            }

            _resolutions.Clear();
            var detected = new List<DisplayResolutionOption>();
            foreach (Resolution r in Screen.resolutions) detected.Add(new DisplayResolutionOption(r.width, r.height));
            detected.Add(new DisplayResolutionOption(Screen.width, Screen.height));
            _resolutions.AddRange(DisplayResolutionCatalog.Build(detected));

            _saved = SettingsDraft.Load();
            _draft = _saved.Clone();
            HideConfirm();
            ShowCategory(_category);
            Refresh();
        }

        public override bool HandleEscape()
        {
            if (_keys != null && _keys.CancelCapture()) return true;
            if (_confirm != null && _confirm.activeSelf)
            {
                HideConfirm();
                return true;
            }
            return false;
        }

        public override bool TryLeave(Action leave)
        {
            if (!_draft.DiffersFrom(_saved)) return true;
            _pendingLeave = leave;
            if (_confirm != null) _confirm.SetActive(true);
            return false;
        }

        // ------------------------------------------------------------------ layout

        private void BuildTabs(RectTransform content)
        {
            float y = 0f;
            int number = 1;
            foreach (var (category, caption, icon) in Categories)
            {
                Button tab = Ui.Button(content, "Tab " + caption, number.ToString("00", CultureInfo.InvariantCulture) + "  " + caption,
                    UiStyle.Secondary, new Vector2(TabWidth, 58f), icon);
                Ui.TopLeft((RectTransform)tab.transform, new Vector2(0f, y), new Vector2(TabWidth, 58f));
                Text caption2 = tab.transform.Find("Caption").GetComponent<Text>();
                caption2.alignment = TextAnchor.MiddleLeft;
                Image bar = Ui.Fill(tab.transform, "Active", UiStyle.Orange);
                bar.rectTransform.anchorMin = new Vector2(0f, 0f);
                bar.rectTransform.anchorMax = new Vector2(0f, 1f);
                bar.rectTransform.pivot = new Vector2(0f, 0.5f);
                bar.rectTransform.sizeDelta = new Vector2(4f, -12f);
                bar.rectTransform.anchoredPosition = new Vector2(2f, 0f);
                SettingsCategory chosen = category;
                tab.onClick.AddListener(() => ShowCategory(chosen));
                _tabs[category] = (tab, bar);
                y += 68f;
                number++;
            }
        }

        private void BuildInfo(RectTransform content)
        {
            AngularPanel panel = Ui.Panel(content, "Info", UiStyle.WithAlpha(UiStyle.Hex("061523"), 0.9f), UiStyle.CutCard,
                UiStyle.WithAlpha(UiStyle.Hairline, 0.6f));
            var rect = (RectTransform)panel.transform;
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(InfoWidth, 0f);

            Text kicker = Ui.Label(rect, "Kicker", "ABOUT THIS SETTING", 12, Ui.Weight.Bold, UiStyle.CyanSoft);
            Ui.TopLeft(kicker.rectTransform, new Vector2(28f, 26f), new Vector2(InfoWidth - 56f, 18f));
            _infoTitle = Ui.Label(rect, "Title", string.Empty, 24, Ui.Weight.Black, UiStyle.Ink, TextAnchor.UpperLeft);
            Ui.TopLeft(_infoTitle.rectTransform, new Vector2(28f, 52f), new Vector2(InfoWidth - 56f, 64f));
            _infoBody = Ui.Label(rect, "Body", string.Empty, 16, Ui.Weight.Regular, UiStyle.Muted, TextAnchor.UpperLeft);
            Ui.TopLeft(_infoBody.rectTransform, new Vector2(28f, 120f), new Vector2(InfoWidth - 56f, 300f));
            _notice = Ui.Label(rect, "Notice", string.Empty, 15, Ui.Weight.Bold, UiStyle.Amber, TextAnchor.LowerLeft);
            _notice.rectTransform.anchorMin = new Vector2(0f, 0f);
            _notice.rectTransform.anchorMax = new Vector2(1f, 0f);
            _notice.rectTransform.pivot = new Vector2(0.5f, 0f);
            _notice.rectTransform.anchoredPosition = new Vector2(0f, 24f);
            _notice.rectTransform.sizeDelta = new Vector2(-56f, 160f);
        }

        private GameObject BuildGroup(RectTransform content, SettingsCategory category, string caption)
        {
            RectTransform group = Ui.Child(content, caption + " Group");
            group.anchorMin = new Vector2(0f, 0f);
            group.anchorMax = new Vector2(0f, 1f);
            group.pivot = new Vector2(0f, 0.5f);
            group.anchoredPosition = new Vector2(RowsLeft, 0f);
            group.sizeDelta = new Vector2(RowsWidth, 0f);

            ScrollRect scroll = Ui.Scroll(group, "Rows", out RectTransform rows);
            Ui.Stretch((RectTransform)scroll.transform);

            var y = new RowCursor(rows);
            switch (category)
            {
                case SettingsCategory.Display: BuildDisplay(y); break;
                case SettingsCategory.Graphics: BuildGraphics(y); break;
                case SettingsCategory.Audio: BuildAudio(y); break;
                case SettingsCategory.Gameplay: BuildGameplay(y); break;
                case SettingsCategory.Vehicles: BuildVehicles(y); break;
                case SettingsCategory.Controls:
                    _keys = KeyBindingList.Build(rows, RowsWidth, () => _draft.Keys, OnKeysChanged, ShowNotice);
                    break;
            }
            group.gameObject.SetActive(false);
            return group.gameObject;
        }

        private void BuildActions(RectTransform actions)
        {
            Button apply = Ui.Button(actions, "Apply", "APPLY", UiStyle.Primary, new Vector2(220f, 52f), "chevrons-up");
            Place(apply, 0f);
            apply.onClick.AddListener(Apply);
            Button reset = Ui.Button(actions, "Reset", "RESET TAB", UiStyle.Secondary, new Vector2(220f, 52f), "wrench");
            Place(reset, 236f);
            reset.onClick.AddListener(ResetCategory);

            _status = Ui.Label(actions, "Status", "NO UNSAVED CHANGES", 13, Ui.Weight.Bold, UiStyle.Faint, TextAnchor.MiddleRight);
            _status.rectTransform.anchorMin = _status.rectTransform.anchorMax = _status.rectTransform.pivot = new Vector2(1f, 0.5f);
            _status.rectTransform.anchoredPosition = new Vector2(-480f, 0f);
            _status.rectTransform.sizeDelta = new Vector2(420f, 40f);

            static void Place(Button button, float fromRight)
            {
                var rect = (RectTransform)button.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 0.5f);
                rect.anchoredPosition = new Vector2(-fromRight, 0f);
            }
        }

        private void BuildConfirm(RectTransform content)
        {
            AngularPanel bar = Ui.Panel(content, "Unsaved", UiStyle.WithAlpha(UiStyle.Hex("170A10"), 0.97f), UiStyle.CutCard,
                UiStyle.Hex("B8475A"));
            bar.raycastTarget = true;
            var rect = (RectTransform)bar.transform;
            Ui.Centre(rect, Vector2.zero, new Vector2(760f, 200f));
            Text title = Ui.Label(rect, "Title", "UNSAVED CHANGES", 26, Ui.Weight.Black, UiStyle.Ink, TextAnchor.MiddleCenter);
            Ui.Centre(title.rectTransform, new Vector2(0f, 56f), new Vector2(700f, 36f));
            Text body = Ui.Label(rect, "Body", "Apply them before leaving, or discard them?", 16, Ui.Weight.Regular, UiStyle.Muted, TextAnchor.MiddleCenter);
            Ui.Centre(body.rectTransform, new Vector2(0f, 18f), new Vector2(700f, 28f));

            Button apply = Ui.Button(rect, "ApplyLeave", "APPLY", UiStyle.Primary, new Vector2(210f, 52f));
            Ui.Centre((RectTransform)apply.transform, new Vector2(-226f, -50f), new Vector2(210f, 52f));
            apply.onClick.AddListener(() => { Apply(); Leave(); });
            Button discard = Ui.Button(rect, "Discard", "DISCARD", UiStyle.Danger, new Vector2(210f, 52f));
            Ui.Centre((RectTransform)discard.transform, new Vector2(0f, -50f), new Vector2(210f, 52f));
            discard.onClick.AddListener(() => { _draft = _saved.Clone(); Refresh(); Leave(); });
            Button keep = Ui.Button(rect, "Keep", "KEEP EDITING", UiStyle.Secondary, new Vector2(210f, 52f));
            Ui.Centre((RectTransform)keep.transform, new Vector2(226f, -50f), new Vector2(210f, 52f));
            keep.onClick.AddListener(HideConfirm);

            _confirm = bar.gameObject;
            _confirm.SetActive(false);
        }

        private void HideConfirm()
        {
            _pendingLeave = null;
            if (_confirm != null) _confirm.SetActive(false);
        }

        private void Leave()
        {
            Action? leave = _pendingLeave;
            HideConfirm();
            leave?.Invoke();
        }

        // ------------------------------------------------------------------ the rows

        private void BuildDisplay(RowCursor y)
        {
            OverlayStepper mode = AddStepper(y, "Display mode",
                "Borderless fills the screen at its own resolution and switches to other windows at once. Fullscreen and windowed use the resolution below.");
            mode.SetOptions(MenuSettingsModel.DisplayModeNames, 0);
            mode.Changed += i => Edit(() => _draft.DisplayMode = i);

            OverlayStepper resolution = AddStepper(y, "Resolution",
                "The size of the picture in windowed and fullscreen. Borderless always uses the display's own.");
            resolution.Changed += i =>
            {
                if (i < 0 || i >= _resolutions.Count) return;
                Edit(() => { _draft.ResolutionWidth = _resolutions[i].Width; _draft.ResolutionHeight = _resolutions[i].Height; });
            };

            Toggle vSync = AddSwitch(y, "V-Sync", "Waits for the display before showing each frame: no tearing, a little more input delay.");
            vSync.onValueChanged.AddListener(v => Edit(() => _draft.VSync = v));

            OverlayStepper fps = AddStepper(y, "Max FPS",
                "The highest frame rate the game will draw. A lower cap leaves more of your CPU free and keeps the fans quieter; the menus always run at 60.");
            fps.Changed += i => Edit(() => _draft.FpsLimit = CpuBudgetRules.FrameRateLimits[i]);

            Slider fov = AddSlider(y, "Field of view", "How wide you see, in degrees across the screen's height.",
                GameOptionsStore.MinFieldOfView, GameOptionsStore.MaxFieldOfView, OverlaySlider.Degrees);
            fov.wholeNumbers = true;
            fov.onValueChanged.AddListener(v => Edit(() => _draft.FieldOfView = v));

            _refreshers.Add(() =>
            {
                mode.SetIndex(_draft.DisplayMode, notify: false);
                var labels = new string[_resolutions.Count];
                for (int i = 0; i < labels.Length; i++) labels[i] = _resolutions[i].Label;
                resolution.SetOptions(labels, DisplayResolutionCatalog.FindBestIndex(_resolutions, _draft.ResolutionWidth, _draft.ResolutionHeight));
                resolution.SetInteractable(MenuSettingsModel.ModeFor(_draft.DisplayMode) != FullScreenMode.FullScreenWindow);
                vSync.SetIsOnWithoutNotify(_draft.VSync);
                double hz = Screen.currentResolution.refreshRateRatio.value;
                var fpsLabels = new string[CpuBudgetRules.FrameRateLimits.Length];
                for (int i = 0; i < fpsLabels.Length; i++)
                    fpsLabels[i] = CpuBudgetRules.FrameRateLimitLabel(CpuBudgetRules.FrameRateLimits[i], hz).Replace("MAX FPS: ", string.Empty);
                fps.SetOptions(fpsLabels, CpuBudgetRules.FrameRateLimitIndex(_draft.FpsLimit));
                fov.SetValueWithoutNotify(_draft.FieldOfView);
                vSync.onValueChanged.Invoke(_draft.VSync);
                fov.onValueChanged.Invoke(_draft.FieldOfView);
            });
        }

        private void BuildGraphics(RowCursor y)
        {
            OverlayStepper quality = AddStepper(y, "Quality preset",
                "Shadows, textures, draw distance and effects together. The game picks one that suits this machine the first time it starts.");
            quality.Changed += i => Edit(() => _draft.Quality = i);

            Slider density = AddSlider(y, "Grass density", "How thick the grass and bushes grow. Lower is lighter on the GPU.", 0f, 1f, OverlaySlider.Percent);
            density.onValueChanged.AddListener(v => Edit(() => _draft.VegetationDensity = v));
            Slider distance = AddSlider(y, "Grass distance", "How far away grass is still drawn.", 0f, 1f, OverlaySlider.Percent);
            distance.onValueChanged.AddListener(v => Edit(() => _draft.VegetationDistance = v));

            _refreshers.Add(() =>
            {
                string[] names = QualitySettings.names;
                var upper = new string[names.Length];
                for (int i = 0; i < names.Length; i++) upper[i] = names[i].ToUpperInvariant();
                quality.SetOptions(upper, _draft.Quality);
                density.SetValueWithoutNotify(_draft.VegetationDensity);
                distance.SetValueWithoutNotify(_draft.VegetationDistance);
                distance.interactable = _draft.VegetationDensity >= 0.01f;
                density.onValueChanged.Invoke(_draft.VegetationDensity);
                distance.onValueChanged.Invoke(_draft.VegetationDistance);
            });
        }

        private void BuildAudio(RowCursor y)
        {
            Slider volume = AddSlider(y, "Master volume", "Every sound in the game: gunfire, vehicles, the world and the menus.", 0f, 1f, OverlaySlider.Percent);
            volume.onValueChanged.AddListener(v => Edit(() => _draft.MasterVolume = v));
            _refreshers.Add(() =>
            {
                volume.SetValueWithoutNotify(_draft.MasterVolume);
                volume.onValueChanged.Invoke(_draft.MasterVolume);
            });
        }

        private void BuildGameplay(RowCursor y)
        {
            Slider sensitivity = AddSlider(y, "Mouse sensitivity", "How far the view turns for a move of the mouse.",
                GameOptionsStore.MinSensitivity, GameOptionsStore.MaxSensitivity, OverlaySlider.TwoPlaces);
            sensitivity.onValueChanged.AddListener(v => Edit(() => _draft.MouseSensitivity = v));
            Slider scope = AddSlider(y, "Scope sensitivity", "The mouse's speed while looking through a magnified scope, as a share of the above.",
                GameOptionsStore.MinSensitivity, GameOptionsStore.MaxSensitivity, OverlaySlider.TwoPlaces);
            scope.onValueChanged.AddListener(v => Edit(() => _draft.ScopeMultiplier = v));
            Toggle invert = AddSwitch(y, "Invert mouse Y", "Pushing the mouse forward looks down, as in a flight stick.");
            invert.onValueChanged.AddListener(v => Edit(() => _draft.InvertMouse = v));
            Toggle aim = AddSwitch(y, "Toggle aim", "One press of the aim key raises the sights and the next lowers them, instead of holding it.");
            aim.onValueChanged.AddListener(v => Edit(() => _draft.ToggleAim = v));
            Toggle crouch = AddSwitch(y, "Toggle crouch", "One press of the crouch key crouches and the next stands, instead of holding it.");
            crouch.onValueChanged.AddListener(v => Edit(() => _draft.ToggleCrouch = v));
            Toggle reload = AddSwitch(y, "Auto reload", "Reload by itself when the magazine runs dry.");
            reload.onValueChanged.AddListener(v => Edit(() => _draft.AutoReload = v));
            Toggle hits = AddSwitch(y, "Hit indicators", "A mark and a click when your shot lands.");
            hits.onValueChanged.AddListener(v => Edit(() => _draft.HitIndicators = v));
            OverlayStepper skill = AddStepper(y, "Practice bot skill", "How sharp the bots are in an offline practice match. Online bots are the server's.");
            skill.SetOptions(GameOptionsStore.DifficultyNames, 0);
            skill.Changed += i => Edit(() => _draft.Difficulty = i);

            _refreshers.Add(() =>
            {
                sensitivity.SetValueWithoutNotify(_draft.MouseSensitivity);
                scope.SetValueWithoutNotify(_draft.ScopeMultiplier);
                sensitivity.onValueChanged.Invoke(_draft.MouseSensitivity);
                scope.onValueChanged.Invoke(_draft.ScopeMultiplier);
                invert.SetIsOnWithoutNotify(_draft.InvertMouse);
                aim.SetIsOnWithoutNotify(_draft.ToggleAim);
                crouch.SetIsOnWithoutNotify(_draft.ToggleCrouch);
                reload.SetIsOnWithoutNotify(_draft.AutoReload);
                hits.SetIsOnWithoutNotify(_draft.HitIndicators);
                foreach (Toggle t in new[] { invert, aim, crouch, reload, hits }) t.onValueChanged.Invoke(t.isOn);
                skill.SetIndex(_draft.Difficulty, notify: false);
            });
        }

        private void BuildVehicles(RowCursor y)
        {
            OverlayStepper style = AddStepper(y, "Helicopter controls",
                "Mouse roll: the mouse banks and pitches, A/D turn the tail. Mouse yaw: the mouse turns and pitches, A/D bank. Joystick: a flight stick's own axes.");
            style.SetOptions(GameOptionsStore.HelicopterStyleNames, 0);
            style.Changed += i => Edit(() => _draft.HelicopterStyle = i);
            Slider sensitivity = AddSlider(y, "Helicopter sensitivity", "How hard the helicopter answers the mouse or the stick.",
                GameOptionsStore.MinSensitivity, GameOptionsStore.MaxSensitivity, OverlaySlider.TwoPlaces);
            sensitivity.onValueChanged.AddListener(v => Edit(() => _draft.HelicopterSensitivity = v));
            Toggle pitch = AddSwitch(y, "Invert helicopter pitch", "Pushing forward lifts the nose instead of dipping it.");
            pitch.onValueChanged.AddListener(v => Edit(() => _draft.HelicopterInvertPitch = v));
            Toggle yaw = AddSwitch(y, "Invert joystick yaw", "Joystick controls only: turns the tail the other way.");
            yaw.onValueChanged.AddListener(v => Edit(() => _draft.HelicopterInvertYaw = v));
            Toggle roll = AddSwitch(y, "Invert joystick roll", "Joystick controls only: banks the other way.");
            roll.onValueChanged.AddListener(v => Edit(() => _draft.HelicopterInvertRoll = v));
            Toggle throttle = AddSwitch(y, "Invert joystick throttle", "Joystick controls only: the throttle axis climbs the other way.");
            throttle.onValueChanged.AddListener(v => Edit(() => _draft.HelicopterInvertThrottle = v));

            _refreshers.Add(() =>
            {
                style.SetIndex(_draft.HelicopterStyle, notify: false);
                sensitivity.SetValueWithoutNotify(_draft.HelicopterSensitivity);
                sensitivity.onValueChanged.Invoke(_draft.HelicopterSensitivity);
                pitch.SetIsOnWithoutNotify(_draft.HelicopterInvertPitch);
                yaw.SetIsOnWithoutNotify(_draft.HelicopterInvertYaw);
                roll.SetIsOnWithoutNotify(_draft.HelicopterInvertRoll);
                throttle.SetIsOnWithoutNotify(_draft.HelicopterInvertThrottle);
                bool joystick = _draft.HelicopterStyle == 2;
                foreach (Toggle t in new[] { yaw, roll, throttle }) t.interactable = joystick;
                foreach (Toggle t in new[] { pitch, yaw, roll, throttle }) t.onValueChanged.Invoke(t.isOn);
            });
        }

        // ------------------------------------------------------------------ row helpers

        private sealed class RowCursor
        {
            private readonly RectTransform _rows;
            private float _y;

            public RowCursor(RectTransform rows) => _rows = rows;

            public SettingRow Next(string title, string description)
            {
                SettingRow row = SettingRow.Create(_rows, title, description, RowsWidth, ControlWidth);
                Ui.TopLeft((RectTransform)row.transform, new Vector2(0f, _y), new Vector2(RowsWidth, SettingRow.Height - 6f));
                _y += SettingRow.Height;
                _rows.sizeDelta = new Vector2(0f, _y);
                return row;
            }
        }

        private OverlayStepper AddStepper(RowCursor y, string title, string description)
            => OverlayStepper.Create(y.Next(title, description).Control);

        private Toggle AddSwitch(RowCursor y, string title, string description)
            => OverlaySwitch.Create(y.Next(title, description).Control);

        private Slider AddSlider(RowCursor y, string title, string description, float min, float max, Func<float, string> format)
            => OverlaySlider.Create(y.Next(title, description).Control, min, max, format, out _);

        // ------------------------------------------------------------------ behaviour

        private void ShowCategory(SettingsCategory category)
        {
            _category = category;
            foreach (KeyValuePair<SettingsCategory, GameObject> group in _groups) group.Value.SetActive(group.Key == category);
            foreach (KeyValuePair<SettingsCategory, (Button Button, Image Bar)> tab in _tabs)
            {
                bool on = tab.Key == category;
                tab.Value.Bar.enabled = on;
                tab.Value.Button.interactable = !on;
            }
            _keys?.CancelCapture();
            ShowNotice(string.Empty);
            foreach (var (c, caption, _) in Categories)
                if (c == category) ShowInfo(caption, CategoryText(c));
        }

        private static string CategoryText(SettingsCategory category) => category switch
        {
            SettingsCategory.Display => "How the game fills your screen and how fast it draws. Point at a setting to read what it does.",
            SettingsCategory.Graphics => "How detailed the world is drawn. Lower settings free the GPU and the CPU for a steadier frame rate.",
            SettingsCategory.Audio => "How loud the game is.",
            SettingsCategory.Gameplay => "How aiming, crouching and reloading feel.",
            SettingsCategory.Vehicles => "How the helicopter flies. Cars, tanks and boats use your movement keys.",
            _ => "Click a key to change it, then press the new one. Backspace or Delete clears it, Esc cancels. A key used elsewhere moves here, and the other action is left unbound until you give it another.",
        };

        private void ShowInfo(SettingRow row)
        {
            if (row == null || !row.transform.IsChildOf(transform)) return;
            ShowInfo(row.Title.ToUpperInvariant(), row.Description);
        }

        private void ShowInfo(string title, string body)
        {
            if (_infoTitle != null) _infoTitle.text = title;
            if (_infoBody != null) _infoBody.text = body;
        }

        private void ShowNotice(string text)
        {
            if (_notice != null) _notice.text = text;
        }

        private void Edit(Action change)
        {
            if (_refreshing) return;
            change();
            Refresh();
        }

        private void OnKeysChanged() => UpdateStatus();

        private void Refresh()
        {
            _refreshing = true;
            try
            {
                foreach (Action refresh in _refreshers) refresh();
                _keys?.Refresh();
            }
            finally
            {
                _refreshing = false;
            }
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_status == null) return;
            bool dirty = _draft.DiffersFrom(_saved);
            _status.text = dirty ? "UNSAVED CHANGES" : "NO UNSAVED CHANGES";
            _status.color = dirty ? UiStyle.Amber : UiStyle.Faint;
        }

        private void Apply()
        {
            _keys?.CancelCapture();
            _draft.Save();
            _saved = _draft.Clone();
            UpdateStatus();
            if (_status != null)
            {
                _status.text = "SETTINGS SAVED";
                _status.color = UiStyle.Green;
            }
        }

        private void ResetCategory()
        {
            _keys?.CancelCapture();
            _draft.ResetToDefaults(_category);
            Refresh();
            ShowNotice(_category == SettingsCategory.Controls ? "Every key is back on its default. Press APPLY to keep it." : string.Empty);
        }
    }
}
