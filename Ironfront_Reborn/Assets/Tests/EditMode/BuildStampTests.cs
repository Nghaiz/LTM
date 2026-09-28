using NUnit.Framework;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// The build stamp, and the one way it can quietly stop meaning anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What these guard is the CHECKOUT, not the build.</b> A stamped binary cannot be tested
    /// from a checkout — the values only exist for the length of one
    /// <c>tools/build-player.ps1</c> run, and the Editor never sees them. What CAN go wrong here,
    /// and would go wrong silently, is the opposite: a stamped value getting committed because a
    /// build was interrupted between the rewrite and the restore. Every subsequent build would
    /// then ship that stale commit as its identity, and the whole mechanism would be worse than
    /// having none — it would be a confident wrong answer instead of an absent one.
    /// </para>
    /// <para>
    /// <b>These are input-integrity guards, not pinned baselines.</b> They assert a healthy
    /// invariant of the source tree and fire on a bad commit, never on a fix, so they owe no
    /// companion under <c>pinned-baseline-test-companion.md</c>. If one goes red, the tree is
    /// wrong: restore the two files rather than adjusting the assertion.
    /// </para>
    /// </remarks>
    public sealed class BuildStampTests
    {
        [Test]
        public void SharedStamp_IsDevelopment_InACheckout()
        {
            Assert.That(BuildStamp.Commit, Is.EqualTo(BuildStamp.DevelopmentCommit),
                "BuildStamp.cs carries a real commit SHA. A build was interrupted between the "
                + "stamp rewrite and the restore, and the value got committed — restore the file. "
                + "Left alone, every future build reports this stale commit as its own identity.");
            Assert.That(BuildStamp.IsDevelopmentBuild, Is.True);
            Assert.That(BuildStamp.Dirty, Is.False);
        }

        [Test]
        public void ServerStamp_IsDevelopment_InACheckout()
        {
            Assert.That(ServerBuildStamp.Commit, Is.EqualTo(BuildStamp.DevelopmentCommit),
                "ServerBuildStamp.cs carries a real commit SHA — same cause and same repair as "
                + "the Shared one above.");
            Assert.That(ServerBuildStamp.IsDevelopmentBuild, Is.True);
            Assert.That(ServerBuildStamp.Dirty, Is.False);
        }

        /// <summary>
        /// Two development builds are not a mismatch.
        /// </summary>
        /// <remarks>
        /// A checkout has every assembly at <c>dev</c> by construction. If this ever went red on a
        /// clean tree the mismatch error would fire on every Editor Play session, and a warning
        /// that fires always is a warning nobody reads by the second day.
        /// </remarks>
        [Test]
        public void DevelopmentAssemblies_DoNotReportAMismatch()
        {
            Assert.That(ServerBuildStamp.AssembliesDisagree, Is.False);
        }

        /// <summary>
        /// The description says <i>why</i> there is no identity, not just that there is none.
        /// </summary>
        /// <remarks>
        /// The reader of this line is somebody trying to find out which build a machine is
        /// running. "dev" alone invites the reading "some development version"; naming the script
        /// that would have stamped it turns the line into an instruction.
        /// </remarks>
        [Test]
        public void Describe_NamesTheScriptThatWouldHaveStampedIt()
        {
            Assert.That(BuildStamp.Describe(), Does.Contain("build-player.ps1"));
            Assert.That(ServerBuildStamp.Describe(), Does.Contain("build-player.ps1"));
        }
    }
}
