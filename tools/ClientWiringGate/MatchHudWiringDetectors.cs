using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ironfront.Tools.ClientWiringGate
{
    /// <summary>
    /// The authoring checks for the in-match readout on <c>Ingame UI Container.prefab</c>: one
    /// clause per element, over the references that decide whether it renders. P17 3.4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why these exist at all.</b> The nine <see cref="AssetWiringDetectors"/> checks that
    /// predate P15 passed this exact prefab with <c>capturePointMarkerPrefab</c> null (P3 § 3.3):
    /// an authoring gate sees the fields it was told about and is otherwise exactly as green as
    /// one that checks nothing. Six of P17's eight criteria are things on a screen, and the whole
    /// reason none of them was caught earlier is that <c>ClientWiringGate</c> reports
    /// <c>KnownUnwiredEvents</c> empty — 0x4B has had a subscriber throughout, and the gap was
    /// entirely in what that subscriber drew.
    /// </para>
    /// <para>
    /// <b>The grading is <see cref="MenuScreenWiringDetectors"/>'s, called rather than copied.</b>
    /// Its three clauses — assigned, resolves to an object that exists, not an object another
    /// field already drives — were earned by mutation against <c>ScoreUi</c>, whose first draft
    /// was proved green on two authorings it exists to forbid. A second copy of them here would
    /// be two checks free to drift about what they forbid. What is P17's is the TABLE below: the
    /// elements, and what each one's absence costs.
    /// </para>
    /// <para>
    /// <b>The killfeed is graded as an ARRAY, and that clause is the one a per-field check cannot
    /// express.</b> Four authored rows where the model holds five contains no null and no
    /// duplicate, so every other clause passes — and the oldest kill on screen silently is not
    /// the oldest kill. The expected length is read off
    /// <c>KillfeedModel.DefaultCapacity</c> through the component's own constant, so raising the
    /// model's capacity changes what this demands rather than leaving a hand-written 5 behind.
    /// </para>
    /// <para>
    /// <b>What this deliberately does not check: where an element sits.</b> A reference at a
    /// genuine, unclaimed <c>Text</c> somewhere else entirely resolves, is distinct, and renders
    /// the killfeed off-screen. That is the paragraph <c>ScoreUiTextRefsAreAssigned</c> already
    /// argues at length, and the answer is the same: descendant-of-the-canvas is a LAYOUT
    /// invariant, YAML can say a reference resolves and never that a player sees it, and P17's
    /// criteria 1, 3 and 6 are graded on screenshots for exactly that reason.
    /// </para>
    /// </remarks>
    public static class MatchHudWiringDetectors
    {
        /// <summary>The ledger row these findings are filed under.</summary>
        private const string Row = "P17";

        /// <summary>The Editor command that authors every element below.</summary>
        private const string BuildCommand = "Ironfront/Net/Build in-match readout";

        /// <summary>Both P17 clauses are in one section, unlike P15's and P16's.</summary>
        private const string Clause = "P17 3.4";

        /// <summary>
        /// Killfeed rows the HUD owes, read off <c>KillfeedModel.DefaultCapacity</c>.
        /// </summary>
        /// <remarks>
        /// Same discipline as <see cref="AssetWiringDetectors.ProjectileKindCount"/>: raising the
        /// model's capacity changes what this check demands, with no second copy to drift. A
        /// hand-written 5 would go on passing while the newest kill had no row to render in.
        /// </remarks>
        public static int KillfeedRowCount =>
            Ironfront.Net.Replication.Client.KillfeedModel.DefaultCapacity;

        /// <summary>
        /// The one component, and every reference that must be authored on it.
        /// </summary>
        /// <remarks>
        /// One entry rather than three, because the three elements share a component, a Canvas
        /// and one registration into <c>NetClientBindings.MatchHud</c> — see <c>MatchHud</c>'s own
        /// remark for why splitting them would be three ways for a build to be half-wired. "A
        /// detector per element" is satisfied by the per-field clauses, which is where the
        /// grading actually happens.
        /// </remarks>
        private static IReadOnlyList<MenuScreenWiringDetectors.Screen> Elements =>
            new[]
            {
                new MenuScreenWiringDetectors.Screen(
                    Row, BuildCommand, Clause, Clause, Clause,
                    "MatchHud", "Scripts/Net/Client/Hud/MatchHud.cs",
                    new[]
                    {
                        ("_killfeedRows", KillfeedRowCount,
                         "the feed renders fewer lines than KillfeedModel holds, so the oldest "
                         + "kill on screen is silently not the oldest kill — and criterion 6 is "
                         + "graded on a screenshot of exactly those lines"),
                    },
                    ("_teamReadoutText",
                     "the local team is resolved every frame and written nowhere, so a player "
                     + "has no way to see which side they are on — criteria 1 and 2, and the "
                     + "element that exists to make a WRONG team visible"),
                    ("_deployRoot",
                     "the deploy screen has no object to activate, so a death suppresses input "
                     + "and shows nothing: the F8 symptom this phase was filed against"),
                    ("_deployKillerText",
                     "the screen comes up without naming who killed you, which is the one piece "
                     + "of information 0x4B already carries and criterion 3 is graded on"),
                    ("_deployTimerText",
                     "the countdown renders nowhere, so a dead player cannot tell a respawn that "
                     + "is coming from one that is stuck"),
                    ("_deployButton",
                     "there is no Deploy control at all and the spacebar is the only way back "
                     + "into the match — criteria 3 and 4 become indistinguishable")),
            };

        /// <summary>
        /// <b>P17</b> — every element of the in-match readout is authored and assigned.
        /// </summary>
        public static IEnumerable<GateFinding> MatchHudRefsAreAssigned(UnityAssetIndex index)
            => MenuScreenWiringDetectors.GradeScreens(index, Elements);

        /// <summary>
        /// <b>P17</b> — the readout's colours come from <c>ITeamPalette</c>, not from the asset.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The sibling of P16's roster check, and it is here for the same reason.</b> Every
        /// clause above grades whether a reference RESOLVES. A team readout authored red in the
        /// prefab resolves perfectly, renders correctly on the day, and is a second copy of the
        /// mapping <c>ColorScheme.TeamColor</c> owns (contracts § 6.3) — so the palette seam
        /// becomes decoration, and the two copies drift the first time a side is re-themed with
        /// nothing failing. Criterion 1 is graded on the colour, which is precisely the value
        /// this forbids the asset from deciding.
        /// </para>
        /// <para>
        /// <b>Two clauses, because either alone is a green that proves nothing.</b> The source
        /// clause says the runtime colouring is still there; on its own it passes a HUD that
        /// calls the palette AND has red baked into the prefab underneath. The asset clause says
        /// the authored ink is uniform — what an unpainted readout looks like — and on its own it
        /// passes an all-grey HUD whose runtime colouring was deleted.
        /// </para>
        /// <para>
        /// <b>The open paren, and doc comments stripped first.</b> P16 learned this by mutation:
        /// deleting the CALL left the identical string standing in the screen's own
        /// <c>&lt;remarks&gt;</c>, so the check was satisfied by a sentence ABOUT the call. That
        /// applies verbatim here — <c>MatchHud</c>'s class remark names <c>ITeamPalette</c> twice.
        /// </para>
        /// </remarks>
        public static IEnumerable<GateFinding> MatchHudTeamColoursComeFromThePalette(
            UnityAssetIndex index)
        {
            const string Source = "Scripts/Net/Client/Hud/MatchHud.cs";
            const string PaletteCall = "NetClientBindings.TeamColourRgb(";

            var findings = new List<GateFinding>();

            string sourceFile = Path.Combine(
                index.AssetsRoot, Source.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(sourceFile))
                throw new AssetGateUnknownException(
                    $"no '{Source}' to read, so criterion 1's runtime half cannot be graded. "
                    + "Has the HUD moved?");

            if (!CodeOf(sourceFile).Contains(PaletteCall, StringComparison.Ordinal))
                findings.Add(new GateFinding(
                    Row, Source, 0,
                    $"MatchHud no longer calls {PaletteCall}, so the team readout and the "
                    + "killfeed render in whatever colour the prefab authored and the "
                    + $"ITeamPalette seam is decoration. Criterion 1 ({Clause})."));

            string guid = MenuScreenWiringDetectors.ScriptGuid(index, Source);
            int seen = 0;

            foreach ((UnityAssetDocument document, string path)
                     in AssetWiringDetectors.Instances(index, guid))
            {
                seen++;
                findings.AddRange(GradeAuthoredInk(index, document, path));
            }

            if (seen == 0)
                findings.Add(new GateFinding(
                    Row, "(nothing)", 0,
                    "MatchHud is on no GameObject in any scene or prefab, so there is no readout "
                    + $"to colour and criteria 1, 2, 3 and 6 have no screen. Run "
                    + $"'{BuildCommand}' ({Clause})."));

            return findings;
        }

        /// <summary>
        /// Reports a team colour baked into the asset under the readout's own elements.
        /// </summary>
        /// <remarks>
        /// <b>Identity, not a transcribed constant.</b> Comparing against the builder's ink value
        /// would be a fourth copy of a colour and would go stale the day the HUD is restyled.
        /// What is asserted is that the elements this component drives are authored in ONE ink,
        /// which stays true under any restyle and false under exactly the authoring this forbids.
        /// </remarks>
        private static IEnumerable<GateFinding> GradeAuthoredInk(
            UnityAssetIndex index, UnityAssetDocument hud, string path)
        {
            var findings = new List<GateFinding>();
            var inks = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string field in new[] { "_teamReadoutText", "_killfeedRows" })
            {
                foreach (UnityObjectRef reference in Referenced(hud, field))
                {
                    if (reference.IsNull) continue;

                    // A colour lives on the Text component in the SAME asset; a reference that
                    // leaves it is already reported by the assigned-and-resolves clauses.
                    if (reference.Guid != null) continue;

                    UnityAssetDocument? text = index.Documents(path)
                        .FirstOrDefault(d => d.AnchorId == reference.FileId);

                    string? ink = text?.Scalar("m_Color");
                    if (ink == null) continue;

                    if (!inks.ContainsKey(ink)) inks.Add(ink, $"{field} ({reference.FileId})");
                }
            }

            if (inks.Count > 1)
                findings.Add(new GateFinding(
                    Row, AssetWiringDetectors.Rel(index, path), 0,
                    "the readout carries " + inks.Count + " different authored colours ("
                    + string.Join("; ", inks.Select(pair => $"{pair.Value} = {pair.Key}"))
                    + "). A team colour authored in the prefab is a second copy of the mapping "
                    + "ColorScheme.TeamColor owns, and it is the copy that wins at load: "
                    + $"ITeamPalette then decorates a decision the asset has made. Criterion 1 "
                    + $"({Clause})."));

            return findings;
        }

        /// <summary>
        /// One field's references, whether it is a single reference or an array.
        /// </summary>
        /// <remarks>
        /// The single form is tried FIRST, and the order is not cosmetic — P16 found by mutation
        /// that <c>ReferenceArray</c> returns an EMPTY list for a single-reference field, so
        /// asking the array first silently skips it. <c>Reference</c> is safe on an array field
        /// by contrast: the value after the colon is empty rather than a brace.
        /// </remarks>
        private static IEnumerable<UnityObjectRef> Referenced(
            UnityAssetDocument document, string field)
        {
            UnityObjectRef? single = document.Reference(field);
            if (single != null) return new[] { single.Value };

            return document.ReferenceArray(field) ?? (IEnumerable<UnityObjectRef>)Array.Empty<UnityObjectRef>();
        }

        /// <summary>The file's code with doc comments stripped. See the palette check's remark.</summary>
        private static string CodeOf(string file)
        {
            var code = new System.Text.StringBuilder();

            foreach (string line in File.ReadLines(file))
                if (!line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                    code.Append(line).Append('\n');

            return code.ToString();
        }
    }
}
