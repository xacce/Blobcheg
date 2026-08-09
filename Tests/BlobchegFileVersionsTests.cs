using NUnit.Framework;

namespace Blobcheg.Tests
{
    /// <summary>
    /// The counter of file rebuilds — what whoever loaded a base learns from that their file was
    /// rewritten. The set is small because there are exactly three promises: an unknown file gives
    /// zero; the asker gets a "yes" once; a foreign file does not answer someone else's question.
    /// </summary>
    public sealed class BlobchegFileVersionsTests
    {
        [Test]
        public void A_file_nobody_rewrote_stays_zero()
        {
            var seen = 0;

            Assert.That(BlobchegFileVersions.Of("untouched.bcheg"), Is.EqualTo(0));
            Assert.That(BlobchegFileVersions.Changed("untouched.bcheg", ref seen), Is.False);
        }

        [Test]
        public void The_asker_gets_a_yes_exactly_once()
        {
            var file = "once-" + TestContext.CurrentContext.Test.ID + ".bcheg";
            var seen = BlobchegFileVersions.Of(file);

            BlobchegFileVersions.Bump(file);

            Assert.That(BlobchegFileVersions.Changed(file, ref seen), Is.True, "the file was rewritten");
            Assert.That(BlobchegFileVersions.Changed(file, ref seen), Is.False,
                "not the second time in a row: otherwise the base would be re-read every frame");
        }

        [Test]
        public void A_rebuild_of_a_neighbouring_file_does_not_wake_a_foreign_base()
        {
            var mine = "mine-" + TestContext.CurrentContext.Test.ID + ".bcheg";
            var other = "other-" + TestContext.CurrentContext.Test.ID + ".bcheg";
            var seen = BlobchegFileVersions.Of(mine);

            BlobchegFileVersions.Bump(other);

            Assert.That(BlobchegFileVersions.Changed(mine, ref seen), Is.False,
                "re-reading a base because a neighbouring one was rewritten is a pointless migration of slots");
        }
    }
}
