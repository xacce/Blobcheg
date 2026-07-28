using System;
using Blobcheg.Authoring;
using UnityEngine;

namespace Blobcheg.AdvancedTests
{
    // ------------------------------------------------------------------ домены
    //
    // Четыре домена и два роутера. Два роутера нужны затем, что один из главных вопросов набора —
    // «а что будет, если подсунуть id, выданный ДРУГИМ роутером»; с одним роутером этот вопрос не
    // задать. Роутер у каждой базы назван явно: в сборке их два, и правило «единственный роутер
    // сборки» здесь не работает.

    /// <summary>Боевая база: в роутере AdvRouter, член «combat».</summary>
    public interface IAdvCombat
    {
    }

    /// <summary>Холодная база: в роутере AdvRouter, член «cold».</summary>
    public interface IAdvCold
    {
    }

    /// <summary>База вне роутеров — у её нод BlobchegId не бывает вовсе.</summary>
    public interface IAdvLoose
    {
    }

    /// <summary>База ЧУЖОГО роутера AdvOtherRouter.</summary>
    public interface IAdvOther
    {
    }

    /// <summary>Домен, который не объявлен ни одной базой. Нода, назвавшая его, обязана падать.</summary>
    public interface IAdvUndeclared
    {
    }

    // ------------------------------------------------------------------ записи

    public struct AdvGun : IAdvCombat
    {
        public float Ammo;
        public int Rpm;
    }

    /// <summary>Близнец <see cref="AdvGun"/>: тот же размер, другой тип. Ловушка реинтерпретации.</summary>
    public struct AdvGunTwin : IAdvCombat
    {
        public float Ammo;
        public int Rpm;
    }

    /// <summary>Имя типа сортируется ПЕРЕД AdvGun — добавление такой ноды двигает оффсеты пушек.</summary>
    public struct AdvArmor : IAdvCombat
    {
        public float Hp;
    }

    public enum AdvTier : byte
    {
        None = 0,
        Low = 1,
        High = 200,
    }

    /// <summary>Смесь bool, enum и выравнивания — round-trip обязан быть побайтовым.</summary>
    public struct AdvMixed : IAdvCombat
    {
        public bool Flag;
        public AdvTier Tier;
        public double Weight;
        public short Small;
    }

    public struct AdvColdInfo : IAdvCold
    {
        /// <summary>Свой id, положенный нодой в саму запись: он известен ДО записи.</summary>
        public uint SelfId;

        public int Tier;

        /// <summary>Id соседней ноды — так одна запись ссылается на другую.</summary>
        public uint LinkId;
    }

    public struct AdvLooseBlock : IAdvLoose
    {
        public long A;
        public long B;
    }

    public struct AdvOtherInfo : IAdvOther
    {
        public int V;
    }

    /// <summary>Запись без единого поля. В C# такая всё равно весит байт — вопрос, что сделает раскладка.</summary>
    public struct AdvEmptyRecord : IAdvLoose
    {
    }

    /// <summary>Запись с сырым указателем внутри. Формально unmanaged, по смыслу — мусор в файле.</summary>
    public unsafe struct AdvPointerRecord : IAdvLoose
    {
        public byte* Ptr;
        public long Tag;
    }

    /// <summary>Указатель не на виду, а в поле-структуре: IntPtr ничем не лучше <c>byte*</c>.</summary>
    public struct AdvPointerHolder
    {
        public IntPtr Handle;
    }

    /// <summary>Запись, у которой указатель лежит на второй ступени вложенности.</summary>
    public struct AdvNestedPointerRecord : IAdvLoose
    {
        public long Head;
        public AdvPointerHolder Inner;
    }

    /// <summary>Четверть толстой записи — 64 Б.</summary>
    public struct AdvChunk
    {
        public double A, B, C, D, E, F, G, H;
    }

    /// <summary>Толстая запись: 512 Б одним типом. Проверяет и первый её байт, и последний.</summary>
    public struct AdvFat : IAdvCombat
    {
        public AdvChunk C0, C1, C2, C3, C4, C5, C6, C7;
    }

    // ------------------------------------------------------------------ базы и роутеры

    [Blobcheg(typeof(IAdvCombat), "combat", Router = typeof(AdvRouter))]
    public partial struct AdvCombatDb
    {
    }

    [Blobcheg(typeof(IAdvCold), "cold", Router = typeof(AdvRouter))]
    public partial struct AdvColdDb
    {
    }

    [Blobcheg(typeof(IAdvOther), "other", Router = typeof(AdvOtherRouter))]
    public partial struct AdvOtherDb
    {
    }

    [Blobcheg(typeof(IAdvLoose))]
    public partial struct AdvLooseDb
    {
    }

