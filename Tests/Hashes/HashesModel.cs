using System;
using Blobcheg.Authoring;

namespace Blobcheg.HashTests
{
    /// <summary>
    /// Модель тестов таблицы: два домена, роутер и таблица над ним. Роутер здесь свой, а не тот, что
    /// в Blobcheg.Tests, потому что роутер, его базы и его таблицу генератор обязан видеть в одной
    /// компиляции — а тесты хешей лежат отдельной сборкой.
    /// </summary>
    public interface ITestHashHot
    {
    }

    public interface ITestHashCold
    {
    }

    public struct TestHashHotRecord : ITestHashHot
    {
        /// <summary>Свой хеш, положенный нодой прямо в запись: он известен до записи.</summary>
        public ulong Self;

        /// <summary>Хеш соседней ноды: так одна запись ссылается на другую, не зная её адресов.</summary>
        public ulong Twin;

        public int Rpm;
    }

    public struct TestHashColdRecord : ITestHashCold
    {
        public int Tier;
    }

    // Биты нумеруются доменами по FullName ordinal: cold — нулевой, hot — первый.
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

    /// <summary>Таблица хешей над роутером. Имя структуры своё, имя файла — от роутера.</summary>
    [BlobchegHashes(typeof(TestHashRouter))]
    public partial struct TestHashTable
    {
    }

    /// <summary>Нода в обеих базах: строка роутера с двумя битами.</summary>
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

    /// <summary>Нода только в холодной базе: в горячей дорожке её нет.</summary>
    public sealed class TestHashColdOnlyNodeSo : BlobchegNodeSo
    {
        public int tier = 7;

        public override Type[] OutTypes => new[] { typeof(ITestHashCold) };

        public override void Write(ref BlobchegNodeWriter writer)
            => writer.Add(new TestHashColdRecord { Tier = tier });
    }
}
