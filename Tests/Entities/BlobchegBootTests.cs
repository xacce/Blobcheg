using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;

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
    public sealed unsafe class BlobchegBootTests
    {
        [Test]
        public void Пересборка_под_живым_миром_доезжает_до_синглтона()
        {
            BlobchegBuild.RebuildAll();

            var world = new World("blobcheg-boot-reraise-tests");
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

                Assert.That(query.CalculateEntityCount(), Is.EqualTo(1), "база не поднялась — дальше проверять нечего");

                var key = BlobchegNaming.NameHash(TestBootDb.DomainName);
                Assert.That(BlobchegBases.TryGet(key, out var before, out var length), Is.True);

                // Пересборка в редакторе кончается ровно этим: номер файла поднят. Дальше сторожить
                // его — забота того, кто базу поднял.
                BlobchegFileVersions.Bump(TestBootDb.FileName);
                system.Update(world.Unmanaged);

                Assert.That(BlobchegBases.TryGet(key, out var after, out var lengthAfter), Is.True);
                Assert.That((ulong)after, Is.Not.EqualTo((ulong)before),
                    "файл переписан — в мире обязан оказаться НОВЫЙ буфер, а не прежние байты");
                Assert.That(lengthAfter, Is.EqualTo(length), "файл тот же, значит и длина та же");

                var database = query.GetSingleton<TestBootDb>();
                Assert.That(database.IsCreated, Is.True, "синглтон обязан держать новый блоб, а не освобождённый старый");
                Assert.That(database.Length, Is.EqualTo(lengthAfter));

                // Второй апдейт без пересборки ничего не трогает: иначе база перечитывалась бы каждый кадр.
                system.Update(world.Unmanaged);
                Assert.That(BlobchegBases.TryGet(key, out var idle, out _), Is.True);
                Assert.That((ulong)idle, Is.EqualTo((ulong)after));
            }
            finally
            {
                world.Dispose();
            }
        }

        [Test]
        public void Битый_файл_отбивается_один_раз_и_чинится_пересборкой()
        {
            BlobchegBuild.RebuildAll();

            var path = Path.Combine(
                Application.streamingAssetsPath, BlobchegNaming.DefaultFolder, TestBootDb.FileName);
            var sane = File.ReadAllBytes(path);

            var world = new World("blobcheg-boot-broken-tests");
            try
            {
                // Версия формата лежит в header'е сразу за magic. Тройка — прошлый формат пакета:
                // ровно такой файл и отбил читатель, когда обновлённый пакет встретил старые .bcheg.
                var broken = (byte[])sane.Clone();
                broken[4] = 3;
                File.WriteAllBytes(path, broken);

                LogAssert.Expect(LogType.Exception, new Regex("версия формата 3"));

                var system = world.CreateSystem<TestBootDbBootSystem>();
                var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootDb>());

                // Дальше кадры идут, а отказ обязан остаться один. Иначе настоящую причину топит
                // пересказ её последствий: чтение забрано, и каждый следующий Poll бьётся об это.
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 2000)
                {
                    system.Update(world.Unmanaged);
                    System.Threading.Thread.Sleep(1);
                }

                LogAssert.NoUnexpectedReceived();

                // Пересборка переписала файл — вот теперь подъём обязан поехать заново, без
                // перезагрузки домена: иначе сорвавшийся мир не чинится вообще ничем.
                File.WriteAllBytes(path, sane);
                BlobchegFileVersions.Bump(TestBootDb.FileName);

                clock.Restart();
                while (query.CalculateEntityCount() == 0 && clock.ElapsedMilliseconds < 5000)
                {
                    system.Update(world.Unmanaged);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(query.CalculateEntityCount(), Is.EqualTo(1),
                    "починенный файл обязан доехать до синглтона");
            }
            finally
            {
                File.WriteAllBytes(path, sane);
                world.Dispose();
            }
        }

        /// <summary>
        /// Путь до файла базы теста. Сносить и портить его можно: сохранённые байты возвращаются в
        /// finally, а пересборка всё равно соберёт его заново.
        /// </summary>
        static string PathOfDatabase()
            => Path.Combine(Application.streamingAssetsPath, BlobchegNaming.DefaultFolder, TestBootDb.FileName);

        /// <summary>
        /// Собирает лог за время прогона. Варнинг здесь — предмет проверки, а <c>LogAssert</c> на
        /// варнингах доказывает только «пришёл хотя бы раз»; нам нужно ещё и «ровно раз».
        /// </summary>
        sealed class LogTrap : System.IDisposable
        {
            readonly List<(LogType type, string text)> _messages = new List<(LogType, string)>();

            public LogTrap() => Application.logMessageReceived += Take;

            void Take(string condition, string trace, LogType type) => _messages.Add((type, condition));

            public int Notifications => _messages.Count(m =>
                m.type == LogType.Warning && m.text.Contains("нотификация, а не проблема"));

            public IEnumerable<string> Loud => _messages
                .Where(m => m.type == LogType.Error || m.type == LogType.Exception || m.type == LogType.Assert)
                .Select(m => m.text);

            public void Dispose() => Application.logMessageReceived -= Take;
        }

        static void Spin(SystemHandle system, World world, int ms, System.Func<bool> until = null)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                system.Update(world.Unmanaged);
                if (until != null && until())
                    return;

                System.Threading.Thread.Sleep(1);
            }
        }

        /// <summary>
        /// Файла базы ещё нет — так выглядит домен, приехавший с пуллом раньше своей пересборки.
        /// Красный error здесь врёт: чинить нечего, и подъём поедет сам.
        /// </summary>
        [Test]
        public void Пропавший_файл_отбивается_варнингом_и_чинится_пересборкой()
        {
            BlobchegBuild.RebuildAll();

            var path = PathOfDatabase();
            var sane = File.ReadAllBytes(path);

            var world = new World("blobcheg-boot-missing-tests");
            using (var log = new LogTrap())
            {
                try
                {
                    File.Delete(path);

                    var system = world.CreateSystem<TestBootDbBootSystem>();
                    var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootDb>());

                    Spin(system, world, 1000);

                    Assert.That(log.Loud, Is.Empty, "переходный момент — не ошибка, красного в логе быть не должно");
                    Assert.That(log.Notifications, Is.EqualTo(1),
                        "варнинг обязан быть, и ровно один: кадры идут, а сказать тут нечего дважды");
                    Assert.That(query.CalculateEntityCount(), Is.Zero, "поднимать пока нечего");

                    // Пересборка написала файл и подняла его номер — дальше система обязана сама.
                    File.WriteAllBytes(path, sane);
                    BlobchegFileVersions.Bump(TestBootDb.FileName);

                    Spin(system, world, 5000, () => query.CalculateEntityCount() == 1);

                    Assert.That(query.CalculateEntityCount(), Is.EqualTo(1),
                        "написанный файл обязан доехать до синглтона без перезагрузки домена");
                }
                finally
                {
                    File.WriteAllBytes(path, sane);
                    world.Dispose();
                }
            }
        }

        /// <summary>
        /// Файл пойман посреди перезаписи: длину читатель узнал от нового header'а, а байты достались
        /// от прежнего. Через кадр то же чтение проходит — значит и это нотификация.
        /// </summary>
        [Test]
        public void Файл_посреди_перезаписи_отбивается_варнингом_и_чинится_пересборкой()
        {
            BlobchegBuild.RebuildAll();

            var path = PathOfDatabase();
            var sane = File.ReadAllBytes(path);

            var world = new World("blobcheg-boot-torn-tests");
            using (var log = new LogTrap())
            {
                try
                {
                    File.WriteAllBytes(path, sane.Concat(new byte[] { 0 }).ToArray());

                    var system = world.CreateSystem<TestBootDbBootSystem>();
                    var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootDb>());

                    Spin(system, world, 1000);

                    Assert.That(log.Loud, Is.Empty, "недописанный файл — это момент, а не поломка");
                    Assert.That(log.Notifications, Is.EqualTo(1));
                    Assert.That(query.CalculateEntityCount(), Is.Zero);

                    File.WriteAllBytes(path, sane);
                    BlobchegFileVersions.Bump(TestBootDb.FileName);

                    Spin(system, world, 5000, () => query.CalculateEntityCount() == 1);

                    Assert.That(query.CalculateEntityCount(), Is.EqualTo(1));
                }
                finally
                {
                    File.WriteAllBytes(path, sane);
                    world.Dispose();
                }
            }
        }

        /// <summary>
        /// То же под живым миром: база уже поднята, а перезаливка попала в середину перезаписи.
        /// Номер файла при этом обязан остаться неувиденным — иначе перечитывать станет нечего до
        /// следующей пересборки, и мир молча останется на прежних байтах.
        /// </summary>
        [Test]
        public void Перезаливка_на_недописанном_файле_повторяет_попытку_сама()
        {
            BlobchegBuild.RebuildAll();

            var path = PathOfDatabase();
            var sane = File.ReadAllBytes(path);
            var key = BlobchegNaming.NameHash(TestBootDb.DomainName);

            var world = new World("blobcheg-boot-torn-reraise-tests");
            using (var log = new LogTrap())
            {
                try
                {
                    var system = world.CreateSystem<TestBootDbBootSystem>();
                    var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootDb>());

                    Spin(system, world, 5000, () => query.CalculateEntityCount() == 1);
                    Assert.That(query.CalculateEntityCount(), Is.EqualTo(1), "база не поднялась — дальше проверять нечего");
                    Assert.That(BlobchegBases.TryGet(key, out var before, out _), Is.True);

                    // Пересборка «переписывает» файл: номер поднят, а байты на диске недописаны.
                    File.WriteAllBytes(path, sane.Concat(new byte[] { 0 }).ToArray());
                    BlobchegFileVersions.Bump(TestBootDb.FileName);

                    Spin(system, world, 1000);

                    Assert.That(log.Loud, Is.Empty);
                    Assert.That(log.Notifications, Is.EqualTo(1), "один варнинг на полосу, а не на кадр");
                    Assert.That(BlobchegBases.TryGet(key, out var held, out _), Is.True);
                    Assert.That((ulong)held, Is.EqualTo((ulong)before), "мир едет на прежней базе, пока новая недописана");

                    // Файл дописан, а номер БОЛЬШЕ НЕ ПОДНИМАЕТСЯ: та пересборка уже случилась,
                    // и повторить попытку — забота самой системы.
                    File.WriteAllBytes(path, sane);

                    Spin(system, world, 5000,
                        () => BlobchegBases.TryGet(key, out var now, out _) && (ulong)now != (ulong)before);

                    Assert.That(BlobchegBases.TryGet(key, out var after, out _), Is.True);
                    Assert.That((ulong)after, Is.Not.EqualTo((ulong)before),
                        "дописанный файл обязан доехать до мира сам — иначе он остался бы на вчерашних байтах молча");
                    Assert.That(query.GetSingleton<TestBootDb>().IsCreated, Is.True);
                }
                finally
                {
                    File.WriteAllBytes(path, sane);
                    world.Dispose();
                }
            }
        }

        [Test]
        public void Бут_система_заведена_и_в_редакторном_мире()
        {
            var filter = (WorldSystemFilterAttribute)typeof(TestBootDbBootSystem)
                .GetCustomAttributes(typeof(WorldSystemFilterAttribute), false)[0];

            Assert.That(filter.FilterFlags & WorldSystemFilterFlags.Editor, Is.Not.EqualTo(default(WorldSystemFilterFlags)),
                "без этого флага базы в редакторном мире нет, и любой проход патча там упирается в «домен не поднят»");
            Assert.That(filter.FilterFlags & WorldSystemFilterFlags.Default, Is.Not.EqualTo(default(WorldSystemFilterFlags)),
                "а игровой мир при этом никуда не делся");
        }

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
