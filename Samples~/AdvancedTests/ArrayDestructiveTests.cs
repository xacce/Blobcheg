using System;
using System.IO;
using NUnit.Framework;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// Массивы в записи: злоупотребления по PLAN-arrays.md. Всё — едиторным циклом, через ноды и
    /// пересборку: билдер снаружи пайплайна потребителю не выдаётся.
    /// </summary>
    public sealed class ArrayDestructiveTests : AdvancedFixture
    {
        [Test]
        public void Массив_на_миллион_элементов_собирается_и_читается()
        {
            var node = Node<AdvWeightsNodeSo>("Huge");
            node.count = 1_000_000;
            Dirty(node);
            Rebuild();

            var db = Loose();
            try
            {
                ref readonly var record = ref db.Read<AdvWeights>(OffsetOf(node, "IAdvLoose"));
                Assert.That(record.Weights.Length, Is.EqualTo(1_000_000));
                Assert.That(record.Weights[0], Is.EqualTo(0f));
                Assert.That(record.Weights[999_999], Is.EqualTo(999_999 * 0.5f),
                    "последний элемент обязан дожить до файла и вернуться");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Забытый_Allocate_читается_пустотой_а_не_мусором()
        {
            var node = Node<AdvForgottenAllocateNodeSo>("Forgotten");
            Rebuild();

            var db = Loose();
            try
            {
                ref readonly var record = ref db.Read<AdvWeights>(OffsetOf(node, "IAdvLoose"));
                Assert.That(record.Rolls, Is.EqualTo(9), "заполненные поля головы доехали");
                Assert.That(record.Weights.IsEmpty, Is.True, "незаполненное поле-массив — это пустота");
                Assert.That(record.Weights.Length, Is.Zero);
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Литерал_с_массивом_на_глубине_отбивается()
        {
            Node<AdvArrayLiteralNodeSo>("DeepLiteral");

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild());
            StringAssert.Contains("Begin", thrown.Message,
                "отказ обязан назвать правильную форму, даже когда массив спрятан на второй ступени");
        }

        [Test]
        public void Окно_массива_после_End_бросает_а_не_пишет_в_освобождённое()
        {
            Node<AdvLateWindowNodeSo>("LateWindow");

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild());
            StringAssert.Contains("End", thrown.Message);
            StringAssert.Contains("LateWindow", thrown.Message, "ошибка обязана назвать ноду");
        }

        [Test]
        public void Упавший_посреди_массива_Write_доносит_свою_ошибку()
        {
            var node = Node<AdvThrowingBuilderNodeSo>("Thrower");

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild());
            StringAssert.Contains(AdvThrowingBuilderNodeSo.Cry, thrown.Message,
                "до человека обязана доехать ошибка ноды, а не жалоба на незакрытый билдер");

            // Порча состояния не пережила падение: без виновника пересборка снова живая.
            Kill(node);
            Assert.DoesNotThrow(() => Rebuild());
        }

        [Test]
        public void Поле_чужого_билдера_отбивается()
        {
            Node<AdvCrossBuilderNodeSo>("Cross");

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild());
            StringAssert.Contains("не из этой записи", thrown.Message);
        }

        [Test]
        public void Дерево_на_рекурсивном_типе_элемента_строится_и_читается()
        {
            var node = Node<AdvTreeNodeSo>("Tree");
            Rebuild();

            var db = Loose();
            try
            {
                ref readonly var tree = ref db.Read<AdvTree>(OffsetOf(node, "IAdvLoose"));
                Assert.That(tree.Roots.Length, Is.EqualTo(2));
                Assert.That(tree.Roots[0].Value, Is.EqualTo(1));
                Assert.That(tree.Roots[1].Value, Is.EqualTo(2));
                Assert.That(tree.Roots[1].Children.IsEmpty, Is.True, "лист без Allocate — пустота");
                Assert.That(tree.Roots[0].Children.Length, Is.EqualTo(2));
                Assert.That(tree.Roots[0].Children[0].Value, Is.EqualTo(11));
                Assert.That(tree.Roots[0].Children[1].Value, Is.EqualTo(12));
                Assert.That(tree.Roots[0].Children[1].Children[0].Value, Is.EqualTo(121),
                    "третий уровень — оффсет меряется от поля своего собственного элемента");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Десять_правок_длины_держат_файл_и_чужие_адреса()
        {
            var neighbour = Node<AdvLooseNodeSo>("Neighbour");
            var victim = Node<AdvWeightsNodeSo>("Victim");
            Dirty(victim);
            Rebuild();
            var neighbourAt = OffsetOf(neighbour, "IAdvLoose");

            var lengths = new long[10];
            for (var edit = 0; edit < 10; edit++)
            {
                victim.count = edit % 2 == 0 ? 40 : 3;
                Dirty(victim);
                Rebuild();

                Assert.That(OffsetOf(neighbour, "IAdvLoose"), Is.EqualTo(neighbourAt),
                    "правка чужой длины не имеет права двигать соседа");
                lengths[edit] = new FileInfo(FileOf("IAdvLoose")).Length;
            }

            for (var i = 4; i < lengths.Length; i++)
                Assert.That(lengths[i], Is.EqualTo(lengths[i - 2]),
                    "файл обязан выйти на устойчивый цикл, а не расти с каждой правкой");
        }
    }
}