    /// <summary>
    /// Вторая база НАД ТЕМ ЖЕ доменом. Абсурд по постановке: два фасада, один файл. Существует
    /// затем, чтобы проверить, что пакет либо это запрещает, либо оба фасада читают одно и то же.
    /// </summary>
    [Blobcheg(typeof(IAdvLoose))]
    public partial struct AdvLooseTwinDb
    {
    }

    [BlobchegRouter]
    public partial struct AdvRouter
    {
    }

    [BlobchegRouter]
    public partial struct AdvOtherRouter
    {
    }

    // ------------------------------------------------------------------ ноды

    /// <summary>Нода в обеих базах роутера: строка с двумя битами.</summary>
    public sealed class AdvComboNodeSo : BlobchegNodeSo
    {
        public float ammo = 30f;
        public int rpm = 600;
        public int tier = 3;

        /// <summary>Сосед, чей id уедет в запись. Может указывать и на эту же ноду.</summary>
        public BlobchegNodeSo link;

        public override Type[] OutTypes => new[] { typeof(IAdvCombat), typeof(IAdvCold) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            w.Add(new AdvGun { Ammo = ammo, Rpm = rpm });
            w.Add(new AdvColdInfo
            {
                SelfId = w.IdIn<AdvRouter>().Value,
                Tier = tier,
                LinkId = link != null ? w.IdOf<AdvRouter>(link).Value : BlobchegId.NoneValue,
            });
        }
    }

    /// <summary>Нода только в холодной базе: строка с дыркой на месте боевого бита.</summary>
    public sealed class AdvColdOnlyNodeSo : BlobchegNodeSo
    {
        public int tier = 9;

        public override Type[] OutTypes => new[] { typeof(IAdvCold) };

        public override void Write(ref BlobchegNodeWriter w)
            => w.Add(new AdvColdInfo { SelfId = w.IdIn<AdvRouter>().Value, Tier = tier, LinkId = BlobchegId.NoneValue });
    }

    public sealed class AdvArmorNodeSo : BlobchegNodeSo
    {
        public float hp = 100f;

        public override Type[] OutTypes => new[] { typeof(IAdvCombat) };

        public override void Write(ref BlobchegNodeWriter w) => w.Add(new AdvArmor { Hp = hp });
    }

    /// <summary>Пишет близнеца пушки: те же байты, другой тип.</summary>
    public sealed class AdvTwinNodeSo : BlobchegNodeSo
    {
        public float ammo = 7f;
        public int rpm = 77;

        public override Type[] OutTypes => new[] { typeof(IAdvCombat) };

        public override void Write(ref BlobchegNodeWriter w) => w.Add(new AdvGunTwin { Ammo = ammo, Rpm = rpm });
    }

    public sealed class AdvMixedNodeSo : BlobchegNodeSo
    {
        public bool flag = true;
        public AdvTier tier = AdvTier.High;
        public double weight = -1234.5678;
        public short small = -31000;

        public override Type[] OutTypes => new[] { typeof(IAdvCombat) };

        public override void Write(ref BlobchegNodeWriter w)
            => w.Add(new AdvMixed { Flag = flag, Tier = tier, Weight = weight, Small = small });
    }

    public sealed class AdvFatNodeSo : BlobchegNodeSo
    {
        public double first = 1.5;
        public double last = -2.5;

        public override Type[] OutTypes => new[] { typeof(IAdvCombat) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            var record = new AdvFat();
            record.C0.A = first;
            record.C7.H = last;
            w.Add(record);
        }
    }

    public sealed class AdvLooseNodeSo : BlobchegNodeSo
    {
        public long a = 1;
        public long b = 2;

        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override void Write(ref BlobchegNodeWriter w) => w.Add(new AdvLooseBlock { A = a, B = b });
    }

    /// <summary>Сырая запись произвольной длины, в том числе нулевой.</summary>
    public sealed class AdvRawNodeSo : BlobchegNodeSo
    {
        public int size;
        public byte seed = 1;

        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            var bytes = new byte[size];
            for (var i = 0; i < size; i++)
                bytes[i] = (byte)(seed + i);

