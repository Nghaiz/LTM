using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The opening state of a match, asserted against the SHIPPED SCENE DATA rather than
    /// against a C# default that no shipped map reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why these live in a dotnet test and not in EditMode.</b> Unity's EditMode suite is not
    /// run by CI — <c>ci.ps1</c> only compiles — so an invariant parked there is enforced by
    /// whoever remembers to run it. These read the scene YAML off disk, exactly as
    /// <c>DustbowlFitsOnTheWireTests</c> does, so they gate every push.
    /// </para>
    /// <para>
    /// <b>Why they are scanned rather than listed.</b> The map set is discovered from
    /// <c>Assets/Scenes</c>, so a third map added later is covered on the day it lands instead
    /// of silently sitting outside a hardcoded pair. The defect these guard against was never
    /// specific to one map: it was the same on every map that authors a capture point.
    /// </para>
    /// </remarks>
    public sealed class CaptureOpeningStateTests
    {
        /// <summary>
        /// The capture speed every point is authored at, per person per second.
        /// </summary>
        /// <remarks>
        /// 0.06 gives a 25 m point a 16.7s neutral capture and a 33.3s takeover. The previous
        /// 0.2 gave 4.5s and 9s — a rate cleared by walking through a radius rather than by
        /// holding it, and the reason 32 bots could carve up a whole map inside two minutes.
        /// </remarks>
        private const string ExpectedCaptureSpeed = "0.06";

        /// <summary>The GUID of <c>CapturePoint.cs</c>, as the scenes reference it.</summary>
        private const string CapturePointScriptGuid = "11005de75c307d114b42494cef599182";

        /// <summary>The GUID of <c>MatchController.cs</c>.</summary>
        private const string MatchControllerScriptGuid = "dd9a98525d9667343a3f9b53a2785a42";

        [Fact]
        public void EveryAuthoredCapturePointUsesTheSlowCaptureSpeed()
        {
            var offenders = new List<string>();
            int checkedPoints = 0;

            foreach (string scene in ScenesWithCapturePoints())
            {
                foreach (string block in ComponentBlocks(SceneText(scene), CapturePointScriptGuid))
                {
                    checkedPoints++;
                    string? speed = Field(block, "captureSpeed");
                    if (speed != ExpectedCaptureSpeed)
                        offenders.Add($"{scene}: captureSpeed {speed ?? "(absent)"}");
                }
            }

            // Guards the empty denominator: a parser that silently matched nothing would pass
            // this test forever while asserting nothing at all.
            Assert.True(
                checkedPoints > 0,
                "no CapturePoint component was found in any scene — the scan matched nothing, "
                + "so this test proves nothing. Check CapturePointScriptGuid against "
                + "Assets/Scripts/Assembly-CSharp/CapturePoint.cs.meta.");

            Assert.True(
                offenders.Count == 0,
                $"{offenders.Count} of {checkedPoints} authored capture point(s) are not at "
                + $"{ExpectedCaptureSpeed}/s:\n  " + string.Join("\n  ", offenders)
                + "\n\nA point left at the old 0.2 falls to one body in 4.5s, which is how a "
                + "map ends up fully owned before a player is out of the loadout screen. "
                + "Changing CapturePoint.captureSpeed's C# default does NOT change an authored "
                + "point — every scene serializes its own value, so the scene data must move too.");
        }

        [Fact]
        public void EveryMatchControllerFallbackMatchesTheAuthoredSpeed()
        {
            var offenders = new List<string>();
            int checkedControllers = 0;

            foreach (string scene in ScenesWithCapturePoints())
            {
                foreach (string block in ComponentBlocks(SceneText(scene), MatchControllerScriptGuid))
                {
                    checkedControllers++;
                    string? speed = Field(block, "_captureSpeed");
                    if (speed != ExpectedCaptureSpeed)
                        offenders.Add($"{scene}: _captureSpeed {speed ?? "(absent)"}");
                }
            }

            Assert.True(
                checkedControllers > 0,
                "no MatchController was found in any scene carrying capture points.");

            Assert.True(
                offenders.Count == 0,
                $"{offenders.Count} of {checkedControllers} MatchController fallback(s) are not "
                + $"at {ExpectedCaptureSpeed}/s:\n  " + string.Join("\n  ", offenders)
                + "\n\nThis is the speed used by a point that authored none, so leaving it fast "
                + "means the next map authored without a per-point value inherits the old bug.");
        }

        [Fact]
        public void EveryMapOpensWithAtLeastOneOwnedPointPerTeam()
        {
            // The mirror of the failure X-53 documented: a map that hands NEITHER team a base
            // makes both spawn-point counts zero, which ApplyElimination reads as a double
            // wipe-out one second into Playing. Asserted per map, because the opening owner is
            // scene data and nothing else checks it.
            foreach (string scene in ScenesWithCapturePoints())
            {
                var owners = ComponentBlocks(SceneText(scene), CapturePointScriptGuid)
                    .Select(block => Field(block, "owner"))
                    .ToList();

                Assert.True(
                    owners.Contains("0"),
                    $"{scene} authors no capture point owned by team 0, so that team opens with "
                    + "no base and the round ends in a draw about a second after it begins.");
                Assert.True(
                    owners.Contains("1"),
                    $"{scene} authors no capture point owned by team 1, so that team opens with "
                    + "no base and the round ends in a draw about a second after it begins.");
            }
        }

        [Fact]
        public void EveryMapOpensWithAtLeastOneNeutralPointToFightOver()
        {
            // The complaint this whole change answers, stated as data: if a map authored every
            // point to a team there would be nothing neutral at the opening whatever the bots
            // did, and "x1 / x1 at the start" would be unreachable by any amount of gating.
            foreach (string scene in ScenesWithCapturePoints())
            {
                var owners = ComponentBlocks(SceneText(scene), CapturePointScriptGuid)
                    .Select(block => Field(block, "owner"))
                    .ToList();

                Assert.True(
                    owners.Contains("-1"),
                    $"{scene} authors every capture point to a team, so the map has nothing "
                    + "neutral to contest and the objective bar can never read x1 / x1.");
            }
        }

        // ------------------------------------------------------------------ scene YAML

        /// <summary>Scene files that carry at least one capture point.</summary>
        private static IEnumerable<string> ScenesWithCapturePoints()
        {
            string dir = Path.Combine(RepoRoot(), "Ironfront_Reborn", "Assets", "Scenes");
            Assert.True(Directory.Exists(dir), $"no scenes directory at {dir}");

            foreach (string path in Directory.EnumerateFiles(dir, "*.unity").OrderBy(p => p))
            {
                if (File.ReadAllText(path).Contains(CapturePointScriptGuid, StringComparison.Ordinal))
                    yield return Path.GetFileNameWithoutExtension(path);
            }
        }

        private static string SceneText(string scene)
            => File.ReadAllText(
                Path.Combine(RepoRoot(), "Ironfront_Reborn", "Assets", "Scenes", scene + ".unity"));

        /// <summary>
        /// Each MonoBehaviour block in <paramref name="sceneText"/> whose script is
        /// <paramref name="scriptGuid"/>, from the m_Script line to the next document marker.
        /// </summary>
        /// <remarks>
        /// Cut at <c>--- !u!</c> rather than by a fixed line count: a serialized field list
        /// grows, and a window that guessed its height would silently start reading a
        /// neighbouring object's fields instead of reporting that it could not find its own.
        /// </remarks>
        private static IEnumerable<string> ComponentBlocks(string sceneText, string scriptGuid)
        {
            foreach (System.Text.RegularExpressions.Match match in Regex.Matches(sceneText, @"m_Script: \{fileID: 11500000, guid: "
                                                             + scriptGuid + @", type: 3\}"))
            {
                int start = match.Index;
                int end = sceneText.IndexOf("--- !u!", start, StringComparison.Ordinal);
                yield return end < 0 ? sceneText.Substring(start) : sceneText[start..end];
            }
        }

        /// <summary>One serialized field's raw value, or null when the block does not carry it.</summary>
        private static string? Field(string block, string name)
        {
            System.Text.RegularExpressions.Match m = Regex.Match(block, @"^\s*" + Regex.Escape(name) + @": (\S+)\s*$",
                                  RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : null;
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
