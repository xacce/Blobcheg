using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// Где патч ищет слот и находит ли. Обход полей — самая тихая часть фичи: не найденный слот не
    /// бросает и не логирует, он просто навсегда остаётся оффсетом, и узнают об этом на первом
    /// <c>Value</c> в джобе в билде.
    /// </summary>
    public sealed unsafe class LayoutAndBufferTests : PatchFixture
    {
        [Test]
        public void Слот_вторым_полем_после_невыровненного_байта()
        {
            var file = HotFile(ammo: 5f, rpm: 55);
            var hot = Raise(file);
            var offset = file["gun"];

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new PackedRef
            {
                Head = 0xAB,
                Gun = new BlobchegReference<PatchGun>(offset),
                Tail = 0xCD,
            });

            Patch();

            var packed = EM.GetComponentData<PackedRef>(entity);

            Assert.That(packed.Gun.Data.Value, Is.EqualTo(hot.AddressOf(offset)),
                "слот по байтовому оффсету 1 обязан быть найден: обход считает оффсеты полей, а не гадает по выравниванию");
            Assert.That(packed.Head, Is.EqualTo((byte)0xAB), "патч заехал левее слота");
            Assert.That(packed.Tail, Is.EqualTo((byte)0xCD), "патч заехал правее слота");
            Assert.That(Copy(packed.Gun.Value).Rpm, Is.EqualTo(55));
        }

        [Test]
        public void Невыровненный_слот_переживает_и_обратный_проход()
        {
            var file = HotFile();
            Raise(file);
            var offset = file["gun"];

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new PackedRef
            {
                Head = 1,
                Gun = new BlobchegReference<PatchGun>(offset),
                Tail = 2,
            });

            Patch();
            var bytes = Save();

            var loaded = LoadRaw(bytes);
            var packed = loaded.EntityManager.GetComponentData<PackedRef>(Single<PackedRef>(loaded));

            Assert.That(packed.Gun.Data.Value, Is.EqualTo(offset));
            Assert.That(packed.Head, Is.EqualTo((byte)1));
            Assert.That(packed.Tail, Is.EqualTo((byte)2));
        }

        [Test]
        public void Слот_на_второй_ступени_вложенности()
        {
            var file = HotFile(ammo: 6f, rpm: 66);
            var hot = Raise(file);
            var offset = file["gun"];

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new ShallowNestRef
            {
                Head = 7,
                Inner = new NestOne { A = 8, Gun = new BlobchegReference<PatchGun>(offset) },
            });

            Patch();

            var nested = EM.GetComponentData<ShallowNestRef>(entity);
            Assert.That(nested.Inner.Gun.Data.Value, Is.EqualTo(hot.AddressOf(offset)));
            Assert.That(nested.Head, Is.EqualTo(7));
            Assert.That(nested.Inner.A, Is.EqualTo(8));
        }

        [Test]
        public void Слот_на_третьей_ступени_вложенности()
        {
            var file = HotFile(ammo: 9f, rpm: 99);
            var hot = Raise(file);
            var offset = file["gun"];

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new DeepNestRef
            {
                Head = -1,
                Inner = new NestTwo
                {
                    S = 3,
                    Inner = new NestOne { A = 4, Gun = new BlobchegReference<PatchGun>(offset) },
                },
            });

            Patch();

            var deep = EM.GetComponentData<DeepNestRef>(entity);
            Assert.That(deep.Inner.Inner.Gun.Data.Value, Is.EqualTo(hot.AddressOf(offset)),
                "обход обязан быть рекурсивным, а не «поля первого уровня»");
            Assert.That(deep.Head, Is.EqualTo(-1));
            Assert.That(deep.Inner.S, Is.EqualTo((short)3));
            Assert.That(Copy(deep.Inner.Inner.Gun.Value).Rpm, Is.EqualTo(99));
        }

        [Test]
        public void Два_слота_разных_типов_записи_в_одном_компоненте_нельзя_перепутать()
        {
            var file = HotFile(ammo: 10f, rpm: 101, hp: 202f, plates: 4);
            var hot = Raise(file);

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new PairRef
            {
                Gun = new BlobchegReference<PatchGun>(file["gun"]),
                Armor = new BlobchegReference<PatchArmor>(file["armor"]),
            });

            Patch();

            var pair = EM.GetComponentData<PairRef>(entity);

            Assert.That(pair.Gun.Data.Value, Is.EqualTo(hot.AddressOf(file["gun"])));
            Assert.That(pair.Armor.Data.Value, Is.EqualTo(hot.AddressOf(file["armor"])));
            Assert.That(pair.Gun.Data.Value, Is.Not.EqualTo(pair.Armor.Data.Value),
                "два слота одного компонента получили один адрес — обход спутал их оффсеты");

            Assert.That(Copy(pair.Gun.Value).Rpm, Is.EqualTo(101));
            Assert.That(Copy(pair.Armor.Value).Plates, Is.EqualTo(4));
        }

        [Test]
        public void Буфер_из_трёх_элементов_патчится_поэлементно()
        {
            var file = Domain(nameof(IPatchHot))
                .Add("g0", new PatchGun { Ammo = 1f, Rpm = 1 })
                .Add("g1", new PatchGun { Ammo = 2f, Rpm = 2 })
                .Add("g2", new PatchGun { Ammo = 3f, Rpm = 3 })
                .Seal();

            var hot = Raise(file);

            var entity = EM.CreateEntity();
            var buffer = EM.AddBuffer<RefElement>(entity);
            for (var i = 0; i < 3; i++)
                buffer.Add(new RefElement { Gun = new BlobchegReference<PatchGun>(file["g" + i]), Marker = i });

            Patch();

            var patched = EM.GetBuffer<RefElement>(entity);
            for (var i = 0; i < 3; i++)
            {
                var element = patched[i];

                Assert.That(element.Gun.Data.Value, Is.EqualTo(hot.AddressOf(file["g" + i])),
                    $"элемент {i} обязан доехать до СВОЕЙ записи, а не до первой в буфере");
                Assert.That(Copy(element.Gun.Value).Rpm, Is.EqualTo(i + 1));
                Assert.That(element.Marker, Is.EqualTo(i));
            }
        }

        // BUG: одна битая ссылка в сцене делает мир незаписываемым
        // Что происходит: битый элемент патч отбил и оставил как был — ровно как обещано. Но при
        //   записи мира ОБРАТНЫЙ проход встречает то же самое число ещё раз, отбивает его тем же
        //   OutOfRange, и конец сериализации поднимает накопленный провал до исключения. Save()
        //   не возвращает байт вовсе: сцену с одной битой ссылкой нельзя сохранить, чтобы починить
        //   её потом.
        // Что должно (план, строка 31): «явная ошибка И состояние согласовано: после неё обратный
        //   проход возвращает нетронутым элементам их исходные оффсеты. Ни один элемент не остался
        //   сырым указателем, который уедет на диск адресом». То есть ошибка — на патче, а запись
        //   мира обязана пройти и вернуть в файл те же три числа, что в нём были.
        // Корневая причина: асимметрия строгости между двумя направлениями одного прохода.
        //   BlobchegBases.TryUnresolve отвечает OutOfRange на любое значение, которое не лежит ни в
        //   текущем поколении, ни в отставных, и при этом не меньше длины буфера, — не различая
        //   «протухший адрес, который сейчас утечёт на диск» и «плохой оффсет, которого патч уже
        //   касался и который адресом никогда не был». Второй случай безопасен по построению:
        //   значение не попадает ни в один известный реестру диапазон, значит указателем оно быть
        //   не может, и оставить его как есть — точный round-trip. Ради первого случая (план,
        //   строка 37) эта строгость не нужна: снятый с учёта буфер уходит в отставные поколения, и
        //   его адрес сворачивается в оффсет штатно — тест
        //   Сохранение_после_снятия_домена_с_учёта_обязано_отбиться именно так и проходит.
        //   Наверху же SerializeUtility.SerializeWorldInternal делает провал обратного прохода
        //   фатальным для всей записи, тогда как прямой проход тот же провал считает бедой одного
        //   слота и метёт дальше.
        [Test]
        public void Битый_элемент_в_середине_буфера_не_оставляет_соседей_наполовину_патченными()
        {
            var file = HotFile();
            var hot = Raise(file);
            var good = file["gun"];
            var bad = (uint)hot.Length + BlobchegFormat.RecordAlign;

            var entity = EM.CreateEntity();
            var buffer = EM.AddBuffer<RefElement>(entity);
            buffer.Add(new RefElement { Gun = new BlobchegReference<PatchGun>(good), Marker = 0 });
            buffer.Add(new RefElement { Gun = new BlobchegReference<PatchGun>(bad), Marker = 1 });
            buffer.Add(new RefElement { Gun = new BlobchegReference<PatchGun>(good), Marker = 2 });

            Assert.Throws<InvalidOperationException>(() => Patch(), "битый элемент обязан быть ошибкой");

            var patched = EM.GetBuffer<RefElement>(entity);

            Assert.That(patched[0].Gun.Data.Value, Is.EqualTo(hot.AddressOf(good)));
            Assert.That(patched[2].Gun.Data.Value, Is.EqualTo(hot.AddressOf(good)),
                "элемент ПОСЛЕ битого обязан быть обработан: провал одного не имеет права глотать остальные");
            Assert.That(patched[1].Gun.Data.Value, Is.EqualTo(bad),
                "битый элемент обязан остаться тем числом, что в нём было, а не превратиться в дикий адрес");

            // И состояние обязано быть согласованным: обратный проход по этому буферу не выдаёт
            // наружу ни одного адреса процесса.
            var bytes = Save();
            BlobchegPatchErrors.Clear();

            Assert.That(Contains(bytes, hot.AddressOf(good)), Is.False,
                "в файл уехал адрес процесса — полупропатченный буфер утёк на диск");

            var loaded = LoadRaw(bytes);
            var stored = loaded.EntityManager.GetBuffer<RefElement>(SingleBuffer<RefElement>(loaded));

            Assert.That(stored[0].Gun.Data.Value, Is.EqualTo(good));
            Assert.That(stored[1].Gun.Data.Value, Is.EqualTo(bad));
            Assert.That(stored[2].Gun.Data.Value, Is.EqualTo(good));
        }

        [Test]
        public void Буфер_на_сто_тысяч_элементов_патчится_целиком()
        {
            const int count = 100_000;

            var file = HotFile();
            var hot = Raise(file);
            var offset = file["gun"];

            var entity = EM.CreateEntity();
            var buffer = EM.AddBuffer<RefElement>(entity);
            buffer.EnsureCapacity(count);
            for (var i = 0; i < count; i++)
                buffer.Add(new RefElement { Gun = new BlobchegReference<PatchGun>(offset), Marker = i });

            Patch();

            var patched = EM.GetBuffer<RefElement>(entity);
            Assert.That(patched.Length, Is.EqualTo(count));

            foreach (var index in new[] { 0, 1, count / 2, count - 2, count - 1 })
            {
                Assert.That(patched[index].Gun.Data.Value, Is.EqualTo(hot.AddressOf(offset)),
                    $"элемент {index} остался непропатченным");
                Assert.That(patched[index].Marker, Is.EqualTo(index));
            }
        }

        [Test]
        public void Десять_тысяч_сущностей_со_слотом_патчатся_за_один_проход()
        {
            const int count = 10_000;

            var file = HotFile(ammo: 12f, rpm: 121);
            var hot = Raise(file);
            var offset = file["gun"];

            var archetype = EM.CreateArchetype(ComponentType.ReadWrite<GunRef>());
            var entities = EM.CreateEntity(archetype, count, Allocator.Temp);
            foreach (var entity in entities)
                EM.SetComponentData(entity, new GunRef { Gun = new BlobchegReference<PatchGun>(offset) });

            Patch();

            var address = hot.AddressOf(offset);
            var wrong = 0;
            foreach (var entity in entities)
                if (EM.GetComponentData<GunRef>(entity).Gun.Data.Value != address)
                    wrong++;

            entities.Dispose();

            Assert.That(wrong, Is.Zero, "патч обязан накрыть все чанки архетипа, а не первый");
        }

        [Test]
        public void Слот_в_компоненте_отключённой_сущности_тоже_патчится()
        {
            var file = HotFile();
            var hot = Raise(file);
            var offset = file["gun"];

            var entity = Gun(offset);
            EM.SetEnabled(entity, false);

            Patch();

            // Отключённая сущность доедет до включения уже в игре, и оффсет в ней к тому моменту
            // должен быть адресом: второго патча не будет.
            Assert.That(EM.GetComponentData<GunRef>(entity).Gun.Data.Value, Is.EqualTo(hot.AddressOf(offset)));
        }
    }
}
