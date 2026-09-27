using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Source invariants for how a remote shot reaches a client's ears.
    /// </summary>
    /// <remarks>
    /// <c>Weapon</c> compiles into <c>Assembly-CSharp</c>, which no test assembly can reference
    /// (ledger E-11b), and the loop's fade is timed on <c>Time.time</c>, which does not advance in
    /// an EditMode test — so both halves are pinned on their source.
    /// </remarks>
    public sealed class RemoteFireSourceInvariantTests
    {
        /// <summary>
        /// A bot's shot is put on the wire, once per shot, and a human's is not announced twice.
        /// </summary>
        /// <remarks>
        /// The 2026-09-27 Island report: only explosions were audible. A client plays a remote
        /// shot only from S_WEAPON_FIRE, the only producers took a ClientSession (which a bot does
        /// not have), and a bot's hitscan bullet leaves ServerProjectileBridge.Launch unannounced.
        /// </remarks>
        [Fact]
        public void ABotShotIsAnnouncedOncePerShot()
        {
            string source = ReadScript("Assembly-CSharp", "Weapon.cs");

            string shoot = MethodBody(
                source, "Weapon.cs", "protected virtual void Shoot(Vector3 direction, bool useMuzzleDirection)");
            int pellets = shoot.IndexOf("SpawnProjectile(direction)", StringComparison.Ordinal);
            int announce = shoot.IndexOf("AnnounceBotShot(direction)", StringComparison.Ordinal);
            Assert.True(
                pellets >= 0 && announce > pellets
                && shoot.IndexOf("AnnounceBotShot(", announce + 1, StringComparison.Ordinal) < 0,
                "Weapon.Shoot must announce a bot's shot exactly once, after the per-pellet loop.");

            string announceBody = MethodBody(source, "Weapon.cs", "private void AnnounceBotShot(Vector3 direction)");
            Assert.Contains("IsClaimed", announceBody, StringComparison.Ordinal);
            Assert.Contains("EmitWeaponFire(", announceBody, StringComparison.Ordinal);
        }

        /// <summary>
        /// A remote automatic weapon's looping report stops after its shooter stops.
        /// </summary>
        /// <remarks>
        /// Weapon.Start sets <c>audio.loop = configuration.auto</c>, and the network path played the
        /// source once per message with nothing ever stopping it: one burst, and that gun sounded
        /// for the rest of the match.
        /// </remarks>
        [Fact]
        public void ARemoteAutomaticWeaponsLoopStopsWhenTheShotsStop()
        {
            string source = ReadScript("Assembly-CSharp", "Weapon.cs");

            string cosmetics = MethodBody(source, "Weapon.cs", "public void PlayFireCosmetics()");
            Assert.Contains("StartFireLoop()", cosmetics, StringComparison.Ordinal);
            Assert.Contains("remoteLoopHoldUntil = ", cosmetics, StringComparison.Ordinal);

            string update = MethodBody(source, "Weapon.cs", "protected virtual void Update()");
            int held = update.IndexOf("remoteLoopHoldUntil", StringComparison.Ordinal);
            int stop = update.IndexOf("StopFireLoop()", StringComparison.Ordinal);
            Assert.True(
                held >= 0 && stop > held,
                "Weapon.Update must fade a remote automatic weapon's loop out once its hold expires.");
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
