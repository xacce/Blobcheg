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
    /// Два вердикта проверки типа: «несёт указатель» и «требует билдер». Второй — не самоочевидный:
    /// поля <see cref="BlobchegArray{T}"/> — это два int'а, элемент среди полей не встречается, и
    /// обход обязан входить в тип-аргумент отдельно.
    /// </summary>
    public sealed class BlobchegRecordTypesTests
    {
        [Test]
        public void Тип_без_массива_билдера_не_требует()
        {
            Assert.That(BlobchegRecordTypes.RequiresBuilder(typeof(PlainRecord)), Is.False);
        }

        [Test]
        public void Тип_с_массивом_требует_билдер()
        {
            Assert.That(BlobchegRecordTypes.RequiresBuilder(typeof(RecordWithArray)), Is.True);
        }

        [Test]
        public void Массив_на_глубине_вложенности_тоже_требует_билдер()
        {
            Assert.That(BlobchegRecordTypes.RequiresBuilder(typeof(RecordWithDeepArray)), Is.True);
        }

        [Test]
        public void Сам_массив_указателем_не_считается()
        {
            // Два int'а — указателя нет; тип с массивом float'ов обязан проходить проверку.
            Assert.DoesNotThrow(() => BlobchegRecordTypes.Require(typeof(RecordWithArray)));
        }

        [Test]
        public void Указатель_внутри_элемента_массива_находится()
        {
            var thrown = Assert.Throws<InvalidOperationException>(
                () => BlobchegRecordTypes.Require(typeof(RecordWithPointerInsideElement)));

            StringAssert.Contains("Items[].Address", thrown.Message,
                "путь ошибки обязан довести до поля внутри элемента");
        }
    }
}
