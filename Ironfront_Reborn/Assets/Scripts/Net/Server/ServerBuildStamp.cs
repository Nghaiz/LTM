using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Which commit the SERVER netcode assembly was built from, and whether it agrees with the
    /// Shared one sitting next to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This assembly gets its own stamp because it is the one that keeps being copied.</b> The
    /// spawn placement fixed in X-89 and X-90 lives in
    /// <c>Ironfront_Data/Managed/Ironfront.Net.Unity.Server.dll</c> — not in
    /// <c>Assembly-CSharp.dll</c>, which is where somebody looking for "the game code" reaches
    /// first. A host that took one DLL rather than the whole build folder is the failure mode this
    /// pair of stamps exists to make visible, and a single stamp anywhere cannot see it: one
    /// assembly can only ever report on itself.
    /// </para>
    /// <para>
    /// <b>Two stamps, one comparison.</b> Because
    /// <see cref="Ironfront.Net.Unity.BuildStamp.Commit"/> is <c>static readonly</c> rather than
    /// <c>const</c> (that file's remark explains why the distinction is load-bearing), reading it
    /// from here goes to the Shared DLL on disk at run time. So the two values disagree exactly
    /// when the two DLLs came from different builds, which is the thing worth shouting about.
    /// </para>
    /// </remarks>
    public static class ServerBuildStamp
    {
        /// <summary>Short commit SHA, or <see cref="BuildStamp.DevelopmentCommit"/>.</summary>
        public static readonly string Commit = "dev";

        /// <summary>UTC build time in ISO-8601, or <see cref="BuildStamp.DevelopmentCommit"/>.</summary>
        public static readonly string BuiltAtUtc = "dev";

        /// <summary>Whether the tree had uncommitted changes when this assembly was built.</summary>
        public static readonly bool Dirty = false;

        /// <summary>True when this assembly carries no build identity at all.</summary>
        public static bool IsDevelopmentBuild => Commit == BuildStamp.DevelopmentCommit;

        /// <summary>One line, safe to print anywhere.</summary>
        public static string Describe()
            => IsDevelopmentBuild
                ? "dev (built from the Editor, not by tools/build-player.ps1)"
                : $"{Commit}{(Dirty ? "-dirty" : string.Empty)} built {BuiltAtUtc}";

        /// <summary>
        /// True when the Server and Shared assemblies were built from different commits.
        /// </summary>
        /// <remarks>
        /// Two development builds agree trivially and are not a mismatch — a checkout has every
        /// assembly at <c>dev</c> by construction, and reporting that as a defect would make the
        /// warning worthless in the only environment developers see every day.
        /// </remarks>
        public static bool AssembliesDisagree
            => !(IsDevelopmentBuild && BuildStamp.IsDevelopmentBuild)
               && Commit != BuildStamp.Commit;

        /// <summary>
        /// Logs the identity of this build, and shouts if the assemblies disagree.
        /// </summary>
        /// <remarks>
        /// An error rather than a warning for the mismatch: a mixed build folder means the running
        /// code is not any commit that was ever tested, so every measurement taken against it is
        /// void — including the ones somebody is about to take to decide whether a fix worked.
        /// </remarks>
        public static void LogAtStartup()
        {
            Debug.Log($"[net] build {Describe()} (server assembly); "
                      + $"shared assembly {BuildStamp.Describe()}");

            if (AssembliesDisagree)
            {
                Debug.LogError(
                    "[net] MIXED BUILD FOLDER: Ironfront.Net.Unity.Server.dll was built from "
                    + $"{Commit} but Ironfront.Net.Unity.Shared.dll beside it was built from "
                    + $"{BuildStamp.Commit}. This process is running a combination that was never "
                    + "built or tested as a whole, so nothing measured on it is trustworthy. "
                    + "Replace the ENTIRE build folder rather than individual DLLs — see "
                    + "docs/handing-over-a-build.md.");
            }
        }
    }
}
