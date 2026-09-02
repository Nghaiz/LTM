using System.Text;
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
            GameObject lobby = BuildLobby(root, out Text signedIn);
            GameObject backBar = BuildPracticeBackBar(root, out Button backButton);

            var so = new SerializedObject(controller);
            Assign(so, "_titleScreen", title);
            Assign(so, "_loginScreen", login);
            Assign(so, "_registerScreen", register);
            Assign(so, "_authenticatingScreen", authenticating);
            Assign(so, "_lobbyScreen", lobby);
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

        private static GameObject BuildLobby(GameObject root, out Text signedIn)
        {
            GameObject panel = Panel(root, "Lobby");

            Label(panel, "Heading", "SIGNED IN", 48, new Vector2(0f, 160f), new Vector2(700f, 70f));
            signedIn = Label(panel, "SignedIn", string.Empty, 34, new Vector2(0f, 60f), new Vector2(900f, 70f));

            // Says what is missing rather than showing an empty screen. P16 replaces this label
            // with the room browser; until then the shell is still the only route on, and a
            // player who has just proved criterion 2 is owed an explanation of why the screen
            // stops here.
            Label(panel, "Note",
                  "The room browser arrives in P16. Shift+F2 opens the debug lobby shell.",
                  26, new Vector2(0f, -40f), new Vector2(1100f, 70f));

            return panel;
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

            return button;
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
