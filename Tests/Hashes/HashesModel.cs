using System;
using Blobcheg.Authoring;

namespace Blobcheg.HashTests
{
    /// <summary>
    /// The model of the table tests: two domains, a router and a table over it. The router here is its
    /// own and not the one in Blobcheg.Tests, because the generator is obliged to see a router, its
    /// bases and its table in one compilation — and the hash tests live in a separate assembly.
    /// </summary>
    public interface ITestHashHot
    {
    }

    public interface ITestHashCold
    {
    }

    public struct TestHashHotRecord : ITestHashHot
    {
        /// <summary>Its own hash, put by the node straight into the record: it is known before the write.</summary>
        public ulong Self;

        /// <summary>The hash of a neighbouring node: that is how one record references another without knowing its addresses.</summary>
        public ulong Twin;

        public int Rpm;
    }

    public struct TestHashColdRecord : ITestHashCold
    {
        public int Tier;
    }

    // The bits are numbered by the domains in ordinal FullName order: cold is zero, hot is one.
    [Blobcheg(typeof(ITestHashCold), "cold")]
    public partial struct TestHashColdDb
    {
    }

    [Blobcheg(typeof(ITestHashHot), "hot")]
    public partial struct TestHashHotDb
    {
    }

    [BlobchegRouter]
    public partial struct TestHashRouter
    {
    }

    /// <summary>The hash table over the router. The struct has its own name, the file name comes from the router.</summary>
    [BlobchegHashes(typeof(TestHashRouter))]
    public partial struct TestHashTable
    {
    }

    /// <summary>A node in both bases: a router row with two bits.</summary>
    public sealed class TestHashNodeSo : BlobchegNodeSo
    {
        public int rpm = 100;
        public BlobchegNodeSo twin;

        public override Type[] OutTypes => new[] { typeof(ITestHashHot), typeof(ITestHashCold) };

        public override void Write(ref BlobchegNodeWriter writer)
        {
            writer.Add(new TestHashHotRecord
            {
                Self = this.HashIn<TestHashRouter>(),
                Twin = twin == null ? 0 : twin.HashIn<TestHashRouter>(),
                Rpm = rpm,
            });

            writer.Add(new TestHashColdRecord { Tier = rpm });
        }
    }

    /// <summary>A node only in the cold base: it is not in the hot lane at all.</summary>
    public sealed class TestHashColdOnlyNodeSo : BlobchegNodeSo
    {
        public int tier = 7;

        public override Type[] OutTypes => new[] { typeof(ITestHashCold) };

        public override void Write(ref BlobchegNodeWriter writer)
            => writer.Add(new TestHashColdRecord { Tier = tier });
    }
}
