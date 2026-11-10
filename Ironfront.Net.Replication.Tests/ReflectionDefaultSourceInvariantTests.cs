using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The map's reflection probes, once rendered, become the scene's default reflection and stop
    /// being looked up per object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner report 2026-10-01: "the client stutters, especially on Forest Lake".</b> Both probes
    /// cover the whole map, so every object reflected the same cubemap anyway; finding that out cost
    /// 2.3 ms of <c>SamplePerObjectReflectionProbes</c> a frame, 3,497 calls, in a 100-bot Forest
    /// Lake match (development build profile, 2026-10-02).
    /// </para>
    /// <para>
    /// A source scan: <c>ReflectionProber</c> is <c>Assembly-CSharp</c>, which no test assembly
    /// compiles.
    /// </para>
    /// </remarks>
    public sealed class ReflectionDefaultSourceInvariantTests
    {
        [Fact]
        public void TheDayRenderIsCopiedThenTheNightRenderedInOneCallThenBothAdopted()
        {
            string setup = Normalized(Method("SetupProbesCoroutine").Body!);

            int lowSkip = setup.IndexOf("if(!QualitySettings.realtimeReflectionProbes){yieldbreak;}", StringComparison.Ordinal);
            int dayRender = setup.IndexOf("intnormal=normalProbe.RenderProbe();", StringComparison.Ordinal);
            int dayCopy = setup.IndexOf("RenderTextureday=normalProbe.IsFinishedRendering(normal)?CopyOf(normalProbe):null;", StringComparison.Ordinal);
            int night = setup.IndexOf("RenderTexturenight=day!=null?RenderNightVisionReflection(day):null;", StringComparison.Ordinal);
            int adopt = setup.IndexOf("AdoptAsDefaultReflection(day,night);", StringComparison.Ordinal);

            // The Low preset has no realtime probes: waiting for one cost five seconds for nothing.
            Assert.True(lowSkip >= 0 && dayRender > lowSkip,
                "the setup must give up on presets without realtime probes before it schedules a render.");
            // Copied after another render, the day copy held that render (2026-10-02).
            Assert.True(dayCopy > dayRender && night > dayCopy && adopt > night,
                "the day render is copied, then the night vision one rendered, and only then both are adopted.");
            // Night vision applied in the coroutine stayed on across every frame of the probe wait:
            // five seconds of green on Low, a flash on higher presets, on every OS (2026-11-10).
            Assert.DoesNotContain("ApplyNightvision", setup, StringComparison.Ordinal);
            Assert.Equal(1, setup.Split("yieldreturnnull;").Length - 1);

            string render = Normalized(Method("RenderNightVisionReflection").Body!);
            Assert.DoesNotContain("yield", render, StringComparison.Ordinal);
            Assert.Contains(
                "TimeOfDay.instance.ApplyNightvision();try{rendered=camera.RenderToCubemap(night);}finally{TimeOfDay.instance.ResetAtmosphere();",
                render, StringComparison.Ordinal);
        }

        [Fact]
        public void AdoptionReplacesTheProbesWithTheSameCubemapAtTheProbesIntensity()
        {
            string adopt = Normalized(Method("AdoptAsDefaultReflection").Body!);

            Assert.StartsWith("{if(day==null){", adopt, StringComparison.Ordinal);
            Assert.Contains("RenderSettings.defaultReflectionMode=DefaultReflectionMode.Custom;", adopt, StringComparison.Ordinal);
            Assert.Contains("RenderSettings.customReflectionTexture=normalReflection;", adopt, StringComparison.Ordinal);
            Assert.Contains("RenderSettings.reflectionIntensity=normalProbe.intensity;", adopt, StringComparison.Ordinal);
            Assert.Contains("normalProbe.enabled=false;", adopt, StringComparison.Ordinal);
            Assert.Contains("nightVisionProbe.enabled=false;", adopt, StringComparison.Ordinal);
            Assert.DoesNotContain("CopyOf(", adopt, StringComparison.Ordinal);
        }

        [Fact]
        public void NightVisionSwapsTheDefaultReflectionOnceThroughIt()
        {
            string toNight = Normalized(Method("SwitchToNightVision").Body!);
            Assert.StartsWith("{if(reflectingThroughDefault){if(nightVisionReflection!=null){RenderSettings.customReflectionTexture=nightVisionReflection;}return;}", toNight, StringComparison.Ordinal);

            string back = Normalized(Method("Reset").Body!);
            Assert.StartsWith("{if(reflectingThroughDefault){RenderSettings.customReflectionTexture=normalReflection;return;}", back, StringComparison.Ordinal);
        }

        private static MethodDeclarationSyntax Method(string name)
            => Parse().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);

        private static string Normalized(SyntaxNode node)
            => string.Concat(node.ToString().Where(c => !char.IsWhiteSpace(c)));

        private static SyntaxNode Parse()
        {
            string path = Path.Combine(RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Assembly-CSharp", "ReflectionProber.cs");
            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
