using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using RegexMatch = System.Text.RegularExpressions.Match;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The wiring that keeps the match's keys out of menus the player cannot see, read from the
    /// files the game loads (owner's report of 2026-10-09: practice "crashed" or threw the player
    /// back to the menu).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Esc menu and the deploy screen hide by switching their canvas off, which left every
    /// button pressable: DEPLOY stayed selected after the click that closed the deploy screen, A
    /// moved the selection onto the hidden EXIT GAME, and the jump key (Submit) pressed it. Two
    /// pieces fix it and both are invisible when missing, so both are pinned here:
    /// <c>MatchMenuKeyGuard</c> on the match's EventSystem, and a hidden menu made non-interactable
    /// through the CanvasGroup beside its canvas (<c>MenuCanvas.SetShown</c>).
    /// </para>
    /// <para>
    /// Unity's EditMode tests cover the guard's behaviour but are not run by CI; these are.
    /// </para>
    /// </remarks>
    public sealed class HiddenMenuKeysWiringTests
    {
        private const string PrefabPath = "Ironfront_Reborn/Assets/Prefab/Ingame UI Container.prefab";
        private const string GuardMeta = "Ironfront_Reborn/Assets/Scripts/Net/Client/Hud/MatchMenuKeyGuard.cs.meta";
        private const string Scripts = "Ironfront_Reborn/Assets/Scripts/";

        private const string GameObjectClass = "1";
        private const string CanvasClass = "223";
        private const string CanvasGroupClass = "225";
        private const string MonoBehaviourClass = "114";

        [Fact]
        public void TheMatchEventSystemCarriesTheGuard()
        {
            Prefab prefab = Prefab.Load(Read(PrefabPath));
            string guardGuid = Regex.Match(Read(GuardMeta), @"guid: ([0-9a-f]{32})").Groups[1].Value;
            Assert.Equal(32, guardGuid.Length);

            List<(string Class, string Body)> components = prefab.ComponentsOf("EventSystem");
            Assert.Contains(components, c => c.Class == MonoBehaviourClass && c.Body.Contains("guid: " + guardGuid));
        }

        [Theory]
        [InlineData("Menu UI")]
        [InlineData("Loadout UI Canvas")]
        public void EachMenuCanvasHasTheGroupThatMakesItInertWhenHidden(string menu)
        {
            List<(string Class, string Body)> components = Prefab.Load(Read(PrefabPath)).ComponentsOf(menu);

            Assert.Contains(components, c => c.Class == CanvasClass);
            Assert.Contains(components, c => c.Class == CanvasGroupClass);
        }

        [Fact]
        public void TheEscMenuShowsAndHidesThroughMenuCanvas()
        {
            string source = Read(Scripts + "Assembly-CSharp/IngameMenuUi.cs");

            Assert.Contains("MenuCanvas.SetShown(instance.canvas, true);", source);
            Assert.Contains("MenuCanvas.SetShown(instance.canvas, false);", source);
            Assert.DoesNotMatch(@"instance\.canvas\.enabled\s*=", source);
        }

        [Fact]
        public void TheDeployScreenShowsAndHidesThroughMenuCanvas()
        {
            string source = Read(Scripts + "Assembly-CSharp/LoadoutUi.cs");

            Assert.Contains("MenuCanvas.SetShown(uiCanvas, true);", source);
            Assert.Contains("MenuCanvas.SetShown(uiCanvas, false);", source);
            Assert.DoesNotMatch(@"uiCanvas\.enabled\s*=", source);
        }

        [Fact]
        public void TheGuardIsToldWhenTheMatchMenusAreUp()
        {
            string source = Read(Scripts + "NetBindings/IronfrontNetBindings.cs");
            RegexMatch install = Regex.Match(source, @"NetClientBindings\.MatchMenuShowing\s*=\s*\(\)\s*=>\s*(?<body>[^;]+);");

            Assert.True(install.Success, "IronfrontNetBindings must install NetClientBindings.MatchMenuShowing");
            Assert.Contains("IngameMenuUi.IsOpen()", install.Groups["body"].Value);
            Assert.Contains("LoadoutUi.IsOpen()", install.Groups["body"].Value);
        }

        // ------------------------------------------------------------------ helpers

        private sealed class Prefab
        {
            private readonly Dictionary<string, (string Class, string Body)> _documents;

            private Prefab(Dictionary<string, (string Class, string Body)> documents) => _documents = documents;

            public static Prefab Load(string yaml)
            {
                var documents = new Dictionary<string, (string Class, string Body)>();
                foreach (string document in Regex.Split(yaml, @"\n(?=--- !u!)"))
                {
                    RegexMatch header = Regex.Match(document, @"^--- !u!(\d+) &(-?\d+)");
                    if (header.Success) documents[header.Groups[2].Value] = (header.Groups[1].Value, document);
                }
                return new Prefab(documents);
            }

            /// <summary>The components of the one GameObject called <paramref name="name"/>.</summary>
            public List<(string Class, string Body)> ComponentsOf(string name)
            {
                List<string> matches = _documents
                    .Where(d => d.Value.Class == GameObjectClass
                                && Regex.IsMatch(d.Value.Body, @"\n  m_Name: " + Regex.Escape(name) + @"\r?\n"))
                    .Select(d => d.Key)
                    .ToList();
                Assert.True(matches.Count == 1, $"expected one GameObject called '{name}' in {PrefabPath}, found {matches.Count}");

                string body = _documents[matches[0]].Body;
                var components = new List<(string Class, string Body)>();
                foreach (RegexMatch component in Regex.Matches(body, @"- component: \{fileID: (-?\d+)\}"))
                    if (_documents.TryGetValue(component.Groups[1].Value, out (string Class, string Body) document))
                        components.Add(document);
                return components;
            }
        }

        private static string Read(string relativePath)
        {
            string path = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;

            throw new InvalidOperationException("Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
