using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The night vision atmosphere used to render the night vision reflection never reaches the
    /// screen: it is applied and reset inside one synchronous render, never across a frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The v4.6.5 green screen.</b> A Linux player (2026-11-10) saw the screen turn bright green
    /// and back, again and again, with no night vision on. <c>ReflectionProber</c> applied the
    /// night vision atmosphere to the whole scene, scheduled the second probe and waited for it
    /// over frames; on the Low preset realtime probes are off, the wait ran its full five seconds,
    /// and every frame drawn in it was green. Each map load and room switch did it -- that player
    /// rejoined after every death. Reproduced in the Editor: 285 green frames over 5.0 s on Low,
    /// two on Ultra, on every platform alike; none on either after the fix.
    /// </para>
    /// <para>
    /// Read off the source because the defect is an ordering across frames, which an edit-mode
    /// test cannot render; the in-Editor measurement above is in the PR.
    /// </para>
    /// </remarks>
    public sealed class NightVisionProbeTests
    {
        private static readonly string ProberPath =
            Path.Combine(Application.dataPath, "Scripts", "Assembly-CSharp", "ReflectionProber.cs");

        [Test]
        public void TheProbeSetupNeverWaitsWithNightVisionOn()
        {
            string setup = BodyOf("private IEnumerator SetupProbesCoroutine()");

            StringAssert.Contains("yield return", setup, "Setup: the setup is no longer a coroutine; re-read this test.");
            Assert.IsFalse(
                setup.Contains("ApplyNightvision"),
                "The probe setup applies the night vision atmosphere itself. Every frame it yields "
                + "while that is on is drawn green on the player's screen (v4.6.5, five seconds of it "
                + "on the Low preset). Render the night vision reflection in RenderNightVisionReflection.");
        }

        [Test]
        public void TheNightVisionReflectionIsRenderedInsideOneCall()
        {
            string render = BodyOf("private RenderTexture RenderNightVisionReflection(");

            Assert.IsFalse(render.Contains("yield"), "The night vision render waits across a frame.");
            Assert.IsTrue(
                Regex.IsMatch(render, @"ApplyNightvision\(\);\s*try\s*\{[^}]*RenderToCubemap\([^}]*\}\s*finally\s*\{[^}]*ResetAtmosphere\(\);"),
                "The night vision atmosphere must be applied, rendered under with the synchronous "
                + "RenderToCubemap, and reset in a finally, so nothing is drawn with it on and a throw "
                + "cannot leave it on.");
        }

        [Test]
        public void TheLowPresetSkipsTheProbesInsteadOfWaitingForThem()
        {
            string setup = BodyOf("private IEnumerator SetupProbesCoroutine()");

            int skip = setup.IndexOf("QualitySettings.realtimeReflectionProbes", System.StringComparison.Ordinal);
            int render = setup.IndexOf("RenderProbe()", System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(skip, 0, "Nothing checks whether realtime probes are on.");
            Assert.Less(skip, render, "The probe is scheduled before the check, so Low still waits five seconds for nothing.");
        }

        private static string BodyOf(string signature)
        {
            Assert.IsTrue(File.Exists(ProberPath), $"missing source: {ProberPath}");
            string source = Regex.Replace(File.ReadAllText(ProberPath), @"//[^\r\n]*", string.Empty);
            int at = source.IndexOf(signature, System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(at, 0, $"no '{signature}' in ReflectionProber.cs");
            int open = source.IndexOf('{', at);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }
            Assert.Fail($"'{signature}' has no closing brace");
            return string.Empty;
        }
    }
}
