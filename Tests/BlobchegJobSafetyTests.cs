using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;

namespace Blobcheg.Tests
{
    /// <summary>
    /// Джоба с полем-блобчегом обязана шедулиться без единой метки у потребителя. Пакет носит
    /// указатели, пакет за них и отвечает: <c>[NativeDisableUnsafePtrRestriction]</c> стоит на самих
    /// полях базы и роутера.
    ///
    /// Стоило это уже один раз: без метки на роутере safety-система рубит шедул именем СВОЕГО поля
    /// (<c>_masks</c>), система падает каждый тик, и снаружи это выглядит как «бак стоит на нуле».
    /// Компилятор такое пропускает — держит только этот тест.
    ///
    /// Указатели здесь нулевые намеренно: проверка сырых указателей идёт по типу джобы на шедуле,
    /// до первого чтения, поэтому поднимать настоящие файлы незачем.
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
        public void Джоба_с_типизированным_роутером_шедулится_без_метки_у_потребителя()
            => Schedules(touched => new RouterFieldJob { Router = default, Touched = touched });

        [Test]
        public void Джоба_с_голым_роутером_шедулится_без_метки_у_потребителя()
            => Schedules(touched => new RouterBlobFieldJob { Router = default, Touched = touched });

        [Test]
        public void Джоба_с_базой_шедулится_без_метки_у_потребителя()
            => Schedules(touched => new DatabaseFieldJob { Cold = default, Touched = touched });

        static void Schedules<T>(System.Func<NativeArray<int>, T> make) where T : struct, IJobParallelFor
        {
            var touched = new NativeArray<int>(4, Allocator.TempJob);

            try
            {
                Assert.DoesNotThrow(() => make(touched).Schedule(touched.Length, 1).Complete(),
                    "поле-блобчег в джобе — обычное дело; метку за него платит пакет");

                Assert.That(touched[3], Is.EqualTo(3), "джоба обязана была отработать, а не молча не встать");
            }
            finally
            {
                touched.Dispose();
            }
        }
    }
}
