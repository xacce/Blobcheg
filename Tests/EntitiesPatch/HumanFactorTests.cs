using System;
using NUnit.Framework;
using UnityEngine;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// Ошибки, которые делают не злоумышленники, а разработчики в первый рабочий день на этом API.
    /// Каждая из них ощущается правильной — потому что в соседнем API так и есть.
    /// </summary>
    public sealed unsafe class HumanFactorTests : PatchFixture
    {
        /// <summary>Тот самый «закешируем, чтобы не искать каждый кадр».</summary>
        static BlobchegReference<PatchGun> s_Cached;

        [SetUp]
        public void ForgetCache() => s_Cached = default;

        [Test]
        public void Ссылка_скопированная_в_обычное_поле_не_переезжает_с_пересборкой()
        {
            var first = HotFile(ammo: 1f, rpm: 11);
            Raise(first);
            var entity = Gun(first["gun"]);

            Patch();

            // «Слот у меня уже есть, положу его в статик — зачем каждый раз лезть в компонент».
            s_Cached = EM.GetComponentData<GunRef>(entity).Gun;
            Assert.That(s_Cached.IsResolved, Is.True);
            Assert.That(Copy(s_Cached.Value).Rpm, Is.EqualTo(11));

            // Пересборка домена под живым редактором. Обещание фичи накрывает слоты в компонентах,
            // а копия в обычном поле остаётся смотреть в прошлое поколение.
            Raise(HotFile(ammo: 2f, rpm: 22));
            Patch();

            Assert.That(EM.GetComponentData<GunRef>(entity).Gun.IsResolved, Is.True, "слот в компоненте переехал");

            Assert.That(s_Cached.IsResolved, Is.False,
                "а копия — нет, и она обязана быть опознаваема как мёртвая: иначе следующий Value " +
                "прочитает буфер, который вот-вот освободят");
            Assert.Throws<InvalidOperationException>(() => Copy(s_Cached.Value),
                "чтение протухшей копии — ошибка, а не байты прошлого поколения");
        }

        [Test]
        public void IsSet_не_обещает_что_Value_можно_читать()
        {
            var file = HotFile();
            Raise(file);
            var entity = Gun(file["gun"]);

            var slot = EM.GetComponentData<GunRef>(entity).Gun;

            // Два похожих свойства рядом — самая массовая ошибка на таком API.
            Assert.That(slot.IsSet, Is.True, "оффсет назначен");
            Assert.That(slot.IsResolved, Is.False, "но патча ещё не было");

            Assert.Throws<InvalidOperationException>(() => Copy(slot.Value),
                "IsSet значит «запись назначена», а не «можно читать»; путать их обязано быть больно, а не тихо");
        }

        [Test]
        public void Привычка_из_BlobAssetReference_читать_Value_сразу_после_AddComponent()
        {
            var file = HotFile();
            Raise(file);

            // У Unity BlobAssetReference<T>.Value работает сразу после бейка. Здесь между бейком и
            // чтением стоит патч импорта, и до него в слоте оффсет.
            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new GunRef { Gun = new BlobchegReference<PatchGun>(file["gun"]) });

            var slot = EM.GetComponentData<GunRef>(entity).Gun;

            var error = Assert.Throws<InvalidOperationException>(() => Copy(slot.Value),
                "привычка из соседнего API обязана упереться в явную ошибку, а не в нули");

            Assert.That(error.Message, Does.Contain("не пропатчен"),
                "сообщение обязано объяснить именно ЭТО, а не «что-то пошло не так»");
        }

        [Test]
        public void Копипаста_бейкера_с_оффсетом_чужого_домена_обязана_отбиться()
        {
            var hot = Raise(HotFile());

            var coldFile = Domain(nameof(IPatchCold));
            for (var i = 0; i < 32; i++)
                coldFile.Add("note" + i.ToString("D2"), new PatchNote { Tier = i });

            coldFile.Seal();
            Raise(coldFile);

            // Строку скопировали, тип поменяли, оффсет забыли: в горячую ссылку уехал адрес из
            // холодной базы.
            var strayOffset = coldFile["note31"];
            Assert.That(strayOffset, Is.GreaterThan((uint)hot.Length));

            Gun(strayOffset);

            Assert.Throws<InvalidOperationException>(() => Patch(),
                "чужой оффсет обязан отбиться, а не превратиться в указатель куда-нибудь");
        }

        [Test]
        public void Две_ссылки_на_одну_запись_равны_и_до_и_после_патча()
        {
            var file = HotFile();
            Raise(file);
            var offset = file["gun"];

            var a = new BlobchegReference<PatchGun>(offset);
            var b = new BlobchegReference<PatchGun>(offset);

            Assert.That(a.Data.Value, Is.EqualTo(b.Data.Value), "до патча равны");

            var first = Gun(offset);
            var second = Gun(offset);
            Patch();

            Assert.That(SlotOf(first), Is.EqualTo(SlotOf(second)),
                "после патча тоже равны: иначе «if (a == b)» начинает врать ровно после загрузки сцены");
        }

        // Находка набора, закрытая в пакете: у слота не было ни оператора равенства, ни
        // IEquatable<>, и единственным работающим сравнением оставался ValueType.Equals — боксинг
        // и рефлексия по полям, в джобе недоступные вовсе. Теперь сравнение своё, и отвечает
        // одинаково до и после патча.
        [Test]
        public void Сравнение_ссылок_не_должно_ходить_через_боксинг()
        {
            var file = HotFile();
            var hot = Raise(file);
            var offset = file["gun"];

            var a = new BlobchegReference<PatchGun>(offset);
            var b = new BlobchegReference<PatchGun>(offset);
            var other = new BlobchegReference<PatchGun>(file["armor"]);

            Assert.That(a == b, Is.True, "две ссылки из одного оффсета обязаны быть равны до патча");
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
            Assert.That(a != other, Is.True, "разные оффсеты — разные ссылки");

            // И то же самое после патча: если сравнение начнёт отвечать иначе, «if (a == b)» в
            // игровом коде начнёт врать ровно после загрузки сцены.
            var first = Gun(offset);
            var second = Gun(offset);
            Patch();

            var pa = EM.GetComponentData<GunRef>(first).Gun;
            var pb = EM.GetComponentData<GunRef>(second).Gun;

            Assert.That(pa == pb, Is.True, "после патча ответ обязан остаться тем же");
            Assert.That(pa.Data.Value, Is.EqualTo(hot.AddressOf(offset)), "сравнивали именно пропатченные слоты");
        }

        [Test]
        public void Неназначенное_поле_редактора_не_превращается_в_запись_по_нулю()
        {
            Raise(HotFile());

            var empty = default(BlobchegRef<PatchGun>);
            Assert.That(empty.IsSet, Is.False);

            // Оптимистичный путь — «просто позовём ToReference()» — обязан упереться в ошибку, а не
            // выдать слот, который потом молча укажет в header.
            var error = Assert.Throws<InvalidOperationException>(() => empty.ToReference(),
                "пустое поле редактора не имеет права стать ссылкой на запись по нулевому оффсету");

            Assert.That(error.Message, Does.Contain(nameof(PatchGun)));
        }

        [Test]
        public void ToReference_и_конструктор_дают_один_и_тот_же_слот()
        {
            var file = HotFile();
            var hot = Raise(file);
            var offset = file["gun"];

            var carrier = ScriptableObject.CreateInstance<BlobchegRefSo>();
            try
            {
                carrier.offset = offset;
                carrier.recordType = typeof(PatchGun).FullName;
                carrier.domainName = nameof(IPatchHot);

                var field = new BlobchegRef<PatchGun>(carrier);

                var viaField = field.ToReference();
                var viaCtor = new BlobchegReference<PatchGun>(offset);

                Assert.That(viaField.Data.Value, Is.EqualTo(viaCtor.Data.Value),
                    "два способа сделать одно и то же обязаны сойтись — иначе у адреса две правды");

                var entity = EM.CreateEntity();
                EM.AddComponentData(entity, new GunRef { Gun = viaField });
                Patch();

                Assert.That(SlotOf(entity), Is.EqualTo(hot.AddressOf(offset)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(carrier);
            }
        }

        [Test]
        public void ToReference_с_ассетом_чужой_записи_обязан_отбиться()
        {
            var file = HotFile();
            Raise(file);

            var carrier = ScriptableObject.CreateInstance<BlobchegRefSo>();
            try
            {
                // Пикером положили ассет БРОНИ в поле, типизированное пушкой.
                carrier.offset = file["armor"];
                carrier.recordType = typeof(PatchArmor).FullName;
                carrier.domainName = nameof(IPatchHot);

                var field = new BlobchegRef<PatchGun>(carrier);

                var error = Assert.Throws<InvalidOperationException>(() => field.ToReference(),
                    "проверка типа записи обязана стоять и на новом пути тоже");

                Assert.That(error.Message, Does.Contain(typeof(PatchArmor).FullName));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(carrier);
            }
        }

        [Test]
        public void Повторная_установка_патча_не_пересобирает_таблицу()
        {
            // Разработчик зовёт Install из своего бутстрапа «на всякий случай» — и делает это до
            // или после того, как его позвал редактор.
            var before = BlobchegPatchTableBuilder.RegisteredTypes.Count;

            BlobchegPatchInstall.Install();
            BlobchegPatchInstall.Install();

            Assert.That(BlobchegPatchTable.IsBuilt, Is.True);
            Assert.That(BlobchegPatchTableBuilder.RegisteredTypes.Count, Is.EqualTo(before),
                "повторная установка не имеет права ни удвоить список типов, ни потерять таблицу");

            // И патч после этого обязан работать.
            var file = HotFile();
            var hot = Raise(file);
            var entity = Gun(file["gun"]);

            Patch();
            Assert.That(SlotOf(entity), Is.EqualTo(hot.AddressOf(file["gun"])));
        }
    }
}
