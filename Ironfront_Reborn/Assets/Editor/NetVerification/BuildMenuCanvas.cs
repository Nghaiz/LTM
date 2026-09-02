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
    /// from <see cref="WireClientFlow"/> and worth saying why. That script adds one component to
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

        private static readonly Color Ink = new Color(0.93f, 0.94f, 0.96f);
        private static readonly Color ErrorInk = new Color(1f, 0.45f, 0.42f);
        private static readonly Color Backdrop = new Color(0.06f, 0.07f, 0.09f, 0.96f);

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

            GameObject title = BuildTitle(root, controller, log);
            GameObject login = BuildLogin(root, controller, log);
            GameObject register = BuildRegister(root, controller, log);
            GameObject authenticating = BuildAuthenticating(root);
            GameObject lobby = BuildLobby(root, out Text signedIn, out Button browseRooms);
            GameObject browser = BuildRoomBrowser(root, controller, log);
            GameObject createRoom = BuildCreateRoom(root, controller, log);
            GameObject roomLobby = BuildRoomLobby(root, controller, log);
            GameObject backBar = BuildPracticeBackBar(root, out Button backButton);

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
            Assign(so, "_practiceBackBar", backBar);
            Assign(so, "_practiceBackButton", backButton);
            Assign(so, "_signedInText", signedIn);
            so.ApplyModifiedPropertiesWithoutUndo();

            // The controller's own Apply() decides this at runtime; the authored state is what a
            // reader of the scene sees, and Title is where a player starts.
            title.SetActive(true);
            login.SetActive(false);
            register.SetActive(false);
            authenticating.SetActive(false);
            lobby.SetActive(false);
            browser.SetActive(false);
            createRoom.SetActive(false);
            roomLobby.SetActive(false);
            backBar.SetActive(false);

            if (!HideLegacyMenu(log)) return false;

            HideDebugShell(log);

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
            GameObject panel = Panel(root, "Title");

            Label(panel, "Heading", "IRONFRONT", 72, new Vector2(0f, 220f), new Vector2(900f, 110f));

            Button multiplayer = MakeButton(
                panel, "Multiplayer", "MULTIPLAYER", new Vector2(0f, 40f), new Vector2(460f, 92f));
            Button practice = MakeButton(
                panel, "Practice", "Practice (offline)", new Vector2(0f, -70f), new Vector2(360f, 64f));

            MenuTitleScreen screen = panel.AddComponent<MenuTitleScreen>();
            var so = new SerializedObject(screen);
            Assign(so, "_controller", controller);
            Assign(so, "_multiplayerButton", multiplayer);
            Assign(so, "_practiceButton", practice);
            so.ApplyModifiedPropertiesWithoutUndo();

            log.AppendLine("title: multiplayer is the primary action, practice is secondary.");
            return panel;
        }

        private static GameObject BuildLogin(
            GameObject root, MenuScreenController controller, StringBuilder log)
        {
            GameObject panel = Panel(root, "Login");

            Label(panel, "Heading", "SIGN IN", 48, new Vector2(0f, 250f), new Vector2(700f, 70f));

            InputField username = Field(
                panel, "Username", "Username", new Vector2(0f, 140f), password: false);
            InputField password = Field(
                panel, "Password", "Password", new Vector2(0f, 60f), password: true);

            Button logIn = MakeButton(panel, "LogIn", "LOG IN", new Vector2(0f, -30f), new Vector2(460f, 74f));
            Button create = MakeButton(
                panel, "CreateAccount", "Create an account", new Vector2(0f, -120f), new Vector2(360f, 56f));

            Text error = Label(
                panel, "Error", string.Empty, 28, new Vector2(0f, -210f), new Vector2(760f, 90f));
            error.color = ErrorInk;

            MenuLoginScreen screen = panel.AddComponent<MenuLoginScreen>();
            var so = new SerializedObject(screen);
            Assign(so, "_controller", controller);
            Assign(so, "_usernameField", username);
            Assign(so, "_passwordField", password);
            Assign(so, "_logInButton", logIn);
            Assign(so, "_createAccountButton", create);
            Assign(so, "_errorText", error);
            so.ApplyModifiedPropertiesWithoutUndo();

            log.AppendLine("login: error label authored; criterion 3 renders here.");
            return panel;
        }

        private static GameObject BuildRegister(
            GameObject root, MenuScreenController controller, StringBuilder log)
        {
            GameObject panel = Panel(root, "Register");

            Label(panel, "Heading", "CREATE ACCOUNT", 48, new Vector2(0f, 290f), new Vector2(900f, 70f));

            InputField username = Field(
                panel, "Username", "Username (3-16, a-z 0-9 _)", new Vector2(0f, 190f), password: false);
            InputField password = Field(
                panel, "Password", "Password", new Vector2(0f, 115f), password: true);
            InputField confirm = Field(
                panel, "ConfirmPassword", "Repeat password", new Vector2(0f, 40f), password: true);
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

        // ------------------------------------------------------------------ P16: the room screens

        /// <summary>The room browser: eight rows, a refresh, a create, a password prompt.</summary>
        private static GameObject BuildRoomBrowser(
            GameObject root, MenuScreenController controller, StringBuilder log)
        {
            GameObject panel = Panel(root, "RoomBrowser");

            Label(panel, "Heading", "ROOMS", 44, new Vector2(-540f, 420f), new Vector2(400f, 64f));

            // Labelled "master", never "ping": see MasterSession.MasterPingMs. A number with no
            // subject is the one thing this readout must not be.
            Text ping = Label(panel, "Ping", "master --", 26, new Vector2(560f, 420f), new Vector2(320f, 50f));

            int rows = MenuRoomBrowserScreen.Rows;
            var buttons = new Object[rows];
            var labels = new Object[rows];

            for (int i = 0; i < rows; i++)
            {
                float y = 330f - (i * 78f);
                Button row = MakeButton(
                    panel, "Row" + i, string.Empty, new Vector2(0f, y), new Vector2(1500f, 68f),
                    out Text caption);

                caption.alignment = TextAnchor.MiddleLeft;
                caption.resizeTextForBestFit = false;
                caption.fontSize = 28;
                Inset(caption.GetComponent<RectTransform>());

                buttons[i] = row;
                labels[i] = caption;
            }

            Text overflow = Label(
                panel, "Overflow", string.Empty, 24, new Vector2(0f, -320f), new Vector2(1500f, 44f));

            Button refresh = MakeButton(
                panel, "Refresh", "REFRESH", new Vector2(-380f, -390f), new Vector2(380f, 70f));
            Button create = MakeButton(
                panel, "CreateRoom", "CREATE ROOM", new Vector2(380f, -390f), new Vector2(380f, 70f));

            Text error = Label(
                panel, "Error", string.Empty, 28, new Vector2(0f, -470f), new Vector2(1500f, 60f));
            error.color = ErrorInk;

            GameObject prompt = BuildPasswordPrompt(
                panel, out InputField password, out Button promptJoin, out Button promptCancel);

            MenuRoomBrowserScreen screen = panel.AddComponent<MenuRoomBrowserScreen>();
            var so = new SerializedObject(screen);
            Assign(so, "_controller", controller);
            AssignArray(so, "_roomButtons", buttons);
            AssignArray(so, "_roomLabels", labels);
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

            prompt.SetActive(false);

            log.AppendLine("room browser: " + rows + " rows, master-ping readout, password prompt.");
            return panel;
        }

        /// <summary>The private-room password prompt, drawn over the browser.</summary>
        private static GameObject BuildPasswordPrompt(
            GameObject parent, out InputField password, out Button join, out Button cancel)
        {
            GameObject prompt = Panel(parent, "PasswordPrompt");

            Label(prompt, "Heading", "PRIVATE ROOM", 40, new Vector2(0f, 140f), new Vector2(700f, 60f));

            password = Field(prompt, "Password", "Room password", new Vector2(0f, 50f), password: true);

            join = MakeButton(prompt, "Join", "JOIN", new Vector2(-160f, -50f), new Vector2(280f, 68f));
            cancel = MakeButton(prompt, "Cancel", "Cancel", new Vector2(160f, -50f), new Vector2(280f, 68f));

            return prompt;
        }

        /// <summary>The create-room form: exactly CreateRoomRequest's six fields.</summary>
        private static GameObject BuildCreateRoom(
            GameObject root, MenuScreenController controller, StringBuilder log)
        {
            GameObject panel = Panel(root, "CreateRoom");

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

            log.AppendLine("create room: criterion 8's even-seats check renders on its error line.");
            return panel;
        }

        /// <summary>The room: two roster columns, side, ready, chat, leave.</summary>
        private static GameObject BuildRoomLobby(
            GameObject root, MenuScreenController controller, StringBuilder log)
        {
            GameObject panel = Panel(root, "RoomLobby");

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

            Text chatLog = Label(
                panel, "ChatLog", string.Empty, 24, new Vector2(0f, -290f), new Vector2(1400f, 130f));
            chatLog.alignment = TextAnchor.LowerLeft;
            chatLog.resizeTextForBestFit = false;

            InputField chatField = Field(panel, "ChatInput", "Say something", new Vector2(-180f, -370f), password: false);
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

        private static GameObject Panel(GameObject root, string name, bool opaque = true)
        {
            var panel = new GameObject(name, typeof(RectTransform));
            panel.transform.SetParent(root.transform, worldPositionStays: false);
            Stretch(panel.GetComponent<RectTransform>());

            if (opaque)
            {
                Image backdrop = panel.AddComponent<Image>();
                backdrop.color = Backdrop;
            }

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
            background.color = new Color(0.16f, 0.20f, 0.26f, 1f);

            Button button = go.AddComponent<Button>();
            button.targetGraphic = background;

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
        /// Starts the debug lobby shell hidden, which is what its own header always described.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Hidden, emphatically NOT removed</b> (3.2 constraint 5, criterion 8). Shift+F2 still
        /// toggles it, <c>Bind</c> still runs, and it is still the only route to the room browser
        /// until P16 lands one. Only the authored starting value of <c>_visible</c> changes.
        /// </para>
        /// <para>
        /// <b>Why it needs changing at all.</b> The scene had <c>_visible: 1</c> because the shell
        /// WAS the user interface — there was nothing else to look at, so starting visible was
        /// correct. With a Canvas behind it, an IMGUI panel drawn from the top-left over the
        /// Title screen is the first thing a player sees, and the phase's own description of the
        /// shell — "behind Shift+F2" — stops being true of the shipped scene.
        /// </para>
        /// <para>
        /// The C# field initializer stays <c>true</c> and is deliberately not touched: a shell
        /// dropped into a scene with no menu should still draw itself, which is the debugging
        /// affordance it exists for. What changes is this scene's authored value.
        /// </para>
        /// </remarks>
        private static void HideDebugShell(StringBuilder log)
        {
            LobbyShellOverlay shell = Object.FindAnyObjectByType<LobbyShellOverlay>(
                FindObjectsInactive.Include);

            if (shell == null)
            {
                // Not a failure: WireClientFlow already fails loudly on a Menu scene with no
                // shell, and duplicating that verdict here would give one fault two voices.
                log.AppendLine("debug shell: none in this scene; nothing to hide.");
                return;
            }

            var so = new SerializedObject(shell);
            SerializedProperty visible = so.FindProperty("_visible");

            if (visible == null)
            {
                log.AppendLine("debug shell: no '_visible' field any more; left as authored.");
                return;
            }

            visible.boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
            log.AppendLine("debug shell: starts hidden, still toggled by Shift+F2, NOT removed "
                           + "(3.2 constraint 5).");
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
