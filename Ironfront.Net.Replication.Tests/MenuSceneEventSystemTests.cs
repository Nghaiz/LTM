using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The menu scene holds exactly one EventSystem.
    /// </summary>
    /// <remarks>
    /// Edit-mode captures of the overlay pages used to create an EventSystem in whatever scene the
    /// Editor had open, and Menu.unity was saved with twenty-one of them (2026-10-09): every start
    /// of the game then logged "There can be only one active Event System" twenty times, and which
    /// of them took input was up to Unity. <c>OverlayHost.EnsureEventSystem</c> now runs in play
    /// mode only; this reads the committed scene, so a leak from any other tool fails here too.
    /// </remarks>
    public sealed class MenuSceneEventSystemTests
    {
        private const string MenuScenePath = "Ironfront_Reborn/Assets/Scenes/Menu.unity";

        [Fact]
        public void TheMenuSceneHasOneEventSystem()
        {
            string scene = File.ReadAllText(Path.Combine(RepoRoot(), MenuScenePath.Replace('/', Path.DirectorySeparatorChar)));

            // m_FirstSelected is a field of the EventSystem component and of nothing else.
            int eventSystems = Regex.Matches(scene, @"^\s+m_FirstSelected:", RegexOptions.Multiline).Count;

            Assert.True(eventSystems == 1,
                $"Menu.unity holds {eventSystems} EventSystem components; it must hold exactly one. More " +
                "than one is a tool that created objects in the open scene (see OverlayHost.EnsureEventSystem); " +
                "delete the extras in the Editor rather than changing this number.");
        }

        private static string RepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Ironfront.sln"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
        }
    }
}
