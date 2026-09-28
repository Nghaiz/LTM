using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Source invariants for what a network respawn leaves in the player's hands, and for the log
    /// that records it.
    /// </summary>
    /// <remarks>
    /// <c>Actor</c> compiles into <c>Assembly-CSharp</c> and <c>IronfrontLog</c> into the
    /// diagnostics assembly, neither of which a test project can reference (ledger E-11b), so
    /// both are pinned on their source.
    /// </remarks>
    public sealed class LocalRespawnSourceInvariantTests
    {
        /// <summary>
        /// Arming a body for a network deploy first discards what it still carries.
        /// </summary>
        /// <remarks>
        /// The 2026-09-27 report: two rifles in the first-person view after respawning. The
        /// offline death drops all five slots through <c>Die</c>, so <c>SpawnAt</c> always arms an
        /// empty body; a client's networked death never runs <c>Die</c>, so each respawn's
        /// <c>EquipLoadout</c> stacked five new weapons on the previous five, and
        /// <c>SwitchToFirstAvailableWeapon</c> unholsters without holstering.
        /// </remarks>
        [Fact]
        public void ANetworkRespawnArmsAnEmptyBody()
        {
            string source = ReadScript("Assembly-CSharp", "Actor.cs");

            string equip = MethodBody(source, "Actor.cs", "public void EquipLoadout()");
            int discard = equip.IndexOf("DiscardCarriedWeapons()", StringComparison.Ordinal);
            int arm = equip.IndexOf("SpawnLoadoutWeapons()", StringComparison.Ordinal);
            Assert.True(discard >= 0 && arm > discard,
                "Actor.EquipLoadout must discard the carried weapons before arming new ones.");

            string discardBody = MethodBody(source, "Actor.cs", "private void DiscardCarriedWeapons()");
            Assert.Contains("Destroy(weapon.gameObject)", discardBody, StringComparison.Ordinal);
            Assert.Contains("weapons[i] = null;", discardBody, StringComparison.Ordinal);
            Assert.Contains("activeWeapon = null;", discardBody, StringComparison.Ordinal);
        }

        /// <summary>
        /// Two clients started in the same second write two session logs, not one.
        /// </summary>
        /// <remarks>
        /// The name was the second alone, opened with <c>FileMode.Create</c>, so the second client
        /// truncated the first's file and both then wrote over each other's bytes.
        /// </remarks>
        [Fact]
        public void ASessionLogIsNamedForItsProcess()
        {
            string source = ReadScript("Net", "Diagnostics", "IronfrontLog.cs");
            string open = MethodBody(source, "IronfrontLog.cs", "private static bool TryOpen()");

            Assert.Contains("GetCurrentProcess().Id", open, StringComparison.Ordinal);
            Assert.Contains("$\"ironfront-{stamp}-{pid}.log\"", open, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ helpers

        private static string ReadScript(params string[] relativeParts)
        {
            var parts = new List<string> { RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts" };
            parts.AddRange(relativeParts);

            string path = Path.Combine(parts.ToArray());
            Assert.True(File.Exists(path), $"Expected to find a script at {path}.");
            return File.ReadAllText(path);
        }

        private static string MethodBody(string source, string fileName, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{fileName} no longer declares '{signature}'.");

            int open = source.IndexOf('{', start);
            Assert.True(open >= 0, $"{fileName}: '{signature}' has no body.");

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"{fileName}: '{signature}' has an unbalanced body.");
            return string.Empty;
        }

        private static string RepoRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Ironfront.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No Ironfront.sln found walking up from {AppContext.BaseDirectory}.");
        }
    }
}
