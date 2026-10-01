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
    /// The minimap draws a hundred moving soldiers without rebuilding a hundred meshes a frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner report 2026-10-01: "the client stutters, especially on Forest Lake".</b> The v3.1.1
    /// player in a 100-bot Forest Lake match spent 9.3 ms of an average frame in
    /// <c>PlayerUpdateCanvases</c>, almost all of it in the minimap: every soldier icon and every dot
    /// of its trail set a new anchor, size and colour every frame, so Unity UI regenerated about 800
    /// meshes a frame; and every body icon was hidden by the <c>ActorBlip</c> it borrows from its
    /// prefab and shown again by its <c>MinimapMarker</c>, an OnDisable and an OnEnable per icon per
    /// frame. #381 had deleted the line that destroys the borrowed blip.
    /// </para>
    /// <para>
    /// A source scan: the minimap is <c>Assembly-CSharp</c>, which no test assembly compiles.
    /// </para>
    /// </remarks>
    public sealed class MinimapRebuildInvariantTests
    {
        [Fact]
        public void ABodyMarkerDestroysTheBlipItBorrowsFromItsPrefab()
        {
            string bind = Normalized(Method(Parse("MinimapMarker.cs"), "Bind").Body!);

            int found = bind.IndexOf("ActorBlipblip=GetComponent<ActorBlip>();", StringComparison.Ordinal);
            int destroyed = bind.IndexOf("Object.Destroy(blip);", StringComparison.Ordinal);
            Assert.True(found >= 0 && destroyed > found,
                "MinimapMarker.Bind must destroy the ActorBlip of the prefab it borrows; left alive it "
                + "hides the icon every frame and the marker shows it again.");
        }

        [Theory]
        [InlineData("MinimapMarker.cs", "LateUpdate")]
        [InlineData("ActorBlip.cs", "LateUpdate")]
        [InlineData("ActorBlip.cs", "DrawSelfDecoration")]
        [InlineData("MinimapTrail.cs", "Draw")]
        public void IconsAreMovedNotReAnchoredEveryFrame(string file, string method)
        {
            MethodDeclarationSyntax body = Method(Parse(file), method);

            string[] anchorWrites = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Select(a => Normalized(a.Left))
                .Where(left => left.EndsWith(".anchorMin", StringComparison.Ordinal)
                            || left.EndsWith(".anchorMax", StringComparison.Ordinal))
                .ToArray();
            Assert.True(anchorWrites.Length == 0,
                $"{file} {method} re-anchors an icon every frame ({string.Join(", ", anchorWrites)}); "
                + "a new anchor rebuilds the graphic's mesh. Use MinimapUi.Place.");
            Assert.Contains("MinimapUi.Place(", Normalized(body.Body!), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("MinimapMarker.cs")]
        [InlineData("ActorBlip.cs")]
        public void IconsStayPutWhileNobodySeesTheMap(string file)
        {
            string lateUpdate = Normalized(Method(Parse(file), "LateUpdate").Body!);

            int gate = lateUpdate.IndexOf("if(!MinimapUi.IsShowing){", StringComparison.Ordinal);
            int project = lateUpdate.IndexOf("WorldToViewportPoint(", StringComparison.Ordinal);
            int place = lateUpdate.IndexOf("MinimapUi.Place(", StringComparison.Ordinal);
            Assert.True(gate >= 0 && project > gate && place > gate,
                $"{file} LateUpdate must ask MinimapUi.IsShowing before it projects or moves anything: "
                + "most of a match the overlay is closed.");

            string hidden = lateUpdate.Substring(gate, lateUpdate.IndexOf("return;", gate, StringComparison.Ordinal) - gate);
            Assert.Contains("trail.Record(", hidden, StringComparison.Ordinal);
            Assert.DoesNotContain("trail.Draw(", hidden, StringComparison.Ordinal);
        }

        [Fact]
        public void TheMapShowsOnAnOpenDeployScreenOrAnOverlayThatIsOut()
        {
            PropertyDeclarationSyntax showing = Parse("MinimapUi.cs").DescendantNodes().OfType<PropertyDeclarationSyntax>()
                .Single(p => p.Identifier.ValueText == "IsShowing");
            string body = Normalized(showing);

            Assert.Contains("!instance.minimap.gameObject.activeInHierarchy", body, StringComparison.Ordinal);
            Assert.Contains("instance.minimap.rectTransform.parent==instance.loadoutParent||instance.minimapOpenness>0f", body, StringComparison.Ordinal);
        }

        [Fact]
        public void PlaceKeepsTheAnchorsAndMovesFromTheParentsCurrentSize()
        {
            string place = Normalized(Method(Parse("MinimapUi.cs"), "Place").Body!);

            Assert.Contains("icon.anchorMin=Vector2.zero;", place, StringComparison.Ordinal);
            Assert.Contains("icon.anchorMax=Vector2.zero;", place, StringComparison.Ordinal);
            Assert.Contains("icon.parentisRectTransformparent?parent.rect.size", place, StringComparison.Ordinal);
            Assert.Contains("icon.anchoredPosition=newVector2(point.x*size.x,point.y*size.y);", place, StringComparison.Ordinal);
        }

        [Fact]
        public void TrailDotsFadeAndShrinkWithoutRebuildingTheirMesh()
        {
            string draw = Normalized(Method(Parse("MinimapTrail.cs"), "Draw").Body!);

            Assert.Contains("dot.canvasRenderer.SetAlpha(0.9f*fade);", draw, StringComparison.Ordinal);
            Assert.Contains("rect.localScale=newVector3(scale,scale,1f);", draw, StringComparison.Ordinal);
            Assert.Contains("MinimapUi.SetSquareSize(rect,dotPixels);", draw, StringComparison.Ordinal);
            Assert.Contains("if(dot.color!=color){dot.color=color;}", draw, StringComparison.Ordinal);
            Assert.DoesNotContain("fade;dot.color", draw, StringComparison.Ordinal);
        }

        private static MethodDeclarationSyntax Method(SyntaxNode root, string name)
            => root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);

        private static string Normalized(SyntaxNode node)
            => string.Concat(node.ToString().Where(c => !char.IsWhiteSpace(c)));

        private static SyntaxNode Parse(string file)
        {
            string path = Path.Combine(RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Assembly-CSharp", file);
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
