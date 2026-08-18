using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Ironfront.Net.Replication.Client;
using Microsoft.CodeAnalysis;

namespace Ironfront.Tools.ClientWiringGate
{
    /// <summary>
    /// Runs the four checks over a file set and reports what it found.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="Program"/> so the empty-file-set failure - the one that decides
    /// whether every other green in this tool means anything - can be exercised by a test rather
    /// than believed.
    /// </remarks>
    public static class GateRunner
    {
        /// <summary>
        /// The number of events <c>ClientMessageRouter</c> is expected to raise.
        /// </summary>
        /// <remarks>
        /// A mismatch is a hard failure rather than an automatic re-baseline. Reflection finding
        /// eight events where there were nine means an event was deleted or renamed, and the
        /// question "did its subscriber go with it" needs a human. Bump this in the same commit
        /// that changes the router, having answered that question.
        /// </remarks>
        public const int ExpectedRouterEventCount = 9;

        /// <summary>
        /// Events knowingly unwired, each with the reason and the work that unblocks it. An entry
        /// here downgrades G1 from a failure to a reported warning FOR THAT EVENT ONLY.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the one place the gate is allowed to be lenient, and it is deliberately
        /// hostile to being left alone.</b> An event listed here that turns out to BE subscribed
        /// fails the run (see <see cref="Run"/>) — so the exemption cannot outlive the gap it
        /// describes without somebody noticing. That is the difference between a documented
        /// blocker and a suppression: a suppression only ever gets quieter.
        /// </para>
        /// <para>
        /// The alternative was to ship task 8's handler anyway. It is refused on correctness, not
        /// convenience: V8 D3 makes <c>ApplyAuthoritativeOwner</c> the single write path for
        /// capture-point ownership and it does not exist yet, and until V8 D2 lands
        /// <c>CapturePoint.UpdateOwner</c> is still running its own 1 Hz arithmetic on the client.
        /// Writing replicated ownership beside it would make two client-side writers — the exact
        /// bug the objectives work exists to remove, one process over.
        /// </para>
        /// </remarks>
        private static readonly (string EventName, string Reason)[] KnownUnwiredEvents =
        {
            ("OnCapturePoint",
                "phase-V10 task 8, hard-blocked on V8 task 1 (D15): ApplyAuthoritativeOwner is "
                + "the single write path for capture-point ownership and does not exist yet."),
        };

