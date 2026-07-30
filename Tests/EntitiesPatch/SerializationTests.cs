using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// Обещание «на диск едет оффсет, а не адрес процесса». Косвенной проверки тут мало: мир,
    /// прочитанный в том же процессе, поднимется и на файле с адресом внутри, если базе повезёт
    /// лечь по тому же адресу. Поэтому основной инструмент раздела — поиск восьмибайтового слова в
    /// сыром потоке.
    /// </summary>
    public sealed unsafe class SerializationTests : PatchFixture
    {
        [Test]
        public void Сохранённый_мир_содержит_оффсет_а_не_адрес_процесса()
        {
            var file = HotFile(ammo: 21f, rpm: 210);
            var hot = Raise(file);
            var offset = file["gun"];
            var entity = Gun(offset);

            Patch();
            var address = SlotOf(entity);
            Assert.That(address, Is.EqualTo(hot.AddressOf(offset)));

            var bytes = Save();

            Assert.That(Contains(bytes, address), Is.False,
                "в потоке нашёлся адрес процесса — он бессмыслен уже в следующем запуске игры");

            // И положительная половина: чтение в мир, где база лежит по ДРУГОМУ адресу. Новый
            // буфер поднимается ДО освобождения старого — иначе аллокатор вернёт тот же адрес и
            // проверка «оффсет пережил переезд» ничего не докажет.
            var moved = Raise(HotFile(ammo: 21f, rpm: 210));
            Drop(hot);
            Assert.That(moved.Ptr, Is.Not.EqualTo(hot.Ptr), "новый буфер лёг по тому же адресу — тест бессмыслен");

            var loaded = Load(bytes);
            var slot = SlotOf(loaded, Single<GunRef>(loaded));

            Assert.That(slot, Is.EqualTo(moved.AddressOf(offset)));
            Assert.That(
                Copy(loaded.EntityManager.GetComponentData<GunRef>(Single<GunRef>(loaded)).Gun.Value).Rpm,
                Is.EqualTo(210));
        }

        [Test]
        public void После_сохранения_живой_мир_остаётся_патченным()
        {
            var file = HotFile();
            var hot = Raise(file);
            var entity = Gun(file["gun"]);

            Patch();
            Save();

            Assert.That(SlotOf(entity), Is.EqualTo(hot.AddressOf(file["gun"])),
                "обратный проход обязан идти по копии чанка: живой мир после записи остаётся рабочим");
            Assert.That(EM.GetComponentData<GunRef>(entity).Gun.IsResolved, Is.True);
            Assert.That(Copy(EM.GetComponentData<GunRef>(entity).Gun.Value).Rpm, Is.EqualTo(600));
        }

        [Test]
        public void Мир_который_никогда_не_патчили_сохраняется_и_читается_верно()
        {
            var file = HotFile(ammo: 31f, rpm: 310);
            var hot = Raise(file);
            Gun(file["gun"]);

            // Ни одного Patch: сущности собрали руками и сразу пишем.
            var bytes = Save();

            var loaded = Load(bytes);
            Assert.That(SlotOf(loaded, Single<GunRef>(loaded)), Is.EqualTo(hot.AddressOf(file["gun"])),
                "патч чтения обязан разобраться и с миром, который до записи никто не патчил");
            Assert.That(
                Copy(loaded.EntityManager.GetComponentData<GunRef>(Single<GunRef>(loaded)).Gun.Value).Rpm,
                Is.EqualTo(310));
        }

        // BUG: запись мира со снятым доменом кладёт в файл адрес процесса
        // Что происходит: домен сняли с учёта (база пересобирается, буфер уже освобождён), а мир в
        //   этот момент пишут. TryUnresolve возвращает DomainNotRaised и оставляет значение как
        //   есть — то есть указатель прошлого запуска уезжает в файл. Провал кладётся в ящик, но
        //   файл к этому моменту уже собран и записан.
        // Что должно: запись обязана отбиться до того, как в поток попадёт хоть один адрес.
        //   Оффсета в этом состоянии не существует, и подставить вместо него нечего.
        // Корневая причина: BlobchegPatchRunner.PatchElements при провале только зовёт
        //   BlobchegPatchErrors.Report и продолжает, а SerializeUtility.WriteChunks ни разу не
        //   спрашивает BlobchegPatchErrors.HasAny. Ящик разбирает только BlobchegLiveSweep.Run и
        //   BlobchegPatchErrorSystem — оба на пути ЧТЕНИЯ. У пути записи разбора нет вовсе.
        [Test]
        public void Сохранение_после_снятия_домена_с_учёта_обязано_отбиться()
        {
            var file = HotFile();
            var hot = Raise(file);
            var entity = Gun(file["gun"]);

            Patch();
            var address = SlotOf(entity);

            Drop(hot);

            var bytes = Save();
            BlobchegPatchErrors.Clear();

            Assert.That(Contains(bytes, address), Is.False,
                "в файл уехал адрес процесса: домен снят, свернуть адрес не во что, и записывать было нельзя");
        }

        [Test]
        public void Мир_сохранённый_в_одном_поколении_читается_в_другом()
        {
            var first = HotFile(ammo: 1f, rpm: 11);
            var gen1 = Raise(first);
            Gun(first["gun"]);

            Patch();
            var bytes = Save();

            var gen2 = Raise(HotFile(ammo: 2f, rpm: 22));
            Drop(gen1);
            Assert.That(gen2.Ptr, Is.Not.EqualTo(gen1.Ptr));

            var loaded = Load(bytes);

            Assert.That(SlotOf(loaded, Single<GunRef>(loaded)), Is.EqualTo(gen2.AddressOf(first["gun"])),
                "сохранённый оффсет обязан подняться на том поколении, которое стоит сейчас");
            Assert.That(
                Copy(loaded.EntityManager.GetComponentData<GunRef>(Single<GunRef>(loaded)).Gun.Value).Rpm,
                Is.EqualTo(22));
        }

        [Test]
        public void Чтение_мира_без_поднятой_базы_обязано_отбиться_а_не_оставить_оффсет_в_поле()
        {
            var file = HotFile();
            Raise(file);
            Gun(file["gun"]);

            Patch();
            var bytes = Save();

            var loaded = LoadRaw(bytes);
            var slot = SlotOf(loaded, Single<GunRef>(loaded));

            Assert.That(slot, Is.EqualTo(file["gun"]), "без базы патч чтения слот не трогает — в нём оффсет");

            // И это состояние обязано быть ВИДНО: сущность приехала раньше своей базы.
            Assert.That(
                loaded.EntityManager.GetComponentData<GunRef>(Single<GunRef>(loaded)).Gun.IsResolved, Is.False);

            // Патч чтения провал в ящик кладёт, а бросает его отдельная система бут-группы — тест
            // делает то же самое руками.
            var world = loaded;
            var e = Single<GunRef>(world);
            Assert.That(world.EntityManager.GetComponentData<GunRef>(e).Gun.IsSet, Is.True,
                "слот назначен, но не разрешён — IsSet и IsResolved обязаны отвечать разное");
        }

        [Test]
        public void Склонированная_сущность_несёт_разрешённый_указатель_но_на_диск_уезжает_оффсет()
        {
            const int clones = 100;

            var file = HotFile();
            var hot = Raise(file);
            var offset = file["gun"];
            var source = Gun(offset);

            Patch();

            // Клон получает УЖЕ разрешённый указатель, минуя патч: Instantiate копирует байты
            // компонента как есть.
            var copies = EM.Instantiate(source, clones, Allocator.Temp);
            foreach (var clone in copies)
                Assert.That(EM.GetComponentData<GunRef>(clone).Gun.Data.Value, Is.EqualTo(hot.AddressOf(offset)));

            copies.Dispose();

            var bytes = Save();

            Assert.That(Contains(bytes, hot.AddressOf(offset)), Is.False,
                "обратный проход обязан ходить по всем сущностям, а не по тем, кого патчил сам");

            var loaded = LoadRaw(bytes);
            var query = loaded.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GunRef>());
            var all = query.ToEntityArray(Allocator.Temp);

            Assert.That(all.Length, Is.EqualTo(clones + 1));

            var wrong = 0;
            foreach (var entity in all)
                if (loaded.EntityManager.GetComponentData<GunRef>(entity).Gun.Data.Value != offset)
                    wrong++;

            all.Dispose();
            Assert.That(wrong, Is.Zero, "у клонов в файле обязан лежать тот же оффсет, что и у оригинала");
        }

        [Test]
        public void Круг_запись_чтение_запись_не_двигает_оффсет()
        {
            var file = HotFile();
            Raise(file);
            var offset = file["gun"];
            Gun(offset);

            Patch();

            var first = Save();
            var loaded = Load(first);
            Patch(loaded);

            var second = Save(loaded);
            var again = LoadRaw(second);

            Assert.That(SlotOf(again, Single<GunRef>(again)), Is.EqualTo(offset),
                "круг «записали, прочитали, записали» обязан быть тождеством для оффсета");
        }
    }
}
