using System;
using Blobcheg.Authoring;
using NUnit.Framework;

namespace Blobcheg.Tests
{
    struct PlainRecord
    {
        public float Damage;
        public int Cost;
    }

    struct RecordWithArray
    {
        public int Levels;
        public BlobchegArray<float> Values;
    }

    struct DeepInner
    {
        public BlobchegArray<int> Cells;
    }

    struct RecordWithDeepArray
    {
        public float Header;
        public DeepInner Inner;
    }

    struct ElementWithPointer
    {
        public IntPtr Address;
    }

    struct RecordWithPointerInsideElement
    {
        public BlobchegArray<ElementWithPointer> Items;
    }

    /// <summary>
    /// Two verdicts of the type check: "carries a pointer" and "requires a builder". The second is not
    /// self-evident: a <see cref="BlobchegArray{T}"/> field is two ints, the element does not occur
    /// among the fields, and the walk is obliged to enter the type argument separately.
    /// </summary>
    public sealed class BlobchegRecordTypesTests
    {
        [Test]
        public void A_type_without_an_array_requires_no_builder()
        {
            Assert.That(BlobchegRecordTypes.RequiresBuilder(typeof(PlainRecord)), Is.False);
        }

        [Test]
        public void A_type_with_an_array_requires_a_builder()
        {
            Assert.That(BlobchegRecordTypes.RequiresBuilder(typeof(RecordWithArray)), Is.True);
        }

        [Test]
        public void An_array_at_depth_requires_a_builder_too()
        {
            Assert.That(BlobchegRecordTypes.RequiresBuilder(typeof(RecordWithDeepArray)), Is.True);
        }

        [Test]
        public void The_array_itself_does_not_count_as_a_pointer()
        {
            // Two ints — there is no pointer; a type with an array of floats is obliged to pass the check.
            Assert.DoesNotThrow(() => BlobchegRecordTypes.Require(typeof(RecordWithArray)));
        }

        [Test]
        public void A_pointer_inside_an_array_element_is_found()
        {
            var thrown = Assert.Throws<InvalidOperationException>(
                () => BlobchegRecordTypes.Require(typeof(RecordWithPointerInsideElement)));

            StringAssert.Contains("Items[].Address", thrown.Message,
                "the error path is obliged to lead to the field inside the element");
        }
    }
}
