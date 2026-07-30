using System;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// Сценарии, которых не бывает. Именно они и вскрывают неявные допущения: там, где разумный
    /// разработчик не пройдёт, допущение никто не проверял.
    /// </summary>
    public sealed unsafe class AbsurdTests : PatchFixture
    {
        [Test]
        public void Патч_не_имеет_права_тронуть_ни_байта_внутри_самой_базы()
        {
            // В ФАЙЛЕ лежит запись, которая сама состоит из ссылки. Если патч ходит не только по
            // памяти компонентов, но и по содержимому записей, он испортит базу — и испортит её
            // для всех, кто читает старым путём.
            var file = Domain(nameof(IPatchHot))
                .Add("gun", new PatchGun { Ammo = 1f, Rpm = 1 })
                .Add("holder", new PatchRefRecord
                {
                    Inner = new BlobchegReference<PatchGun>(BlobchegFormat.HeaderSize),
                    Tag = 0x0BAD_F00D,
                })
                .Seal();

            var hot = Raise(file);

            var before = new byte[hot.Length];
            fixed (byte* dst = before)
                UnsafeUtility.MemCpy(dst, (byte*)hot.Ptr, hot.Length);

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new RecordRef
            {
                Record = new BlobchegReference<PatchRefRecord>(file["holder"]),
            });

            Patch();
            Save();

            var after = new byte[hot.Length];
            fixed (byte* dst = after)
                UnsafeUtility.MemCpy(dst, (byte*)hot.Ptr, hot.Length);

            CollectionAssert.AreEqual(before, after,
                "патч изменил байты самой базы: он обязан ходить по памяти компонентов, а содержимое " +
                "записей — вопрос доверия, как и у любого другого чтения");

            // И слот компонента при этом пропатчен, а вложенная в запись ссылка — нет.
            Assert.That(EM.GetComponentData<RecordRef>(entity).Record.Data.Value,
                Is.EqualTo(hot.AddressOf(file["holder"])));

            var record = Copy(EM.GetComponentData<RecordRef>(entity).Record.Value);
            Assert.That(record.Tag, Is.EqualTo(0x0BAD_F00D));
            Assert.That(record.Inner.Data.Value, Is.EqualTo((ulong)BlobchegFormat.HeaderSize),
                "ссылка ВНУТРИ записи так и осталась оффсетом — патч в базу не лезет");
        }

        [Test]
        public void База_зарегистрированная_по_адресу_чужой_записи_отвечает_детерминированно()
        {
            var file = HotFile();
            var hot = Raise(file);
            var entity = Gun(file["gun"]);

            Patch();
            var address = SlotOf(entity);

            // Абсурд: регистрируем ВТОРОЙ домен по адресу записи внутри первого. Реестр про
            // пересечение буферов ничего не знает — вопрос в том, кто теперь владелец адреса.
            var parasite = BlobchegNaming.NameHash("IPatchParasite");
            BlobchegBases.Register(parasite, (byte*)address, BlobchegFormat.HeaderSize * 2);

            try
            {
                // Слот несёт СВОЙ домен, поэтому свёртка обязана считаться от своей базы, а не от
                // того, кто зарегистрировался последним.
                Assert.That(BlobchegBases.TryUnresolve(hot.Key, address, out var mine),
                    Is.EqualTo(BlobchegRebase.Patched));
                Assert.That(mine, Is.EqualTo((ulong)file["gun"]),
                    "оффсет обязан считаться от базы своего домена, а не от последнего зарегистрированного");

                Assert.That(BlobchegBases.TryUnresolve(parasite, address, out var theirs),
                    Is.EqualTo(BlobchegRebase.Patched));
                Assert.That(theirs, Is.Zero, "для паразита тот же адрес — начало его собственного буфера");

                // Обещание при этом держится: обратный проход мира отдаёт оффсет своей базы.
                var bytes = Save();
                Assert.That(Contains(bytes, address), Is.False);
            }
            finally
            {
                BlobchegBases.Unregister(parasite, (byte*)address);
            }
        }

        // План (строка 48) допускал ровно два исхода: «ЛИБО явный отказ при установке/патче, ЛИБО
        // патчится как всё остальное». Реализация выбрала первый. Обход типов теперь берёт в
        // кандидаты и ISharedComponentData — не чтобы регистрировать, а чтобы заметить в нём слот:
        // тип не регистрируется, а беда уходит строкой в BlobchegPatchTableBuilder.Diagnostics и
        // оттуда в Debug.LogError на установке патча. То есть отказ звучит именно «при установке».
        //
        // Недопустимая середина плана — «молча пропущен и затем сериализован адресом процесса» —
        // при этом закрыта с двух сторон: раз слот не патчится вовсе, адресу процесса неоткуда
        // взяться в файле, и обратный проход тут ничего не портит.
        [Test]
        public void Слот_в_общем_компоненте_либо_патчится_либо_отбивается_вслух()
        {
            var file = HotFile();
            var hot = Raise(file);
            var offset = file["gun"];

            string complaint = null;
            foreach (var diagnostic in BlobchegPatchTableBuilder.Diagnostics)
                if (diagnostic.Contains(nameof(SharedRef)))
                    complaint = diagnostic;

            Assert.That(complaint, Is.Not.Null,
                "слот в общем компоненте обязан быть назван вслух на сборке таблицы: молчание здесь " +
                "означало бы, что разработчик узнает о непропатченном слоте первым Value — в джобе, " +
                "в билде, спустя недели");

            Assert.That(complaint, Does.Contain("BlobchegReference"),
                "и назвать, что именно в этом типе патчу недоступно");

            var entity = EM.CreateEntity();
            EM.AddSharedComponent(entity, new SharedRef { Gun = new BlobchegReference<PatchGun>(offset) });

            Patch();

            var shared = EM.GetSharedComponent<SharedRef>(entity);

            Assert.That(shared.Gun.Data.Value, Is.EqualTo((ulong)offset),
                "патч общие компоненты не обходит — и раз он об этом сказал, слот обязан остаться " +
                "ровно тем оффсетом, что в него положили");
            Assert.That(shared.Gun.IsResolved, Is.False,
                "и не врать, будто он разрешён");

            // Главное из плана: адрес процесса в файл не уезжает. Он туда и не может попасть —
            // патча не было, в слоте по-прежнему оффсет.
            var bytes = Save();
            Assert.That(Contains(bytes, hot.AddressOf(offset)), Is.False);
        }

        [Test]
        public void Один_чанк_с_двумя_поколениями_сразу()
        {
            // Ровно то, что даёт живой путь: часть сущностей чанка уже прошла патч, часть приехала
            // чейнджсетом сырой, а база между этими двумя событиями пересобралась.
            var first = HotFile(ammo: 1f, rpm: 11);
            Raise(first);
            var offset = first["gun"];

            var old = Gun(offset);
            Patch();

            var gen2 = Raise(HotFile(ammo: 2f, rpm: 22));
            var fresh = Gun(offset);

            Patch();

            Assert.That(SlotOf(old), Is.EqualTo(gen2.AddressOf(offset)),
                "старая сущность обязана переехать на новое поколение");
            Assert.That(SlotOf(fresh), Is.EqualTo(gen2.AddressOf(offset)),
                "новая — разрешиться в него же");

            Assert.That(Copy(EM.GetComponentData<GunRef>(old).Gun.Value).Rpm, Is.EqualTo(22));
            Assert.That(Copy(EM.GetComponentData<GunRef>(fresh).Gun.Value).Rpm, Is.EqualTo(22));
        }

        [Test]
        public void Адрес_поднятой_базы_положенный_в_слот_руками()
        {
            // Разработчик прочитал, что после патча в слоте лежит адрес, и решил положить его туда
            // сам — на бейке, из значения, добытого в редакторе. В файл такой мир уехать не должен
            // никак: адрес процесса не переживает даже перезапуск редактора.
            var file = HotFile();
            var hot = Raise(file);
            var address = hot.AddressOf(file["gun"]);

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new GunRef
            {
                Gun = new BlobchegReference<PatchGun> { Data = new BlobchegReferenceData { Value = address } },
            });

            Assert.DoesNotThrow(() => Patch(), "адрес живой базы в слоте — уже валидное состояние, патч его не трогает");
            Assert.That(SlotOf(entity), Is.EqualTo(address));

            var bytes = Save();
            Assert.That(Contains(bytes, address), Is.False,
                "и в файл он всё равно обязан уехать оффсетом");
        }

        [Test]
        public void Мир_с_сущностью_на_каждый_байт_записи()
        {
            // Абсурд по постановке: сущности со ссылками на КАЖДЫЙ байт записи, включая середину.
            // Проверяется не смысл, а то, что ни один из них не даёт недетерминированного ответа.
            //
            // План (строка 10, «Оффсет мимо выравнивания обязан отбиться») требует от каждого
            // невыровненного оффсета явной ошибки — значит из восьми байт записи адресом имеет
            // право стать ровно один, её собственное начало. Реализация так и отвечает: BadOffset
            // на семи остальных. Детерминированность от этого не страдает, а усиливается:
            // единственный принятый ответ — единственный законный.
            var file = HotFile();
            var hot = Raise(file);
            var start = file["gun"];

            Assume.That(start % BlobchegFormat.RecordAlign, Is.Zero,
                "начало записи не выровнено — тест проверяет не ту границу");

            // Выровненная сущность создаётся ПОСЛЕДНЕЙ: если бы провал первого же байта глотал
            // остальных, она осталась бы непропатченной, и это было бы видно.
            var broken = new Entity[8];
            for (var i = 1u; i < 8; i++)
                broken[i] = Gun(start + i);

            var whole = Gun(start);

            var error = Assert.Throws<InvalidOperationException>(() => Patch(),
                "семь байт из восьми — не начало записи, и каждый обязан отбиться");

            Assert.That(error.Message, Does.Contain(nameof(GunRef)));

            Assert.That(SlotOf(whole), Is.EqualTo(hot.AddressOf(start)),
                "выровненное начало записи — единственный из восьми, кто обязан пройти, и провал " +
                "соседей не имеет права его проглотить");

            for (var i = 1u; i < 8; i++)
                Assert.That(SlotOf(broken[i]), Is.EqualTo((ulong)(start + i)),
                    $"байт {i} отбит — слот обязан остаться тем числом, что в нём было, а не " +
                    "превратиться в адрес середины записи");
        }

        [Test]
        public void Патч_мира_без_единого_поднятого_домена_и_без_ссылок()
        {
            // Ни базы, ни ссылок, ни сущностей — и всё равно ни одного исключения: живой путь
            // зовётся на КАЖДЫЙ применённый чейнджсет, в том числе в проектах без Blobcheg вовсе.
            Assert.DoesNotThrow(() => Patch());
            Assert.That(BlobchegPatchErrors.HasAny, Is.False);

            Assert.DoesNotThrow(() => Save());
            Assert.That(BlobchegPatchErrors.HasAny, Is.False);
        }

        [Test]
        public void Ссылка_на_запись_прямо_поверх_отладочного_контура()
        {
            // Ещё один адрес, которого не бывает: оффсет самой debug-секции. Он выровнен, он за
            // header'ом и он внутри буфера — по границам от записи неотличим. Отбить его может
            // только сам контур, и план (строка 19) ровно этого и требует: «в границах — отбой по
            // типу записи (отладочный контур). Никогда — молчаливое чтение чужих байт».
            //
            // Реализация теперь так и делает: патч, получив адрес, спрашивает контур, начинается ли
            // по нему запись объявленного типа, и валится кодом WrongRecord. Проверка, которая
            // раньше жила только на старом пути (BlobchegBlob.Read), доехала до нового.
            var file = HotFile();
            var hot = Raise(file);
            Assert.That(hot.Blob.HasDebug, Is.True, "контур не записан — тест проверяет не то");

            var contour = BlobchegFormat.AlignUp((uint)hot.Length - 1);
            if (contour >= (uint)hot.Length)
                contour = BlobchegFormat.AlignUp(file["gun"] + 16);

            Assume.That(contour, Is.LessThan((uint)hot.Length));

            Gun(contour);

            var error = Assert.Throws<InvalidOperationException>(() => Patch(),
                "по этому оффсету записи нет, и патч обязан это увидеть");

            Assert.That(error.Message, Does.Contain(nameof(GunRef)));

            // Старый путь на том же адресе отказывается ровно так же — оба пути говорят одно.
            Assert.Throws<InvalidOperationException>(() => Copy(hot.Blob.Read<PatchGun>(contour)),
                "Read знает, что записи по этому оффсету нет");
        }
    }
}
