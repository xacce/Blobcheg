using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// Порядок вызовов и жизненный цикл базы. Здесь ломаются обещания «патч идемпотентен» и «домен
    /// не поднят — явная ошибка», а заодно проверяется обратное направление: обратный проход по
    /// миру, который никогда не патчили, обязан быть no-op, а не вычитанием адреса из оффсета.
    /// </summary>
    public sealed unsafe class OrderAndLifecycleTests : PatchFixture
    {
        // BUG: сообщение о неподнятом домене называет ключ, а не домен
        // Что происходит: текст ошибки содержит «домен 8A1C…F3 не поднят» — шестнадцать
        //   шестнадцатеричных цифр вместо имени маркер-интерфейса. Человеку с этим числом делать
        //   нечего: в коде оно не встречается нигде.
        // Что должно: в сообщении обязано стоять имя домена — «IPatchGhost».
        // Корневая причина: BlobchegPatchErrors.Slot хранит только ulong DomainKey, а обратной
        //   карты «ключ → имя» нет ни в ящике, ни в BlobchegPatchTable. При этом
        //   BlobchegPatchTableBuilder.CollectDomains строит ровно такую карту на сборке таблицы и
        //   выбрасывает её сразу после — имена есть, их просто не сохранили.
        [Test]
        public void Патч_без_поднятой_базы_называет_домен_в_сообщении()
        {
            Raise(HotFile());

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new GhostRef
            {
                Ghost = new BlobchegReference<PatchGhostRecord>(BlobchegFormat.HeaderSize),
            });

            var error = Assert.Throws<InvalidOperationException>(() => Patch(),
                "домен не поднят — это ошибка, а не нули в полях");

            Assert.That(error.Message, Does.Contain(nameof(GhostRef)),
                "компонент в сообщении есть — по нему сцену хотя бы можно найти");
            Assert.That(error.Message, Does.Contain(nameof(IPatchGhost)),
                "а домен обязан быть назван именем: ключ FNV-64 не гуглится и в проекте не встречается");
        }

        [Test]
        public void Двойной_патч_не_складывает_адрес_дважды()
        {
            var file = HotFile();
            var hot = Raise(file);
            var entity = Gun(file["gun"]);

            Patch();
            var once = SlotOf(entity);

            Patch();
            var twice = SlotOf(entity);

            Assert.That(once, Is.EqualTo(hot.AddressOf(file["gun"])));
            Assert.That(twice, Is.EqualTo(once),
                "второй проход по уже пропатченному полю обязан быть no-op, а не «база плюс база плюс оффсет»");

            var gun = Copy(EM.GetComponentData<GunRef>(entity).Gun.Value);
            Assert.That(gun.Rpm, Is.EqualTo(600));
        }

        [Test]
        public void Тройной_патч_и_обратный_проход_возвращают_исходный_оффсет()
        {
            var file = HotFile();
            Raise(file);
            var offset = file["gun"];
            Gun(offset);

            Patch();
            Patch();
            Patch();

            var bytes = Save();
            using (var loaded = LoadRaw(bytes))
            {
                Assert.That(SlotOf(loaded, Single<GunRef>(loaded)), Is.EqualTo(offset),
                    "сколько бы раз ни патчили, в файл обязан уехать тот самый оффсет");
            }
        }

        [Test]
        public void Обратный_проход_по_непатченному_миру_не_уводит_оффсет_в_минус()
        {
            var file = HotFile();
            Raise(file);
            var offset = file["gun"];
            Gun(offset);

            // Патча не было вовсе: сущность создали руками и сразу пишем мир. Слепое вычитание
            // адреса базы дало бы здесь оффсет минус адрес — то есть число под ulong.MaxValue.
            var bytes = Save();

            using (var loaded = LoadRaw(bytes))
            {
                Assert.That(SlotOf(loaded, Single<GunRef>(loaded)), Is.EqualTo(offset));
            }
        }

        [Test]
        public void Двойной_обратный_проход_не_вычитает_базу_дважды()
        {
            var file = HotFile();
            Raise(file);
            var offset = file["gun"];
            Gun(offset);

            Patch();
            var first = Save();

            // Мир из файла, в слотах сырые оффсеты. Поднимаем базу заново и пишем его ещё раз —
            // это и есть второй обратный проход по тем же данным.
            var once = LoadRaw(first);
            Assert.That(SlotOf(once, Single<GunRef>(once)), Is.EqualTo(offset));

            Raise(HotFile());
            var second = Save(once);

            var twice = LoadRaw(second);
            Assert.That(SlotOf(twice, Single<GunRef>(twice)), Is.EqualTo(offset),
                "второе сворачивание того же оффсета обязано дать то же число");
        }

        [Test]
        public void Снятие_базы_с_учёта_при_живых_указателях_обязано_быть_видно()
        {
            var file = HotFile();
            var hot = Raise(file);
            var entity = Gun(file["gun"]);

            Patch();
            var address = SlotOf(entity);
            Assert.That(BlobchegBases.IsKnownAddress(address), Is.True);

            Drop(hot);

            // Память освобождена — разыменовывать нельзя, поэтому спрашиваем реестр, а не память.
            Assert.That(BlobchegBases.IsKnownAddress(address), Is.False,
                "снятый с учёта диапазон обязан перестать считаться живой записью");
            Assert.That(EM.GetComponentData<GunRef>(entity).Gun.IsResolved, Is.False,
                "IsResolved обязан честно сказать «нет» — иначе следующий Value читает освобождённую память");
        }

        /// <summary>
        /// Принятый предел, а не победа. База — value-структура с владеющим указателем, и ячейки,
        /// пережившей освобождение самой памяти, у неё нет. Поэтому «освободили буфер, а с учёта
        /// снять забыли» реестр отличить не может: он хранит адрес и длину, а не поколение
        /// аллокации. Тест существует затем, чтобы предел выглядел решением, а не недосмотром.
        /// </summary>
        [Test]
        public void Освобождённый_но_не_снятый_буфер_реестр_отличить_не_может_принятый_предел()
        {
            var buffer = BlobchegBuffer.Alloc(64, Allocator.Persistent);
            var key = BlobchegNaming.NameHash("IPatchFreed");
            var address = (ulong)buffer.Ptr + BlobchegFormat.HeaderSize;

            BlobchegBases.Register(key, buffer.Ptr, buffer.Length);
            Assert.That(BlobchegBases.IsKnownAddress(address), Is.True);

            // Именно та ошибка, которую делают: буфер освободили напрямую, Unregister не позвали.
            buffer.Dispose();

            Assert.That(BlobchegBases.IsKnownAddress(address), Is.True,
                "реестр по-прежнему отвечает «да» — и не может ответить иначе: у адреса нет поколения. " +
                "Контракт прямой: с учёта снимает тот, кто ставил, и ровно там же, где освобождает");

            BlobchegBases.Unregister(key, buffer.Ptr);
        }

        // BUG: пересборка в порядке «сначала освободить старую, потом поднять новую» теряет все розданные указатели
        // Что происходит: если старая база снимается с учёта ДО того, как встала новая, слот домена
        //   исчезает из реестра целиком; следующая регистрация заводит слот заново с PrevPtrs = 0.
        //   Все уже розданные указатели становятся OutOfRange, и патч валится вместо перевода.
        // Что должно: обещание фичи — пересборка переводит уже розданные указатели на новый буфер,
        //   без оговорок про порядок.
        // Корневая причина: прошлое поколение живёт в BlobchegBases.Table.PrevPtrs и заполняется
        //   ТОЛЬКО в ветке Register, где слот уже существует. Unregister в этот момент уже удалил
        //   слот свопом с последним (t.Keys[slot] = t.Keys[last]), и адрес старого буфера забыт
        //   навсегда. Порядок «поднять новую, потом освободить старую» нигде не проверяется — он
        //   только описан в комментарии к Unregister.
        [Test]
        public void Пересборка_в_порядке_снять_потом_поднять_обязана_перевести_указатели()
        {
            var first = HotFile(ammo: 1f, rpm: 11);
            var gen1 = Raise(first);
            var entity = Gun(first["gun"]);

            Patch();
            Assert.That(SlotOf(entity), Is.EqualTo(gen1.AddressOf(first["gun"])));

            // Пересборка домена: старую освободили, новую подняли.
            Drop(gen1);
            Raise(HotFile(ammo: 2f, rpm: 22));

            Assert.DoesNotThrow(() => Patch(),
                "пересборка обязана переводить розданные указатели независимо от порядка снятия и подъёма");

            var gun = Copy(EM.GetComponentData<GunRef>(entity).Gun.Value);
            Assert.That(gun.Rpm, Is.EqualTo(22), "после пересборки читается новое поколение");
        }

        [Test]
        public void Снятие_с_учёта_чужим_указателем_не_сносит_живую_базу()
        {
            var hot = Raise(HotFile());
            var cold = Raise(Domain(nameof(IPatchCold)).Add("note", new PatchNote { Tier = 1 }).Seal());

            // Типичная опечатка: сняли домен, передав указатель соседней базы.
            BlobchegBases.Unregister(hot.Key, (byte*)cold.Ptr);

            Assert.That(BlobchegBases.TryGet(hot.Key, out var ptr, out _), Is.True,
                "снятие чужим указателем не имеет права снести живую базу");
            Assert.That((ulong)ptr, Is.EqualTo(hot.Ptr));
        }

        [Test]
        public void Снятие_с_учёта_домена_которого_нет_не_бросает_и_ничего_не_ломает()
        {
            var hot = Raise(HotFile());

            Assert.DoesNotThrow(
                () => BlobchegBases.Unregister(BlobchegNaming.NameHash("IPatchNeverWas"), (byte*)hot.Ptr));

            Assert.That(BlobchegBases.TryGet(hot.Key, out _, out _), Is.True);
        }
    }
}
