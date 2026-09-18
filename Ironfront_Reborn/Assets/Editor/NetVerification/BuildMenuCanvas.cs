using System.Text;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client;
using Ironfront.Net.Unity.Client.Menu;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.EditorTools
{
    /// <summary>
    /// Authors the multiplayer menu Canvas into <c>Menu.unity</c> and assigns every reference.
    /// P15 3.2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A command, not a drag — and here that is a correctness rule, not a preference.</b>
    /// 3.2 constraint 1 forbids authoring by editing scene YAML, because fileIDs are
    /// Editor-assigned and a hand-written reference resolves to null while looking assigned
    /// (P3 § 3.3). Everything below goes through the real Editor APIs, so every fileID it writes
    /// is one Unity minted. It is also re-runnable, which a drag is not: a mistake is fixed by
    /// running it again rather than by hunting a field somebody missed.
    /// </para>
    /// <para>
    /// <b>Re-running REBUILDS rather than reporting and skipping</b>, which is the opposite call
    /// from the retired WireClientFlow tool and worth saying why. That script added one component to
    /// an object a human authored, so leaving it alone is respecting authored work. This script
    /// IS the authoring for its whole subtree — every object under <see cref="RootName"/> was
    /// written by it — so a deterministic rebuild is what makes the scene a function of this
    /// file. Nothing outside that subtree is touched.
    /// </para>
    /// <para>
    /// <b>It lives in this asmdef and could not live in <c>Assets/Editor</c> proper.</b>
    /// <c>Ironfront.Net.Unity.Client</c> ships <c>autoReferenced: false</c>, so
    /// <c>Assembly-CSharp-Editor</c> cannot name <c>MenuScreenController</c> — the seal is
    /// two-way (contracts § 6.1). <c>Ironfront.Net.Unity.EditorHarness</c> exists because C5b hit
    /// exactly this, and this file is its second occupant.
    /// </para>
    /// <para>
    /// The mirror of that constraint is why this script does <b>not</b> register the two seams:
    /// <c>MenuSceneBindings</c> is in the predefined assembly, which no asmdef can reference. It
    /// installs itself from a <c>RuntimeInitializeOnLoadMethod</c> instead — see its remark.
    /// </para>
    /// <para>
    /// <b>Run headlessly:</b>
    /// <c>Unity -batchmode -nographics -quit -projectPath Ironfront_Reborn
    /// -executeMethod Ironfront.Net.Unity.EditorTools.BuildMenuCanvas.Run</c>.
    /// </para>
    /// </remarks>
    public static class BuildMenuCanvas
    {
        private const string ScenePath = "Assets/Scenes/Menu.unity";
        private const string ReportFile = "build-menu-canvas.txt";

        /// <summary>The root this script owns entirely and rebuilds on every run.</summary>
        public const string RootName = "Multiplayer Menu";

        /// <summary>
        /// Above the legacy Canvas, which uses the default 0.
        /// </summary>
        /// <remarks>
        /// The two Canvases coexist for the whole of P15 (3.2 constraint 5 keeps the legacy menu,
        /// and criterion 5 needs it), so the ordering has to be stated rather than left to
        /// hierarchy position — which changes whenever somebody reorders the scene.
        /// </remarks>
        private const int SortingOrder = 100;

        private static readonly Color Ink = Hex("E7F2FB");
        private static readonly Color ErrorInk = Hex("FF5265");
        private static readonly Color Backdrop = new Color(0.02f, 0.07f, 0.12f, 0.94f);
        private static readonly Color Cyan = Hex("35B6FF");
        private static readonly Color Orange = Hex("FF7417");
        private static readonly Color FieldInk = Hex("081827");

        // The rest of the palette, named for its token in `css/tokens.css` rather than for the
        // surface that happens to use it, so a screen below can be read against the stylesheet.
        private static readonly Color Ink900 = Hex("07111D");
        private static readonly Color CyanSoft = Hex("7ACFFF");
        private static readonly Color Green = Hex("3BDB83");
        private static readonly Color Line = new Color(113f / 255f, 179f / 255f, 226f / 255f, 0.42f);
        private static readonly Color Surface = new Color(5f / 255f, 18f / 255f, 31f / 255f, 0.93f);

        // The corners, in reference pixels, exactly as the stylesheet cuts them.
        private const float CutPanel = 20f;
        private const float CutCard = 18f;
        private const float CutMenuButton = 13f;
        private const float CutAction = 10f;

        [MenuItem("Ironfront/Net/Build multiplayer menu Canvas")]
        public static void RunFromMenu() => Execute(exitOnFailure: false);

        /// <summary>The <c>-executeMethod</c> entry point.</summary>
        public static void Run() => Execute(exitOnFailure: Application.isBatchMode);

        private static void Execute(bool exitOnFailure)
        {
            var log = new StringBuilder();
            bool ok;

            try
            {
                ok = Build(log);
            }
            catch (System.Exception ex)
            {
                log.AppendLine("FAILED: " + ex);
                ok = false;
            }

            string report = log.ToString();
            System.IO.File.WriteAllText(ReportFile, report);

            if (ok) Debug.Log("[build-menu-canvas]\n" + report);
            else Debug.LogError("[build-menu-canvas] FAILED\n" + report);

            if (!ok && exitOnFailure) EditorApplication.Exit(1);
        }

        private static bool Build(StringBuilder log)
        {
            log.AppendLine("art: " + IronfrontRebornUiAssetCatalog.ConfigureImporters());

            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath)
            {
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                log.AppendLine("opened: " + scene.path);
            }
            else
            {
                log.AppendLine("already open: " + scene.path);
            }

            RemovePreviousRoot(scene, log);

            GameObject root = new GameObject(
                RootName, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;

            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            MenuScreenController controller = root.AddComponent<MenuScreenController>();

            MenuToast toast = BuildToast(root);
            GameObject title = BuildTitle(root, controller, log);
            GameObject login = BuildLogin(root, controller, log);
            GameObject register = BuildRegister(root, controller, log);
            GameObject practice = BuildPractice(root, controller, toast, out Button practiceBack);
            GameObject settings = BuildSettings(root, toast, out Button settingsBack);
            GameObject authenticating = BuildAuthenticating(root);
            GameObject lobby = BuildLobby(root, out Text signedIn, out Button browseRooms);
            GameObject browser = BuildRoomBrowser(root, controller, toast, log);
            GameObject createRoom = BuildCreateRoom(root, controller, toast, log);
            GameObject roomLobby = BuildRoomLobby(root, controller, toast, log);

            var so = new SerializedObject(controller);
            Assign(so, "_titleScreen", title);
            Assign(so, "_loginScreen", login);
            Assign(so, "_registerScreen", register);
            Assign(so, "_authenticatingScreen", authenticating);
            Assign(so, "_lobbyScreen", lobby);
            Assign(so, "_roomBrowserScreen", browser);
            Assign(so, "_createRoomScreen", createRoom);
            Assign(so, "_roomLobbyScreen", roomLobby);
            Assign(so, "_browseRoomsButton", browseRooms);
            Assign(so, "_practiceBackBar", practice);
            Assign(so, "_practiceBackButton", practiceBack);
            Assign(so, "_settingsScreen", settings);
            Assign(so, "_settingsBackButton", settingsBack);
            Assign(so, "_signedInText", signedIn);
            so.ApplyModifiedPropertiesWithoutUndo();

            // The controller's own Apply() decides this at runtime; the authored state is what a
            // reader of the scene sees, and Title is where a player starts.
            title.SetActive(true);
            login.SetActive(false);
            register.SetActive(false);
            practice.SetActive(false);
            settings.SetActive(false);
            authenticating.SetActive(false);
            lobby.SetActive(false);
            browser.SetActive(false);
            createRoom.SetActive(false);
            roomLobby.SetActive(false);
            toast.gameObject.SetActive(false);
            toast.transform.SetAsLastSibling();

            if (!HideLegacyMenu(log)) return false;


            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            log.AppendLine("saved: " + ScenePath);
            return true;
        }

        /// <summary>
        /// Deletes the subtree a previous run authored, if there is one.
        /// </summary>
        /// <remarks>
        /// Matched by name at the scene root, because that is what this script controls. A
        /// component-type search would also match a Canvas somebody deliberately built by hand,
        /// and deleting authored work nobody asked to delete is a worse failure than leaving a
        /// stale root behind.
        /// </remarks>
        private static void RemovePreviousRoot(Scene scene, StringBuilder log)
        {
            foreach (GameObject rootObject in scene.GetRootGameObjects())
            {
                if (rootObject.name != RootName) continue;

                log.AppendLine("rebuilding: removed the previous '" + RootName + "'.");
                Object.DestroyImmediate(rootObject);
                return;
            }
        }

        // ------------------------------------------------------------------ the screens

        private static GameObject BuildTitle(
            GameObject root, MenuScreenController controller, StringBuilder log)
        {
            GameObject panel = Panel(root, "Main Menu", opaque: false);
            FullscreenSprite(panel, "Background", "backgrounds/main-menu.png");
            // The supplied wordmark, used as-is. It replaces a Text label that spelled the same
            // name in the default font -- the artwork was in the pack the whole time and nothing
            // referenced it.
            Icon(panel, "Logo", "branding/ironfront-reborn-logo.svg",
                new Vector2(-560f, 350f), new Vector2(475f, 130f));

            Text tagline = Label(panel, "Tagline", "TACTICAL WARFARE. REFORGED.", 16,
                new Vector2(-560f, 286f), new Vector2(520f, 28f));
            tagline.alignment = TextAnchor.MiddleLeft;

            // `.menu-button` for the three secondary rows and `--primary` for Multiplayer, which is
            // also the only one the stylesheet makes taller and orange. Each carries the icon the
            // prototype names for it; `PackButton` has taken an icon since it was written and no
            // call site had ever passed one.
            Button multiplayer = PackButton(panel, "Multiplayer", "MULTIPLAYER",
                new Vector2(-560f, 150f), new Vector2(480f, 76f), "primary", "icons/users.svg");
            Button practice = PackButton(panel, "Practice", "PRACTICE",
                new Vector2(-560f, 58f), new Vector2(480f, 76f), "menu", "icons/target.svg");
            Button settings = PackButton(panel, "Settings", "SETTINGS",
                new Vector2(-560f, -34f), new Vector2(480f, 76f), "menu", "icons/settings.svg");
            Button exit = PackButton(panel, "Exit", "EXIT",
                new Vector2(-560f, -126f), new Vector2(480f, 76f), "menu", "icons/power.svg");

            Text footer = Label(panel, "Footer", "TEAM 10 LTM  •  CLASSROOM MULTIPLAYER PROJECT", 14,
                new Vector2(-545f, -465f), new Vector2(520f, 24f));
            footer.alignment = TextAnchor.MiddleLeft;
            footer.color = new Color(0.72f, 0.76f, 0.80f, 0.9f);

            SetVerticalNavigation(multiplayer, practice, settings, exit);
            ConfigureKeyboard(panel, new Selectable[] { multiplayer, practice, settings, exit }, multiplayer, null);

            MenuTitleScreen screen = panel.AddComponent<MenuTitleScreen>();
            var so = new SerializedObject(screen);
            Assign(so, "_controller", controller);
            Assign(so, "_multiplayerButton", multiplayer);
            Assign(so, "_practiceButton", practice);
            Assign(so, "_settingsButton", settings);
            Assign(so, "_exitButton", exit);
            so.ApplyModifiedPropertiesWithoutUndo();

            log.AppendLine("title: supplied background/logo and four fully wired actions.");
            return panel;
        }

        private static GameObject BuildLogin(
            GameObject root, MenuScreenController controller, StringBuilder log)
        {
            GameObject panel = Panel(root, "Sign In", opaque: false);
            FullscreenSprite(panel, "Background", "backgrounds/auth.png");
            Text brand = Label(panel, "Logo", "IRONFRONT  REBORN", 36,
                new Vector2(-650f, 440f), new Vector2(500f, 58f));
            brand.alignment = TextAnchor.MiddleLeft;
            Angular(panel, "GlassPanel", new Vector2(0f, -5f), new Vector2(700f, 680f),
                CutCard, Surface);

            Label(panel, "Heading", "SIGN IN", 34, new Vector2(0f, 234f), new Vector2(520f, 52f));
            Text subheading = Label(panel, "Subheading", "CONNECT TO THE FRONT", 15,
                new Vector2(0f, 199f), new Vector2(520f, 24f));
            subheading.color = new Color(0.76f, 0.79f, 0.82f);

            InputField username = PackField(panel, "Username", "Username",
                new Vector2(0f, 132f), new Vector2(480f, 58f), password: false);
            InputField password = PackField(panel, "Password", "Password",
                new Vector2(0f, 62f), new Vector2(480f, 58f), password: true);
            Button reveal = AddPasswordReveal(panel, password, new Vector2(275f, 62f));
            Toggle remember = PackToggle(panel, "RememberMe", "Remember username",
                new Vector2(-125f, 10f));
            Button logIn = PackButton(panel, "LogIn", "LOG IN", new Vector2(0f, -52f),
                new Vector2(420f, 72f), "primary");
            Label(panel, "Divider", "────────────  OR  ────────────", 14,
                new Vector2(0f, -103f), new Vector2(480f, 24f));
            Button create = PackButton(panel, "CreateAccount", "CREATE ACCOUNT",
                new Vector2(0f, -154f), new Vector2(420f, 72f), "secondary");
            Button back = PackButton(panel, "Back", "BACK", new Vector2(-500f, -310f),
                new Vector2(142f, 48f), "secondary");

            Text error = Label(panel, "Error", string.Empty, 18,
                new Vector2(0f, -221f), new Vector2(540f, 52f));
            error.color = ErrorInk;

            SetVerticalNavigation(username, password, reveal, remember, logIn, create, back);
            ConfigureKeyboard(panel,
                new Selectable[] { username, password, reveal, remember, logIn, create, back }, logIn, back);

            MenuLoginScreen screen = panel.AddComponent<MenuLoginScreen>();
            var so = new SerializedObject(screen);
            Assign(so, "_controller", controller);
            Assign(so, "_usernameField", username);
            Assign(so, "_passwordField", password);
            Assign(so, "_logInButton", logIn);
            Assign(so, "_createAccountButton", create);
            Assign(so, "_rememberMeToggle", remember);
            Assign(so, "_forgotPasswordButton", null);
            Assign(so, "_backButton", back);
            Assign(so, "_errorText", error);
            so.ApplyModifiedPropertiesWithoutUndo();

            log.AppendLine("login: supplied pack, remember/forgot/back, explicit keyboard order.");
            return panel;
        }

        private static GameObject BuildRegister(
            GameObject root, MenuScreenController controller, StringBuilder log)
        {
            GameObject panel = Panel(root, "Create Account", opaque: false);
            FullscreenSprite(panel, "Background", "backgrounds/auth.png");
            Angular(panel, "GlassPanel", Vector2.zero, new Vector2(760f, 820f), CutCard, Surface);

            Label(panel, "Heading", "CREATE ACCOUNT", 48, new Vector2(0f, 290f), new Vector2(900f, 70f));

            InputField username = Field(
                panel, "Username", "Username (3-16, a-z 0-9 _)", new Vector2(0f, 190f), password: false);
            InputField password = Field(
                panel, "Password", "Password", new Vector2(0f, 115f), password: true);
            InputField confirm = Field(
                panel, "ConfirmPassword", "Repeat password", new Vector2(0f, 40f), password: true);
            Button revealPassword = AddPasswordReveal(panel, password, new Vector2(345f, 115f));
            Button revealConfirm = AddPasswordReveal(panel, confirm, new Vector2(345f, 40f));
            InputField displayName = Field(
                panel, "DisplayName", "Display name (optional)", new Vector2(0f, -35f), password: false);

            Button create = MakeButton(
                panel, "Create", "CREATE ACCOUNT", new Vector2(0f, -125f), new Vector2(460f, 74f));
            Button back = MakeButton(
                panel, "Back", "Back to sign in", new Vector2(0f, -210f), new Vector2(360f, 56f));

            Text error = Label(
                panel, "Error", string.Empty, 28, new Vector2(0f, -300f), new Vector2(760f, 90f));
            error.color = ErrorInk;

            MenuRegisterScreen screen = panel.AddComponent<MenuRegisterScreen>();
            var so = new SerializedObject(screen);
            Assign(so, "_controller", controller);
            Assign(so, "_usernameField", username);
            Assign(so, "_passwordField", password);
            Assign(so, "_confirmPasswordField", confirm);
            Assign(so, "_displayNameField", displayName);
            Assign(so, "_createButton", create);
            Assign(so, "_backButton", back);
            Assign(so, "_errorText", error);
            so.ApplyModifiedPropertiesWithoutUndo();
            ConfigureKeyboard(panel,
                new Selectable[] { username, password, revealPassword, confirm, revealConfirm,
                    displayName, create, back }, create, back);

            log.AppendLine("register: criterion 2 is driven from here.");
            return panel;
        }

        private static GameObject BuildAuthenticating(GameObject root)
        {
            GameObject panel = Panel(root, "Authenticating");
            Label(panel, "Message", "Signing in...", 44, Vector2.zero, new Vector2(700f, 90f));
            return panel;
        }

        private static GameObject BuildLobby(GameObject root, out Text signedIn, out Button browseRooms)
        {
            GameObject panel = Panel(root, "Lobby");

            Label(panel, "Heading", "SIGNED IN", 48, new Vector2(0f, 160f), new Vector2(700f, 70f));
            signedIn = Label(panel, "SignedIn", string.Empty, 34, new Vector2(0f, 60f), new Vector2(900f, 70f));

            // P16 3.2: the one edge out of Lobby the transition table has. Before this button the
            // signed-in screen was terminal for anyone not pressing Shift+F2, which is F2 in the
            // player-facing audit -- an account you can make and then do nothing with.
            browseRooms = MakeButton(
                panel, "BrowseRooms", "BROWSE ROOMS", new Vector2(0f, -50f), new Vector2(460f, 84f));

            return panel;
        }

        private static MenuToast BuildToast(GameObject root)
        {
            var toastObject = new GameObject("Development Toast", typeof(RectTransform), typeof(Image));
            toastObject.transform.SetParent(root.transform, false);
            RectTransform rect = toastObject.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 34f);
            rect.sizeDelta = new Vector2(560f, 58f);
            toastObject.GetComponent<Image>().color = new Color(0.03f, 0.09f, 0.15f, 0.98f);
            Text label = Label(toastObject, "Message", string.Empty, 18, Vector2.zero,
                new Vector2(530f, 50f));
            label.color = Ink;
            MenuToast toast = toastObject.AddComponent<MenuToast>();
            toast.Configure(label);
            return toast;
        }

        private static GameObject BuildPractice(GameObject root, MenuScreenController controller,
            MenuToast toast, out Button back)
        {
            GameObject panel = Panel(root, "Practice", opaque: false);
            FullscreenSprite(panel, "Background", "backgrounds/multiplayer.png");
            Angular(panel, "OperationsPanel", new Vector2(0f, -30f), new Vector2(1540f, 870f),
                CutPanel, Surface);
            Label(panel, "Kicker", "COMBAT SIMULATION // SOLO TRAINING", 15,
                new Vector2(-510f, 375f), new Vector2(520f, 28f)).alignment = TextAnchor.MiddleLeft;
            Label(panel, "Heading", "PRACTICE MODE", 46,
                new Vector2(-480f, 325f), new Vector2(580f, 66f)).alignment = TextAnchor.MiddleLeft;
            Text note = Label(panel, "Note",
                "Map names come from the installed game. Prototype theaters are not used.", 16,
                new Vector2(-390f, 275f), new Vector2(760f, 34f));
            note.alignment = TextAnchor.MiddleLeft;

            Angular(panel, "MapPreview", new Vector2(-445f, -35f), new Vector2(560f, 520f),
                0f, Hex("061522"), AngularEdge.All, 1f, Hex("3F6986"));
            Label(panel, "MapPreviewTitle", "SELECTED THEATER", 18,
                new Vector2(-445f, 80f), new Vector2(470f, 50f));
            Label(panel, "Offline", "OFFLINE SIMULATION // LOCAL SESSION", 16,
                new Vector2(-445f, 10f), new Vector2(470f, 40f));

            Dropdown map = MakeDropdown(panel, "PracticeMap", new Vector2(300f, 225f));
            Button mode = MakeButton(panel, "PracticeMode", "GAME MODE",
                new Vector2(300f, 145f), new Vector2(560f, 60f));
            Button difficulty = MakeButton(panel, "Difficulty", "AI DIFFICULTY",
                new Vector2(300f, 65f), new Vector2(560f, 60f));
            Button bots = MakeButton(panel, "BotCount", "BOT COUNT",
                new Vector2(300f, -15f), new Vector2(560f, 60f));
            Button weather = MakeButton(panel, "Weather", "WEATHER / TIME",
                new Vector2(300f, -95f), new Vector2(560f, 60f));
            Button rules = MakeButton(panel, "Rules", "VEHICLES / FRIENDLY FIRE",
                new Vector2(300f, -175f), new Vector2(560f, 60f));
            back = PackButton(panel, "Back", "BACK", new Vector2(200f, -365f),
                new Vector2(280f, 68f), "secondary");
            Button start = PackButton(panel, "StartPractice", "CONTINUE TO PRACTICE",
                new Vector2(510f, -365f), new Vector2(340f, 68f), "primary");

            MenuPracticeScreen screen = panel.AddComponent<MenuPracticeScreen>();
            var so = new SerializedObject(screen);
            Assign(so, "_controller", controller);
            Assign(so, "_mapDropdown", map);
            Assign(so, "_startButton", start);
            AssignArray(so, "_unsupportedControls", new Object[] { mode, difficulty, bots, weather, rules });
            Assign(so, "_toast", toast);
            so.ApplyModifiedPropertiesWithoutUndo();
            ConfigureKeyboard(panel,
                new Selectable[] { map, mode, difficulty, bots, weather, rules, back, start },
                start, back);
            return panel;
        }

        private static GameObject BuildSettings(GameObject root, MenuToast toast, out Button back)
        {
            GameObject panel = Panel(root, "Settings", opaque: false);
            FullscreenSprite(panel, "Background", "backgrounds/multiplayer.png");
            Angular(panel, "OperationsPanel", new Vector2(0f, -30f), new Vector2(1540f, 870f),
                CutPanel, Surface);
            Label(panel, "Kicker", "SYSTEM CONTROL // CLIENT CONFIGURATION", 15,
                new Vector2(-480f, 375f), new Vector2(620f, 28f)).alignment = TextAnchor.MiddleLeft;
            Label(panel, "Heading", "SETTINGS", 46,
                new Vector2(-560f, 325f), new Vector2(460f, 66f)).alignment = TextAnchor.MiddleLeft;

            Button displayTab = MakeButton(panel, "DisplayTab", "01  DISPLAY",
                new Vector2(-570f, 190f), new Vector2(260f, 64f));
            Button audioTab = MakeButton(panel, "AudioTab", "02  AUDIO",
                new Vector2(-570f, 110f), new Vector2(260f, 64f));
            Button gameplayTab = MakeButton(panel, "GameplayTab", "03  GAMEPLAY",
                new Vector2(-570f, 30f), new Vector2(260f, 64f));

            Label(panel, "GroupHeading", "DISPLAY, AUDIO & FIELD CONTROLS", 24,
                new Vector2(170f, 245f), new Vector2(900f, 44f)).alignment = TextAnchor.MiddleLeft;
            Dropdown resolution = MakeDropdown(panel, "Resolution", new Vector2(-60f, 165f));
            Dropdown displayMode = MakeDropdown(panel, "DisplayMode", new Vector2(520f, 165f));
            Dropdown quality = MakeDropdown(panel, "Quality", new Vector2(-60f, 85f));
            Toggle vSync = MakeToggle(panel, "VSync", "V-SYNC", new Vector2(520f, 85f));
            Slider volume = MakeSlider(panel, "MasterVolume", "MASTER VOLUME",
                new Vector2(-60f, -15f), 0f, 1f);
            Slider fov = MakeSlider(panel, "FieldOfView", "FIELD OF VIEW",
                new Vector2(520f, -15f), 60f, 120f);
            Slider sensitivity = MakeSlider(panel, "Sensitivity", "MOUSE SENSITIVITY",
                new Vector2(-60f, -120f), 0.05f, 1f);
            Button fps = MakeButton(panel, "FpsLimit", "FPS LIMIT // LATER",
                new Vector2(520f, -120f), new Vector2(560f, 60f));
            Button advancedAudio = MakeButton(panel, "AdvancedAudio", "ADVANCED AUDIO",
                new Vector2(-60f, -205f), new Vector2(560f, 60f));
            Button accessibility = MakeButton(panel, "Accessibility", "ACCESSIBILITY",
                new Vector2(520f, -205f), new Vector2(560f, 60f));

            Button reset = PackButton(panel, "Reset", "RESET DEFAULTS",
                new Vector2(90f, -365f), new Vector2(280f, 68f), "secondary");
            back = PackButton(panel, "Back", "CANCEL",
                new Vector2(390f, -365f), new Vector2(260f, 68f), "secondary");
            Button save = PackButton(panel, "Save", "APPLY SETTINGS",
                new Vector2(690f, -365f), new Vector2(300f, 68f), "primary");

            MenuSettingsScreen screen = panel.AddComponent<MenuSettingsScreen>();
            var so = new SerializedObject(screen);
            Assign(so, "_resolution", resolution);
            Assign(so, "_displayMode", displayMode);
            Assign(so, "_quality", quality);
            Assign(so, "_vSync", vSync);
            Assign(so, "_masterVolume", volume);
            Assign(so, "_fieldOfView", fov);
            Assign(so, "_sensitivity", sensitivity);
            Assign(so, "_saveButton", save);
            Assign(so, "_resetButton", reset);
            AssignArray(so, "_unsupportedButtons",
                new Object[] { displayTab, audioTab, gameplayTab, fps, advancedAudio, accessibility });
            Assign(so, "_toast", toast);
            so.ApplyModifiedPropertiesWithoutUndo();
            ConfigureKeyboard(panel,
                new Selectable[] { displayTab, audioTab, gameplayTab, resolution, displayMode,
                    quality, vSync, volume, fov, sensitivity, fps, advancedAudio, accessibility,
                    reset, back, save }, save, back);
            return panel;
        }

        // ------------------------------------------------------------------ P16: the room screens

        /// <summary>The room browser: eight rows, a refresh, a create, a password prompt.</summary>
        private static GameObject BuildRoomBrowser(
            GameObject root, MenuScreenController controller, MenuToast toast, StringBuilder log)
        {
            GameObject panel = Panel(root, "Rooms", opaque: false);
            FullscreenSprite(panel, "Background", "backgrounds/multiplayer.png");
            Label(panel, "Logo", "IRONFRONT REBORN", 28,
                new Vector2(-720f, 470f), new Vector2(360f, 54f)).alignment = TextAnchor.MiddleLeft;
            // `.topbar nav button.is-current`: a washed fill with a 4px orange rule under it, not a
            // sprite. The tab the pack used to supply no longer exists.
            Angular(panel, "RoomsTab", new Vector2(-300f, 470f), new Vector2(210f, 54f), 0f,
                new Color(46f / 255f, 90f / 255f, 123f / 255f, 0.3f),
                AngularEdge.Bottom, 4f, Orange);
            Label(panel, "RoomsTabLabel", "ROOMS", 18, new Vector2(-160f, 316f), new Vector2(210f, 54f));
            Angular(panel, "GlassPanel", new Vector2(0f, -25f), new Vector2(1540f, 850f),
                CutPanel, Surface);

            Label(panel, "Heading", "MULTIPLAYER ROOMS", 28,
                new Vector2(-405f, 230f), new Vector2(320f, 48f)).alignment = TextAnchor.MiddleLeft;

            // Labelled "master", never "ping": see MasterSession.MasterPingMs. A number with no
            // subject is the one thing this readout must not be.
            Text ping = Label(panel, "Ping", "master --", 16,
                new Vector2(445f, 230f), new Vector2(190f, 40f));
            SignalBars(panel, new Vector2(335f, 230f));

            InputField search = PackField(panel, "Search", "Search rooms or maps...",
                new Vector2(-285f, 180f), new Vector2(500f, 56f), password: false,
                iconAsset: "icons/search.svg");
            Button refresh = PackButton(panel, "Refresh", "REFRESH", new Vector2(385f, 180f),
                new Vector2(210f, 52f), "secondary");
            Button mode = PackButton(panel, "ModeFilter", "MODE", new Vector2(95f, 180f),
                new Vector2(180f, 52f), "secondary");
            Button region = PackButton(panel, "RegionFilter", "REGION", new Vector2(580f, 180f),
                new Vector2(170f, 52f), "secondary");

            int rows = MenuRoomBrowserScreen.Rows;
            var buttons = new Object[rows];
            var labels = new Object[rows];

            for (int i = 0; i < rows; i++)
            {
                float y = 122f - (i * 48f);
                Button row = MakeRoomRow(panel, i, new Vector2(0f, y), out Text caption);

                buttons[i] = row;
                labels[i] = caption;
            }

            Text overflow = Label(
                panel, "Overflow", string.Empty, 14, new Vector2(-260f, -275f), new Vector2(520f, 26f));
            overflow.alignment = TextAnchor.MiddleLeft;

            Button create = PackButton(panel, "CreateRoom", "CREATE ROOM",
                new Vector2(440f, -280f), new Vector2(280f, 60f), "primary");
            Button quick = PackButton(panel, "QuickMatch", "QUICK MATCH",
                new Vector2(135f, -280f), new Vector2(280f, 60f), "secondary");

            Text error = Label(
                panel, "Error", string.Empty, 16, new Vector2(0f, -326f), new Vector2(900f, 34f));
            error.color = ErrorInk;

            GameObject prompt = BuildPasswordPrompt(
                panel, out InputField password, out Button promptJoin, out Button promptCancel);

            MenuRoomBrowserScreen screen = panel.AddComponent<MenuRoomBrowserScreen>();
            var so = new SerializedObject(screen);
            Assign(so, "_controller", controller);
            AssignArray(so, "_roomButtons", buttons);
            AssignArray(so, "_roomLabels", labels);
            Assign(so, "_searchField", search);
            Assign(so, "_refreshButton", refresh);
            Assign(so, "_createRoomButton", create);
            Assign(so, "_pingText", ping);
            Assign(so, "_overflowText", overflow);
            Assign(so, "_errorText", error);
            Assign(so, "_passwordPrompt", prompt);
            Assign(so, "_passwordField", password);
            Assign(so, "_passwordJoinButton", promptJoin);
            Assign(so, "_passwordCancelButton", promptCancel);
            so.ApplyModifiedPropertiesWithoutUndo();

            panel.AddComponent<MenuDevelopmentControls>().Configure(toast, mode, region, quick);
            ConfigureKeyboard(panel,
                new Selectable[] { search, mode, refresh, region, quick, create }, refresh, null);

            prompt.SetActive(false);

            log.AppendLine("room browser: supplied pack, search, " + rows + " real-data rows.");
            return panel;
        }

        /// <summary>The private-room password prompt, drawn over the browser.</summary>
        private static GameObject BuildPasswordPrompt(
            GameObject parent, out InputField password, out Button join, out Button cancel)
        {
            GameObject prompt = Panel(parent, "PasswordPrompt", opaque: false);
            Image shade = prompt.AddComponent<Image>();
            shade.color = new Color(0f, 0f, 0f, 0.72f);
            Angular(prompt, "GlassPanel", Vector2.zero, new Vector2(500f, 280f), CutCard, Surface);

            Label(prompt, "Heading", "PRIVATE ROOM", 28,
                new Vector2(0f, 86f), new Vector2(420f, 48f));

            password = PackField(prompt, "Password", "Room password", new Vector2(0f, 28f),
                new Vector2(400f, 52f), password: true);

            join = PackButton(prompt, "Join", "JOIN", new Vector2(-105f, -62f),
                new Vector2(190f, 52f), "command");
            cancel = PackButton(prompt, "Cancel", "CANCEL", new Vector2(105f, -62f),
                new Vector2(190f, 52f), "secondary");
            SetVerticalNavigation(password, join, cancel);

            return prompt;
        }

        /// <summary>The create-room form: exactly CreateRoomRequest's six fields.</summary>
        private static GameObject BuildCreateRoom(
            GameObject root, MenuScreenController controller, MenuToast toast, StringBuilder log)
        {
            GameObject panel = Panel(root, "Create Room", opaque: false);
            FullscreenSprite(panel, "Background", "backgrounds/multiplayer.png");
            Angular(panel, "OperationsPanel", Vector2.zero, new Vector2(1460f, 920f),
                CutPanel, Surface);

            Label(panel, "Heading", "CREATE ROOM", 48, new Vector2(0f, 330f), new Vector2(900f, 70f));

            InputField name = Field(panel, "Name", "Room name", new Vector2(0f, 230f), password: false);
            Dropdown map = MakeDropdown(panel, "Map", new Vector2(0f, 155f));

            InputField maxPlayers = Field(
                panel, "MaxPlayers", "Players (even, 2-" + ProtocolConstants.MAX_PLAYERS + ")",
                new Vector2(0f, 80f), password: false);
            maxPlayers.contentType = InputField.ContentType.IntegerNumber;

            InputField bots = Field(panel, "BotCount", "Bots", new Vector2(0f, 5f), password: false);
            bots.contentType = InputField.ContentType.IntegerNumber;

            Toggle isPrivate = MakeToggle(panel, "Private", "Private room", new Vector2(0f, -70f));
            InputField password = Field(
                panel, "Password", "Room password", new Vector2(0f, -140f), password: true);

            Button create = MakeButton(
                panel, "Create", "CREATE", new Vector2(-160f, -240f), new Vector2(300f, 74f));
            Button back = MakeButton(
                panel, "Back", "Back", new Vector2(160f, -240f), new Vector2(300f, 74f));
            Button mode = MakeButton(panel, "Mode", "MODE: IN DEVELOPMENT",
                new Vector2(430f, 155f), new Vector2(420f, 60f));
            Button region = MakeButton(panel, "Region", "REGION: IN DEVELOPMENT",
                new Vector2(430f, 80f), new Vector2(420f, 60f));
            Button balance = MakeButton(panel, "Balance", "AUTO BALANCE: IN DEVELOPMENT",
                new Vector2(430f, 5f), new Vector2(420f, 60f));

            Text error = Label(
                panel, "Error", string.Empty, 28, new Vector2(0f, -330f), new Vector2(1100f, 90f));
            error.color = ErrorInk;

            MenuCreateRoomScreen screen = panel.AddComponent<MenuCreateRoomScreen>();
            var so = new SerializedObject(screen);
            Assign(so, "_controller", controller);
            Assign(so, "_nameField", name);
            Assign(so, "_mapDropdown", map);
            Assign(so, "_maxPlayersField", maxPlayers);
            Assign(so, "_botCountField", bots);
            Assign(so, "_privateToggle", isPrivate);
            Assign(so, "_passwordField", password);
            Assign(so, "_createButton", create);
            Assign(so, "_backButton", back);
            Assign(so, "_errorText", error);
            so.ApplyModifiedPropertiesWithoutUndo();
            panel.AddComponent<MenuDevelopmentControls>().Configure(toast, mode, region, balance);
            ConfigureKeyboard(panel,
                new Selectable[] { name, map, maxPlayers, bots, isPrivate, password, create, back },
                create, back);

            log.AppendLine("create room: criterion 8's even-seats check renders on its error line.");
            return panel;
        }

        /// <summary>The room: two roster columns, side, ready, chat, leave.</summary>
        private static GameObject BuildRoomLobby(
            GameObject root, MenuScreenController controller, MenuToast toast, StringBuilder log)
        {
            GameObject panel = Panel(root, "Waiting Room", opaque: false);
            FullscreenSprite(panel, "Background", "backgrounds/multiplayer.png");
            Angular(panel, "OperationsPanel", Vector2.zero, new Vector2(1580f, 940f),
                CutPanel, Surface);

            Text heading = Label(
                panel, "Heading", string.Empty, 44, new Vector2(0f, 440f), new Vector2(1400f, 66f));
            Text status = Label(
                panel, "Status", string.Empty, 28, new Vector2(0f, 380f), new Vector2(1400f, 50f));

            // NO colour is set on either heading or any row here. Both are written at runtime
            // from ITeamPalette (criterion 10); authoring one would be the second copy of the
            // team-colour mapping that contracts 6.3 exists to prevent.
            Text zeroHeading = Label(
                panel, "TeamZeroHeading", "TEAM 1", 34, new Vector2(-420f, 310f), new Vector2(560f, 56f));
            Text oneHeading = Label(
                panel, "TeamOneHeading", "TEAM 2", 34, new Vector2(420f, 310f), new Vector2(560f, 56f));

            int perSide = MenuRoomLobbyScreen.RowsPerSide;
            var zeroRows = new Object[perSide];
            var oneRows = new Object[perSide];

            for (int i = 0; i < perSide; i++)
            {
                float y = 250f - (i * 52f);
                zeroRows[i] = RosterRow(panel, "TeamZeroRow" + i, new Vector2(-420f, y));
                oneRows[i] = RosterRow(panel, "TeamOneRow" + i, new Vector2(420f, y));
            }

            Button switchSide = MakeButton(
                panel, "SwitchSide", "SWITCH SIDE", new Vector2(-420f, -190f), new Vector2(400f, 74f),
                out Text switchLabel);
            Button ready = MakeButton(
                panel, "Ready", "READY", new Vector2(420f, -190f), new Vector2(400f, 74f),
                out Text readyLabel);
            Button leave = MakeButton(
                panel, "Leave", "LEAVE ROOM", new Vector2(0f, -430f), new Vector2(340f, 62f));
            Button invite = MakeButton(panel, "CopyInvite", "COPY INVITE",
                new Vector2(-560f, 440f), new Vector2(260f, 52f));
            Button start = MakeButton(panel, "StartGame", "START GAME",
                new Vector2(560f, -430f), new Vector2(300f, 62f));

            Text chatLog = Label(
                panel, "ChatLog", string.Empty, 24, new Vector2(0f, -290f), new Vector2(1400f, 130f));
            chatLog.alignment = TextAnchor.LowerLeft;
            chatLog.resizeTextForBestFit = false;

            InputField chatField = Field(panel, "ChatInput", "Say something", new Vector2(-180f, -370f), password: false);

            // The master's own limit, so the field cannot accept a line the master will refuse.
            // Nothing capped this before, and the refusal that came back was reported as "you are
            // sending too often" -- so a player who pasted a long message waited, re-sent the
            // identical text, and got the identical sentence. Read from the shared protocol
            // constant rather than typed here: two places holding one number is how they drift.
            chatField.characterLimit = MspChatLimits.MaxTextCharacters;
            Button chatSend = MakeButton(
                panel, "ChatSend", "SEND", new Vector2(280f, -370f), new Vector2(240f, 60f));

            Text error = Label(
                panel, "Error", string.Empty, 26, new Vector2(0f, -490f), new Vector2(1400f, 56f));
            error.color = ErrorInk;

            MenuRoomLobbyScreen screen = panel.AddComponent<MenuRoomLobbyScreen>();
            var so = new SerializedObject(screen);
            Assign(so, "_controller", controller);
            Assign(so, "_teamZeroHeading", zeroHeading);
            Assign(so, "_teamOneHeading", oneHeading);
            AssignArray(so, "_teamZeroRows", zeroRows);
            AssignArray(so, "_teamOneRows", oneRows);
            Assign(so, "_switchSideButton", switchSide);
            Assign(so, "_switchSideLabel", switchLabel);
            Assign(so, "_readyButton", ready);
            Assign(so, "_readyLabel", readyLabel);
            Assign(so, "_leaveButton", leave);
            Assign(so, "_headingText", heading);
            Assign(so, "_statusText", status);
            Assign(so, "_errorText", error);
            Assign(so, "_chatLog", chatLog);
            Assign(so, "_chatField", chatField);
            Assign(so, "_chatSendButton", chatSend);
            so.ApplyModifiedPropertiesWithoutUndo();
            panel.AddComponent<MenuDevelopmentControls>().Configure(toast, invite, start);
            ConfigureKeyboard(panel,
                new Selectable[] { switchSide, ready, chatField, chatSend, leave, start }, ready, leave);

            log.AppendLine("room lobby: " + perSide + " rows per side, colours left to ITeamPalette.");
            return panel;
        }

        /// <summary>One roster line. Left-aligned and fixed-size, so names do not jump about.</summary>
        private static Text RosterRow(GameObject parent, string name, Vector2 position)
        {
            Text row = Label(parent, name, string.Empty, 28, position, new Vector2(560f, 46f));
            row.alignment = TextAnchor.MiddleLeft;
            row.resizeTextForBestFit = false;
            return row;
        }

        private static GameObject BuildPracticeBackBar(GameObject root, out Button backButton)
        {
            GameObject bar = Panel(root, "PracticeBackBar", opaque: false);
            backButton = MakeButton(
                bar, "Back", "< Back to multiplayer", new Vector2(0f, 0f), new Vector2(360f, 56f));

            RectTransform rect = backButton.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(24f, -24f);

            return bar;
        }

        // ------------------------------------------------------------------ widgets

        private static void FullscreenSprite(GameObject parent, string name, string asset)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Stretch(go.GetComponent<RectTransform>());
            Image image = go.GetComponent<Image>();
            image.sprite = IronfrontRebornUiAssetCatalog.Sprite(asset);
            image.preserveAspect = false;
            image.raycastTarget = false;
            go.transform.SetAsFirstSibling();
        }

        /// <summary>
        /// An angular surface, drawn as geometry in the prototype's own <c>clip-path</c> shape.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Geometry rather than a sprite, and that is what the stylesheet does too.</b> Every
        /// angular surface in the pack is a flat background under a six-point polygon, so drawing
        /// the polygon is both exact and resolution-independent — a stretched 9-slice smears its
        /// corners, and these SVGs have no 9-slice region to begin with (their importer writes
        /// <c>SpriteBorder: {x: 0, y: 0, z: 0, w: 0}</c>).
        /// </para>
        /// <para>
        /// This is the single helper behind every panel, card, button, field and table in all
        /// eight screens, which is why the flat-rectangle bug it replaces was visible everywhere
        /// at once.
        /// </para>
        /// </remarks>
        private static AngularPanel Angular(GameObject parent, string name, Vector2 position,
            Vector2 size, float cut, Color fill, AngularEdge edge = AngularEdge.All,
            float edgeWidth = 1f, Color? edgeColour = null)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(AngularPanel));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, size);

            AngularPanel panel = go.GetComponent<AngularPanel>();
            panel.color = fill;
            panel.Configure(cut, edge, edgeWidth, edgeColour ?? Line);
            panel.raycastTarget = false;
            return panel;
        }

        /// <summary>
        /// A pack sprite, for what geometry cannot draw: the wordmark, the icons, the badges.
        /// </summary>
        /// <remarks>
        /// <b>This throws on a missing path, and that is the point.</b> The previous revision asked
        /// the catalog for names the current pack does not contain — <c>"panel"</c>,
        /// <c>"preview"</c>, <c>"tab"</c>, <c>"signal"</c>, <c>"toggle-off"</c>,
        /// <c>"fields/input_default.png"</c> — and painted a flat rectangle when the answer was
        /// null. Eight screens therefore shipped with no iconography, no branding and no angular
        /// panels at all, and nothing anywhere said so: the builder reported success, the authoring
        /// test passed, and the art was simply absent. A missing sprite is now a failed build with
        /// the path in the message.
        /// </remarks>
        private static Image Icon(GameObject parent, string name, string asset, Vector2 position,
            Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, size);

            Image image = go.GetComponent<Image>();
            image.sprite = IronfrontRebornUiAssetCatalog.Sprite(asset);
            image.preserveAspect = true;
            image.raycastTarget = false;
            return image;
        }

        private static Button PackButton(
            GameObject parent, string name, string caption, Vector2 position, Vector2 size,
            string kind, string iconAsset = null)
        {
            // `.action` and `.menu-button` differ in their corner, not just their colour: the tall
            // menu row cuts 13px and the short action 10px.
            float cut = size.y >= 62f ? CutMenuButton : CutAction;

            Color fill, edge, captionInk;
            switch (kind)
            {
                case "primary":
                    fill = Orange; edge = Hex("FFD08A"); captionInk = Ink900;
                    break;
                case "command":
                    fill = Hex("0D5B96"); edge = Hex("72CCFF"); captionInk = Ink;
                    break;
                case "danger":
                    fill = new Color(84f / 255f, 18f / 255f, 28f / 255f, 0.65f);
                    edge = Hex("A84150"); captionInk = Ink;
                    break;
                case "menu":
                    fill = new Color(7f / 255f, 22f / 255f, 35f / 255f, 0.86f);
                    edge = new Color(126f / 255f, 184f / 255f, 225f / 255f, 0.45f);
                    captionInk = Ink;
                    break;
                default:
                    fill = Hex("0B1C2B"); edge = Hex("76B9E5"); captionInk = Ink;
                    break;
            }

            var go = new GameObject(name, typeof(RectTransform), typeof(AngularPanel),
                typeof(Button));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, size);

            AngularPanel face = go.GetComponent<AngularPanel>();
            face.color = fill;
            face.Configure(cut, AngularEdge.All, 1f, edge);

            // The two signature buttons are gradients in the stylesheet rather than flat fills,
            // and AngularPanel reproduces a linear gradient exactly.
            if (kind == "primary") face.SetGradient(Hex("F35A0D"), 135f);
            else if (kind == "command") face.SetGradient(Hex("1C8CCE"), 135f);

            Button button = go.GetComponent<Button>();
            button.targetGraphic = face;
            button.transition = Selectable.Transition.ColorTint;
            ColorBlock colours = button.colors;
            colours.normalColor = Color.white;
            colours.highlightedColor = kind == "primary" ? Hex("FFD9AE") : CyanSoft;
            colours.pressedColor = kind == "primary" ? Hex("E95D0D") : Hex("176F9F");
            colours.disabledColor = new Color(0.35f, 0.4f, 0.45f, 0.45f);
            colours.colorMultiplier = 1f;
            button.colors = colours;

            // `.menu-button` reserves 68px on the left for a 28px glyph; `.action` centres a 21px
            // one beside its caption. Either way the caption shifts by the space the icon takes.
            if (iconAsset != null)
            {
                float inset = size.y >= 62f ? 23f : 16f;
                Icon(go, "Icon", iconAsset,
                    new Vector2(-size.x * 0.5f + inset + size.y * 0.21f, 0f),
                    new Vector2(size.y * 0.38f, size.y * 0.38f));
            }

            Text text = Label(go, "Caption", caption, Mathf.Clamp(Mathf.RoundToInt(size.y * 0.32f), 14, 25),
                iconAsset == null ? Vector2.zero : new Vector2(16f, 0f),
                iconAsset == null ? size : new Vector2(size.x - 72f, size.y));
            text.fontStyle = FontStyle.Bold;
            text.color = captionInk;
            text.raycastTarget = false;

            return button;
        }

        private static Button LinkButton(
            GameObject parent, string name, string caption, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, size);
            Image hitArea = go.GetComponent<Image>();
            hitArea.color = new Color(0f, 0f, 0f, 0f);

            Button button = go.GetComponent<Button>();
            button.targetGraphic = hitArea;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 0.67f, 0.3f);
            colors.pressedColor = new Color(1f, 0.47f, 0.12f);
            button.colors = colors;

            Text text = Label(go, "Caption", caption, 15, Vector2.zero, size);
            text.alignment = TextAnchor.MiddleRight;
            text.color = new Color(0.95f, 0.59f, 0.24f);
            text.raycastTarget = false;
            return button;
        }

        private static Button AddPasswordReveal(GameObject parent, InputField field, Vector2 position)
        {
            Button button = LinkButton(parent, field.name + "Reveal", "SHOW", position,
                new Vector2(90f, 40f));
            Text caption = button.GetComponentInChildren<Text>(true);
            MenuPasswordReveal reveal = button.gameObject.AddComponent<MenuPasswordReveal>();
            reveal.Configure(field, button, caption);
            return button;
        }

        /// <summary>
        /// A solid rectangle with no sprite, for the small pieces of chrome the stylesheet draws
        /// rather than illustrates.
        /// </summary>
        private static Image Plain(GameObject parent, string name, Vector2 position, Vector2 size,
            Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, size);

            Image image = go.GetComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>
        /// The four ascending bars of <c>.signal-bars</c>, aligned to their own bottom edge.
        /// </summary>
        /// <remarks>
        /// Four rects, because that is what the stylesheet draws: <c>i</c> elements 3px wide with a
        /// 2px gap, 5/9/14/19px tall against a common baseline. The pack never contained a
        /// <c>signal</c> sprite, and the previous revision asked for one anyway and painted a
        /// single flat green rectangle when the lookup came back empty — a "signal strength"
        /// readout that could not show strength.
        /// </remarks>
        private static void SignalBars(GameObject parent, Vector2 position)
        {
            var group = new GameObject("PingSignal", typeof(RectTransform));
            group.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(group.GetComponent<RectTransform>(), position, new Vector2(18f, 20f));

            float[] heights = { 5f, 9f, 14f, 19f };
            for (int i = 0; i < heights.Length; i++)
            {
                // Bottom-aligned: each bar's centre sits half its own height above the baseline.
                Plain(group, "Bar" + (i + 1),
                    new Vector2(-6.5f + (i * 5f), -10f + (heights[i] * 0.5f)),
                    new Vector2(3f, heights[i]), Green);
            }
        }

        private static InputField PackField(
            GameObject parent, string name, string placeholder, Vector2 position, Vector2 size,
            bool password, string iconAsset = null)
        {
            // `.field` is a plain rectangle -- no cut -- with a 1px #557996 border and
            // `box-shadow: inset 3px 0 var(--cyan)`, a 3px cyan bar down the left edge. Two
            // surfaces, because an AngularPanel strokes one colour on one edge.
            var go = new GameObject(name, typeof(RectTransform), typeof(AngularPanel),
                typeof(InputField));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, size);

            AngularPanel frame = go.GetComponent<AngularPanel>();
            frame.color = new Color(3f / 255f, 13f / 255f, 23f / 255f, 0.82f);
            frame.Configure(0f, AngularEdge.All, 1f, Hex("557996"));

            Angular(go, "Accent", Vector2.zero, size, 0f, Color.clear,
                AngularEdge.Left, 3f, Cyan);

            // `.field img`: 22px square with a 15px left margin, sitting in the 53px glyph column
            // the text inset already leaves clear.
            if (iconAsset != null)
                Icon(go, "Glyph", iconAsset, new Vector2((-size.x * 0.5f) + 26f, 0f),
                    new Vector2(22f, 22f));

            // `.field input` starts after the 53px glyph column the prototype reserves for the
            // leading icon, so the text never sits under the accent bar.
            Text text = Label(go, "Text", string.Empty, 18, Vector2.zero, size);
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;
            text.resizeTextForBestFit = false;
            text.color = Color.white;
            FieldInset(text.GetComponent<RectTransform>());

            Text hint = Label(go, "Placeholder", placeholder, 18, Vector2.zero, size);
            hint.alignment = TextAnchor.MiddleLeft;
            hint.color = Hex("738CA2");
            hint.resizeTextForBestFit = false;
            FieldInset(hint.GetComponent<RectTransform>());

            InputField field = go.GetComponent<InputField>();
            field.targetGraphic = frame;
            field.textComponent = text;
            field.placeholder = hint;
            field.contentType = password ? InputField.ContentType.Password : InputField.ContentType.Standard;
            field.lineType = InputField.LineType.SingleLine;
            field.transition = Selectable.Transition.ColorTint;
            ColorBlock colours = field.colors;
            colours.normalColor = Color.white;
            colours.highlightedColor = Hex("7ACFFF");
            colours.selectedColor = Cyan;
            field.colors = colours;
            return field;
        }

        /// <summary>The text inset of <c>.field input</c>, past the 53px glyph column.</summary>
        private static void FieldInset(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(53f, 6f);
            rect.offsetMax = new Vector2(-14f, -6f);
        }

        private static Toggle PackToggle(GameObject parent, string name, string caption, Vector2 position)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Toggle));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, new Vector2(250f, 42f));

            // `.check span`: a 20x20 box, 1px #8ab5d1 on #06131f, holding a cyan tick when set.
            AngularPanel box = Angular(go, "Box", new Vector2(-102f, 0f), new Vector2(20f, 20f),
                0f, Hex("06131F"), AngularEdge.All, 1f, Hex("8AB5D1"));
            box.raycastTarget = true;

            // The tick is two rotated strokes rather than a glyph, so it cannot depend on which
            // font the player's machine resolves.
            Image longStroke = Plain(box.gameObject, "TickLong", new Vector2(1.5f, 0.5f),
                new Vector2(3f, 9.9f), Cyan);
            longStroke.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -45f);
            Image shortStroke = Plain(box.gameObject, "TickShort", new Vector2(-4.5f, -0.5f),
                new Vector2(3f, 7.1f), Cyan);
            shortStroke.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 45f);

            Text text = Label(go, "Caption", caption, 15,
                new Vector2(18f, 0f), new Vector2(200f, 36f));
            text.alignment = TextAnchor.MiddleLeft;

            Toggle toggle = go.GetComponent<Toggle>();
            toggle.targetGraphic = box;
            toggle.transition = Selectable.Transition.None;
            toggle.isOn = false;

            // Toggle.graphic drives exactly one Graphic and the tick is two strokes, so the pair is
            // bound through MenuTickGraphic -- by the Toggle's own field, one stroke would show in
            // both states.
            go.AddComponent<MenuTickGraphic>().Configure(toggle, longStroke, shortStroke);
            return toggle;
        }

        private static Button MakeRoomRow(
            GameObject parent, int index, Vector2 position, out Text caption)
        {
            var go = new GameObject("Row" + index, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, new Vector2(1040f, 44f));

            Image image = go.GetComponent<Image>();
            image.color = new Color(0.04f, 0.14f, 0.21f, 0.86f);
            image.raycastTarget = false;

            caption = Label(go, "Caption", string.Empty, 16,
                new Vector2(-74f, 0f), new Vector2(850f, 40f));
            caption.alignment = TextAnchor.MiddleLeft;
            caption.resizeTextForBestFit = false;
            caption.raycastTarget = false;

            return PackButton(go, "JoinButton", "JOIN", new Vector2(435f, 0f),
                new Vector2(120f, 38f), "secondary");
        }

        private static void SetVerticalNavigation(params Selectable[] controls)
        {
            for (int i = 0; i < controls.Length; i++)
            {
                Selectable control = controls[i];
                if (control == null) continue;

                Navigation navigation = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnUp = controls[(i - 1 + controls.Length) % controls.Length],
                    selectOnDown = controls[(i + 1) % controls.Length],
                };
                control.navigation = navigation;
            }
        }

        private static void ConfigureKeyboard(GameObject panel, Selectable[] controls,
            Button primary, Button cancel)
        {
            MenuKeyboardNavigator navigator = panel.GetComponent<MenuKeyboardNavigator>()
                ?? panel.AddComponent<MenuKeyboardNavigator>();
            navigator.Configure(controls, primary, cancel);
        }

        private static GameObject Panel(GameObject root, string name, bool opaque = true)
        {
            var panel = new GameObject(name, typeof(RectTransform), typeof(CanvasGroup));
            panel.transform.SetParent(root.transform, worldPositionStays: false);
            Stretch(panel.GetComponent<RectTransform>());

            if (opaque)
            {
                Image backdrop = panel.AddComponent<Image>();
                backdrop.color = Backdrop;
            }

            panel.AddComponent<MenuScreenTransition>();

            return panel;
        }

        private static Text Label(
            GameObject parent, string name, string text, int size, Vector2 position, Vector2 size2)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, worldPositionStays: false);

            RectTransform rect = go.GetComponent<RectTransform>();
            Centre(rect, position, size2);

            Text label = go.AddComponent<Text>();
            label.text = text;
            label.font = DefaultFont();
            label.fontSize = size;
            label.color = Ink;
            label.alignment = TextAnchor.MiddleCenter;

            // A label that outgrows its rect must shrink rather than clip: the error line is the
            // longest text on the Canvas and is exactly the one criterion 3 grades on the pixels.
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = 14;
            label.resizeTextMaxSize = size;

            return label;
        }

        private static Button MakeButton(
            GameObject parent, string name, string caption, Vector2 position, Vector2 size)
            => MakeButton(parent, name, caption, position, size, out Text _);

        /// <summary>
        /// A button, and its caption, for the callers that re-write the caption at runtime.
        /// </summary>
        /// <remarks>
        /// P16's Switch-side and Ready buttons both change what they say -- "SIDES LOCKED",
        /// "NOT READY" -- so the caption is a reference the screen holds rather than a string
        /// authored once. Finding it with GetComponentInChildren at runtime would work and would
        /// be ungradeable: the wiring gate reads serialized references, and a caption found by
        /// search is not one.
        /// </remarks>
        private static Button MakeButton(
            GameObject parent, string name, string caption, Vector2 position, Vector2 size,
            out Text captionText)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, size);

            Image background = go.AddComponent<Image>();
            background.color = new Color(0.03f, 0.12f, 0.19f, 0.96f);

            Button button = go.AddComponent<Button>();
            button.targetGraphic = background;
            ColorBlock colours = button.colors;
            colours.normalColor = Color.white;
            colours.highlightedColor = Cyan;
            colours.pressedColor = Hex("176F9F");
            button.colors = colours;

            Text caption2 = Label(go, "Caption", caption, Mathf.RoundToInt(size.y * 0.42f),
                                  Vector2.zero, size);
            Stretch(caption2.GetComponent<RectTransform>());

            captionText = caption2;
            return button;
        }

        /// <summary>A checkbox with a caption beside it.</summary>
        private static Toggle MakeToggle(
            GameObject parent, string name, string caption, Vector2 position)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, new Vector2(560f, 50f));

            var boxObject = new GameObject("Box", typeof(RectTransform));
            boxObject.transform.SetParent(go.transform, worldPositionStays: false);
            Centre(boxObject.GetComponent<RectTransform>(), new Vector2(-250f, 0f), new Vector2(40f, 40f));
            Image box = boxObject.AddComponent<Image>();
            box.color = new Color(0.12f, 0.14f, 0.18f, 1f);

            var markObject = new GameObject("Mark", typeof(RectTransform));
            markObject.transform.SetParent(boxObject.transform, worldPositionStays: false);
            Centre(markObject.GetComponent<RectTransform>(), Vector2.zero, new Vector2(26f, 26f));
            Image mark = markObject.AddComponent<Image>();
            mark.color = Ink;

            Text label = Label(go, "Caption", caption, 28, new Vector2(30f, 0f), new Vector2(460f, 44f));
            label.alignment = TextAnchor.MiddleLeft;
            label.resizeTextForBestFit = false;

            Toggle toggle = go.AddComponent<Toggle>();
            toggle.targetGraphic = box;
            toggle.graphic = mark;
            toggle.isOn = false;

            return toggle;
        }

        /// <summary>
        /// An empty dropdown. Its options are filled at runtime from <c>MapCatalog</c>.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT authored with the map names: the catalogue is the single source of
        /// them, and a list baked into a scene would be a second copy that goes stale the first
        /// time a map is added -- with no compiler and no gate able to notice.
        /// </remarks>
        private static Dropdown MakeDropdown(GameObject parent, string name, Vector2 position)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, new Vector2(560f, 60f));

            Image background = go.AddComponent<Image>();
            background.color = new Color(0.12f, 0.14f, 0.18f, 1f);

            Text caption = Label(go, "Label", string.Empty, 30, Vector2.zero, new Vector2(540f, 48f));
            caption.alignment = TextAnchor.MiddleLeft;
            caption.resizeTextForBestFit = false;
            Inset(caption.GetComponent<RectTransform>());

            var templateObject = new GameObject("Template", typeof(RectTransform));
            templateObject.transform.SetParent(go.transform, worldPositionStays: false);
            RectTransform templateRect = templateObject.GetComponent<RectTransform>();
            templateRect.anchorMin = new Vector2(0f, 0f);
            templateRect.anchorMax = new Vector2(1f, 0f);
            templateRect.pivot = new Vector2(0.5f, 1f);
            templateRect.anchoredPosition = Vector2.zero;
            templateRect.sizeDelta = new Vector2(0f, 160f);
            Image templateBackground = templateObject.AddComponent<Image>();
            templateBackground.color = new Color(0.10f, 0.12f, 0.16f, 1f);
            ScrollRect scroll = templateObject.AddComponent<ScrollRect>();

            var viewportObject = new GameObject("Viewport", typeof(RectTransform));
            viewportObject.transform.SetParent(templateObject.transform, worldPositionStays: false);
            RectTransform viewport = viewportObject.GetComponent<RectTransform>();
            Stretch(viewport);
            viewportObject.AddComponent<Mask>().showMaskGraphic = false;
            viewportObject.AddComponent<Image>().color = Color.white;

            var contentObject = new GameObject("Content", typeof(RectTransform));
            contentObject.transform.SetParent(viewportObject.transform, worldPositionStays: false);
            RectTransform content = contentObject.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = new Vector2(0f, 52f);

            var itemObject = new GameObject("Item", typeof(RectTransform));
            itemObject.transform.SetParent(contentObject.transform, worldPositionStays: false);
            RectTransform itemRect = itemObject.GetComponent<RectTransform>();
            itemRect.anchorMin = new Vector2(0f, 0.5f);
            itemRect.anchorMax = new Vector2(1f, 0.5f);
            itemRect.sizeDelta = new Vector2(0f, 52f);
            Toggle item = itemObject.AddComponent<Toggle>();

            var itemBackgroundObject = new GameObject("Item Background", typeof(RectTransform));
            itemBackgroundObject.transform.SetParent(itemObject.transform, worldPositionStays: false);
            Stretch(itemBackgroundObject.GetComponent<RectTransform>());
            Image itemBackground = itemBackgroundObject.AddComponent<Image>();
            itemBackground.color = new Color(0.16f, 0.20f, 0.26f, 1f);

            Text itemLabel = Label(itemObject, "Item Label", string.Empty, 28, Vector2.zero, new Vector2(540f, 44f));
            itemLabel.alignment = TextAnchor.MiddleLeft;
            itemLabel.resizeTextForBestFit = false;
            Inset(itemLabel.GetComponent<RectTransform>());

            item.targetGraphic = itemBackground;

            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;

            Dropdown dropdown = go.AddComponent<Dropdown>();
            dropdown.targetGraphic = background;
            dropdown.captionText = caption;
            dropdown.template = templateRect;
            dropdown.itemText = itemLabel;

            templateObject.SetActive(false);

            return dropdown;
        }

        private static Slider MakeSlider(GameObject parent, string name, string caption,
            Vector2 position, float minimum, float maximum)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Slider));
            root.transform.SetParent(parent.transform, false);
            Centre(root.GetComponent<RectTransform>(), position, new Vector2(560f, 78f));
            root.GetComponent<Image>().color = new Color(0.03f, 0.10f, 0.16f, 0.98f);
            Label(root, "Caption", caption, 16, new Vector2(0f, 22f), new Vector2(510f, 26f))
                .alignment = TextAnchor.MiddleLeft;

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(root.transform, false);
            Centre(fillArea.GetComponent<RectTransform>(), new Vector2(0f, -18f), new Vector2(500f, 12f));
            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(fillArea.transform, false);
            Stretch(fill.GetComponent<RectTransform>());
            fill.GetComponent<Image>().color = Orange;

            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(root.transform, false);
            Centre(handleArea.GetComponent<RectTransform>(), new Vector2(0f, -18f), new Vector2(500f, 24f));
            var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handle.transform.SetParent(handleArea.transform, false);
            Centre(handle.GetComponent<RectTransform>(), Vector2.zero, new Vector2(18f, 28f));
            Image handleImage = handle.GetComponent<Image>();
            handleImage.color = Cyan;

            Slider slider = root.GetComponent<Slider>();
            slider.fillRect = fill.GetComponent<RectTransform>();
            slider.handleRect = handle.GetComponent<RectTransform>();
            slider.targetGraphic = handleImage;
            slider.minValue = minimum;
            slider.maxValue = maximum;
            return slider;
        }

        private static InputField Field(
            GameObject parent, string name, string placeholder, Vector2 position, bool password)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, new Vector2(560f, 60f));

            Image background = go.AddComponent<Image>();
            background.color = new Color(0.12f, 0.14f, 0.18f, 1f);

            Text text = Label(go, "Text", string.Empty, 30, Vector2.zero, new Vector2(540f, 48f));
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;
            text.resizeTextForBestFit = false;
            Inset(text.GetComponent<RectTransform>());

            Text hint = Label(go, "Placeholder", placeholder, 30, Vector2.zero, new Vector2(540f, 48f));
            hint.alignment = TextAnchor.MiddleLeft;
            hint.color = new Color(0.55f, 0.58f, 0.63f);
            hint.resizeTextForBestFit = false;
            Inset(hint.GetComponent<RectTransform>());

            InputField field = go.AddComponent<InputField>();
            field.targetGraphic = background;
            field.textComponent = text;
            field.placeholder = hint;

            // The masking is the InputField's, not a font trick: ContentType.Password is what
            // makes the value invisible on screen AND keeps it out of the Text component's own
            // string, which is what a screenshot of this phase would otherwise capture.
            field.contentType = password ? InputField.ContentType.Password : InputField.ContentType.Standard;
            field.lineType = InputField.LineType.SingleLine;

            return field;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void Inset(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(14f, 6f);
            rect.offsetMax = new Vector2(-14f, -6f);
        }

        private static void Centre(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        /// <summary>
        /// The built-in font every legacy <c>Text</c> in this project already uses.
        /// </summary>
        /// <remarks>
        /// <c>LegacyRuntime.ttf</c> is where Unity moved Arial. A null font renders nothing at
        /// all — no error, no warning, an empty rect — which on a screenshot-graded phase would
        /// read as "the label is unassigned" and send the reader after the wrong fault.
        /// </remarks>
        private static Font DefaultFont()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return font;
        }

        private static Color Hex(string rgb)
        {
            if (!ColorUtility.TryParseHtmlString("#" + rgb, out Color colour))
                return Color.white;
            return colour;
        }

        private static void Assign(SerializedObject so, string field, Object value)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null)
                throw new System.InvalidOperationException(
                    so.targetObject.GetType().Name + " has no serialized field '" + field
                    + "'. The builder and the component have drifted; fix the builder rather "
                    + "than assigning by hand.");

            property.objectReferenceValue = value;
        }

        /// <summary>
        /// Assigns a serialized array of object references, resizing it to match.
        /// </summary>
        /// <remarks>
        /// Resized rather than assumed: the field's initialiser sets a length in C#, but a
        /// component added to a REBUILT object deserializes whatever the previous authoring
        /// held. Setting the size here means the array is exactly as long as the rows this run
        /// created, so the gate's per-entry check has one entry per authored row and a shrunk
        /// screen cannot leave a stale reference on the end.
        /// </remarks>
        private static void AssignArray(SerializedObject so, string field, Object[] values)
        {
            SerializedProperty property = so.FindProperty(field);
            if (property == null)
                throw new System.InvalidOperationException(
                    so.targetObject.GetType().Name + " has no serialized field '" + field
                    + "'. The builder and the component have drifted; fix the builder rather "
                    + "than assigning by hand.");

            if (!property.isArray)
                throw new System.InvalidOperationException(
                    so.targetObject.GetType().Name + "." + field + " is not an array, so the "
                    + "builder is assigning it the wrong way round.");

            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        }

        /// <summary>
        /// Puts the legacy menu away without deleting it. 3.2 constraint 5, 3.5.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The object deactivated is the one carrying <c>MainMenu</c>.</b> Deactivating its
        /// <c>menuContent</c> instead would last exactly one frame — <c>MainMenu.Update</c>
        /// re-asserts it — which is the same fact <c>LegacyPracticeLauncher</c> depends on from
        /// the other direction.
        /// </para>
        /// <para>
        /// <b>Found by type through a reflection lookup, not by naming <c>MainMenu</c>.</b> This
        /// file compiles into an asmdef and <c>MainMenu</c> is in the predefined assembly, so the
        /// name is unavailable here for the same reason <c>Net/Client</c> cannot say it. The
        /// component is located by its script asset instead, which is what the scene stores
        /// anyway.
        /// </para>
        /// <para>
        /// Absent is a FAILURE, not a warning: without the legacy menu there is no Practice, and
        /// criterion 5 cannot be met. A scene missing it is a scene this script should not
        /// silently declare finished.
        /// </para>
        /// </remarks>
        private static bool HideLegacyMenu(StringBuilder log)
        {
            System.Type mainMenu = null;
            foreach (System.Reflection.Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                mainMenu = assembly.GetType("MainMenu", throwOnError: false);
                if (mainMenu != null) break;
            }

            if (mainMenu == null)
            {
                log.AppendLine("FAILED: no MainMenu type in this domain, so Practice has nothing "
                               + "to reveal (criterion 5).");
                return false;
            }

            Object[] found = Object.FindObjectsByType(
                mainMenu, FindObjectsInactive.Include, FindObjectsSortMode.None);

            if (found.Length == 0)
            {
                log.AppendLine("FAILED: MainMenu is on no GameObject in " + ScenePath + ", so the "
                               + "Practice entry would lead nowhere (criterion 5).");
                return false;
            }

            foreach (Object instance in found)
            {
                GameObject host = ((Component)instance).gameObject;
                host.SetActive(false);
                log.AppendLine("legacy menu: '" + host.name + "' deactivated, NOT deleted "
                               + "(3.2 constraint 5). IPracticeLauncher reveals it.");
            }

            return true;
        }
    }
}
