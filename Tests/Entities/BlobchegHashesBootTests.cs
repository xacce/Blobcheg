using System.Diagnostics;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Entities;

namespace Blobcheg.Tests
{
    public interface ITestBootHashData
    {
    }

    public struct TestBootHashRecord : ITestBootHashData
    {
        public ulong Self;
    }

    [Blobcheg(typeof(ITestBootHashData), "boot")]
    public partial struct TestBootHashDb
    {
    }

    /// <summary>Роутер этой сборки. Свой, потому что таблица и роутер обязаны быть одной компиляции.</summary>
    [BlobchegRouter]
    public partial struct TestBootRouter
    {
    }

    /// <summary>
    /// Таблица, объявленная <c>IComponentData</c>: под неё генератор выпускает
    /// <c>TestBootHashesBootSystem</c>. Не выпустил — этот файл не соберётся.
    /// </summary>
    [BlobchegHashes(typeof(TestBootRouter))]
    [DisableAutoCreation]
    public partial struct TestBootHashes : IComponentData
    {
    }

    /// <summary>
    /// Нод у этого роутера в проекте нет, и таблица собирается пустой — доказывать здесь надо
    /// подъём, а не лукап: лукап доказан в Blobcheg.Hashes.Tests, где ноды свои.
    /// </summary>
    public sealed class BlobchegHashesBootTests
    {
        [Test]
        public void Бут_система_поднимает_таблицу_в_синглтон()
        {
            BlobchegBuild.RebuildAll();

            var world = new World("blobcheg-hashes-boot-tests");
            try
            {
                var system = world.CreateSystem<TestBootHashesBootSystem>();
                var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootHashes>());

                var clock = Stopwatch.StartNew();
                while (query.CalculateEntityCount() == 0 && clock.ElapsedMilliseconds < 5000)
                {
                    system.Update(world.Unmanaged);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(query.CalculateEntityCount(), Is.EqualTo(1),
                    "бут-система обязана положить таблицу синглтоном за пять секунд");

                var table = query.GetSingleton<TestBootHashes>();
                Assert.That(table.IsCreated, Is.True);
                Assert.That(table.Tag, Is.EqualTo(BlobchegNaming.TagOf(TestBootHashes.RouterName)));
            }
            finally
            {
                world.Dispose();
            }
        }

        [Test]
        public void Таблица_перечитывается_под_живым_миром()
        {
            BlobchegBuild.RebuildAll();

            var world = new World("blobcheg-hashes-reraise-tests");
            try
            {
                var system = world.CreateSystem<TestBootHashesBootSystem>();
                var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootHashes>());

                var clock = Stopwatch.StartNew();
                while (query.CalculateEntityCount() == 0 && clock.ElapsedMilliseconds < 5000)
                {
                    system.Update(world.Unmanaged);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(query.CalculateEntityCount(), Is.EqualTo(1), "таблица не поднялась — дальше проверять нечего");

                // Пересборка в редакторе кончается ровно этим: номер файла поднят.
                BlobchegFileVersions.Bump(TestBootHashes.FileName);
                system.Update(world.Unmanaged);

                var table = query.GetSingleton<TestBootHashes>();
                Assert.That(table.IsCreated, Is.True,
                    "синглтон обязан держать новый блоб, а не освобождённый старый");
            }
            finally
            {
                world.Dispose();
            }
        }

        [Test]
        public void Бут_система_таблицы_стоит_в_группе_подъёма()
        {
            var system = typeof(TestBootHashesBootSystem);

            Assert.That(system.GetCustomAttributes(typeof(DisableAutoCreationAttribute), false), Is.Not.Empty,
                "[DisableAutoCreation] на таблице обязан оказаться на её бут-системе");

            var inGroup = (UpdateInGroupAttribute)system.GetCustomAttributes(typeof(UpdateInGroupAttribute), false)[0];
            Assert.That(inGroup.GroupType, Is.EqualTo(typeof(BlobchegBootGroup)));
        }
    }
}
