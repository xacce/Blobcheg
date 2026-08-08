using System;
using NUnit.Framework;
using Unity.Collections;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// Порча файла. Целостность — единственная проверка пакета, которая работает ВСЕГДА, без
    /// дефайнов и в билде; здесь её ломают всеми способами, какими ломается файл на диске: обрезали,
    /// дописали, перевернули байт, подменили заголовок, соврали в прологе роутера.
    ///
    /// Часть тестов перепечатывает header (<c>Reseal</c>) намеренно: без этого первым срабатывал бы
    /// хеш содержимого, и проверялось бы не то, что задумано.
    /// </summary>
    public sealed class CorruptionTests : AdvancedFixture
    {
        void Baked()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            node.rpm = 555;
            Dirty(node);
            Node<AdvColdOnlyNodeSo>("Cold");
            Node<AdvArmorNodeSo>("Armor");
            Rebuild();
        }

        static void RefusedAsBase(byte[] file, string because)
        {
            var buffer = BlobchegBuffer.From(file, Allocator.Persistent);
            try
            {
                Assert.Throws<InvalidOperationException>(() => { _ = new AdvCombatDb(buffer); }, because);
            }
            finally
            {
                // Освобождает ровно один раз в обоих исходах: конструктор, который бросил, владения
                // не забрал, а конструктор, который прошёл, сидит на этой же памяти.
                buffer.Dispose();
            }
        }

        /// <summary>
        /// Отказ, у которого есть срок годности. Расхождение с длиной из header'а — единственная
        /// порча, которая на живом диске значит не «сломано», а «ещё пишется»: длину читатель узнаёт
        /// до тела, и между этими двумя чтениями пересборка успевает подменить файл. Тип отказа
        /// обязан это различать — на нём стоит вся разница между варнингом и красным в редакторе.
        /// </summary>
        static void RefusedAsBaseTransiently(byte[] file, string because)
        {
            var buffer = BlobchegBuffer.From(file, Allocator.Persistent);
            try
            {
                Assert.Throws<BlobchegTransientException>(() => { _ = new AdvCombatDb(buffer); }, because);
            }
            finally
            {
                // Освобождает ровно один раз в обоих исходах: конструктор, который бросил, владения
                // не забрал, а конструктор, который прошёл, сидит на этой же памяти.
                buffer.Dispose();
            }
        }

        static void RefusedAsRouter(byte[] file, string because)
        {
            var buffer = BlobchegBuffer.From(file, Allocator.Persistent);
            try
            {
                Assert.Throws<InvalidOperationException>(() => { _ = new AdvRouter(buffer); }, because);
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [Test]
        public void Обрезанный_на_байт_файл_не_поднимается()
        {
            Baked();

            var file = Bytes("IAdvCombat");
            var cut = new byte[file.Length - 1];
            Array.Copy(file, cut, cut.Length);

            RefusedAsBaseTransiently(cut, "в header'е записана длина файла — обрезание видно сразу");
        }

        [Test]
        public void Дописанный_хвост_не_поднимается()
        {
            Baked();

            var file = Bytes("IAdvCombat");
            var grown = new byte[file.Length + 16];
            Array.Copy(file, grown, file.Length);

            RefusedAsBaseTransiently(grown, "дописанный хвост — тоже расхождение с длиной из header'а");
        }

        [Test]
        public void Перевёрнутый_байт_в_теле_не_поднимается()
        {
            Baked();

            var file = Bytes("IAdvCombat");
            file[BlobchegFormat.HeaderSize + 2] ^= 0xFF;

            RefusedAsBase(file, "целостность считается по всему телу и обязана поймать один байт");
        }

        [Test]
        public void Перевёрнутый_бит_в_теле_не_поднимается()
        {
            Baked();

            var file = Bytes("IAdvCombat");
            file[file.Length - 1] ^= 0x01;

            RefusedAsBase(file, "один бит в последнем байте — тот же случай, что и весь байт");
        }

        [Test]
        public void Испорченный_magic_не_поднимается()
        {
            Baked();

            // Перепечатывать header тут нельзя: Seal вернул бы magic на место. Хеш содержимого
            // считается только по телу файла, поэтому правка header'а его и не задевает.
            var file = Bytes("IAdvCombat");
            file[0] ^= 0xFF;

            RefusedAsBase(file, "не blobcheg-файл — это ошибка, а не попытка прочитать что получится");
        }

        [Test]
        public void Чужая_версия_формата_не_поднимается()
        {
            Baked();

            var file = Bytes("IAdvCombat");
            BlobchegBytes.WriteU16(file, 4, (ushort)(BlobchegFormat.Version + 7));

            RefusedAsBase(file, "читатель не понимает чужую версию и не имеет права догадываться");
        }

        [Test]
        public void Случайный_мусор_нужной_длины_не_поднимается()
        {
            Baked();

            var file = Bytes("IAdvCombat");
            var garbage = new byte[file.Length];
            var random = new System.Random(20260728);
            random.NextBytes(garbage);

            RefusedAsBase(garbage, "мусор обязан быть отбит, а не уронить процесс чтением куда попало");
        }

        [Test]
        public void Файл_роутера_не_поднимается_как_база()
        {
            Baked();

            RefusedAsBase(Bytes("AdvRouter"),
                "перепутанные файлы — самый дешёвый способ прочитать одно вместо другого");
        }

        [Test]
        public void Файл_базы_не_поднимается_как_роутер()
        {
            Baked();

            RefusedAsRouter(Bytes("IAdvCombat"), "и в обратную сторону тоже");
        }

        [Test]
        public void Флаг_роутера_в_файле_базы_не_поднимается()
        {
            Baked();

            var file = Bytes("IAdvCombat");
            BlobchegBytes.WriteU16(file, 6, (ushort)(BlobchegBytes.ReadU16(file, 6) | BlobchegFormat.FlagRouter));

            RefusedAsBase(file, "флаг соврал про род файла — это ошибка подъёма");
        }

        [Test]
        public void Debug_секция_за_концом_файла_не_поднимается()
        {
            Baked();

            var file = Bytes("IAdvCombat");
            BlobchegBytes.WriteU32(file, 12, (uint)file.Length);

            RefusedAsBase(file, "оффсет debug-секции обязан лежать внутри файла");
        }

        [Test]
        public void Роутер_с_подменённым_LayoutHash_не_поднимается()
        {
            Baked();

            var file = Bytes("AdvRouter");
            var at = BlobchegRouterFormat.PrologOffset + 8;
            BlobchegBytes.WriteU64(file, at, BlobchegBytes.ReadU64(file, at) ^ 0xFFFFFFFFFFFFFFFFUL);
            Reseal(file);

            RefusedAsRouter(file,
                "нумерация бит в файле и в кодогене разошлась — читать такой роутер значит читать не ту базу");
        }

        [Test]
        public void Роутер_с_чужим_числом_баз_не_поднимается()
        {
            Baked();

            var file = Bytes("AdvRouter");
            BlobchegBytes.WriteU32(file, BlobchegRouterFormat.PrologOffset + 4, (uint)(AdvRouter.DomainCount + 1));
            Reseal(file);

            RefusedAsRouter(file, "в файле одно число баз, в коде другое — маска означает разное");
        }

        [Test]
        public void Роутер_с_завышенным_числом_строк_не_поднимается()
        {
            Baked();

            var file = Bytes("AdvRouter");
            BlobchegBytes.WriteU32(file, BlobchegRouterFormat.PrologOffset + 0, 100000);
            Reseal(file);

            RefusedAsRouter(file, "строк больше, чем влезает в файл — Get увёл бы чтение в чужую память");
        }

        [Test]
        public void Роутер_с_прологом_мимо_файла_не_поднимается()
        {
            Baked();

            var file = Bytes("AdvRouter");
            BlobchegBytes.WriteU32(file, BlobchegRouterFormat.PrologOffset + 16, (uint)file.Length + 1024);
            Reseal(file);

            RefusedAsRouter(file, "массив масок указан за концом файла");
        }

        [Test]
        public void Роутер_с_прологом_внутрь_header_не_поднимается()
        {
            Baked();

            var file = Bytes("AdvRouter");
            BlobchegBytes.WriteU32(file, BlobchegRouterFormat.PrologOffset + 16, 8);
            Reseal(file);

            RefusedAsRouter(file, "маски не могут начинаться внутри header'а");
        }

        [Test]
        public void Целый_файл_после_перепечатки_header_поднимается()
        {
            Baked();

            // Контроль на сам инструмент теста: Reseal без правки тела обязан оставить файл рабочим.
            // Иначе все тесты выше зелены по неверной причине.
            var file = Bytes("IAdvCombat");
            Reseal(file);

            var buffer = BlobchegBuffer.From(file, Allocator.Persistent);
            var db = new AdvCombatDb(buffer);
            try
            {
                Assert.That(db.IsCreated, Is.True);
            }
            finally
            {
                db.Dispose();
            }
        }
    }
}
