using System.Diagnostics;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Entities;

namespace Blobcheg.Tests
{
    public interface ITestBootData
    {
    }

    public struct TestBootRecord : ITestBootData
    {
        public int Value;
    }

    /// <summary>
    /// База, объявленная <c>IComponentData</c>: под неё генератор выпускает бут-систему
    /// <c>TestBootDbBootSystem</c>. Если он её не выпустил, этот файл не соберётся.
    ///
    /// <c>[DisableAutoCreation]</c> уезжает на выпущенную систему: тестовой базе нечего делать в
    /// дефолтном мире потребителя, тем более что её файла на свежем чекауте ещё нет. Мир под неё
    /// тест создаёт свой.
    /// </summary>
    [Blobcheg(typeof(ITestBootData))]
    [DisableAutoCreation]
    public partial struct TestBootDb : IComponentData
    {
    }

    /// <summary>
    /// Выпущенная генератором бут-система. Мир создаётся свой: доказывать надо саму систему, а не
    /// то, в каком порядке её создал дефолтный мир редактора.
    /// </summary>
    public sealed class BlobchegBootTests
    {
        [Test]
        public void Бут_система_поднимает_базу_в_синглтон()
        {
            // Файл базы обязан лежать на диске: писатель открывается на каждый объявленный домен,
            // поэтому пустой ITestBootData тоже собирается.
            BlobchegBuild.RebuildAll();

            var world = new World("blobcheg-boot-tests");
            try
            {
                var system = world.CreateSystem<TestBootDbBootSystem>();
                var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootDb>());

                var clock = Stopwatch.StartNew();
                while (query.CalculateEntityCount() == 0 && clock.ElapsedMilliseconds < 5000)
                {
                    system.Update(world.Unmanaged);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(query.CalculateEntityCount(), Is.EqualTo(1),
                    "бут-система обязана положить базу синглтоном за пять секунд");

                var database = query.GetSingleton<TestBootDb>();
                Assert.That(database.IsCreated, Is.True);
                Assert.That(database.Length, Is.GreaterThanOrEqualTo(BlobchegFormat.HeaderSize));
            }
            finally
            {
                // Мир диспозится — OnDestroy системы отдаёт буфер базы обратно.
                world.Dispose();
            }
        }

        [Test]
        public void Запрет_автосоздания_едет_с_базы_на_выпущенную_систему()
        {
            var system = typeof(TestBootDbBootSystem);

            Assert.That(system.GetCustomAttributes(typeof(DisableAutoCreationAttribute), false), Is.Not.Empty,
                "[DisableAutoCreation] на базе обязан оказаться на её бут-системе, иначе дефолтный мир " +
                "потребителя поднимает чужую базу");

            var inGroup = (UpdateInGroupAttribute)system.GetCustomAttributes(typeof(UpdateInGroupAttribute), false)[0];
            Assert.That(inGroup.GroupType, Is.EqualTo(typeof(BlobchegBootGroup)));
        }

        [Test]
        public void Группа_подъёма_стоит_до_командного_буфера_инициализации()
        {
            var group = typeof(BlobchegBootGroup);

            var inGroup = (UpdateInGroupAttribute)group.GetCustomAttributes(typeof(UpdateInGroupAttribute), false)[0];
            Assert.That(inGroup.GroupType, Is.EqualTo(typeof(InitializationSystemGroup)));
            Assert.That(inGroup.OrderFirst, Is.True);

            var before = (UpdateBeforeAttribute)group.GetCustomAttributes(typeof(UpdateBeforeAttribute), false)[0];
            Assert.That(before.SystemType, Is.EqualTo(typeof(BeginInitializationEntityCommandBufferSystem)),
                "базы обязаны подняться раньше, чем проиграется первый командный буфер кадра");
        }
    }
}
