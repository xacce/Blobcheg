using System;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Blobcheg.Tests
{
    enum ViewGrade : byte
    {
        None = 0,
        Fair = 1,
        Grand = 7,
    }

    struct ViewInner
    {
        public float2 point;
        public ViewGrade grade;
    }

    struct ViewRecord
    {
        public int count;
        public bool flag;
        public float rate;
        public ViewGrade grade;
        public ViewInner inner;
        public FixedString32Bytes label;
        public BlobchegArray<float2> offsets;
    }

    struct ViewTail
    {
        public BlobchegArray<int> cells;
    }

    // Bytes come from the rebuild's own builder, so the decoded layout is the one that ships in a file.
    public sealed class BlobchegRecordViewTests
    {
        [Test]
        public void Fields_come_out_with_their_values()
        {
            byte[] built = null;
            var b = new BlobchegBuilder<ViewRecord>("node", bytes => built = bytes);
            b.Root.count = 42;
            b.Root.flag = true;
            b.Root.rate = 1.25f;
            b.Root.grade = ViewGrade.Fair;
            b.Root.inner = new ViewInner { point = new float2(3f, -0.5f), grade = ViewGrade.Grand };
            b.Root.label = "щит";
            b.Allocate(ref b.Root.offsets, 0);
            b.End();

            var record = Decode(built, typeof(ViewRecord));

            Assert.That(Child(record, "count").Value, Is.EqualTo("42"));
            Assert.That(Child(record, "flag").Value, Is.EqualTo("true"));
            Assert.That(Child(record, "rate").Value, Is.EqualTo("1.25"));
            Assert.That(Child(record, "grade").Value, Is.EqualTo("Fair"));
            Assert.That(Child(record, "label").Value, Is.EqualTo("\"щит\""));
        }

        [Test]
        public void A_nested_struct_opens_up_and_a_vector_stays_one_line()
        {
            byte[] built = null;
            var b = new BlobchegBuilder<ViewRecord>("node", bytes => built = bytes);
            b.Root.inner = new ViewInner { point = new float2(3f, -0.5f), grade = ViewGrade.Grand };
            b.Allocate(ref b.Root.offsets, 0);
            b.End();

            var inner = Child(Decode(built, typeof(ViewRecord)), "inner");

            Assert.That(inner.IsLeaf, Is.False, "a struct is opened, not printed");
            Assert.That(Child(inner, "point").Value, Is.EqualTo("(3, -0.5)"));
            Assert.That(Child(inner, "grade").Value, Is.EqualTo("Grand"));
        }

        [Test]
        public void An_array_unfolds_into_its_elements()
        {
            byte[] built = null;
            var b = new BlobchegBuilder<ViewRecord>("node", bytes => built = bytes);
            var offsets = b.Allocate(ref b.Root.offsets, 3);
            offsets[0] = new float2(0f, 0f);
            offsets[1] = new float2(0.25f, -1f);
            offsets[2] = new float2(2f, 2f);
            b.End();

            var array = Child(Decode(built, typeof(ViewRecord)), "offsets");

            Assert.That(array.Value, Is.EqualTo("[3]"));
            Assert.That(array.TypeName, Is.EqualTo("BlobchegArray<float2>"));
            Assert.That(array.Children.Count, Is.EqualTo(3));
            Assert.That(array.Children[1].Name, Is.EqualTo("[1]"));
            Assert.That(array.Children[1].Value, Is.EqualTo("(0.25, -1)"));
        }

        [Test]
        public void An_empty_array_has_no_elements_and_does_not_dereference_its_offset()
        {
            byte[] built = null;
            var b = new BlobchegBuilder<ViewTail>("node", bytes => built = bytes);
            b.Allocate(ref b.Root.cells, 0);
            b.End();

            var array = Child(Decode(built, typeof(ViewTail)), "cells");

            Assert.That(array.Value, Is.EqualTo("[0]"));
            Assert.That(array.Children, Is.Empty);
        }

        [Test]
        public void A_long_array_is_cut_and_says_how_much_was_left()
        {
            const int count = BlobchegRecordView.MaxElements + 5;

            byte[] built = null;
            var b = new BlobchegBuilder<ViewTail>("node", bytes => built = bytes);
            var cells = b.Allocate(ref b.Root.cells, count);
            for (var i = 0; i < count; i++)
                cells[i] = i;
            b.End();

            var array = Child(Decode(built, typeof(ViewTail)), "cells");

            Assert.That(array.Value, Is.EqualTo("[" + count + "]"));
            Assert.That(array.Children.Count, Is.EqualTo(BlobchegRecordView.MaxElements + 1));
            Assert.That(array.Children[BlobchegRecordView.MaxElements].Value, Is.EqualTo("ещё 5"));
        }

        [Test]
        public void An_unnamed_enum_value_prints_as_a_number()
        {
            byte[] built = null;
            var b = new BlobchegBuilder<ViewRecord>("node", bytes => built = bytes);
            b.Root.grade = (ViewGrade)5; // normal for a file baked before the member was added
            b.Allocate(ref b.Root.offsets, 0);
            b.End();

            Assert.That(Child(Decode(built, typeof(ViewRecord)), "grade").Value, Is.EqualTo("5"));
        }

        // The view reads live editor memory: a record that does not fit must never be read past the buffer.
        [Test]
        public void A_record_that_does_not_fit_the_buffer_says_so_instead_of_reading_it()
        {
            byte[] built = null;
            var b = new BlobchegBuilder<ViewTail>("node", bytes => built = bytes);
            b.Allocate(ref b.Root.cells, 4);
            b.End();

            var record = Decode(built, typeof(ViewTail), cut: 20); // cuts the head: the first read touches it

            Assert.That(record.Children, Is.Empty);
            Assert.That(record.Value, Does.Contain("не влезает"));
        }

        [Test]
        public void An_array_pointing_out_of_the_buffer_is_refused_and_not_followed()
        {
            byte[] built = null;
            var b = new BlobchegBuilder<ViewTail>("node", bytes => built = bytes);
            var cells = b.Allocate(ref b.Root.cells, 2);
            cells[0] = 1;
            cells[1] = 2;
            b.End();

            BitConverter.GetBytes(1 << 20).CopyTo(built, 0); // tail offset leading outside, as after a stale rebuild

            var array = Child(Decode(built, typeof(ViewTail)), "cells");

            Assert.That(array.Children, Is.Empty);
            Assert.That(array.Value, Does.Contain("за границей буфера"));
        }

        [Test]
        public void The_text_form_carries_the_names_and_the_values()
        {
            byte[] built = null;
            var b = new BlobchegBuilder<ViewRecord>("node", bytes => built = bytes);
            b.Root.count = 7;
            b.Allocate(ref b.Root.offsets, 0);
            b.End();

            var text = BlobchegRecordView.ToText(Decode(built, typeof(ViewRecord)));

            Assert.That(text, Does.Contain("count : Int32 = 7"));
            Assert.That(text, Does.Contain("inner : ViewInner"));
        }

        // Laid after the header as in a real file: at offset zero an arithmetic mistake would go unnoticed.
        static unsafe BlobchegRecordNode Decode(byte[] record, Type type, int cut = 0)
        {
            const int at = BlobchegFormat.HeaderSize;

            var buffer = new byte[at + record.Length];
            Array.Copy(record, 0, buffer, at, record.Length);

            fixed (byte* bytes = buffer)
                return BlobchegRecordView.Of(bytes, buffer.Length - cut, at, type, "record");
        }

        static BlobchegRecordNode Child(BlobchegRecordNode node, string name)
        {
            foreach (var child in node.Children)
            {
                if (child.Name == name)
                    return child;
            }

            Assert.Fail($"поля '{name}' в разборе нет");
            return null;
        }
    }
}
