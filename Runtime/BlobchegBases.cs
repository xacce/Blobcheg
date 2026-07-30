using System;
using Unity.Burst;

namespace Blobcheg
{
    /// <summary>
    /// Исход перевода слота. Код возврата, а не исключение: перевод зовётся из Burst-кода, где
    /// исключения с собранным сообщением нет, а разные беды обязаны доехать до человека разными
    /// словами.
    /// </summary>
    public enum BlobchegRebase : byte
    {
        /// <summary>Слот пуст или уже в нужной форме.</summary>
        Unchanged = 0,

        /// <summary>Слот переписан.</summary>
        Patched = 1,

        /// <summary>Базы этого домена в процессе нет — переводить не на что.</summary>
        DomainNotRaised = 2,

        /// <summary>Значение не адрес живого поколения и как оффсет не помещается в базу.</summary>
        OutOfRange = 3,

        /// <summary>Как оффсет значение невозможно: внутри header'а или не кратно выравниванию записи.</summary>
        BadOffset = 4,

        /// <summary>По полученному адресу не начинается запись ожидаемого типа.</summary>
        WrongRecord = 5,
    }

    /// <summary>
    /// Реестр адресов поднятых баз: домен → где сейчас лежит его буфер. Нужен ровно тем, кто
    /// работает с записью по указателю, а не по оффсету, — патчу сущностей и его проверкам.
    ///
    /// Живёт в рантайм-сборке, а не рядом с патчем, потому что регистрируется здесь сам
    /// <see cref="BlobchegBlob"/>: базу можно поднять и без Entities, а вопрос «в слоте адрес или
    /// ещё оффсет» задают все одинаково.
    ///
    /// Отставные поколения буфера хранятся рядом с текущим. Пересборка домена в редакторе двигает
    /// базу, и перевести розданные адреса на новую можно только зная старую; двух импортов подряд
    /// в один кадр хватает, чтобы одного прошлого поколения стало мало, поэтому их
    /// <see cref="RetiredGenerations"/>. Отставное поколение не разыменовывается никогда — из него
    /// берётся только арифметика, — поэтому освобождённый буфер остаётся в списке.
    ///
    /// В этом классе не должно появиться ни одного managed-статика: его читает Burst-код, а Бёрст
    /// тянет за собой весь статический конструктор. Имена доменов для сообщений живут отдельно, в
    /// <see cref="BlobchegDomainNames"/>.
    /// </summary>
    public static unsafe class BlobchegBases
    {
        /// <summary>
        /// Потолок доменов в процессе. Реестр — плоский массив с линейным поиском: доменов в проекте
        /// единицы, а хешмапа под Бёрстом стоила бы дороже, чем перебор.
        /// </summary>
        public const int MaxDomains = 64;

        /// <summary>Сколько прошлых поколений буфера домена помним ради перевода указателей.</summary>
        public const int RetiredGenerations = 4;

        internal struct Table
        {
            public fixed ulong Keys[MaxDomains];
            public fixed ulong Ptrs[MaxDomains];
            public fixed int Lengths[MaxDomains];
            public fixed uint DebugOffsets[MaxDomains];
            public fixed ulong RetiredPtrs[MaxDomains * RetiredGenerations];
            public fixed int RetiredLengths[MaxDomains * RetiredGenerations];
            public int Count;
        }

        static readonly SharedStatic<Table> s_Table = SharedStatic<Table>.GetOrCreate<Table>();

        /// <summary>
        /// Ставит базу домена на учёт. Повторная регистрация того же домена — это пересборка:
        /// прежний адрес уходит в отставные поколения, чтобы розданные указатели можно было
        /// перевести на новый буфер.
        /// </summary>
        public static void Register(ulong domainKey, byte* ptr, int length, uint debugOffset = 0)
        {
            if (domainKey == 0)
                throw new ArgumentException("Blobcheg: домен с нулевым ключом", nameof(domainKey));

            if (ptr == null || length < BlobchegFormat.HeaderSize)
                throw new ArgumentException($"Blobcheg: буфер домена {domainKey:X16} пуст или короче header'а");

            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);
            if (slot < 0)
                slot = AddSlot(ref t, domainKey);
            else
                Retire(ref t, slot);

            t.Ptrs[slot] = (ulong)ptr;
            t.Lengths[slot] = length;
            t.DebugOffsets[slot] = debugOffset;
        }

