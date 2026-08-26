using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ironfront.Tools.ClientWiringGate
{
    /// <summary>
    /// Runs the authoring checks over the Unity asset tree and reports what they found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate runner, same process and same repo-root resolver (P-D6: one gate, not two). The
    /// two halves ask different questions of different inputs — Roslyn over
    /// <c>Assets/Scripts/**/*.cs</c> for "does anything subscribe", YAML over
    /// <c>Assets/**/*.{unity,prefab}</c> for "is the subscriber on a GameObject" — and a run is
    /// only meaningful when both have answered.
    /// </para>
    /// <para>
    /// <b>The exit-code contract is the source half's, unchanged.</b> 0 clean, 1 the gate found
    /// something, 2 the gate could not tell. Every "could not tell" here arrives as an
    /// <see cref="AssetGateUnknownException"/> and lands on 2 — a prefab that cannot be located
    /// or parsed must never read as a pass, which is the one way this whole file could end up
    /// proving nothing.
    /// </para>
    /// </remarks>
    public static class AssetGateRunner
    {
        /// <summary>
        /// Every authoring check, by name so a failure report can say which one spoke.
        /// </summary>
        /// <remarks>
        /// A list rather than a chain of calls so <c>--list-asset-checks</c> can print it and the
        /// fixture tests can assert the registered set. A check that exists as a method but is
        /// not in this list is a file that runs on nobody's machine.
        /// </remarks>
        public static IReadOnlyList<(string Name, Func<UnityAssetIndex, IEnumerable<GateFinding>> Run)> Checks { get; } =
            new (string, Func<UnityAssetIndex, IEnumerable<GateFinding>>)[]
            {
                (nameof(AssetWiringDetectors.PresentersAreOnTheClientObject),
                 AssetWiringDetectors.PresentersAreOnTheClientObject),
                (nameof(AssetWiringDetectors.PrefabsByKindIsComplete),
                 AssetWiringDetectors.PrefabsByKindIsComplete),
                (nameof(AssetWiringDetectors.ProjectileCatalogInstallerIsWired),
                 AssetWiringDetectors.ProjectileCatalogInstallerIsWired),
                (nameof(AssetWiringDetectors.ExplosionEffectsAreAuthored),
                 AssetWiringDetectors.ExplosionEffectsAreAuthored),
                (nameof(AssetWiringDetectors.RemoteActorPrefabIsAuthored),
                 AssetWiringDetectors.RemoteActorPrefabIsAuthored),
                (nameof(AssetWiringDetectors.TracerPrefabIsCosmeticOnly),
                 AssetWiringDetectors.TracerPrefabIsCosmeticOnly),
                (nameof(AssetWiringDetectors.LobbyShellOverlayIsInAScene),
                 AssetWiringDetectors.LobbyShellOverlayIsInAScene),
                (nameof(AssetWiringDetectors.ScoreUiTextRefsAreAssigned),
                 AssetWiringDetectors.ScoreUiTextRefsAreAssigned),
                (nameof(AssetWiringDetectors.ThrowReleaseDelayMatchesTheThrowClip),
                 AssetWiringDetectors.ThrowReleaseDelayMatchesTheThrowClip),
            };

        /// <summary>
        /// Grades the asset tree under <paramref name="assetsRoot"/>.
        /// </summary>
        /// <remarks>
        /// Every check runs even after one has produced findings. Authoring gaps arrive in
        /// clusters — one missing scene pass is six of them — and a gate that stopped at the
        /// first would turn a single Editor session into six round trips.
        /// </remarks>
        public static int Run(string assetsRoot, TextWriter output, TextWriter error)
        {
            UnityAssetIndex index;

            try
            {
                index = UnityAssetIndex.Build(assetsRoot);
            }
            catch (AssetGateUnknownException exception)
            {
                error.WriteLine($"[asset-wiring] FAIL - {exception.Message}");
                return 2;
            }

            // Reported every run, never silent. The point of naming a gap is that a reader of the
            // CI log sees it, not that it stops being visible.
            foreach ((string owner, string field, string reason) in AssetWiringDetectors.KnownUnauthoredFields)
                output.WriteLine($"[asset-wiring] KNOWN GAP - {owner}.{field} is unauthored. {reason}");

            var findings = new List<GateFinding>();

            foreach ((string name, Func<UnityAssetIndex, IEnumerable<GateFinding>> run) in Checks)
            {
                try
                {
                    findings.AddRange(run(index));
                }
                catch (AssetGateUnknownException exception)
                {
                    error.WriteLine(
                        $"[asset-wiring] FAIL - {name} could not reach a verdict: "
                        + exception.Message);
                    return 2;
                }
            }

            if (findings.Count > 0)
            {
                error.WriteLine(
                    $"[asset-wiring] FAIL - {findings.Count} finding(s) across "
                    + $"{Checks.Count} check(s):");

                foreach (GateFinding finding in findings
                             .OrderBy(f => f.RuleId, StringComparer.Ordinal)
                             .ThenBy(f => f.FilePath, StringComparer.Ordinal))
                {
                    error.WriteLine("  " + finding);
                }

                error.WriteLine();
                error.WriteLine(
                    "  These are authoring gaps, not code faults. Fix them in the Editor and "
                    + "save; hand-editing the YAML risks a fileID this gate cannot tell from a "
                    + "correct one.");
                return 1;
            }

            output.WriteLine(
                $"[asset-wiring] {Checks.Count} authoring check(s) clean across "
                + $"{index.Scenes().Count} scene(s) and {index.Prefabs().Count} prefab(s). No "
                + "types were resolved - this says the components are present and their "
                + "references are non-null, not that they render correctly.");
            return 0;
        }
    }
}
