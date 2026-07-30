using NUnit.Framework;

namespace Blobcheg.Tests
{
    /// <summary>
    /// Счётчик пересборок файла — то, по чему поднявший базу узнаёт, что его файл переписали.
    /// Набор маленький, потому что обещаний ровно три: незнакомый файл — ноль; спросивший получает
    /// «да» один раз; чужой файл на чужой вопрос не отвечает.
    /// </summary>
    public sealed class BlobchegFileVersionsTests
    {
        [Test]
        public void Файл_который_никто_не_переписывал_остаётся_нулём()
        {
            var seen = 0;

            Assert.That(BlobchegFileVersions.Of("нетронутый.bcheg"), Is.EqualTo(0));
            Assert.That(BlobchegFileVersions.Changed("нетронутый.bcheg", ref seen), Is.False);
        }

        [Test]
        public void Спросивший_получает_да_ровно_один_раз()
        {
            var file = "однажды-" + TestContext.CurrentContext.Test.ID + ".bcheg";
            var seen = BlobchegFileVersions.Of(file);

            BlobchegFileVersions.Bump(file);

            Assert.That(BlobchegFileVersions.Changed(file, ref seen), Is.True, "файл переписан");
            Assert.That(BlobchegFileVersions.Changed(file, ref seen), Is.False,
                "второй раз подряд — уже нет: иначе база перечитывалась бы каждый кадр");
        }

        [Test]
        public void Пересборка_соседнего_файла_чужую_базу_не_будит()
        {
            var mine = "моя-" + TestContext.CurrentContext.Test.ID + ".bcheg";
            var other = "чужая-" + TestContext.CurrentContext.Test.ID + ".bcheg";
            var seen = BlobchegFileVersions.Of(mine);

            BlobchegFileVersions.Bump(other);

            Assert.That(BlobchegFileVersions.Changed(mine, ref seen), Is.False,
                "перечитывать базу из-за того, что переписали соседнюю, — это лишнее переселение слотов на ровном месте");
        }
    }
}
