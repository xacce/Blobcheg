using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// Чтение из джоб, реентранс пересборки и объём: сотня тысяч строк роутера, запись в мегабайты,
    /// ровно одна нода и сотня тысяч чтений подряд.
    /// </summary>
    public sealed class ConcurrencyAndVolumeTests : AdvancedFixture
    {
        /// <summary>
        /// Чтение базы прямо из бёрстовой джобы — то, ради чего пакет вообще существует. Если это не
        /// компилируется или не шедулится, всё остальное значения не имеет.
        /// </summary>
        [BurstCompile]
        struct AdvReadJob : IJobParallelFor
        {
            public AdvCombatDb Db;

            [ReadOnly] public NativeArray<uint> Offsets;

            [WriteOnly] public NativeArray<int> Rpm;

            public void Execute(int index) => Rpm[index] = Db.Read<AdvGun>(Offsets[index]).Rpm;
        }

        [Test]
        public void Параллельное_чтение_из_джоб_даёт_те_же_значения()
        {
            var nodes = new List<AdvComboNodeSo>();
            for (var i = 0; i < 16; i++)
            {
                var node = Node<AdvComboNodeSo>("Combo" + i.ToString("D2"));
                node.rpm = 100 + i;
                Dirty(node);
                nodes.Add(node);
            }

            Rebuild();

            var offsets = new NativeArray<uint>(nodes.Count, Allocator.TempJob);
            var rpm = new NativeArray<int>(nodes.Count, Allocator.TempJob);
            var db = Combat();
            try
            {
                for (var i = 0; i < nodes.Count; i++)
                    offsets[i] = OffsetOf(nodes[i], "IAdvCombat");

                new AdvReadJob { Db = db, Offsets = offsets, Rpm = rpm }.Schedule(nodes.Count, 4).Complete();

                for (var i = 0; i < nodes.Count; i++)
                {
                    Assert.That(rpm[i], Is.EqualTo(nodes[i].rpm),
                        $"нода {nodes[i].name} прочитана из джобы не тем значением");
                }
            }
            finally
            {
                offsets.Dispose();
                rpm.Dispose();
                db.Dispose();
            }
        }

        /// <summary>
        /// Нода в своём <c>Write</c> может тронуть AssetDatabase чем угодно и войти в пересборку из
        /// середины пересборки. Защита стоит на самой пересборке, а не на хуке импорта: вложенный
        /// заход идёт поверх наполовину заполненного коллектора и наполовину розданных id, и «файл
        /// собран» после него не значит ничего.
        /// </summary>
        [Test]
        public void Пересборка_из_середины_пересборки_отбивается()
        {
            Node<AdvReentrantNodeSo>("Reentrant");
            AdvReentrantNodeSo.Forget();

            Rebuild();

            Assert.That(AdvReentrantNodeSo.Reentered, Is.Zero,
                "нода позвала пересборку из Write, и пересборка это позволила");
        }

        [Test]
        public void Сто_тысяч_строк_роутера_адресуются()
        {
            const int rows = 100_000;
            const int domains = 3;

            var pairs = Enumerable.Range(0, domains)
                .Select(i => new KeyValuePair<string, string>("Domain" + i, "member" + i))
                .ToList();

            var width = BlobchegRouterFormat.MaskWidthFor(domains);
            var layout = BlobchegRouterFormat.LayoutHash(pairs, width);

            var writer = BlobchegRouterWriter.Open(Scratch, "AdvVolumeRouter", domains, layout);
            for (var i = 0; i < rows; i++)
            {
                // Каждая третья строка пустая: дырки в маске обязаны выживать на объёме так же,
                // как на двух строках.
                writer.Append("row" + i, i % 3 == 0
                    ? Array.Empty<BlobchegRouterCell>()
                    : new[] { new BlobchegRouterCell(i % domains, (uint)(BlobchegFormat.HeaderSize + i * 16)) });
            }

            writer.Flush();

            var blob = new BlobchegRouterBlob(
                BlobchegBuffer.From(File.ReadAllBytes(writer.FilePath), Allocator.Persistent),
                "AdvVolumeRouter", domains, layout);

            try
            {
                Assert.That(blob.Count, Is.EqualTo(rows));

                for (var i = 0; i < rows; i++)
                {
                    var row = blob.Get(blob.IdAt((uint)i));
                    if (i % 3 == 0)
                    {
                        Assert.That(row.Mask, Is.EqualTo(0ul), $"строка {i} обязана остаться пустой");
                        continue;
                    }

                    Assert.That(row.Offset(i % domains), Is.EqualTo((uint)(BlobchegFormat.HeaderSize + i * 16)),
                        $"строка {i} отдала чужой оффсет");
                }

                Assert.Throws<InvalidOperationException>(() => blob.Get(blob.IdAt((uint)rows)));
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void Запись_в_мегабайты_переживает_круг()
        {
            const int megabytes = 2;

            var huge = Node<AdvHugeNodeSo>("Huge");
            huge.megabytes = megabytes;
            Dirty(huge);
            Node<AdvLooseNodeSo>("Small");

            Rebuild();

            var offset = (int)OffsetOf(huge, "IAdvLoose");
            var size = megabytes * 1024 * 1024;
            var file = Bytes("IAdvLoose");

            Assert.That(file.Length, Is.GreaterThanOrEqualTo(offset + size),
                "многомегабайтная запись обязана целиком лечь в файл");
            Assert.That(file[offset], Is.EqualTo((byte)0), "первый байт записи");
            Assert.That(file[offset + 4096], Is.EqualTo((byte)1), "и метка внутри неё");
            Assert.That(file[offset + size - 1], Is.EqualTo((byte)0xFE), "и последний её байт");
        }

        [Test]
        public void Ровно_одна_нода_адресуется_нулевой_строкой()
        {
            var only = Node<AdvColdOnlyNodeSo>("Only");
            only.tier = 77;
            Dirty(only);

            Rebuild();

            var id = IdOf(only, AdvRouter.RouterName);
            Assert.That(id.Index, Is.EqualTo(0u), "единственная нода — это строка ноль");
            Assert.That(id.IsValid, Is.True, "но её id при этом не ноль: строку ноль от «не назначен» отличает тег");

            var router = Router();
            var cold = Cold();
            try
            {
                Assert.That(router.Count, Is.EqualTo(1));
                Assert.That(cold.Read<AdvColdInfo>(router.GetCold(id)).Tier, Is.EqualTo(77));
                Assert.Throws<InvalidOperationException>(() => router.Get(router.IdAt(1)));
            }
            finally
            {
                router.Dispose();
                cold.Dispose();
            }
        }

        [Test]
        public void Сто_тысяч_чтений_подряд_стабильны()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            node.rpm = 4242;
            Dirty(node);
            Rebuild();

            var offset = OffsetOf(node, "IAdvCombat");
            var db = Combat();
            try
            {
                const int reads = 100_000;

                // Прогрев: первое чтение платит за JIT метода, и на сотне тысяч витков эта плата
                // размазалась бы по замеру.
                for (var i = 0; i < 1024; i++)
                {
                    if (db.Read<AdvGun>(offset).Rpm != 4242)
                        Assert.Fail("прогрев прочитал не то, что записано");
                }

                var wrong = 0;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < reads; i++)
                {
                    if (db.Read<AdvGun>(offset).Rpm != 4242)
                        wrong++;
                }

                watch.Stop();

                Assert.That(wrong, Is.Zero, "чтение — чистая реинтерпретация; состояния между вызовами у неё нет");

                // Не порог, а хронометраж: сколько стоит чтение в редакторе через сгенерированный
                // фасад, на файле настоящей пересборки. Снят до того, как в CheckRead ляжет
                // AtomicSafetyHandle, — чтобы потом было видно, чья цена. Разложение по слоям —
                // в Tests/BlobchegReadCostTests.
                UnityEngine.Debug.Log(
                    $"Blobcheg: {reads} чтений через фасад за {watch.Elapsed.TotalMilliseconds:F2} мс — " +
                    $"{watch.Elapsed.TotalMilliseconds * 1e6 / reads:F2} нс на чтение " +
                    $"(редактор, отладочный контур {(db.HasDebug ? "есть" : "нет")})");
            }
            finally
            {
                db.Dispose();
            }
        }
    }
}