            w.AddBytes<IAdvLoose>(bytes);
        }
    }

    public sealed class AdvOtherNodeSo : BlobchegNodeSo
    {
        public int v = 5;

        public override Type[] OutTypes => new[] { typeof(IAdvOther) };

        public override void Write(ref BlobchegNodeWriter w) => w.Add(new AdvOtherInfo { V = v });
    }

    /// <summary>Нода сразу в двух роутерах: <c>w.Id</c> у неё не бывает, только <c>IdIn</c>.</summary>
    public sealed class AdvBothRoutersNodeSo : BlobchegNodeSo
    {
        public bool askSingleId;

        public uint LastMain;
        public uint LastOther;

        public override Type[] OutTypes => new[] { typeof(IAdvCombat), typeof(IAdvOther) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            if (askSingleId)
            {
                // Спрашиваем «свой единственный id» у ноды, у которой их два. Обязано падать.
                LastMain = w.Id.Value;
            }

            LastMain = w.IdIn<AdvRouter>().Value;
            LastOther = w.IdIn<AdvOtherRouter>().Value;

            w.Add(new AdvGun { Ammo = 1f, Rpm = 1 });
            w.Add(new AdvOtherInfo { V = 2 });
        }
    }

    /// <summary>Объявила домен и ничего в него не написала.</summary>
    public sealed class AdvSilentNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvCombat) };

        public override void Write(ref BlobchegNodeWriter w)
        {
        }
    }

    /// <summary>Пишет в домен, которого нет в её OutTypes.</summary>
    public sealed class AdvStrayNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvCold) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            w.Add(new AdvColdInfo { SelfId = w.IdIn<AdvRouter>().Value });
            w.Add(new AdvGun { Ammo = 1f, Rpm = 1 });
        }
    }

    /// <summary>Пишет в один домен дважды.</summary>
    public sealed class AdvDoubleNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvCombat) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            w.Add(new AdvGun { Ammo = 1f, Rpm = 1 });
            w.Add(new AdvGun { Ammo = 2f, Rpm = 2 });
        }
    }

    /// <summary>Не объявила ни одного домена.</summary>
    public sealed class AdvNoOutTypesNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => Array.Empty<Type>();

        public override void Write(ref BlobchegNodeWriter w)
        {
        }
    }

    /// <summary>Назвала домен, который не объявлен ни одной базой.</summary>
    public sealed class AdvUndeclaredNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvUndeclared) };

        public override void Write(ref BlobchegNodeWriter w)
        {
        }
    }

    /// <summary>Роняет пересборку из середины <c>Write</c>.</summary>
    public sealed class AdvThrowNodeSo : BlobchegNodeSo
    {
        public bool armed = true;

        public override Type[] OutTypes => new[] { typeof(IAdvCombat) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            w.Add(new AdvGun { Ammo = 3f, Rpm = 3 });

            if (armed)
                throw new InvalidOperationException("AdvThrowNodeSo: нарочно роняем пересборку");
        }
    }

    /// <summary>
    /// Зовёт пересборку из <c>Write</c> — то есть из середины пересборки. Глубина ограничена САМОЙ
    /// нодой, а не пакетом: без этого ограничителя тест уронил бы редактор переполнением стека, и
    /// отчёта не осталось бы вовсе.
    /// </summary>
    public sealed class AdvReentrantNodeSo : BlobchegNodeSo
    {
        /// <summary>Сколько раз пересборка вошла сама в себя. Ноль — пакет реентранс отбил.</summary>
        public static int Reentered;

        static int _depth;

        /// <summary>
        /// Зваться <c>Reset</c> ей нельзя: у ScriptableObject это магическое имя, и Unity зовёт его
        /// сама при создании экземпляра — на статическом методе это ошибка в консоли на каждый
        /// CreateInstance.
        /// </summary>
        public static void Forget()
        {
            Reentered = 0;
            _depth = 0;
        }

        public override Type[] OutTypes => new[] { typeof(IAdvCombat) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            if (_depth == 0)
            {
                _depth++;
                try
                {
                    BlobchegBuild.RebuildAll();
                    Reentered++;
                }
                catch (Exception)
                {
                    // Пакет отбил вложенную пересборку — это и есть ожидаемое поведение.
                }
                finally
                {
                    _depth--;
                }
            }

            w.Add(new AdvGun { Ammo = 4f, Rpm = 4 });
        }
    }

    /// <summary>Кладёт в файл сырой указатель.</summary>
    public sealed class AdvPointerNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override unsafe void Write(ref BlobchegNodeWriter w)
            => w.Add(new AdvPointerRecord { Ptr = (byte*)0xDEADBEEF, Tag = 42 });
    }

    /// <summary>Кладёт в файл указатель, спрятанный на второй ступени вложенности.</summary>
    public sealed class AdvNestedPointerNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override void Write(ref BlobchegNodeWriter w)
            => w.Add(new AdvNestedPointerRecord { Head = 1, Inner = new AdvPointerHolder { Handle = new IntPtr(0x1234) } });
    }

    /// <summary>Запись без полей.</summary>
    public sealed class AdvEmptyRecordNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override void Write(ref BlobchegNodeWriter w) => w.Add(new AdvEmptyRecord());
    }

    /// <summary>Одна запись на мегабайты.</summary>
    public sealed class AdvHugeNodeSo : BlobchegNodeSo
    {
        public int megabytes = 2;

        public override Type[] OutTypes => new[] { typeof(IAdvLoose) };

        public override void Write(ref BlobchegNodeWriter w)
        {
            var bytes = new byte[megabytes * 1024 * 1024];
            for (var i = 0; i < bytes.Length; i += 4096)
                bytes[i] = (byte)(i / 4096);

            bytes[bytes.Length - 1] = 0xFE;
            w.AddBytes<IAdvLoose>(bytes);
        }
    }
}