        /// <summary>
        /// Every event <c>ClientMessageRouter</c> raises, read off the type itself.
        /// </summary>
        /// <remarks>
        /// Reflection rather than a hand-maintained list, per D21: a renamed event changes the
        /// gate's input automatically, so there is no second copy to drift. The type is
        /// engine-free, which is the whole reason this is possible without a Unity assembly.
        /// </remarks>
        public static IReadOnlyList<string> RouterEventNames() =>
            typeof(ClientMessageRouter)
                .GetEvents(BindingFlags.Public | BindingFlags.Instance)
                .Select(e => e.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// Runs G1-G4. Returns 0 when clean, 1 when the gate found something, and 2 when the gate
        /// could not tell - which is a different kind of failure and must never read as a pass.
        /// </summary>
        /// <summary>The reason this event is a known gap, or null when it is not one.</summary>
        private static string? ExemptionReasonFor(string eventName)
        {
            foreach ((string name, string reason) in KnownUnwiredEvents)
                if (string.Equals(name, eventName, StringComparison.Ordinal)) return reason;

            return null;
        }

        public static int Run(
            IReadOnlyList<string> routerEventNames,
            IReadOnlyList<string> files,
            TextWriter output,
            TextWriter error)
        {
            if (files.Count == 0)
            {
                // Silence here would be a pass, and a check that passes because it looked at
                // nothing is worse than no check: it reports green forever from the wrong working
                // directory. Taken from tools/UnitySyntaxCheck, which learned it first.
                error.WriteLine(
                    "[client-wiring] FAIL - no files to scan. Run from the repository root, or "
                    + "pass paths explicitly. A gate that scanned nothing has proved nothing.");
                return 2;
            }

            if (routerEventNames.Count != ExpectedRouterEventCount)
            {
                error.WriteLine(
                    $"[client-wiring] FAIL - expected {ExpectedRouterEventCount} events on "
                    + $"ClientMessageRouter, found {routerEventNames.Count} "
                    + $"({string.Join(", ", routerEventNames)}). An event was added, renamed or "
                    + "deleted. Decide whether its subscriber went with it, then update "
                    + $"{nameof(GateRunner)}.{nameof(ExpectedRouterEventCount)} in the same commit.");
                return 2;
            }

            var subscribed = new Dictionary<string, string>(StringComparer.Ordinal);
            var findings = new List<GateFinding>();
            int scanned = 0;

            foreach (string path in files)
            {
                if (!File.Exists(path))
                {
                    error.WriteLine($"[client-wiring] FAIL - missing: {path}");
                    return 2;
                }

                SyntaxTree tree = ClientWiringDetectors.Parse(File.ReadAllText(path), path);
                scanned++;

                foreach (string name in ClientWiringDetectors.FindSubscribedEventNames(tree, path))
                    if (!subscribed.ContainsKey(name)) subscribed.Add(name, path);

                findings.AddRange(ClientWiringDetectors.FindClientDamagePathReferences(tree, path));
                findings.AddRange(ClientWiringDetectors.FindEmptyCatchClauses(tree, path));
                findings.AddRange(ClientWiringDetectors.FindUnguardedLocalSingletonTouches(tree, path));
                findings.AddRange(ClientWiringDetectors.FindDeltaScoreReferences(tree, path));
            }

            var dead = routerEventNames.Where(name => !subscribed.ContainsKey(name)).ToList();

            // A stale exemption is a false green with a comment attached, so it is a HARD failure:
            // if an event listed as knowingly-unwired is now subscribed, the list is out of date
            // and the entry has to go before this run can pass.
            foreach ((string exemptName, string _) in KnownUnwiredEvents)
            {
                if (!subscribed.ContainsKey(exemptName)) continue;

                findings.Add(new GateFinding(
                    "G1", "GateRunner.cs", 0,
                    $"ClientMessageRouter.{exemptName} IS subscribed but is still listed in "
                    + "KnownUnwiredEvents. Delete that entry — an exemption that outlives the gap "
                    + "it describes is how a gate stops discriminating."));
            }

            foreach (string name in dead)
            {
                string? reason = ExemptionReasonFor(name);

                if (reason != null)
                {
                    // Reported every run, never silent. The point of naming it is that a reader of
                    // the CI log sees the gap, not that the gap stops being visible.
                    output.WriteLine(
                        $"[client-wiring] KNOWN GAP - ClientMessageRouter.{name} has no "
                        + $"production subscriber. {reason}");
                    continue;
                }

                findings.Add(new GateFinding(
                    "G1", "(nothing)", 0,
                    $"ClientMessageRouter.{name} has no production subscriber. The server frames "
                    + "it, the client decodes it, the router raises it, and the delegate is null. "
                    + "Subscribe from a presenter under Assets/Scripts, or delete the event."));
            }

            if (findings.Count > 0)
            {
                error.WriteLine(
                    $"[client-wiring] FAIL - {findings.Count} finding(s) across {scanned} file(s):");
                foreach (GateFinding finding in findings.OrderBy(f => f.RuleId, StringComparer.Ordinal))
                    error.WriteLine("  " + finding);
                error.WriteLine();
                error.WriteLine(
                    $"  {routerEventNames.Count - dead.Count} of {routerEventNames.Count} router "
                    + "events have a production subscriber.");
                return 1;
            }

            output.WriteLine(
                $"[client-wiring] {routerEventNames.Count - dead.Count} of "
                + $"{routerEventNames.Count} ClientMessageRouter events have a production "
                + $"subscriber and the rest are named gaps above; G2-G5 clean across {scanned} "
                + "file(s). No types were resolved - this says something subscribes, not that it "
                + "renders correctly.");
            return 0;
        }
    }
}
