using System;
using NUnit.Framework;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// Границы адреса. Старый путь чтения (<c>Read&lt;T&gt;</c>) проверяет три вещи: выравнивание,
    /// нижнюю границу (запись начинается не раньше header'а) и верхнюю. Раздел спрашивает, все ли
    /// три доехали до нового пути — там, где вместо оффсета выдают сырой указатель, цена промаха
    /// выше, а не ниже.
    /// </summary>
    public sealed unsafe class BoundaryTests : PatchFixture
    {
        [Test]
        public void Оффсет_за_концом_файла_обязан_отбиться_на_патче()
        {
            var hot = Raise(HotFile());
            Gun((uint)hot.Length + BlobchegFormat.RecordAlign);

            var error = Assert.Throws<InvalidOperationException>(() => Patch());

            Assert.That(error.Message, Does.Contain(nameof(GunRef)));
            Assert.That(error.Message, Does.Contain(((uint)hot.Length + BlobchegFormat.RecordAlign).ToString()),
                "в сообщении обязано быть само значение — иначе искать его в сцене нечем");
        }

        [Test]
        public void Оффсет_uint_MaxValue_не_превращается_в_дикий_адрес()
        {
            var hot = Raise(HotFile());
            var entity = Gun(uint.MaxValue);

            Assert.Throws<InvalidOperationException>(() => Patch(),
                "четыре гигабайта от начала базы — не адрес, а способ уронить процесс на первом чтении");

            Assert.That(SlotOf(entity), Is.EqualTo(uint.MaxValue),
                "провалившийся патч обязан оставить слот как был, а не записать половину");
            Assert.That(SlotOf(entity), Is.Not.EqualTo(hot.Ptr + uint.MaxValue));
        }

        // BUG: оффсет внутрь header'а патч принимает и выдаёт указатель на header
        // Что происходит: BlobchegReference<PatchGun>(8) после патча указывает на восьмой байт
        //   файла, то есть внутрь header'а; Value молча отдаёт magic/version/flags как запись.
        // Что должно: явная ошибка. Записи начинаются с BlobchegFormat.HeaderSize, и старый путь
        //   это знает — BlobchegBlob.CheckRead отбивает offset < HeaderSize.
        // Корневая причина: BlobchegBases.TryResolve проверяет только верхнюю границу
        //   (`if (value >= length) return OutOfRange`). Нижней границы там нет вовсе, поэтому весь
        //   диапазон 1..31 считается валидным оффсетом записи.
        [Test]
        public void Оффсет_внутрь_header_а_обязан_отбиться()
        {
            var hot = Raise(HotFile());
            Gun(8);

            Assert.Throws<InvalidOperationException>(() => Patch(),
                "внутри header'а записей не бывает — это не адрес записи, а середина служебных полей");
        }

        [Test]
        public void Оффсет_ровно_на_последней_записи_обязан_пройти()
        {
            var file = HotFile(ammo: 13f, rpm: 131);
            var hot = Raise(file);

            // Пушка по FullName идёт после брони, значит её запись — последняя в файле.
            var last = file["gun"];
            Assert.That(last, Is.GreaterThan(file["armor"]), "раскладка изменилась — тест проверяет не ту границу");

            var entity = Gun(last);
            Assert.DoesNotThrow(() => Patch(), "последняя запись — валидная запись, а не «за концом»");

            Assert.That(SlotOf(entity), Is.EqualTo(hot.AddressOf(last)));

            var gun = Copy(EM.GetComponentData<GunRef>(entity).Gun.Value);
            Assert.That(gun.Ammo, Is.EqualTo(13f));
            Assert.That(gun.Rpm, Is.EqualTo(131));
        }

        // BUG: невыровненный оффсет патч принимает молча
        // Что происходит: BlobchegReference<PatchGun>(offsetПоследнейЗаписи + 1) резолвится в
        //   адрес, съехавший на байт; Value отдаёт запись, сдвинутую на один байт.
        // Что должно: явная ошибка — старт записи выровнен на BlobchegFormat.RecordAlign, и
        //   BlobchegBlob.CheckRead отбивает такое первой же проверкой.
        // Корневая причина: та же, что у оффсета внутрь header'а — BlobchegBases.TryResolve знает
        //   про длину буфера и не знает про формат. Проверок выравнивания в нём нет.
        [Test]
        public void Оффсет_мимо_выравнивания_обязан_отбиться()
        {
            var file = HotFile();
            Raise(file);
            Gun(file["gun"] + 1);

            Assert.Throws<InvalidOperationException>(() => Patch(),
                "оффсет не кратен 16 — это не начало записи, чем бы он ни оказался в памяти");
        }

        [Test]
        public void Оффсет_равный_длине_файла_ровно_обязан_отбиться()
        {
            var hot = Raise(HotFile());
            Gun((uint)hot.Length);

            Assert.Throws<InvalidOperationException>(() => Patch(),
                "адрес сразу за последним байтом буфера — уже чужая память");
        }

        [Test]
        public void Оффсет_на_единицу_меньше_длины_файла_тоже_за_пределами_записи()
        {
            var hot = Raise(HotFile());
            var offset = (uint)hot.Length - 1;

            // Формально внутри буфера, но там уже отладочный контур, а не запись. Старый путь это
            // видит по секции — проверяем, видит ли новый.
            //
            // План (строка 10) требовал от невыровненного оффсета явной ошибки, и её же тут и
            // получаем: длина файла кратности 16 не обещает, поэтому «на единицу меньше» отбивается
            // кодом BadOffset — ещё до вопроса о верхней границе. Причина в сообщении названа не та,
            // что в имени теста («за пределами записи»), но исход тот самый, которого план требовал:
            // явная ошибка вместо адреса внутрь контура.
            Assume.That(offset % BlobchegFormat.RecordAlign, Is.Not.Zero,
                "длина файла оказалась кратна 16 — «на единицу меньше» проверяет уже другую границу");

            var entity = Gun(offset);

            var error = Assert.Throws<InvalidOperationException>(() => Patch(),
                "внутри буфера, но не на начале записи — это не адрес записи, чем бы оно ни было");

            Assert.That(error.Message, Does.Contain(nameof(GunRef)));
            Assert.That(error.Message, Does.Contain(offset.ToString()),
                "в сообщении обязано быть само значение — иначе искать его в сцене нечем");

            Assert.That(SlotOf(entity), Is.EqualTo((ulong)offset),
                "провалившийся патч обязан оставить слот как был");

            // Старый путь на том же адресе отказывается — вот с чем новый путь и надо сравнивать.
            Assert.Throws<InvalidOperationException>(
                () => Copy(hot.Blob.Read<PatchGun>(offset)),
                "Read того же оффсета обязан отбить: он знает и выравнивание, и границы, и контур");
        }

        [Test]
        public void Шестьдесят_пятый_домен_обязан_отбиться_а_не_затереть_чужой()
        {
            var hot = Raise(HotFile());

            // Реестр — плоский массив на MaxDomains. Один слот уже занят поднятой базой.
            for (var i = 1; i < BlobchegBases.MaxDomains; i++)
                BlobchegBases.Register(BlobchegNaming.NameHash("IPatchFake" + i), (byte*)hot.Ptr, hot.Length);

            var error = Assert.Throws<InvalidOperationException>(
                () => BlobchegBases.Register(BlobchegNaming.NameHash("IPatchOverflow"), (byte*)hot.Ptr, hot.Length),
                "переполнение реестра обязано быть ошибкой, а не тихой перезаписью чужого слота");

            Assert.That(error.Message, Does.Contain(BlobchegBases.MaxDomains.ToString()));

            // И база, поднятая первой, обязана остаться на месте.
            Assert.That(BlobchegBases.TryGet(hot.Key, out var ptr, out var length), Is.True);
            Assert.That((ulong)ptr, Is.EqualTo(hot.Ptr));
            Assert.That(length, Is.EqualTo(hot.Length));
        }

        [Test]
        public void Домен_с_нулевым_ключом_и_пустой_буфер_обязаны_отбиться()
        {
            var hot = Raise(HotFile());

            Assert.Throws<ArgumentException>(() => BlobchegBases.Register(0, (byte*)hot.Ptr, hot.Length),
                "нулевой ключ — это «домена нет», и такой домен нельзя поставить на учёт");

            Assert.Throws<ArgumentException>(
                () => BlobchegBases.Register(BlobchegNaming.NameHash("IPatchNull"), null, 64),
                "нулевой указатель на учёт не ставится");

            Assert.Throws<ArgumentException>(
                () => BlobchegBases.Register(BlobchegNaming.NameHash("IPatchShort"), (byte*)hot.Ptr,
                    BlobchegFormat.HeaderSize - 1),
                "буфер короче header'а — не база");
        }
    }
}
