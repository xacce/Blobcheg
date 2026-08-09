using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;

namespace Blobcheg.Tests
{
    /// <summary>
    /// A job with a blobcheg field is obliged to schedule without a single attribute on the consumer's
    /// side. The package carries the pointers, the package answers for them:
    /// <c>[NativeDisableUnsafePtrRestriction]</c> stands on the fields of the base and the router
    /// themselves.
    ///
    /// This has already cost once: without the attribute on the router the safety system kills the
    /// schedule naming ITS OWN field (<c>_masks</c>), the system fails every tick, and from the outside
    /// that looks like "the tank is stuck at zero". The compiler lets it through — only this test holds
    /// it.
    ///
    /// The pointers here are null on purpose: the raw-pointer check runs on the job type at schedule
    /// time, before the first read, so there is no reason to load real files.
    /// </summary>
    public sealed class BlobchegJobSafetyTests
    {
        struct RouterFieldJob : IJobParallelFor
        {
            public TestGameRouter Router;

            public NativeArray<int> Touched;

            public void Execute(int index) => Touched[index] = index;
        }

        struct RouterBlobFieldJob : IJobParallelFor
        {
            public BlobchegRouterBlob Router;

            public NativeArray<int> Touched;

            public void Execute(int index) => Touched[index] = index;
        }

        struct DatabaseFieldJob : IJobParallelFor
        {
            public TestColdDb Cold;

            public NativeArray<int> Touched;

            public void Execute(int index) => Touched[index] = index;
        }

        [Test]
        public void A_job_with_a_typed_router_schedules_without_a_consumer_attribute()
            => Schedules(touched => new RouterFieldJob { Router = default, Touched = touched });

        [Test]
        public void A_job_with_a_bare_router_schedules_without_a_consumer_attribute()
            => Schedules(touched => new RouterBlobFieldJob { Router = default, Touched = touched });

        [Test]
        public void A_job_with_a_base_schedules_without_a_consumer_attribute()
            => Schedules(touched => new DatabaseFieldJob { Cold = default, Touched = touched });

        static void Schedules<T>(System.Func<NativeArray<int>, T> make) where T : struct, IJobParallelFor
        {
            var touched = new NativeArray<int>(4, Allocator.TempJob);

            try
            {
                Assert.DoesNotThrow(() => make(touched).Schedule(touched.Length, 1).Complete(),
                    "a blobcheg field in a job is an ordinary thing; the package pays the attribute for it");

                Assert.That(touched[3], Is.EqualTo(3), "the job was obliged to run, not to quietly fail to start");
            }
            finally
            {
                touched.Dispose();
            }
        }
    }
}
