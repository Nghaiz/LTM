using System.Collections.Generic;
using System.Text;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client;
using Ironfront.Net.Unity.Client.Menu;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
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

        /// <summary>
        /// Player builds always serialize a fresh menu from this source, so a checkout cannot
        /// accidentally package an older generated Canvas merely because the scene authoring
        /// command was not run by hand.
        /// </summary>
        internal static void RebuildForPlayerBuild()
        {
            var log = new StringBuilder();
            if (!Build(log)) throw new BuildFailedException(log.ToString());
            Debug.Log("[build-menu-canvas/prebuild]\n" + log);
        }

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
            canvas.pixelPerfect = true;

            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            MenuScreenController controller = root.AddComponent<MenuScreenController>();

            MenuToast toast = BuildToast(root);
            GameObject title = BuildTitle(root, controller, log);
            GameObject login = BuildLogin(root, controller, toast, log);
            GameObject register = BuildRegister(root, controller, log);
            GameObject practice = BuildPractice(root, controller, toast, out Button practiceBack);
            GameObject settings = BuildSettings(root, controller, toast, out Button settingsBack);
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
            AngularPanel menuShade = Angular(panel, "MenuShade", Vector2.zero,
                new Vector2(1920f, 1080f), 0f, new Color(2f / 255f, 9f / 255f, 17f / 255f, 0.97f),
                AngularEdge.None, 0f, Color.clear);
            menuShade.SetGradient(new Color(4f / 255f, 11f / 255f, 18f / 255f, 0.08f), 90f);
            PackCorner(panel, new Vector2(850f, 430f), new Vector2(170f, 170f));
            // The supplied wordmark, used as-is. It replaces a Text label that spelled the same
            // name in the default font -- the artwork was in the pack the whole time and nothing
            // referenced it.
            Icon(panel, "Logo", "branding/ironfront-reborn-logo.png",
                new Vector2(-560f, 350f), new Vector2(475f, 130f));

            Text tagline = Label(panel, "Eyebrow", "ONLINE TACTICAL WARFARE", 13,
                new Vector2(-560f, 286f), new Vector2(520f, 28f));
            tagline.alignment = TextAnchor.MiddleLeft;

            // `.menu-button` for the three secondary rows and `--primary` for Multiplayer, which is
            // also the only one the stylesheet makes taller and orange. Each carries the icon the
            // prototype names for it; `PackButton` has taken an icon since it was written and no
            // call site had ever passed one.
            Button multiplayer = PackButton(panel, "Multiplayer", "MULTIPLAYER",
                new Vector2(-560f, 150f), new Vector2(480f, 76f), "primary", "icons/users.png");
            Button practice = PackButton(panel, "Practice", "PRACTICE OFFLINE",
                new Vector2(-560f, 58f), new Vector2(480f, 68f), "menu", "icons/target.png");
            Button settings = PackButton(panel, "Settings", "SETTINGS",
                new Vector2(-560f, -22f), new Vector2(480f, 68f), "menu", "icons/settings.png");
            Button exit = PackButton(panel, "Exit", "EXIT",
                new Vector2(-560f, -102f), new Vector2(480f, 68f), "menu", "icons/power.png");

            Text footer = Label(panel, "Tagline", "SIMPLE BATTLES\nENDLESS POSSIBILITIES", 12,
                new Vector2(-545f, -450f), new Vector2(520f, 48f));
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
            GameObject root, MenuScreenController controller, MenuToast toast, StringBuilder log)
        {
            GameObject panel = Panel(root, "Sign In", opaque: false);
            FullscreenSprite(panel, "Background", "backgrounds/auth.png");
            FullscreenTint(panel, "AuthVignette", new Color(3f / 255f, 9f / 255f, 16f / 255f, 0.30f));
            Icon(panel, "Logo", "branding/ironfront-reborn-logo.png",
                new Vector2(-660f, 440f), new Vector2(300f, 82f));
            Text authMotto = Label(panel, "AuthMotto", "SAME BATTLEFIELD\nMORE FRIENDS", 11,
                new Vector2(700f, 440f), new Vector2(320f, 52f));
            authMotto.alignment = TextAnchor.UpperRight;
            authMotto.color = Hex("9DB7CB");
            Angular(panel, "GlassPanel", new Vector2(0f, -5f), new Vector2(700f, 680f),
                CutCard, Surface);

            Label(panel, "Kicker", "SECURE CONNECTION // EU-01", 10,
                new Vector2(0f, 260f), new Vector2(520f, 22f)).color = CyanSoft;
            Label(panel, "Heading", "SIGN IN", 38, new Vector2(0f, 224f), new Vector2(520f, 52f));
            Text subheading = Label(panel, "Subheading", "Welcome back, soldier.", 15,
                new Vector2(0f, 190f), new Vector2(520f, 24f));
            subheading.color = new Color(0.76f, 0.79f, 0.82f);

            InputField username = PackField(panel, "Username", "Username / Callsign",
                new Vector2(0f, 122f), new Vector2(480f, 54f), password: false,
                iconAsset: "icons/user.png");
            InputField password = PackField(panel, "Password", "Password",
                new Vector2(0f, 56f), new Vector2(480f, 54f), password: true,
                iconAsset: "icons/lock.png");
            Button reveal = AddPasswordReveal(panel, password, new Vector2(210f, 56f));
            Toggle remember = PackToggle(panel, "RememberMe", "Remember me",
                new Vector2(-125f, 10f));
            Button forgot = LinkButton(panel, "ForgotPassword", "Forgot password?",
                new Vector2(145f, 10f), new Vector2(210f, 38f));
            Button logIn = PackButton(panel, "LogIn", "LOG IN", new Vector2(0f, -52f),
                new Vector2(420f, 72f), "primary");
            Label(panel, "Divider", "────────────  OR  ────────────", 14,
                new Vector2(0f, -103f), new Vector2(480f, 24f));
            Button create = PackButton(panel, "CreateAccount", "CREATE AN ACCOUNT",
                new Vector2(0f, -154f), new Vector2(420f, 50f), "secondary", "icons/user.png");
            Button back = PackButton(panel, "Back", "BACK", new Vector2(-500f, -310f),
                new Vector2(142f, 48f), "secondary");

            Text error = Label(panel, "Error", string.Empty, 18,
                new Vector2(0f, -221f), new Vector2(540f, 52f));
            error.color = ErrorInk;

            SetVerticalNavigation(username, password, reveal, remember, forgot, logIn, create, back);
            ConfigureKeyboard(panel,
                new Selectable[] { username, password, reveal, remember, forgot, logIn, create, back }, logIn, back);
            panel.AddComponent<MenuDevelopmentControls>().Configure(toast, forgot);

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
            FullscreenTint(panel, "AuthVignette", new Color(3f / 255f, 9f / 255f, 16f / 255f, 0.30f));
            Angular(panel, "GlassPanel", Vector2.zero, new Vector2(760f, 820f), CutCard, Surface);

            Icon(panel, "Logo", "branding/ironfront-reborn-logo.png",
                new Vector2(-660f, 440f), new Vector2(300f, 82f));
            Label(panel, "Kicker", "NEW OPERATIVE // REGISTRATION", 10,
                new Vector2(0f, 326f), new Vector2(660f, 22f)).color = CyanSoft;
            Label(panel, "Heading", "CREATE ACCOUNT", 38, new Vector2(0f, 286f), new Vector2(660f, 58f));
            Label(panel, "Subtitle", "Create your secure battlefield identity.", 15,
                new Vector2(0f, 248f), new Vector2(660f, 28f)).color = Hex("8AA5BA");

            InputField username = PackField(panel, "Username", "Username (3-16, a-z 0-9 _)",
                new Vector2(0f, 190f), new Vector2(600f, 54f), false, "icons/user.png");
            InputField password = PackField(panel, "Password", "Password",
                new Vector2(0f, 125f), new Vector2(600f, 54f), true, "icons/lock.png");
            InputField confirm = PackField(panel, "ConfirmPassword", "Repeat password",
                new Vector2(0f, 60f), new Vector2(600f, 54f), true, "icons/shield.png");
            Button revealPassword = AddPasswordReveal(panel, password, new Vector2(270f, 125f));
            Button revealConfirm = AddPasswordReveal(panel, confirm, new Vector2(270f, 60f));
            InputField displayName = PackField(panel, "DisplayName", "Display name (optional)",
                new Vector2(0f, -5f), new Vector2(600f, 54f), false);

            Button create = MakeButton(
                panel, "Create", "CREATE OPERATIVE", new Vector2(0f, -105f), new Vector2(600f, 50f));
            Button back = MakeButton(
                panel, "Back", "Already enlisted? Sign in", new Vector2(0f, -165f), new Vector2(360f, 44f));

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
            MultiplayerShade(panel);
            PackPanel(panel, "OperationsPanel", new Vector2(0f, -30f), new Vector2(1540f, 870f));
            TopBar(panel, controller, toast, "PRACTICE", "OFFLINE SIMULATION", "LOCAL SESSION",
                offline: true, ("SETTINGS", MenuNavigationAction.Settings));
            Label(panel, "Kicker", "COMBAT SIMULATION // SOLO TRAINING", 11,
                new Vector2(-510f, 375f), new Vector2(520f, 28f)).alignment = TextAnchor.MiddleLeft;
            Label(panel, "Heading", "PRACTICE MODE", 42,
                new Vector2(-480f, 325f), new Vector2(580f, 66f)).alignment = TextAnchor.MiddleLeft;
            Text note = Label(panel, "Note",
                "Configure a local battle and deploy immediately.", 14,
                new Vector2(-390f, 275f), new Vector2(760f, 34f));
            note.alignment = TextAnchor.MiddleLeft;
            note.color = Hex("8DA8BA");

            AngularPanel mapCard = Angular(panel, "MapPreview", new Vector2(-475f, -40f),
                new Vector2(490f, 520f),
                0f, Hex("061522"), AngularEdge.All, 1f, Hex("3F6986"));
            Image practiceArt = Plain(mapCard.gameObject, "MapArt", new Vector2(0f, 112f),
                new Vector2(490f, 295f), Color.white);
            practiceArt.sprite = IronfrontRebornUiAssetCatalog.Sprite("backgrounds/multiplayer.png");
            Label(panel, "MapPreviewTitle", "SELECTED THEATER", 11,
                new Vector2(-475f, -8f), new Vector2(430f, 28f)).alignment = TextAnchor.MiddleLeft;
            Label(panel, "Offline", "OFFLINE SIMULATION // LOCAL SESSION", 14,
                new Vector2(-475f, -50f), new Vector2(430f, 40f)).alignment = TextAnchor.MiddleLeft;

            const float rightCentre = 290f;
            const float rightWidth = 820f;
            const float gap = 14f;
            float two = (rightWidth - gap) * 0.5f;
            float three = (rightWidth - (gap * 2f)) / 3f;
            float leftTwo = rightCentre - ((two + gap) * 0.5f);
            float rightTwo = rightCentre + ((two + gap) * 0.5f);
            float leftThree = rightCentre - three - gap;
            float rightThree = rightCentre + three + gap;

            Label(panel, "ConfigHeading", "SIMULATION PARAMETERS                         01", 13,
                new Vector2(rightCentre, 242f), new Vector2(rightWidth, 32f))
                .alignment = TextAnchor.MiddleLeft;
            Dropdown map = MakeDropdown(panel, "PracticeMap", new Vector2(leftTwo, 190f),
                new Vector2(two, 48f));
            Button mode = MakeButton(panel, "PracticeMode", "GAME MODE // IN DEVELOPMENT",
                new Vector2(rightTwo, 190f), new Vector2(two, 48f));
            Button difficulty = MakeButton(panel, "Difficulty", "AI DIFFICULTY // IN DEVELOPMENT",
                new Vector2(leftThree, 115f), new Vector2(three, 48f));
            Button bots = MakeButton(panel, "BotCount", "BOT COUNT // IN DEVELOPMENT",
                new Vector2(rightCentre, 115f), new Vector2(three, 48f));
            Button matchTime = MakeButton(panel, "MatchTime", "MATCH TIME // IN DEVELOPMENT",
                new Vector2(rightThree, 115f), new Vector2(three, 48f));
            Button weather = MakeButton(panel, "Weather", "WEATHER // IN DEVELOPMENT",
                new Vector2(leftThree, 40f), new Vector2(three, 48f));
            Button timeOfDay = MakeButton(panel, "TimeOfDay", "TIME OF DAY // IN DEVELOPMENT",
                new Vector2(rightCentre, 40f), new Vector2(three, 48f));
            Button playerTeam = MakeButton(panel, "PlayerTeam", "PLAYER TEAM // IN DEVELOPMENT",
                new Vector2(rightThree, 40f), new Vector2(three, 48f));
            Button vehicles = MakeButton(panel, "Vehicles", "VEHICLES // IN DEVELOPMENT",
                new Vector2(leftTwo, -45f), new Vector2(two, 65f));
            Button friendlyFire = MakeButton(panel, "FriendlyFire", "FRIENDLY FIRE // IN DEVELOPMENT",
                new Vector2(rightTwo, -45f), new Vector2(two, 65f));
            Label(panel, "PracticeSummary", "DEPLOYMENT   •   LOCAL HOST", 13,
                new Vector2(rightCentre, -125f), new Vector2(rightWidth, 54f));
            back = PackButton(panel, "Back", "BACK", new Vector2(325f, -365f),
                new Vector2(220f, 50f), "secondary");
            Button start = PackButton(panel, "StartPractice", "START PRACTICE  ›",
                new Vector2(585f, -365f), new Vector2(280f, 50f), "primary");

            MenuPracticeScreen screen = panel.AddComponent<MenuPracticeScreen>();
            var so = new SerializedObject(screen);
            Assign(so, "_controller", controller);
            Assign(so, "_mapDropdown", map);
            Assign(so, "_startButton", start);
            AssignArray(so, "_unsupportedControls", new Object[] { mode, difficulty, bots,
                matchTime, weather, timeOfDay, playerTeam, vehicles, friendlyFire });
            Assign(so, "_toast", toast);
            so.ApplyModifiedPropertiesWithoutUndo();
            ConfigureKeyboard(panel,
                new Selectable[] { map, mode, difficulty, bots, matchTime, weather, timeOfDay,
                    playerTeam, vehicles, friendlyFire, back, start },
                start, back);
            return panel;
        }

        private static GameObject BuildSettings(
            GameObject root, MenuScreenController controller, MenuToast toast, out Button back)
        {
            GameObject panel = Panel(root, "Settings", opaque: false);
            FullscreenSprite(panel, "Background", "backgrounds/multiplayer.png");
            MultiplayerShade(panel);
            PackPanel(panel, "OperationsPanel", new Vector2(0f, -30f), new Vector2(1540f, 870f));
            TopBar(panel, controller, toast, "SYSTEM SETTINGS", "CONFIGURATION", "LOCAL PROFILE",
                offline: true, ("MAIN MENU", MenuNavigationAction.MainMenu));
            Label(panel, "Kicker", "SYSTEM CONTROL // CLIENT CONFIGURATION", 11,
                new Vector2(-480f, 375f), new Vector2(620f, 28f)).alignment = TextAnchor.MiddleLeft;
            Label(panel, "Heading", "SETTINGS", 42,
                new Vector2(-560f, 325f), new Vector2(460f, 66f)).alignment = TextAnchor.MiddleLeft;
            Text settingsNote = Label(panel, "Subtitle",
                "Optimize visuals, performance and battlefield awareness.", 14,
                new Vector2(-360f, 286f), new Vector2(700f, 28f));
            settingsNote.alignment = TextAnchor.MiddleLeft;
            settingsNote.color = Hex("8DA8BA");
            Text profile = Label(panel, "SettingsProfile", "PROFILE   DEFAULT", 13,
                new Vector2(570f, 338f), new Vector2(260f, 30f));
            profile.alignment = TextAnchor.MiddleRight;
            profile.color = Orange;

            Button displayTab = MakeButton(panel, "DisplayTab", "01  DISPLAY",
                new Vector2(-570f, 190f), new Vector2(260f, 64f));
            Button audioTab = MakeButton(panel, "AudioTab", "02  AUDIO",
                new Vector2(-570f, 110f), new Vector2(260f, 64f));
            Button gameplayTab = MakeButton(panel, "GameplayTab", "03  GAMEPLAY",
                new Vector2(-570f, 30f), new Vector2(260f, 64f));

            GameObject displayGroup = Panel(panel, "DisplayGroup", opaque: false);
            GameObject audioGroup = Panel(panel, "AudioGroup", opaque: false);
            GameObject gameplayGroup = Panel(panel, "GameplayGroup", opaque: false);

            Label(displayGroup, "GroupHeading", "DISPLAY & PERFORMANCE", 24,
                new Vector2(170f, 245f), new Vector2(900f, 44f)).alignment = TextAnchor.MiddleLeft;
            Dropdown resolution = MakeDropdown(displayGroup, "Resolution", new Vector2(-60f, 165f));
            Dropdown displayMode = MakeDropdown(displayGroup, "DisplayMode", new Vector2(520f, 165f));
            Dropdown quality = MakeDropdown(displayGroup, "Quality", new Vector2(-60f, 85f));
            Toggle vSync = MakeToggle(displayGroup, "VSync", "V-SYNC", new Vector2(520f, 85f));
            Button fps = MakeButton(displayGroup, "FpsLimit", "FPS LIMIT // IN DEVELOPMENT",
                new Vector2(-60f, -15f), new Vector2(560f, 60f));
            Button motionBlur = MakeButton(displayGroup, "MotionBlur", "MOTION BLUR // IN DEVELOPMENT",
                new Vector2(520f, -15f), new Vector2(560f, 60f));

            Label(audioGroup, "GroupHeading", "AUDIO MIXER", 24,
                new Vector2(170f, 245f), new Vector2(900f, 44f)).alignment = TextAnchor.MiddleLeft;
            Slider volume = MakeSlider(audioGroup, "MasterVolume", "MASTER VOLUME",
                new Vector2(-60f, 165f), 0f, 1f);
            Button music = MakeButton(audioGroup, "MusicVolume", "MUSIC // IN DEVELOPMENT",
                new Vector2(520f, 165f), new Vector2(560f, 60f));
            Button sfx = MakeButton(audioGroup, "SfxVolume", "SOUND EFFECTS // IN DEVELOPMENT",
                new Vector2(-60f, 85f), new Vector2(560f, 60f));
            Button voice = MakeButton(audioGroup, "VoiceVolume", "VOICE CHAT // IN DEVELOPMENT",
                new Vector2(520f, 85f), new Vector2(560f, 60f));
            Button advancedAudio = MakeButton(audioGroup, "AdvancedAudio", "DYNAMIC RANGE // IN DEVELOPMENT",
                new Vector2(-60f, -15f), new Vector2(560f, 60f));
            Button outputDevice = MakeButton(audioGroup, "OutputDevice", "OUTPUT DEVICE // IN DEVELOPMENT",
                new Vector2(520f, -15f), new Vector2(560f, 60f));

            Label(gameplayGroup, "GroupHeading", "GAMEPLAY & ACCESSIBILITY", 24,
                new Vector2(170f, 245f), new Vector2(900f, 44f)).alignment = TextAnchor.MiddleLeft;
            Slider fov = MakeSlider(gameplayGroup, "FieldOfView", "FIELD OF VIEW",
                new Vector2(-60f, 165f), 60f, 120f);
            Slider sensitivity = MakeSlider(gameplayGroup, "Sensitivity", "MOUSE SENSITIVITY",
                new Vector2(520f, 165f), 0.05f, 1f);
            Button language = MakeButton(gameplayGroup, "Language", "LANGUAGE // IN DEVELOPMENT",
                new Vector2(-60f, 85f), new Vector2(560f, 60f));
            Button colorblind = MakeButton(gameplayGroup, "Colorblind", "COLORBLIND // IN DEVELOPMENT",
                new Vector2(520f, 85f), new Vector2(560f, 60f));
            Button accessibility = MakeButton(gameplayGroup, "Accessibility", "SUBTITLES // IN DEVELOPMENT",
                new Vector2(-60f, -15f), new Vector2(560f, 60f));
            Button cameraShake = MakeButton(gameplayGroup, "CameraShake", "CAMERA SHAKE // IN DEVELOPMENT",
                new Vector2(520f, -15f), new Vector2(560f, 60f));

            audioGroup.SetActive(false);
            gameplayGroup.SetActive(false);

            Button reset = PackButton(panel, "Reset", "RESET DEFAULTS",
                new Vector2(90f, -365f), new Vector2(280f, 50f), "secondary");
            back = PackButton(panel, "Back", "CANCEL",
                new Vector2(390f, -365f), new Vector2(260f, 50f), "secondary");
            Button save = PackButton(panel, "Save", "APPLY SETTINGS",
                new Vector2(690f, -365f), new Vector2(300f, 50f), "primary");
            Text settingsStatus = Label(panel, "SettingsStatus", "NO UNSAVED CHANGES", 11,
                new Vector2(-520f, -365f), new Vector2(300f, 30f));
            settingsStatus.alignment = TextAnchor.MiddleLeft;
            settingsStatus.color = Hex("718FA4");

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
            Assign(so, "_statusText", settingsStatus);
            AssignArray(so, "_categoryButtons", new Object[] { displayTab, audioTab, gameplayTab });
            AssignArray(so, "_categoryGroups", new Object[] { displayGroup, audioGroup, gameplayGroup });
            AssignArray(so, "_unsupportedButtons",
                new Object[] { fps, motionBlur, music, sfx, voice, advancedAudio, outputDevice,
                    language, colorblind, accessibility, cameraShake });
            Assign(so, "_toast", toast);
            so.ApplyModifiedPropertiesWithoutUndo();
            ConfigureKeyboard(panel,
                new Selectable[] { displayTab, audioTab, gameplayTab, resolution, displayMode,
                    quality, vSync, fps, motionBlur, volume, music, sfx, voice, advancedAudio,
                    outputDevice, fov, sensitivity, language, colorblind, accessibility,
                    cameraShake, reset, back, save }, save, back);
            return panel;
        }

        // ------------------------------------------------------------------ P16: the room screens

        /// <summary>The room browser: eight rows, a refresh, a create, a password prompt.</summary>
        private static GameObject BuildRoomBrowser(
            GameObject root, MenuScreenController controller, MenuToast toast, StringBuilder log)
        {
            GameObject panel = Panel(root, "Rooms", opaque: false);
            FullscreenSprite(panel, "Background", "backgrounds/multiplayer.png");
            MultiplayerShade(panel);
            TopBar(panel, controller, toast, "MULTIPLAYER", "MULTIPLAYER", "MASTER SERVER",
                offline: false,
                ("MAIN MENU", MenuNavigationAction.MainMenu),
                ("SETTINGS", MenuNavigationAction.Settings));
            PackPanel(panel, "OperationsPanel", new Vector2(0f, -25f), new Vector2(1540f, 850f));

            Label(panel, "Kicker", "TEAM 10 LTD // MULTIPLAYER OPERATIONS", 11,
                new Vector2(-405f, 265f), new Vector2(520f, 20f)).alignment = TextAnchor.MiddleLeft;
            Label(panel, "Heading", "ROOMS", 42,
                new Vector2(-405f, 230f), new Vector2(320f, 48f)).alignment = TextAnchor.MiddleLeft;
            Text browserNote = Label(panel, "Subtitle",
                "Find a room or deploy with the fastest available squad.", 14,
                new Vector2(-275f, 198f), new Vector2(580f, 24f));
            browserNote.alignment = TextAnchor.MiddleLeft;
            browserNote.color = Hex("8DA8BA");

            // Labelled "master", never "ping": see MasterSession.MasterPingMs. A number with no
            // subject is the one thing this readout must not be.
            Text ping = Label(panel, "Ping", "master --", 16,
                new Vector2(445f, 230f), new Vector2(190f, 40f));
            SignalBars(panel, new Vector2(335f, 230f));

            InputField search = PackField(panel, "Search", "Search rooms, maps or modes...",
                new Vector2(-285f, 150f), new Vector2(500f, 48f), password: false,
                iconAsset: "icons/search.png");
            Button refresh = PackButton(panel, "Refresh", "REFRESH", new Vector2(585f, 150f),
                new Vector2(150f, 48f), "secondary", "icons/refresh.png");
            Button mode = PackButton(panel, "ModeFilter", "ALL MODES", new Vector2(90f, 150f),
                new Vector2(230f, 48f), "secondary");
            Button region = PackButton(panel, "RegionFilter", "ALL REGIONS", new Vector2(330f, 150f),
                new Vector2(230f, 48f), "secondary");

            RoomTableHeader(panel, new Vector2(0f, 112f));

            int rows = MenuRoomBrowserScreen.Rows;
            var joins = new Object[rows];
            var names = new Object[rows];
            var maps = new Object[rows];
            var players = new Object[rows];
            var statuses = new Object[rows];

            for (int i = 0; i < rows; i++)
            {
                float y = 72f - (i * 46f);
                (Button Join, Text Name, Text Map, Text Players, Text Status) row =
                    MakeRoomRow(panel, i, new Vector2(0f, y));

                joins[i] = row.Join;
                names[i] = row.Name;
                maps[i] = row.Map;
                players[i] = row.Players;
                statuses[i] = row.Status;
            }

            Text overflow = Label(
                panel, "Overflow", string.Empty, 14, new Vector2(-260f, -275f), new Vector2(520f, 26f));
            overflow.alignment = TextAnchor.MiddleLeft;

            Button create = PackButton(panel, "CreateRoom", "CREATE ROOM",
                new Vector2(440f, -320f), new Vector2(280f, 50f), "primary", "icons/plus.png");
            Button quick = PackButton(panel, "QuickMatch", "QUICK MATCH",
                new Vector2(135f, -320f), new Vector2(280f, 50f), "secondary", "icons/target.png");

            Text error = Label(
                panel, "Error", string.Empty, 16, new Vector2(0f, -372f), new Vector2(900f, 34f));
            error.color = ErrorInk;

            GameObject prompt = BuildPasswordPrompt(
                panel, out InputField password, out Button promptJoin, out Button promptCancel);

            MenuRoomBrowserScreen screen = panel.AddComponent<MenuRoomBrowserScreen>();
            var so = new SerializedObject(screen);
            Assign(so, "_controller", controller);
            AssignRoomRows(so, "_rows", joins, names, maps, players, statuses);            Assign(so, "_searchField", search);
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
            MultiplayerShade(panel);
            PackPanel(panel, "OperationsPanel", Vector2.zero, new Vector2(1460f, 920f));
            TopBar(panel, controller, toast, "CREATE OPERATION", "HOST", "ROOM SETUP",
                offline: false, ("ROOMS", MenuNavigationAction.RoomBrowser));

            // `.create-layout`: two columns of 1.6fr and .8fr with a 28px gap, inside a 1400px
            // content width. The fields keep the wide column and the preview takes the narrow one,
            // which is the opposite of what the single-column revision did -- it had the fields
            // centred and no preview card at all.
            const float leftCentre = -243f;
            const float rightCentre = 471f;
            const float leftWidth = 915f;

            Label(panel, "MissionNumber", "OPERATION // 01", 14, new Vector2(leftCentre, 400f),
                new Vector2(leftWidth, 24f)).alignment = TextAnchor.MiddleLeft;
            Text heading = Label(panel, "Heading", "CREATE ROOM", 44, new Vector2(leftCentre, 356f),
                new Vector2(leftWidth, 60f));
            heading.alignment = TextAnchor.MiddleLeft;

            var fieldSize = new Vector2(leftWidth, 56f);
            InputField name = PackField(panel, "Name", "Room name", new Vector2(leftCentre, 282f),
                fieldSize, password: false);
            // `.two-col`: map and mode share the row exactly as in the HTML. Mode is retained as
            // an honest development control because the room protocol has no mode field.
            float half = (leftWidth - 14f) * 0.5f;
            float leftHalf = leftCentre - (half * 0.5f) - 7f;
            float rightHalf = leftCentre + (half * 0.5f) + 7f;
            Dropdown map = MakeDropdown(panel, "Map", new Vector2(leftHalf, 206f),
                new Vector2(half, 56f));
            Button mode = MakeButton(panel, "Mode", "GAME MODE // IN DEVELOPMENT",
                new Vector2(rightHalf, 206f), new Vector2(half, 56f));

            // `.three-col`: region, maximum players and bots.
            float third = (leftWidth - 28f) / 3f;
            float firstThird = leftCentre - third - 14f;
            float thirdThird = leftCentre + third + 14f;
            Button region = MakeButton(panel, "Region", "REGION // IN DEVELOPMENT",
                new Vector2(firstThird, 130f), new Vector2(third, 56f));
            InputField maxPlayers = PackField(panel, "MaxPlayers",
                "Players (even, 2-" + ProtocolConstants.MAX_PLAYERS + ")",
                new Vector2(leftCentre, 130f), new Vector2(third, 56f),
                password: false);
            maxPlayers.contentType = InputField.ContentType.IntegerNumber;

            InputField bots = PackField(panel, "BotCount", "Bots",
                new Vector2(thirdThird, 130f), new Vector2(third, 56f),
                password: false);
            bots.contentType = InputField.ContentType.IntegerNumber;

            Toggle isPrivate = MakeSwitch(panel, "Private", "PRIVATE ROOM",
                new Vector2(leftHalf, 54f), new Vector2(half, 65f));
            Button balance = MakeButton(panel, "Balance", "AUTO-BALANCE // IN DEVELOPMENT",
                new Vector2(rightHalf, 54f), new Vector2(half, 65f));
            InputField password = PackField(panel, "Password", "Room password",
                new Vector2(leftCentre, -26f), fieldSize, password: true);

            // `.map-preview`: a card with the multiplayer backdrop as its art, the map's own name,
            // and three stat cells under a rule. The title is bound to the dropdown rather than
            // authored, so the card cannot name a map the player did not choose.
            const float previewHeight = 500f;
            AngularPanel preview = Angular(panel, "MapPreview", new Vector2(rightCentre, 106f),
                new Vector2(457f, previewHeight), 0f, Hex("061522"), AngularEdge.All, 1f,
                Hex("3F6986"));

            Image art = Plain(preview.gameObject, "PreviewArt",
                new Vector2(0f, (previewHeight * 0.5f) - 105f), new Vector2(457f, 210f),
                Color.white);
            art.sprite = IronfrontRebornUiAssetCatalog.Sprite("backgrounds/multiplayer.png");

            Text previewKicker = Label(preview.gameObject, "PreviewKicker", "SELECTED THEATER", 11,
                new Vector2(0f, 80f), new Vector2(417f, 20f));
            previewKicker.alignment = TextAnchor.MiddleLeft;
            previewKicker.fontStyle = FontStyle.Bold;
            previewKicker.color = Orange;
            previewKicker.resizeTextForBestFit = false;

            Text previewTitle = Label(preview.gameObject, "PreviewTitle", string.Empty, 28,
                new Vector2(0f, 44f), new Vector2(417f, 44f));
            previewTitle.alignment = TextAnchor.MiddleLeft;
            previewTitle.resizeTextForBestFit = false;

            Text previewNote = Label(preview.gameObject, "PreviewNote",
                "The map every player in this room will load.", 13,
                new Vector2(0f, 6f), new Vector2(417f, 40f));
            previewNote.alignment = TextAnchor.UpperLeft;
            previewNote.color = Hex("8DA8BA");
            previewNote.resizeTextForBestFit = false;

            string[] previewStats = { "CAPACITY", "BOTS", "SECURITY" };
            var previewValues = new Text[previewStats.Length];
            for (int i = 0; i < previewStats.Length; i++)
            {
                AngularPanel cell = Angular(preview.gameObject, "Stat" + i,
                    new Vector2(-139f + (i * 139f), -120f), new Vector2(129f, 62f), 0f,
                    Hex("0A2032"), AngularEdge.All, 0f, Color.clear);
                cell.raycastTarget = false;

                Text caption = Label(cell.gameObject, "Key", previewStats[i], 10,
                    new Vector2(0f, 14f), new Vector2(113f, 18f));
                caption.alignment = TextAnchor.UpperLeft;
                caption.color = Hex("7193AA");
                caption.resizeTextForBestFit = false;

                Text value = Label(cell.gameObject, "Value", "--", 14, new Vector2(0f, -12f),
                    new Vector2(113f, 22f));
                value.alignment = TextAnchor.MiddleLeft;
                value.resizeTextForBestFit = false;
                previewValues[i] = value;
            }

            Text error = Label(
                panel, "Error", string.Empty, 20, new Vector2(leftCentre, -250f),
                new Vector2(leftWidth, 70f));
            error.color = ErrorInk;

            // `.form-footer`: right-aligned, under a rule.
            Angular(panel, "FormRule", new Vector2(0f, -310f), new Vector2(1400f, 1f), 0f,
                new Color(103f / 255f, 160f / 255f, 201f / 255f, 0.28f),
                AngularEdge.All, 0f, Color.clear);

            Button back = PackButton(panel, "Back", "CANCEL",
                new Vector2(270f, -375f), new Vector2(250f, 50f), "secondary");
            Button create = PackButton(panel, "Create", "CREATE OPERATION  ›",
                new Vector2(555f, -375f), new Vector2(300f, 50f), "primary");

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
            Assign(so, "_mapPreviewTitle", previewTitle);
            Assign(so, "_mapPreviewCapacity", previewValues[0]);
            Assign(so, "_mapPreviewBots", previewValues[1]);
            Assign(so, "_mapPreviewSecurity", previewValues[2]);
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
            MultiplayerShade(panel);
            PackPanel(panel, "OperationsPanel", Vector2.zero, new Vector2(1580f, 940f));
            // The prototype's ROOMS link is deliberately NOT wired here. On every other screen it
            // navigates to the browser; from inside a room it would mean walking out of a match the
            // player is already in, and the lobby's own LEAVE control is the one place that
            // decision belongs. The item is still drawn -- greyed out it would read as broken -- so
            // it takes the shared development notice, which is the spec's rule for a control the
            // game cannot honestly honour.
            TopBar(panel, controller, toast, "LOBBY", "IN MATCH", "ROOM LOBBY", offline: false,
                ("ROOMS", null));

            Label(panel, "Kicker", "OPERATION LOBBY // WAITING FOR DEPLOYMENT", 11,
                new Vector2(-430f, 420f), new Vector2(700f, 20f)).alignment = TextAnchor.MiddleLeft;
            Text heading = Label(panel, "Heading", string.Empty, 34,
                new Vector2(-350f, 382f), new Vector2(860f, 52f));
            heading.alignment = TextAnchor.MiddleLeft;
            Text status = Label(panel, "Status", string.Empty, 14,
                new Vector2(-350f, 346f), new Vector2(860f, 30f));
            status.alignment = TextAnchor.MiddleLeft;
            status.color = Hex("8DA8BA");

            Angular(panel, "TeamZeroCard", new Vector2(-420f, 100f), new Vector2(620f, 510f),
                CutCard, new Color(5f / 255f, 25f / 255f, 43f / 255f, 0.88f),
                AngularEdge.All, 1f, Hex("3E87B7"));
            Angular(panel, "TeamOneCard", new Vector2(420f, 100f), new Vector2(620f, 510f),
                CutCard, new Color(30f / 255f, 17f / 255f, 12f / 255f, 0.88f),
                AngularEdge.All, 1f, Orange);
            Text versus = Label(panel, "Versus", "VS", 28, new Vector2(0f, 115f),
                new Vector2(100f, 100f));
            versus.color = Orange;
            versus.fontStyle = FontStyle.Bold;

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
            var zeroReadyBadges = new Object[perSide];
            var oneReadyBadges = new Object[perSide];

            for (int i = 0; i < perSide; i++)
            {
                float y = 250f - (i * 52f);
                (Text Row, Image ReadyBadge) zero = RosterRow(
                    panel, "TeamZeroRow" + i, new Vector2(-420f, y));
                (Text Row, Image ReadyBadge) one = RosterRow(
                    panel, "TeamOneRow" + i, new Vector2(420f, y));
                zeroRows[i] = zero.Row;
                oneRows[i] = one.Row;
                zeroReadyBadges[i] = zero.ReadyBadge;
                oneReadyBadges[i] = one.ReadyBadge;
            }

            Button switchSide = PackButton(panel, "SwitchSide", "SWITCH SIDE",
                new Vector2(165f, -410f), new Vector2(200f, 50f), "secondary");
            Text switchLabel = switchSide.GetComponentInChildren<Text>(includeInactive: true);
            Button ready = PackButton(panel, "Ready", "READY UP",
                new Vector2(385f, -410f), new Vector2(200f, 50f), "primary");
            Text readyLabel = ready.GetComponentInChildren<Text>(includeInactive: true);
            Button leave = PackButton(panel, "Leave", "LEAVE ROOM",
                new Vector2(-600f, -410f), new Vector2(220f, 50f), "danger", "icons/leave.png");
            Button invite = PackButton(panel, "CopyInvite", "COPY INVITE",
                new Vector2(585f, 392f), new Vector2(220f, 48f), "secondary", "icons/copy.png");
            Button start = PackButton(panel, "StartGame", "START GAME",
                new Vector2(615f, -410f), new Vector2(220f, 50f), "command");

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
            AssignArray(so, "_teamZeroReadyBadges", zeroReadyBadges);
            AssignArray(so, "_teamOneReadyBadges", oneReadyBadges);
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
        private static (Text Row, Image ReadyBadge) RosterRow(
            GameObject parent, string name, Vector2 position)
        {
            Text row = Label(parent, name, string.Empty, 28, position, new Vector2(560f, 46f));
            row.alignment = TextAnchor.MiddleLeft;
            row.resizeTextForBestFit = false;
            Image ready = Icon(parent, name + "ReadyBadge", "badges/ready.png",
                position + new Vector2(235f, 0f), new Vector2(66f, 22f));
            ready.gameObject.SetActive(false);
            return (row, ready);
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

        private static Image FullscreenTint(GameObject parent, string name, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Stretch(go.GetComponent<RectTransform>());
            Image image = go.GetComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;
            return image;
        }

        private static void MultiplayerShade(GameObject parent)
            => FullscreenTint(parent, "MultiplayerShade",
                new Color(3f / 255f, 11f / 255f, 18f / 255f, 0.35f));

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
        /// This remains for CSS-only shapes that have no supplied bitmap (team cards, rules,
        /// toggles and table chrome). Repeated panels, buttons and fields deliberately go through
        /// the PNG helpers below so the game renders the pack artwork itself.
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

        /// <summary>A stretchable raster face supplied by the HTML UI pack.</summary>
        private static Image PackSurface(GameObject parent, string name, string asset,
            Vector2 position, Vector2 size, bool raycastTarget = false)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, size);

            Image image = go.GetComponent<Image>();
            image.sprite = IronfrontRebornUiAssetCatalog.Sprite(asset);
            image.type = Image.Type.Simple;
            image.preserveAspect = false;
            image.color = Color.white;
            image.raycastTarget = raycastTarget;
            return image;
        }

        /// <summary>
        /// The exact <c>operations-panel.png</c> face used by the HTML pages. Child content is
        /// parented to the screen (as before), while the face itself is sent behind it.
        /// </summary>
        private static Image PackPanel(GameObject parent, string name, Vector2 position,
            Vector2 size)
        {
            Image panel = PackSurface(parent, name, "panels/operations-panel.png", position, size);
            return panel;
        }

        /// <summary>The pack's raster button face, selected by the HTML action variant.</summary>
        private static Image PackButtonFace(GameObject buttonObject, string kind)
        {
            string asset = kind == "primary"
                ? "buttons/primary.png"
                : "buttons/secondary.png";
            Image face = buttonObject.GetComponent<Image>();
            face.sprite = IronfrontRebornUiAssetCatalog.Sprite(asset);
            face.type = Image.Type.Simple;
            face.preserveAspect = false;
            face.raycastTarget = true;
            return face;
        }

        /// <summary>The pack's raster field frame, including its cyan inset rule.</summary>
        private static Image PackFieldFace(GameObject fieldObject)
        {
            Image face = fieldObject.GetComponent<Image>();
            face.sprite = IronfrontRebornUiAssetCatalog.Sprite("inputs/field.png");
            face.type = Image.Type.Simple;
            face.preserveAspect = false;
            face.raycastTarget = true;
            return face;
        }

        /// <summary>The small HUD corner used by the HTML main page.</summary>
        private static Image PackCorner(GameObject parent, Vector2 position, Vector2 size)
            => PackSurface(parent, "HudCorner", "decorative/corner.png", position, size);

        private static Button PackButton(
            GameObject parent, string name, string caption, Vector2 position, Vector2 size,
            string kind, string iconAsset = null)
        {
            Color captionInk;
            switch (kind)
            {
                case "primary":
                    captionInk = Ink900;
                    break;
                case "command":
                    captionInk = Ink;
                    break;
                case "danger":
                    captionInk = Ink;
                    break;
                case "menu":
                    captionInk = Ink;
                    break;
                default:
                    captionInk = Ink;
                    break;
            }

            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, size);

            Image face = PackButtonFace(go, kind);
            // Primary and ordinary outline actions keep the pack artwork untouched. The HTML has
            // no separate danger/command bitmap, so those two semantic states tint the supplied
            // secondary face rather than falling back to unrelated generated geometry.
            if (kind == "command") face.color = Hex("1C8CCE");
            else if (kind == "danger") face.color = Hex("A84150");

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
        /// <summary>
        /// The prototype's <c>.topbar</c> — brand, section nav and account chip — shared by all
        /// five multiplayer screens.
        /// </summary>
        /// <param name="links">
        /// One entry per nav item after the current one. A null <c>Go</c> renders the item but
        /// routes it to the development notice instead of navigating, which is the spec's rule for
        /// a control the game cannot honestly honour.
        /// </param>
        /// <remarks>
        /// <para>
        /// <b>This was missing from every multiplayer screen</b>, which the three-screen revision
        /// papered over with a per-screen <c>Logo</c> Text label and a hand-drawn "tab" rectangle.
        /// The bar is the one element that makes the five screens read as one application rather
        /// than five unrelated panels, so it is authored once here.
        /// </para>
        /// <para>
        /// <b>The account chip states the screen, not a person.</b> The prototype fills it with
        /// <c>VANGUARD_07 / LEVEL 18</c>, which the spec forbids copying: those are mock values,
        /// and a build that invented a username would be lying about who is signed in. The bar is
        /// authored with a true description of where the player is; binding the real operative once
        /// there is a session to read it from is a separate change, and the two Text objects carry
        /// names so it can find them.
        /// </para>
        /// </remarks>
        private static void TopBar(GameObject panel, MenuScreenController controller,
            MenuToast toast, string current,
            string chipTitle, string chipDetail, bool offline,
            params (string Label, MenuNavigationAction? Go)[] links)
        {
            // `.topbar`: 76px tall, three columns -- a 310px brand, centred nav, a 260px chip. The
            // canvas is 1920x1080 anchored at its centre, so the bar's own centre sits 38px below
            // the top edge.
            const float barCentreY = 502f;
            const float barHeight = 76f;

            AngularPanel panelBar = Angular(panel, "TopBar", new Vector2(0f, barCentreY),
                new Vector2(1920f, barHeight), 0f, new Color(5f / 255f, 17f / 255f, 28f / 255f, 0.9f),
                AngularEdge.Bottom, 1f, new Color(97f / 255f, 165f / 255f, 213f / 255f, 0.4f));

            // `.brand-button`: a 210px wordmark on its own washed, right-ruled panel.
            Angular(panelBar.gameObject, "BrandButton", new Vector2(-805f, 0f),
                new Vector2(310f, barHeight), 0f,
                new Color(24f / 255f, 57f / 255f, 83f / 255f, 0.75f),
                AngularEdge.All, 0f, Color.clear);
            Icon(panelBar.gameObject, "BrandMark", "branding/ironfront-reborn-logo.png",
                new Vector2(-805f, 0f), new Vector2(210f, 46f));

            // `.account-chip`: a status dot and two lines, right-aligned on its own gradient.
            Angular(panelBar.gameObject, "AccountChip", new Vector2(830f, 0f),
                new Vector2(260f, barHeight), 0f,
                new Color(20f / 255f, 55f / 255f, 80f / 255f, 0.25f),
                AngularEdge.All, 0f, Color.clear);
            Plain(panelBar.gameObject, "StatusDot", new Vector2(740f, 0f), new Vector2(10f, 10f),
                offline ? Cyan : Green);

            Text chipName = Label(panelBar.gameObject, "AccountName", chipTitle, 16,
                new Vector2(848f, 10f), new Vector2(200f, 22f));
            chipName.alignment = TextAnchor.MiddleLeft;
            chipName.color = Ink;
            chipName.resizeTextForBestFit = false;

            Text chipDetailText = Label(panelBar.gameObject, "AccountDetail", chipDetail, 11,
                new Vector2(848f, -12f), new Vector2(200f, 18f));
            chipDetailText.alignment = TextAnchor.MiddleLeft;
            chipDetailText.color = Hex("6F94AD");
            chipDetailText.resizeTextForBestFit = false;

            // `.topbar nav button`: 145px minimum, a 4px rule on the current one. Laid out from the
            // centre outward in the order the prototype lists them.
            var items = new List<(string Label, MenuNavigationAction? Go)>(links.Length + 1)
                { (current, null) };
            items.AddRange(links);

            const float itemWidth = 165f;
            float first = -((items.Count - 1) * itemWidth) * 0.5f;
            var unsupported = new List<Button>();

            for (int i = 0; i < items.Count; i++)
            {
                bool isCurrent = i == 0;
                (string Label, MenuNavigationAction? Go) item = items[i];
                var position = new Vector2(first + (i * itemWidth), 0f);

                AngularPanel face = Angular(panelBar.gameObject, "Nav" + i, position,
                    new Vector2(itemWidth, barHeight), 0f,
                    isCurrent
                        ? new Color(46f / 255f, 90f / 255f, 123f / 255f, 0.3f)
                        : Color.clear,
                    isCurrent ? AngularEdge.Bottom : AngularEdge.None, 4f, Orange);

                Text caption = Label(panelBar.gameObject, "Nav" + i + "Label", item.Label, 14,
                    position, new Vector2(itemWidth, barHeight));
                caption.fontStyle = FontStyle.Bold;
                caption.color = isCurrent ? Color.white : Hex("9DB3C4");
                caption.raycastTarget = false;

                if (isCurrent) continue;

                // The current section is a label, not a control. Every other item is a real button,
                // and one that cannot navigate still has to SAY so rather than sit there inert --
                // which is what the spec's shared development notice is for.
                face.raycastTarget = true;
                Button button = face.gameObject.AddComponent<Button>();
                button.targetGraphic = face;
                button.transition = Selectable.Transition.None;

                if (item.Go.HasValue)
                    face.gameObject.AddComponent<MenuNavigationButton>()
                        .Configure(controller, item.Go.Value);
                else
                {
                    unsupported.Add(button);
                }
            }

            if (unsupported.Count > 0)
                panelBar.gameObject.AddComponent<MenuDevelopmentControls>()
                    .Configure(toast, unsupported.ToArray());
        }

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
            var go = new GameObject(name, typeof(RectTransform), typeof(Image),
                typeof(InputField));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, size);

            Image frame = PackFieldFace(go);

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

        /// <summary>
        /// The column widths of the room table, as fractions of the row, and where each column's
        /// centre falls.
        /// </summary>
        /// <remarks>
        /// The prototype's <c>grid-template-columns: 1.55fr 1.25fr .7fr .62fr .65fr .7fr</c> has six
        /// columns; this table has four plus the join button, because MODE and PING have no source
        /// in the room protocol — see <c>MenuRoomBrowserScreen.RoomRow</c>. The name column keeps the
        /// prototype's emphasis as the widest, and the two narrow numeric columns stay narrow so the
        /// eye can compare them down the list, which is the whole reason a table is a table.
        /// </remarks>
        private static readonly (string Title, float Width, float Centre)[] RoomColumns =
        {
            ("ROOM", 476f, -462f),
            ("MAP", 392f, -28f),
            ("PLAYERS", 196f, 266f),
            ("STATUS", 196f, 462f),
        };

        /// <summary>The <c>.room-row--head</c> strip: same columns, read once.</summary>
        private static void RoomTableHeader(GameObject parent, Vector2 position)
        {
            const float rowWidth = 1400f;

            AngularPanel header = Angular(parent, "RoomTableHead", position, new Vector2(rowWidth, 26f),
                0f, Hex("0B2133"), AngularEdge.All, 0f, Color.clear);
            header.raycastTarget = false;

            foreach ((string title, _, float centre) in RoomColumns)
            {
                Text cell = Label(header.gameObject, "Head" + title, title, 12,
                    new Vector2(centre, 0f), new Vector2(180f, 24f));
                cell.alignment = TextAnchor.MiddleLeft;
                cell.fontStyle = FontStyle.Bold;
                cell.color = Hex("82A8C2");
                cell.resizeTextForBestFit = false;
                cell.raycastTarget = false;
            }

            Text joinHead = Label(header.gameObject, "HeadJoin", string.Empty, 12,
                new Vector2(632f, 0f), new Vector2(140f, 24f));
            joinHead.resizeTextForBestFit = false;
            joinHead.raycastTarget = false;
        }

        /// <summary>
        /// One room row: four cells and a join button, on the prototype's <c>.room-row</c>.
        /// </summary>
        /// <remarks>
        /// The row is a numbered <c>Row{i}</c> object that the screen activates and deactivates whole,
        /// so an unused row costs nothing and no cell can outlive its row.
        /// </remarks>
        private static (Button Join, Text Name, Text Map, Text Players, Text Status) MakeRoomRow(
            GameObject parent, int index, Vector2 position)
        {
            const float rowWidth = 1400f;

            var go = new GameObject("Row" + index, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, new Vector2(rowWidth, 42f));

            Image image = go.GetComponent<Image>();
            image.color = new Color(4f / 255f, 15f / 255f, 26f / 255f, 0.55f);
            image.raycastTarget = false;

            Text[] cells = new Text[RoomColumns.Length];
            for (int i = 0; i < RoomColumns.Length; i++)
            {
                Text cell = Label(go, "Cell" + RoomColumns[i].Title, string.Empty, 15,
                    new Vector2(RoomColumns[i].Centre, 0f),
                    new Vector2(RoomColumns[i].Width - 20f, 38f));
                cell.alignment = TextAnchor.MiddleLeft;
                cell.resizeTextForBestFit = false;
                cell.raycastTarget = false;
                cells[i] = cell;
            }

            // The name is what the eye lands on, so it is the one cell the prototype bolds and
            // brightens; the rest are read second.
            cells[0].fontStyle = FontStyle.Bold;
            cells[0].color = Hex("EAF6FF");
            for (int i = 1; i < cells.Length; i++) cells[i].color = Hex("9DBAD0");

            Button join = PackButton(go, "JoinButton", "JOIN", new Vector2(632f, 0f),
                new Vector2(130f, 34f), "command");

            return (join, cells[0], cells[1], cells[2], cells[3]);
        }

        /// <summary>
        /// Fills the screen's array of row structs.
        /// </summary>
        /// <remarks>
        /// <see cref="AssignArray"/> cannot do this: it writes object references into an array of
        /// references, whereas a row is a struct whose cells are five separate fields. The array has
        /// to be resized and each element's children written by name — and a name that has drifted
        /// from the component is reported here rather than silently leaving a cell empty.
        /// </remarks>
        private static void AssignRoomRows(SerializedObject so, string field,
            Object[] joins, Object[] names, Object[] maps, Object[] players, Object[] statuses)
        {
            SerializedProperty array = so.FindProperty(field);
            if (array == null || !array.isArray)
                throw new System.InvalidOperationException(
                    so.targetObject.GetType().Name + " has no serialized array '" + field +
                    "'. The builder and the component have drifted; fix the builder rather than " +
                    "assigning by hand.");

            array.arraySize = joins.Length;
            for (int i = 0; i < joins.Length; i++)
            {
                SerializedProperty element = array.GetArrayElementAtIndex(i);
                Relative(element, "Join").objectReferenceValue = joins[i];
                Relative(element, "Name").objectReferenceValue = names[i];
                Relative(element, "Map").objectReferenceValue = maps[i];
                Relative(element, "Players").objectReferenceValue = players[i];
                Relative(element, "Status").objectReferenceValue = statuses[i];
            }
        }

        private static SerializedProperty Relative(SerializedProperty element, string name)
        {
            SerializedProperty property = element.FindPropertyRelative(name);
            if (property == null)
                throw new System.InvalidOperationException(
                    "The room-row struct has no field '" + name + "'. A row cell would have stayed " +
                    "empty and the table would have looked authored.");
            return property;
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
            label.font = size >= 18 ? BoldFont() : DefaultFont();
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
            Button button = PackButton(parent, name, caption, position, size, "secondary");
            captionText = button.GetComponentInChildren<Text>(includeInactive: true);
            return button;
        }

        /// <summary>A checkbox with a caption beside it.</summary>
        private static Toggle MakeToggle(
            GameObject parent, string name, string caption, Vector2 position)
            => MakeSwitch(parent, name, caption, position, new Vector2(560f, 65f));

        private static Toggle MakeSwitch(
            GameObject parent, string name, string caption, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Toggle));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, size);
            Image card = PackFieldFace(go);

            var boxObject = new GameObject("Track", typeof(RectTransform), typeof(Image));
            boxObject.transform.SetParent(go.transform, worldPositionStays: false);
            Centre(boxObject.GetComponent<RectTransform>(),
                new Vector2((-size.x * 0.5f) + 30f, 0f), new Vector2(30f, 17f));
            Image track = boxObject.GetComponent<Image>();
            track.color = Hex("176F9F");

            var markObject = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            markObject.transform.SetParent(boxObject.transform, worldPositionStays: false);
            Centre(markObject.GetComponent<RectTransform>(), new Vector2(7f, 0f), new Vector2(11f, 11f));
            Image mark = markObject.GetComponent<Image>();
            mark.color = CyanSoft;

            Text label = Label(go, "Caption", caption, 14, new Vector2(30f, 0f),
                new Vector2(size.x - 90f, 44f));
            label.alignment = TextAnchor.MiddleLeft;
            label.resizeTextForBestFit = false;

            Toggle toggle = go.GetComponent<Toggle>();
            toggle.targetGraphic = card;
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
            => MakeDropdown(parent, name, position, new Vector2(560f, 60f));

        private static Dropdown MakeDropdown(GameObject parent, string name, Vector2 position,
            Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            Centre(go.GetComponent<RectTransform>(), position, size);

            Image background = PackFieldFace(go);

            Text caption = Label(go, "Label", string.Empty, 18, Vector2.zero,
                new Vector2(size.x - 20f, size.y - 12f));
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

            Text itemLabel = Label(itemObject, "Item Label", string.Empty, 18, Vector2.zero,
                new Vector2(size.x - 20f, 44f));
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
            => PackField(parent, name, placeholder, position, new Vector2(560f, 60f), password);

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
        /// The bundled UI font used by the HTML prototype's fallback stack.
        /// </summary>
        /// <remarks>
        /// The explicit project path keeps glyph metrics stable between Editor and player builds.
        /// A built-in fallback remains so a missing asset produces readable diagnostics instead
        /// of an entirely blank menu.
        /// </remarks>
        private static Font DefaultFont()
        {
            Font font = AssetDatabase.LoadAssetAtPath<Font>("Assets/Font/Roboto-Regular.ttf");
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return font;
        }

        private static Font BoldFont()
        {
            Font font = AssetDatabase.LoadAssetAtPath<Font>("Assets/Font/Roboto-Bold.ttf");
            return font != null ? font : DefaultFont();
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

    /// <summary>Regenerates the HTML-pack Canvas immediately before Unity collects scenes.</summary>
    internal sealed class BuildMenuCanvasBeforePlayerBuild : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
            => BuildMenuCanvas.RebuildForPlayerBuild();
    }
}
