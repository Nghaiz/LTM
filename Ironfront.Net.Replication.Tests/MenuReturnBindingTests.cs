using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A player who leaves a match lands on a menu that works.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Playtest 2026-09-29, found while testing bug 2.</b> Two faults, one symptom: back on the
    /// menu, MULTIPLAYER did nothing until the game was restarted.
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <c>ClientFlowBootstrap</c> bound the menu once, in <c>Awake</c>. The Menu scene brings a
    /// new menu every time it loads, so every return from a match -- its end, a kick, QUIT TO
    /// MENU -- drew a menu with no flow behind it. Measured in the Editor: after the return the
    /// menu's flow field was null; with the re-bind it was set and the room browser opened.
    /// </description></item>
    /// <item><description>
    /// <c>IngameMenuUi.Menu</c> loaded the Menu scene underneath the flow, which stayed InMatch
    /// with the game-server link up. Measured: through the flow, the state went InMatch to Lobby
    /// and the Menu scene loaded.
    /// </description></item>
    /// </list>
    /// <para>
    /// Read off disk, because CI does not run the EditMode suite and both are Unity components.
    /// </para>
    /// </remarks>
    public sealed class MenuReturnBindingTests
    {
        private const string BootstrapPath =
            "Ironfront_Reborn/Assets/Scripts/Net/Client/ClientFlowBootstrap.cs";

        private const string IngameMenuPath =
            "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/IngameMenuUi.cs";

        [Fact]
        public void TheMenuSceneIsBoundAgainEveryTimeItLoads()
        {
            string handler = MethodBody(Read(BootstrapPath), "private void OnSceneLoaded(");

            System.Text.RegularExpressions.Match menuBranch = Regex.Match(
                handler,
                @"if \(string\.Equals\(scene\.name, MenuScene, StringComparison\.Ordinal\)\)\s*\{(?<body>.*?)return;",
                RegexOptions.Singleline);

            Assert.True(menuBranch.Success, "OnSceneLoaded no longer has a Menu-scene branch.");
            Assert.Contains("BindMenuCanvas(", menuBranch.Groups["body"].Value);
        }

        /// <summary>
        /// Owner report 2026-10-03: kicked from a match, the pointer was gone over the menu every
        /// time the window took focus, until a restart. The match left it locked and hidden, and
        /// only the pause menu's way out ever freed it.
        /// </summary>
        [Fact]
        public void TheMenuGetsAFreeVisiblePointerEveryTimeItLoads()
        {
            string handler = MethodBody(Read(BootstrapPath), "private void OnSceneLoaded(");

            System.Text.RegularExpressions.Match menuBranch = Regex.Match(
                handler,
                @"if \(string\.Equals\(scene\.name, MenuScene, StringComparison\.Ordinal\)\)\s*\{(?<body>.*?)return;",
                RegexOptions.Singleline);

            Assert.True(menuBranch.Success, "OnSceneLoaded no longer has a Menu-scene branch.");
            string body = menuBranch.Groups["body"].Value;
            Assert.Contains("Cursor.lockState = CursorLockMode.None;", body);
            Assert.Contains("Cursor.visible = true;", body);
        }

        [Fact]
        public void TheFlowOffersTheWayOutOfAMatch()
        {
            string source = Read(BootstrapPath);

            Assert.Contains("NetClientBindings.LeaveMatch = LeaveMatchFromMenu;", source);
            Assert.Contains("_session.LeaveMatch();", MethodBody(source, "private bool LeaveMatchFromMenu("));
        }

        [Fact]
        public void QuitToMenuLeavesThroughTheFlowBeforeLoadingTheScene()
        {
            string menu = MethodBody(Read(IngameMenuPath), "public void Menu(");

            int leave = menu.IndexOf("NetClientBindings.TryLeaveMatch()", StringComparison.Ordinal);
            int load = menu.IndexOf("SceneManager.LoadScene(1)", StringComparison.Ordinal);

            Assert.True(leave >= 0, "IngameMenuUi.Menu no longer asks the flow to leave the match.");
            Assert.True(load > leave,
                "IngameMenuUi.Menu must ask the flow first and load the scene only when there was "
                + "no match to leave; loading it underneath the flow strands the player's menu.");
        }

        /// <summary>The body of the first method whose declaration starts with <paramref name="signature"/>.</summary>
        private static string MethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"'{signature}' was not found.");

            int open = source.IndexOf('{', start);
            int depth = 0;

            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }

            throw new InvalidOperationException($"'{signature}' has no closing brace.");
        }

        private static string Read(string relative)
            => File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null;
                 d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
