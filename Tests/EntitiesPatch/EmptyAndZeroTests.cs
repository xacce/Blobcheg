using System;
using NUnit.Framework;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// Пустое и нулевое. Главный вопрос раздела — не путает ли патч «не назначено» с «запись по
    /// нулевому адресу»: у оффсета ноль значил бы начало header'а, у адреса — нулевой указатель.
    /// </summary>
    public sealed unsafe class EmptyAndZeroTests : PatchFixture
    {
        [Test]
        public void Ноль_в_слоте_это_не_запись_по_нулевому_адресу()
        {
            var hot = Raise(HotFile());

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new GunRef());

            Patch();

            var slot = EM.GetComponentData<GunRef>(entity).Gun;

            Assert.That(slot.Data.Value, Is.Zero,
                "ноль обязан остаться нулём: адрес «база плюс ноль» — это начало header'а, а не запись");
            Assert.That(slot.Data.Value, Is.Not.EqualTo(hot.Ptr));
            Assert.That(slot.IsSet, Is.False);
            Assert.That(slot.IsResolved, Is.False);

            Assert.Throws<InvalidOperationException>(() => Copy(slot.Value),
                "чтение неназначенной ссылки — ошибка, а не нулевая структура");
        }

        [Test]
        public void Патч_мира_без_единой_ссылки_не_бросает_и_ничего_не_трогает()
        {
            Raise(HotFile());

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new PlainData { Value = 4242 });

            Assert.DoesNotThrow(() => Patch(), "мир без слотов патчу неинтересен");
            Assert.That(EM.GetComponentData<PlainData>(entity).Value, Is.EqualTo(4242),
                "компонент без слотов патч не имеет права трогать");
        }

        [Test]
        public void Буфер_нулевой_длины_патчится_без_единого_касания()
        {
            Raise(HotFile());

            var entity = EM.CreateEntity();
            EM.AddBuffer<RefElement>(entity);

            Assert.DoesNotThrow(() => Patch(), "буфер из нуля элементов — нормальное состояние, а не провал");
            Assert.That(EM.GetBuffer<RefElement>(entity).Length, Is.Zero);

            // И обратный проход тоже: у пустого буфера нечего сворачивать, но пройти по нему он обязан.
            byte[] saved = null;
            Assert.DoesNotThrow(() => saved = Save());
            Assert.That(saved, Is.Not.Null);
            Assert.That(BlobchegPatchErrors.HasAny, Is.False, "пустой буфер не имеет права положить провал в ящик");
        }

        [Test]
        public void Ссылка_в_поднятую_но_пустую_базу_обязана_отбиться()
        {
            // База без единой записи — файл ровно в один header. Первый возможный оффсет записи
            // (HeaderSize) в такой базе уже за концом файла.
            var empty = Raise(Domain(nameof(IPatchHot)).Seal());
            Assert.That(empty.Length, Is.EqualTo(BlobchegFormat.HeaderSize));

            Gun(BlobchegFormat.HeaderSize);

            var error = Assert.Throws<InvalidOperationException>(() => Patch(),
                "оффсет за концом пустой базы обязан быть ошибкой, а не указателем на первый байт после header'а");

            Assert.That(error.Message, Does.Contain(nameof(GunRef)));
        }

        [Test]
        public void Нулевой_слот_переживает_патч_и_обратный_проход_нулём()
        {
            Raise(HotFile());

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new GunRef());

            Patch();
            var bytes = Save();

            using (var loaded = LoadRaw(bytes))
            {
                var slot = SlotOf(loaded, Single<GunRef>(loaded));

                // Слепое вычитание адреса базы из нуля дало бы ulong.MaxValue минус адрес — то есть
                // абсурдное число, которое следующий патч уже не отличит ни от чего.
                Assert.That(slot, Is.Zero, "ноль обязан уехать в файл нулём");
            }
        }

        [Test]
        public void Буфер_из_одного_нулевого_элемента_не_становится_адресом_базы()
        {
            var hot = Raise(HotFile());

            var entity = EM.CreateEntity();
            var buffer = EM.AddBuffer<RefElement>(entity);
            buffer.Add(new RefElement { Marker = 1 });

            Patch();

            var element = EM.GetBuffer<RefElement>(entity)[0];
            Assert.That(element.Gun.Data.Value, Is.Zero);
            Assert.That(element.Gun.Data.Value, Is.Not.EqualTo(hot.Ptr));
            Assert.That(element.Marker, Is.EqualTo(1), "соседнее поле элемента патч трогать не имеет права");
        }

        [Test]
        public void Мир_без_единой_сущности_сохраняется_и_читается()
        {
            Raise(HotFile());

            var bytes = Save();
            using (var loaded = Load(bytes))
            {
                var query = loaded.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GunRef>());
                Assert.That(query.CalculateEntityCount(), Is.Zero);
            }

            Assert.That(BlobchegPatchErrors.HasAny, Is.False);
        }
    }
}