        /// <summary>
        /// Снимает с учёта конкретный буфер, а не домен вообще. Указатель в аргументе обязателен:
        /// при пересборке порядок бывает любой, и <c>Dispose</c> старой базы не должен снести живую
        /// новую.
        ///
        /// Снятый буфер уходит в отставные, а не забывается: указатели в него у сущностей уже есть,
        /// и следующий подъём домена обязан суметь их перевести.
        /// </summary>
        public static void Unregister(ulong domainKey, byte* ptr)
        {
            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);
            if (slot < 0)
                return;

            if (t.Ptrs[slot] != (ulong)ptr)
                return;

            Retire(ref t, slot);

            t.Ptrs[slot] = 0;
            t.Lengths[slot] = 0;
            t.DebugOffsets[slot] = 0;
        }

        /// <summary>Адрес и длина текущего буфера домена. <c>false</c> — база не поднята.</summary>
        public static bool TryGet(ulong domainKey, out byte* ptr, out int length)
        {
            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);
            if (slot < 0 || t.Ptrs[slot] == 0)
            {
                ptr = null;
                length = 0;
                return false;
            }

            ptr = (byte*)t.Ptrs[slot];
            length = t.Lengths[slot];
            return true;
        }

        /// <summary>Отладочный контур текущего буфера домена. Ноль в <paramref name="debugOffset"/> — контура нет.</summary>
        public static bool TryGetDebug(ulong domainKey, out byte* ptr, out uint debugOffset)
        {
            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);
            if (slot < 0 || t.Ptrs[slot] == 0)
            {
                ptr = null;
                debugOffset = 0;
                return false;
            }

            ptr = (byte*)t.Ptrs[slot];
            debugOffset = t.DebugOffsets[slot];
            return true;
        }

        /// <summary>
        /// Уже адрес внутри текущего буфера этого домена, а не оффсет? На этом стоит идемпотентность
        /// патча: оффсет меряется от нуля файла и в диапазон настоящей аллокации не попадает.
        /// </summary>
        public static bool IsAddressOf(ulong domainKey, ulong value)
        {
            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);
            return slot >= 0 && InCurrent(ref t, slot, value);
        }

        /// <summary>
        /// Адрес внутри буфера любой поднятой базы. Вопрос «это вообще указатель» без привязки к
        /// домену; им пользуются проверки чтения, которым домен поля неизвестен.
        /// </summary>
        public static bool IsKnownAddress(ulong value)
        {
            ref var t = ref s_Table.Data;

            for (var i = 0; i < t.Count; i++)
                if (InCurrent(ref t, i, value))
                    return true;

            return false;
        }

        /// <summary>
        /// Превращает содержимое слота в адрес. Три входа в одном, потому что вызывающий их не
        /// различает: в поле может лежать оффсет (сущность только что приехала), адрес текущего
        /// поколения (патч уже был) или адрес отставного (домен пересобрали под живым миром).
        ///
        /// Не бросает: зовётся из Burst-кода. Разбираться с кодом возврата — забота вызывающего.
        /// </summary>
        public static BlobchegRebase TryResolve(ulong domainKey, ulong value, out ulong address)
        {
            address = value;

            if (value == 0)
                return BlobchegRebase.Unchanged;

            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);
            if (slot < 0 || t.Ptrs[slot] == 0)
                return BlobchegRebase.DomainNotRaised;

            var start = t.Ptrs[slot];

            // Уже адрес текущего поколения — патч идемпотентен.
            if (InCurrent(ref t, slot, value))
                return BlobchegRebase.Unchanged;

            var retired = RetiredIndexOf(ref t, slot, value);
            if (retired >= 0)
            {
                // Адрес отставного поколения: домен пересобрали, оффсет тот же, база уехала.
                address = start + (value - t.RetiredPtrs[slot * RetiredGenerations + retired]);
                return BlobchegRebase.Patched;
            }

            // Дальше значение может быть только оффсетом. Внутри header'а записей не бывает, и
            // начало записи всегда выровнено — иначе это не адрес записи, чем бы оно ни было.
            if (value < BlobchegFormat.HeaderSize || (value & (BlobchegFormat.RecordAlign - 1)) != 0)
                return BlobchegRebase.BadOffset;

            if (value >= (ulong)t.Lengths[slot])
                return BlobchegRebase.OutOfRange;

            address = start + value;
            return BlobchegRebase.Patched;
        }

        /// <summary>
        /// Обратный ход: адрес снова в оффсет. Нужен перед записью мира — в файл обязан уехать
        /// оффсет, адрес процесса там бессмыслен. Оффсет на входе оставляется как есть.
        /// </summary>
        public static BlobchegRebase TryUnresolve(ulong domainKey, ulong value, out ulong offset)
        {
            offset = value;

            if (value == 0)
                return BlobchegRebase.Unchanged;

            ref var t = ref s_Table.Data;

            var slot = IndexOf(ref t, domainKey);

            // Домена нет вовсе — значит и адреса в него не бывало, в слоте лежит оффсет. Мир,
            // который никогда не патчили, сохраняется как есть; это единственное место, где
            // ненайденный домен не ошибка.
            if (slot < 0)
                return BlobchegRebase.Unchanged;

            if (InCurrent(ref t, slot, value))
            {
                offset = value - t.Ptrs[slot];
                return BlobchegRebase.Patched;
            }

            var retired = RetiredIndexOf(ref t, slot, value);
            if (retired >= 0)
            {
                offset = value - t.RetiredPtrs[slot * RetiredGenerations + retired];
                return BlobchegRebase.Patched;
            }

            // Не попало ни в одно поколение — значит это не указатель, а число, которое и так
            // оффсет. Хорош он или плох, обратный ход не судит: прямой проход уже отбил его вслух,
            // а второй отказ на том же числе делал бы мир с одной битой ссылкой незаписываемым.
            // Строгость двух направлений одного прохода обязана быть одинаковой.
            return BlobchegRebase.Unchanged;
        }

        /// <summary>Только для тестов: снять всё и начать с чистого реестра.</summary>
        internal static void Clear()
        {
            ref var t = ref s_Table.Data;

            for (var i = 0; i < MaxDomains; i++)
            {
                t.Keys[i] = 0;
                t.Ptrs[i] = 0;
                t.Lengths[i] = 0;
                t.DebugOffsets[i] = 0;

                for (var g = 0; g < RetiredGenerations; g++)
                {
                    t.RetiredPtrs[i * RetiredGenerations + g] = 0;
                    t.RetiredLengths[i * RetiredGenerations + g] = 0;
                }
            }

            t.Count = 0;
        }

        static int AddSlot(ref Table t, ulong domainKey)
        {
            if (t.Count == MaxDomains)
                throw new InvalidOperationException(
                    $"Blobcheg: доменов в процессе больше {MaxDomains} — потолок реестра адресов");

            var slot = t.Count++;
            t.Keys[slot] = domainKey;
            t.Ptrs[slot] = 0;
            t.Lengths[slot] = 0;
            t.DebugOffsets[slot] = 0;

            for (var g = 0; g < RetiredGenerations; g++)
            {
                t.RetiredPtrs[slot * RetiredGenerations + g] = 0;
                t.RetiredLengths[slot * RetiredGenerations + g] = 0;
            }

            return slot;
        }

        /// <summary>Сдвигает текущее поколение в голову списка отставных; самое старое выпадает.</summary>
        static void Retire(ref Table t, int slot)
        {
            if (t.Ptrs[slot] == 0)
                return;

            var b = slot * RetiredGenerations;
            for (var g = RetiredGenerations - 1; g > 0; g--)
            {
                t.RetiredPtrs[b + g] = t.RetiredPtrs[b + g - 1];
                t.RetiredLengths[b + g] = t.RetiredLengths[b + g - 1];
            }

            t.RetiredPtrs[b] = t.Ptrs[slot];
            t.RetiredLengths[b] = t.Lengths[slot];
        }

        static bool InCurrent(ref Table t, int slot, ulong value)
        {
            var start = t.Ptrs[slot];
            return start != 0 && value >= start && value < start + (ulong)t.Lengths[slot];
        }

        static int RetiredIndexOf(ref Table t, int slot, ulong value)
        {
            var b = slot * RetiredGenerations;
            for (var g = 0; g < RetiredGenerations; g++)
            {
                var start = t.RetiredPtrs[b + g];
                if (start != 0 && value >= start && value < start + (ulong)t.RetiredLengths[b + g])
                    return g;
            }

            return -1;
        }

        static int IndexOf(ref Table t, ulong domainKey)
        {
            for (var i = 0; i < t.Count; i++)
                if (t.Keys[i] == domainKey)
                    return i;

            return -1;
        }
    }
}
