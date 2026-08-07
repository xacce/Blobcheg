using System.Diagnostics;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Entities;

namespace Blobcheg.Tests
{
    /// <summary>
    /// Гейт и номер сборки — то, чем подъём говорит наружу «база в этом мире сменилась». Мир
    /// собирается из двух систем и крутится руками: доказывать надо порядок «держатель гасит, потом
    /// бут зажигает», а не то, в каком порядке их создал дефолтный мир.
    /// </summary>
    public sealed class BlobchegUpdatedTests
    {
        /// <summary>Кадр: сперва держатель гейта, потом подъём — тот же порядок, что даёт OrderFirst в группе.</summary>
        static void Frame(World world, SystemHandle gate, SystemHandle boot)
        {
            gate.Update(world.Unmanaged);
            boot.Update(world.Unmanaged);
        }

        [Test]
        public void Гейт_зажигается_подъёмом_и_гаснет_следующим_кадром()
        {
            BlobchegBuild.RebuildAll();

            var world = new World("blobcheg-gate-tests");
            try
            {
                var gateSystem = world.CreateSystem<BlobchegUpdatedSystem>();
                var boot = world.CreateSystem<TestBootDbBootSystem>();

                var gate = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<BlobchegUpdated>());
                var raised = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootDb>());

                var clock = Stopwatch.StartNew();
                while (raised.CalculateEntityCount() == 0 && clock.ElapsedMilliseconds < 5000)
                {
                    Frame(world, gateSystem, boot);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(raised.CalculateEntityCount(), Is.EqualTo(1), "база не поднялась — дальше проверять нечего");
                Assert.That(gate.CalculateEntityCount(), Is.EqualTo(1),
                    "приезд базы — то же событие, что и её пересборка: у кого от неё производное, тому нужен и первый подъём");

                // Следующий кадр: держатель гасит гейт до того, как бут-системы успеют зажечь его снова.
                Frame(world, gateSystem, boot);
                Assert.That(gate.CalculateEntityCount(), Is.EqualTo(0),
                    "гейт живёт кадр — иначе он значит «когда-то поднимали», а не «только что перезапеклось»");
            }
            finally
            {
                world.Dispose();
            }
        }

        [Test]
        public void Пересборка_под_живым_миром_зажигает_гейт_снова()
        {
            BlobchegBuild.RebuildAll();

            var world = new World("blobcheg-gate-reraise-tests");
            try
            {
                var gateSystem = world.CreateSystem<BlobchegUpdatedSystem>();
                var boot = world.CreateSystem<TestBootDbBootSystem>();

                var gate = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<BlobchegUpdated>());
                var raised = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootDb>());

                var clock = Stopwatch.StartNew();
                while (raised.CalculateEntityCount() == 0 && clock.ElapsedMilliseconds < 5000)
                {
                    Frame(world, gateSystem, boot);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(raised.CalculateEntityCount(), Is.EqualTo(1), "база не поднялась — дальше проверять нечего");

                // Кадр без пересборки: гейт погас и сам собой не зажигается.
                Frame(world, gateSystem, boot);
                Assert.That(gate.CalculateEntityCount(), Is.EqualTo(0),
                    "файл никто не переписывал — зажигать гейт не с чего");

                // Пересборка в редакторе кончается ровно этим: номер файла поднят.
                BlobchegFileVersions.Bump(TestBootDb.FileName);
                Frame(world, gateSystem, boot);

                Assert.That(gate.CalculateEntityCount(), Is.EqualTo(1),
                    "перезаливка обязана доехать до потребителя — иначе правка ноды не видна в мире");
            }
            finally
            {
                world.Dispose();
            }
        }

        [Test]
        public void Номер_синглтона_поднимается_пересборкой()
        {
            BlobchegBuild.RebuildAll();

            var world = new World("blobcheg-version-tests");
            try
            {
                var boot = world.CreateSystem<TestBootDbBootSystem>();
                var raised = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TestBootDb>());

                var clock = Stopwatch.StartNew();
                while (raised.CalculateEntityCount() == 0 && clock.ElapsedMilliseconds < 5000)
                {
                    boot.Update(world.Unmanaged);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(raised.CalculateEntityCount(), Is.EqualTo(1), "база не поднялась — дальше проверять нечего");

                var before = raised.GetSingleton<TestBootDb>().Version;

                // Кадр без пересборки номер не двигает: иначе производное пересобиралось бы каждый кадр.
                boot.Update(world.Unmanaged);
                Assert.That(raised.GetSingleton<TestBootDb>().Version, Is.EqualTo(before));

                BlobchegFileVersions.Bump(TestBootDb.FileName);
                boot.Update(world.Unmanaged);

                Assert.That(raised.GetSingleton<TestBootDb>().Version, Is.GreaterThan(before),
                    "номер снят с файла на подъёме — перечитанный буфер обязан приехать с новым");
            }
            finally
            {
                world.Dispose();
            }
        }
    }
}
