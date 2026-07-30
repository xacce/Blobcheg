using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// Личность записи и личность домена. Раздел спрашивает одно: может ли слот молча отдать не ту
    /// запись — чужого типа, чужого домена или чужого поколения буфера.
    /// </summary>
    public sealed unsafe class IdentityAndDomainTests : PatchFixture
    {
        // ------------------------------------------------------------- чужой тип и чужой домен

        // План (строка 19) требовал: «в границах — отбой по типу записи (отладочный контур).
        // Никогда — молчаливое чтение чужих байт». МОМЕНТ отбоя план не оговаривал, и реализация
        // выбрала ранний: тип записи теперь доезжает до таблицы слотов
        // (BlobchegFieldSlot.RecordTypeHash), и патч, получив адрес, сразу спрашивает контур —
        // начинается ли по нему запись объявленного типа. Не сходится — WrongRecord прямо на патче.
        //
        // Это строже, чем ждал тест (отбой на чтении Value), и обещание закрывает раньше: до Value
        // дело просто не доходит, сцену с таким слотом не импортировать. Проверка, существовавшая
        // только на старом пути, доехала до нового — ровно то, чего требовал план.
        [Test]
        public void Запись_прочитанная_через_слот_близнецом_обязана_отбиться()
        {
            var file = HotFile(ammo: 42f, rpm: 7);
            var hot = Raise(file);
            var gunOffset = file["gun"];

            var carrier = EM.CreateEntity();
            EM.AddComponentData(carrier, new ArmorRef { Armor = new BlobchegReference<PatchArmor>(gunOffset) });

            // Старый путь на том же оффсете отказывается — значит проверка в пакете есть.
            Assert.Throws<InvalidOperationException>(() => Copy(hot.Blob.Read<PatchArmor>(gunOffset)),
                "Read близнецом обязан отбиться — это уже закрытая находка пакета");

            var error = Assert.Throws<InvalidOperationException>(() => Patch(),
                "и слот обязан отбиться на том же оффсете: иначе проверка типа существует только на " +
                "старом пути, а новый её потерял");

            Assert.That(error.Message, Does.Contain(nameof(ArmorRef)),
                "компонент в сообщении есть — по нему сцену хотя бы можно найти");
            Assert.That(error.Message, Does.Contain(nameof(IPatchHot)),
                "и домен назван именем, а не ключом");
        }

        [Test]
        public void Оффсет_чужой_базы_за_пределами_своей_обязан_отбиться()
        {
            var hot = Raise(HotFile());

            // Холодная база нарочно длиннее горячей: тогда её хвостовой оффсет в горячей — за концом.
            var coldFile = Domain(nameof(IPatchCold));
            for (var i = 0; i < 32; i++)
                coldFile.Add("note" + i.ToString("D2"), new PatchNote { Tier = i, Extra = i * 2 });

            coldFile.Seal();
            Raise(coldFile);

            var far = coldFile["note31"];
            Assert.That(far, Is.GreaterThan((uint)hot.Length),
                "холодная база не переросла горячую — тест проверяет не ту границу");

            Gun(far);

            Assert.Throws<InvalidOperationException>(() => Patch(),
                "оффсет чужой базы, не помещающийся в свою, обязан отбиться по границам");
        }

        // ------------------------------------------------------------- один адрес, два потребителя

        [Test]
        public void Один_оффсет_в_двух_компонентах_даёт_один_адрес_и_один_оффсет_обратно()
        {
            var file = HotFile();
            var hot = Raise(file);
            var offset = file["gun"];

            var a = EM.CreateEntity();
            EM.AddComponentData(a, new GunRef { Gun = new BlobchegReference<PatchGun>(offset) });

            var b = EM.CreateEntity();
            EM.AddComponentData(b, new GunRefTwin { Gun = new BlobchegReference<PatchGun>(offset) });

            Patch();

            Assert.That(EM.GetComponentData<GunRef>(a).Gun.Data.Value, Is.EqualTo(hot.AddressOf(offset)));
            Assert.That(EM.GetComponentData<GunRefTwin>(b).Gun.Data.Value, Is.EqualTo(hot.AddressOf(offset)),
                "один оффсет — один адрес, в каком бы компоненте он ни лежал");

            var bytes = Save();
            var loaded = LoadRaw(bytes);

            Assert.That(SlotOf(loaded, Single<GunRef>(loaded)), Is.EqualTo(offset));
            Assert.That(
                loaded.EntityManager.GetComponentData<GunRefTwin>(Single<GunRefTwin>(loaded)).Gun.Data.Value,
                Is.EqualTo(offset), "и обратно обоим обязан вернуться тот же самый оффсет");
        }

        // ------------------------------------------------------------- поколения буфера

        [Test]
        public void Повторная_регистрация_домена_не_имеет_права_оставить_указатели_смотреть_в_старое()
        {
            var first = HotFile(ammo: 1f, rpm: 11);
            var gen1 = Raise(first);
            var entity = Gun(first["gun"]);

            Patch();
            Assert.That(SlotOf(entity), Is.EqualTo(gen1.AddressOf(first["gun"])));

            // Пересборка в «правильном» порядке: новая база встала на учёт, старая ещё жива.
            var gen2 = Raise(HotFile(ammo: 2f, rpm: 22));

            var slot = EM.GetComponentData<GunRef>(entity).Gun;

            // Середины быть не должно: либо указатель уже смотрит в новое поколение, либо чтение
            // честно отказывается. Молча отдать байты старого буфера нельзя.
            if (slot.Data.Value == gen2.AddressOf(first["gun"]))
                Assert.Pass("указатель переведён сразу регистрацией");

            Assert.That(slot.IsResolved, Is.False,
                "указатель всё ещё в прошлом поколении — тогда IsResolved обязан сказать «нет»");
            Assert.Throws<InvalidOperationException>(() => Copy(slot.Value),
                "и чтение обязано отказаться, а не отдать байты буфера, который вот-вот освободят");
        }

        [Test]
        public void Пересборка_с_патчем_между_поколениями_доводит_до_нового_буфера()
        {
            var first = HotFile(ammo: 1f, rpm: 11);
            Raise(first);
            var entity = Gun(first["gun"]);
            Patch();

            var gen2 = Raise(HotFile(ammo: 2f, rpm: 22));
            Patch();

            Assert.That(SlotOf(entity), Is.EqualTo(gen2.AddressOf(first["gun"])));
            Assert.That(Copy(EM.GetComponentData<GunRef>(entity).Gun.Value).Rpm, Is.EqualTo(22));
        }

        // BUG: две пересборки подряд без патча между ними теряют указатель
        // Что происходит: gen1 → gen2 → gen3 без патча в промежутке. Патч после третьей регистрации
        //   валится с OutOfRange: адрес первого поколения не найден ни в текущем, ни в прошлом.
        // Что должно: обещание фичи — пересборка переводит уже розданные указатели на новый буфер.
        //   Два импорта ассета в одном кадре редактора дают ровно две регистрации подряд.
        // Корневая причина: BlobchegBases.Table держит РОВНО ОДНО прошлое поколение
        //   (PrevPtrs[slot]), и ветка повторного Register его перезаписывает: PrevPtrs[slot] =
        //   Ptrs[slot]. После третьей регистрации адрес первого буфера в реестре не существует, и
        //   TryResolve уходит в последнюю ветку, где heap-адрес заведомо >= length.
        [Test]
        public void Две_пересборки_подряд_обязаны_довести_указатель_до_третьего_поколения()
        {
            var first = HotFile(ammo: 1f, rpm: 11);
            Raise(first);
            var entity = Gun(first["gun"]);
            Patch();

            Raise(HotFile(ammo: 2f, rpm: 22));
            var gen3 = Raise(HotFile(ammo: 3f, rpm: 33));

            Assert.DoesNotThrow(() => Patch(),
                "два импорта подряд — обычный день редактора, и указатели обязаны их пережить");

            Assert.That(SlotOf(entity), Is.EqualTo(gen3.AddressOf(first["gun"])));
            Assert.That(Copy(EM.GetComponentData<GunRef>(entity).Gun.Value).Rpm, Is.EqualTo(33));
        }

        // План (строка 25) допускал два исхода: «ЛИБО ссылка едет за записью, ЛИБО явная ошибка.
        // Молча отданный сосед — порча». Реализация выбрала вторую половину.
        //
        // Доехать за записью она не может и не сможет: перевод поколения — это арифметика
        // `новая база + прежний сдвиг`, а сопоставить записи двух раскладок нечем — ни ключа, ни
        // хеша содержимого в самой записи нет. Зато сверка по отладочному контуру видит, что по
        // полученному адресу начинается БРОНЯ, а не пушка, и валит патч кодом WrongRecord. Ровно
        // это план и называл вторым допустимым исходом; недопустимое — молчание — закрыто.
        [Test]
        public void Поколение_сдвинувшее_запись_не_имеет_права_отдать_соседнюю()
        {
            // gen1: только пушка.
            var first = Domain(nameof(IPatchHot)).Add("gun", new PatchGun { Ammo = 1f, Rpm = 11 }).Seal();
            Raise(first);

            var entity = Gun(first["gun"]);
            Patch();

            // gen2: перед пушкой появилась броня — по FullName она идёт первой и двигает пушку.
            var second = Domain(nameof(IPatchHot))
                .Add("armor", new PatchArmor { Hp = 500f, Plates = 9 })
                .Add("gun", new PatchGun { Ammo = 2f, Rpm = 22 })
                .Seal();

            Raise(second);
            Assert.That(second["gun"], Is.Not.EqualTo(first["gun"]), "раскладка не поехала — тест проверяет не то");

            var error = Assert.Throws<InvalidOperationException>(() => Patch(),
                "перевод поколения привёл указатель на чужую запись — молчать об этом нельзя");

            Assert.That(error.Message, Does.Contain(nameof(GunRef)),
                "компонент в сообщении есть — по нему сцену хотя бы можно найти");
            Assert.That(error.Message, Does.Contain(nameof(IPatchHot)));

            // Что отбой сработал именно на несовпадении записи, а не на всякой пересборке вообще,
            // держит соседний тест: Пересборка_с_патчем_между_поколениями_доводит_до_нового_буфера
            // гоняет ту же пару поколений с НЕ поехавшей раскладкой и проходит молча.
        }

        // ------------------------------------------------------------- домен записи
        //
        // Записи «вне доменов» и «сразу в двух доменах» нельзя положить в живой компонент этой
        // сборки: BlobchegPatchTableBuilder.Build обходит ВСЕ типы компонентов процесса и падает на
        // первой такой ссылке целиком — то есть выключил бы патч всему проекту, а не одному тесту.
        // Поэтому проверка идёт прямо по разрешению домена.
        //
        // API DESIGN: у сборки таблицы нет режима «проверь один тип и скажи, что не так». Есть одна
        // кнопка Build на весь процесс, и её отказ — это отказ патча целиком, из
        // [InitializeOnLoadMethod], с одним типом в тексте. Диагностировать «какие ещё компоненты
        // объявлены неправильно» нечем, и написать тест на это с публичной поверхности нельзя —
        // ниже рефлексия по приватному DomainKeyOf.

        static Exception DomainFailure(Type record)
        {
            var builder = typeof(BlobchegPatchTableBuilder);

            var collect = builder.GetMethod("CollectDomains", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(collect, Is.Not.Null, "CollectDomains переименован — тест разрешения домена ослеп");

            var resolve = builder.GetMethod("DomainKeyOf", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(resolve, Is.Not.Null, "DomainKeyOf переименован — тест разрешения домена ослеп");

            var domains = collect.Invoke(null, null);

            try
            {
                resolve.Invoke(null, new[] { record, domains });
                return null;
            }
            catch (TargetInvocationException e)
            {
                return e.InnerException;
            }
        }

        [Test]
        public void Запись_вне_доменов_обязана_быть_ошибкой_а_не_догадкой()
        {
            var error = DomainFailure(typeof(PatchLoose));

            Assert.That(error, Is.Not.Null, "запись без маркер-интерфейса патчить неоткуда — это ошибка");
            Assert.That(error.Message, Does.Contain(nameof(PatchLoose)),
                "в сообщении обязано быть имя записи: искать её иначе не по чему");
        }

        [Test]
        public void Запись_сразу_в_двух_доменах_обязана_назвать_оба()
        {
            var error = DomainFailure(typeof(PatchBoth));

            Assert.That(error, Is.Not.Null, "из какой базы брать адрес — не то, о чём можно догадаться");
            Assert.That(error.Message, Does.Contain(nameof(IPatchHot)));
            Assert.That(error.Message, Does.Contain(nameof(IPatchCold)),
                "названы обязаны быть оба домена — иначе непонятно, какой из них лишний");
        }

        [Test]
        public void Ссылка_на_саму_базу_как_на_запись_обязана_отбиться()
        {
            // База формально unmanaged и в BlobchegReference<T> пролезает. Домена у неё нет —
            // домен у неё в атрибуте, а не в интерфейсе, и это разные вещи.
            var error = DomainFailure(typeof(PatchHotDb));

            Assert.That(error, Is.Not.Null,
                "база — не запись своей базы; ссылка на неё обязана отбиться, а не завести домен сама себе");
            Assert.That(error.Message, Does.Contain(nameof(PatchHotDb)));
        }

        [Test]
        public void Голое_нутро_слота_в_поле_компонента_обязано_быть_ошибкой()
        {
            // Человеческий фактор: разработчик заглянул внутрь BlobchegReference<T>, увидел там
            // BlobchegReferenceData и объявил полем «настоящий» тип. Домен из него не выводится.
            var walk = typeof(BlobchegPatchTableBuilder).GetMethod("Walk", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(walk, Is.Not.Null, "Walk переименован — тест обхода полей ослеп");

            var collect = typeof(BlobchegPatchTableBuilder)
                .GetMethod("CollectDomains", BindingFlags.NonPublic | BindingFlags.Static);
            var domains = collect.Invoke(null, null);

            var found = new List<BlobchegFieldSlot>();
            var seen = new HashSet<Type>();

            var error = Assert.Throws<TargetInvocationException>(
                () => walk.Invoke(null, new object[] { typeof(NakedData), 0, found, seen, domains, 0 }));

            Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(error.InnerException.Message, Does.Contain(nameof(BlobchegReferenceData)));
        }

        /// <summary>
        /// Не компонент: тип живёт только ради теста выше. Объяви его <c>IComponentData</c> — и
        /// сборка таблицы упадёт на старте редактора, выключив патч всему проекту.
        /// </summary>
        struct NakedData
        {
            public BlobchegReferenceData Slot;
        }

        [Test]
        public void Ключ_домена_считается_по_имени_маркера_и_совпадает_с_личностью_файла()
        {
            var hot = Raise(HotFile());

            Assert.That(hot.Key, Is.EqualTo(BlobchegNaming.NameHash(nameof(IPatchHot))),
                "ключ реестра и личность файла обязаны быть одним числом — иначе патч ищет базу не там");
            Assert.That(BlobchegBases.IsAddressOf(hot.Key, hot.AddressOf(BlobchegFormat.HeaderSize)), Is.True);
            Assert.That(BlobchegBases.IsAddressOf(BlobchegNaming.NameHash(nameof(IPatchCold)),
                hot.AddressOf(BlobchegFormat.HeaderSize)), Is.False,
                "адрес горячей базы не имеет права считаться адресом холодной");
        }

        [Test]
        public void Тест_модель_не_отравила_таблицу_патча()
        {
            // Если бы в сборке нашёлся компонент со ссылкой на запись без домена, Build упал бы, и
            // ВСЕ остальные тесты набора проверяли бы пустоту, зеленея.
            Assert.That(BlobchegPatchTable.IsBuilt, Is.True);

            var registered = BlobchegPatchTableBuilder.RegisteredTypes;
            var names = new List<string>();
            foreach (var type in registered)
                names.Add(type.GetManagedType().Name);

            foreach (var expected in new[]
                     {
                         nameof(GunRef), nameof(GunRefTwin), nameof(ArmorRef), nameof(NoteRef), nameof(GhostRef),
                         nameof(PairRef), nameof(PackedRef), nameof(ShallowNestRef), nameof(DeepNestRef),
                         nameof(RefElement), nameof(RecordRef),
                     })
                Assert.That(names, Does.Contain(expected), $"обход не нашёл слот в '{expected}'");

            Assert.That(names, Does.Not.Contain(nameof(PlainData)), "компонент без слотов в таблице лишний");
        }

        [Test]
        public void Реестр_доменов_чистится_между_тестами()
        {
            // Страховка самого стенда: реестр — процессный статик, и незакрытая база соседнего
            // теста делала бы этот набор недетерминированным.
            Assert.That(BlobchegBases.TryGet(BlobchegNaming.NameHash(nameof(IPatchHot)), out _, out _), Is.False);
            Assert.That(BlobchegPatchErrors.HasAny, Is.False);
        }

        [Test]
        public void Пустой_перечислитель_зарегистрированных_типов_не_бывает()
        {
            Assert.That(BlobchegPatchTableBuilder.RegisteredTypes, Is.Not.Null);
            Assert.That(BlobchegPatchTableBuilder.RegisteredTypes.Count, Is.GreaterThan(0));
        }
    }
}
