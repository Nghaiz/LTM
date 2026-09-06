namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Which commit the SHARED netcode assembly was built from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The values below are rewritten by <c>tools/build-player.ps1</c> immediately before it
    /// invokes Unity, and restored immediately after.</b> A checkout therefore always carries
    /// <see cref="DevelopmentCommit"/>, the Editor always compiles, and only a build produced by
    /// that script claims an identity — which is the honest arrangement, because only that build
    /// is a thing anybody hands to anybody else.
    /// </para>
    /// <para>
    /// <b>Compiled INTO the assembly rather than written beside the executable.</b> A
    /// <c>build-stamp.txt</c> in the output folder would report the identity of the FOLDER, and
    /// the failure this exists to catch is somebody copying one stale DLL into an otherwise
    /// current folder — where a folder-level stamp reads new while the code is old. It would lie
    /// in precisely the case it was written for. A field in the assembly cannot be separated from
    /// the code it describes.
    /// </para>
    /// <para>
    /// <b><c>static readonly</c> and NOT <c>const</c>, and the detector dies without it.</b> The C#
    /// compiler inlines a <c>const</c> at every use site, INCLUDING use sites in other assemblies:
    /// <c>Ironfront.Net.Unity.Server</c> reading a <c>const</c> here would bake this value into its
    /// own DLL at its own compile time. A stale Server DLL dropped into a current folder would
    /// then compare its baked copy of this string against its own stamp — both stale, both equal,
    /// mismatch undetectable. A <c>static readonly</c> field is read from the defining assembly at
    /// RUN time, so <see cref="Ironfront.Net.Unity.Server.ServerBuildStamp"/> sees whatever the
    /// Shared DLL on disk actually says. The whole cross-assembly check in
    /// <c>ServerBuildStamp.WarnIfAssembliesDisagree</c> rests on this one keyword.
    /// </para>
    /// <para>
    /// <b>Why this is not <c>ConnectRequestPayload.ProtocolVersion</c>.</b> That byte answers "can
    /// these two speak to each other", and two builds with identical wire formats but different
    /// gameplay code share it — which is exactly the X-90 case, where the spawn arithmetic changed
    /// and nothing on the wire did. A field carried over the wire would also be useless on its
    /// first deployment for a second reason: it is only informative once BOTH ends send it, and
    /// the machine under suspicion is by definition the one running the older build that does not.
    /// </para>
    /// <para>
    /// <b>What it cannot tell you.</b> Nothing about a build cut before this shipped. Those
    /// binaries report <see cref="DevelopmentCommit"/> and are indistinguishable from a developer's
    /// own build; for that first exchange the procedure in <c>docs/handing-over-a-build.md</c> is
    /// the only instrument.
    /// </para>
    /// </remarks>
    public static class BuildStamp
    {
        /// <summary>
        /// What <see cref="Commit"/> reads in any tree that was not built by
        /// <c>tools/build-player.ps1</c>.
        /// </summary>
        public const string DevelopmentCommit = "dev";

        /// <summary>Short commit SHA, or <see cref="DevelopmentCommit"/>.</summary>
        public static readonly string Commit = "dev";

        /// <summary>UTC build time in ISO-8601, or <see cref="DevelopmentCommit"/>.</summary>
        public static readonly string BuiltAtUtc = "dev";

        /// <summary>
        /// Whether the tree had uncommitted changes when the build ran. A dirty build's
        /// <see cref="Commit"/> names a commit the binary does NOT match, so this flag is the only
        /// thing that makes the SHA safe to read.
        /// </summary>
        public static readonly bool Dirty = false;

        /// <summary>True when this assembly carries no build identity at all.</summary>
        public static bool IsDevelopmentBuild => Commit == DevelopmentCommit;

        /// <summary>One line, safe to print anywhere.</summary>
        public static string Describe()
            => IsDevelopmentBuild
                ? "dev (built from the Editor, not by tools/build-player.ps1)"
                : $"{Commit}{(Dirty ? "-dirty" : string.Empty)} built {BuiltAtUtc}";
    }
}
