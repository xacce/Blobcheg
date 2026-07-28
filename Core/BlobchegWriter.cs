using System;
using System.Collections.Generic;
using System.IO;

namespace Blobcheg
{
    /// <summary>Одна запись на входе писателя. Тип нужен раскладке, имя ноды — только debug-секции.</summary>
    public readonly struct BlobchegRecord
    {
        /// <summary>Полное имя типа записи. <c>null</c> — сырой блок, такие ложатся в хвост файла.</summary>
        public readonly string TypeName;

        /// <summary>Стабильный ключ порядка внутри типа. Пайплайн передаёт GUID ассета ноды.</summary>
        public readonly string SortKey;

        /// <summary>BurstRuntime.GetHashCode32 типа, 0 для сырых. Едет только в debug-секцию.</summary>
        public readonly uint TypeHash;

        /// <summary>Имя ноды для debug-секции.</summary>
        public readonly string NodeName;

        public readonly byte[] Bytes;

        public BlobchegRecord(string typeName, string sortKey, uint typeHash, string nodeName, byte[] bytes)
        {
            TypeName = typeName;
            SortKey = sortKey ?? throw new ArgumentNullException(nameof(sortKey));
            TypeHash = typeHash;
            NodeName = nodeName ?? string.Empty;
            Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        }

        public bool IsRaw => string.IsNullOrEmpty(TypeName);
    }

    /// <summary>
    /// Писатель базы: обычный C# на <see cref="System.IO"/>, ничего от Unity не хочет.
    /// Оффсет не выдаётся в момент <see cref="Append"/> — раскладка зависит от полного набора
    /// записей, поэтому Append возвращает тикет, а <see cref="Flush"/> меняет тикеты на оффсеты.
    /// </summary>
    public sealed class BlobchegWriter
    {
        readonly List<BlobchegRecord> _records = new List<BlobchegRecord>();
        readonly HashSet<string> _keys = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<int, uint> _claims = new Dictionary<int, uint>();

        uint[] _offsets;
        ulong[] _revisions;
        bool _flushed;

        BlobchegWriter(string directory, string domainName)
        {
            Directory = directory ?? throw new ArgumentNullException(nameof(directory));
            DomainName = domainName;
            FilePath = Path.Combine(directory, BlobchegNaming.FileName(domainName));
        }

        public string Directory { get; }
        public string DomainName { get; }
        public string FilePath { get; }

        /// <summary>Хеш содержимого последней раскладки. До <see cref="Flush"/> — ошибка.</summary>
        public ulong ContentHash { get; private set; }

        /// <summary>Файл на диске отличался от собранного и был переписан.</summary>
        public bool FileChanged { get; private set; }

        public int RecordCount => _records.Count;

        public static BlobchegWriter Open(string directory, string domainName)
            => new BlobchegWriter(directory, domainName);

        /// <summary>Кладёт запись в очередь и возвращает тикет. Байты копируются вызывающим заранее.</summary>
        public int Append(in BlobchegRecord record)
        {
            if (_flushed)
                throw new InvalidOperationException(
                    $"Blobcheg: Append в домен '{DomainName}' после Flush — раскладка уже посчитана");

            var key = (record.TypeName ?? string.Empty) + " " + record.SortKey;
            if (!_keys.Add(key))
                throw new InvalidOperationException(
                    $"Blobcheg: в домене '{DomainName}' две записи типа '{record.TypeName}' с одним ключом " +
                    $"'{record.SortKey}' — одна нода пишет в базу ровно одну запись");

            _records.Add(record);
            return _records.Count - 1;
        }

        /// <summary>
        /// Пачка записей за один вызов. Тикеты идут подряд от текущего конца, поэтому позиция
        /// записи в пачке — это и есть её тикет.
        ///
        /// Существует ради цены вызова: в едиторном рантайме один заход через границу сборки стоит
        /// ощутимо дороже самой работы внутри, а записей на пересборку — по одной на ноду в домене.
        /// </summary>
        public int AppendAll(List<BlobchegRecord> records)
        {
            var first = _records.Count;

            for (var i = 0; i < records.Count; i++)
                Append(records[i]);

            return first;
        }

        /// <summary>
        /// Адрес, который эта запись уже получила прошлой пересборкой. Источник — носитель ноды,
        /// поэтому журнал адресов живёт в гите вместе с нодой и переживает чекаут без .bcheg.
        ///
        /// Заявка — это просьба, а не приказ: если запись выросла и наехала на соседнюю, соседняя
        /// заявку теряет и уезжает в хвост. Иначе файл собрался бы с наложением записей.
        /// </summary>
        public void Claim(int ticket, uint offset)
        {
            if (_flushed)
                throw new InvalidOperationException(
                    $"Blobcheg: Claim в домен '{DomainName}' после Flush — раскладка уже посчитана");

            if (ticket < 0 || ticket >= _records.Count)
                throw new ArgumentOutOfRangeException(nameof(ticket),
                    $"Blobcheg: домен '{DomainName}' — заявка на тикет {ticket}, а записей {_records.Count}");

            // Мусорный адрес — не повод разложить файл криво: заявка просто не учитывается, запись
            // получит место в хвосте, а носитель — новый адрес.
            if (offset < BlobchegFormat.HeaderSize || offset % BlobchegFormat.RecordAlign != 0)
                return;

            _claims[ticket] = offset;
        }

        /// <summary>
        /// Раскладывает записи группами по конечному типу, считает оффсеты и целостность, пишет
        /// файл атомарно. Если содержимое совпало с тем, что уже лежит на диске, файл не трогается.
        /// </summary>
        public void Flush(bool withDebug = false)
        {
            if (_flushed)
                throw new InvalidOperationException($"Blobcheg: повторный Flush домена '{DomainName}'");

            // Пустой базе описывать нечего, а секция из нуля записей сделала бы её длиннее header'а
            // и утащила бы за собой смысл «в базе не осталось ни одной ноды».
            withDebug &= _records.Count > 0;

            var order = BuildOrder();
            var file = Layout(order, withDebug, out var offsets);

            _offsets = offsets;
            _revisions = new ulong[_records.Count];
            for (var i = 0; i < _records.Count; i++)
                _revisions[i] = BlobchegHash.Of(_records[i].Bytes);

            var flags = withDebug ? BlobchegFormat.FlagHasDebug : (ushort)0;
            ContentHash = BlobchegBytes.Seal(file, flags, withDebug ? DebugOffset : 0u,
                BlobchegNaming.NameHash(DomainName));

            _flushed = true;
            FileChanged = BlobchegBytes.WriteIfChanged(Directory, FilePath, file, ContentHash);
        }

        /// <summary>Адрес записи. Единственное, что вообще существует; до Flush — ошибка.</summary>
        public uint OffsetOf(int ticket)
        {
            RequireFlushed(nameof(OffsetOf));
            return _offsets[ticket];
        }

        /// <summary>Ревизия записи — хеш её байтов. Ключ инкрементальности; до Flush — ошибка.</summary>
        public ulong RevisionOf(int ticket)
        {
            RequireFlushed(nameof(RevisionOf));
            return _revisions[ticket];
        }

        void RequireFlushed(string what)
        {
            if (!_flushed)
                throw new InvalidOperationException(
                    $"Blobcheg: {what} до Flush домена '{DomainName}' — раскладка ещё не посчитана");
        }

        /// <summary>
        /// Порядок не зависит от порядка обхода: типы по FullName, внутри типа по ключу ноды,
        /// сырые блоки переменной длины — в хвост, чтобы не таскать за собой типизированные.
        /// </summary>
        int[] BuildOrder()
        {
            var order = new int[_records.Count];
            for (var i = 0; i < order.Length; i++)
                order[i] = i;

            Array.Sort(order, (a, b) =>
            {
                var ra = _records[a];
                var rb = _records[b];

                var rawA = ra.IsRaw ? 1 : 0;
                var rawB = rb.IsRaw ? 1 : 0;
                if (rawA != rawB)
                    return rawA - rawB;

                if (rawA == 0)
                {
                    var byType = string.CompareOrdinal(ra.TypeName, rb.TypeName);
                    if (byType != 0)
                        return byType;
                }

                return string.CompareOrdinal(ra.SortKey, rb.SortKey);
            });

            return order;
        }

        /// <summary>
        /// Заявленные адреса занимают свои места, всё остальное ложится за ними в хвост. Дырка от
        /// удалённой ноды остаётся нулями: подвинуть соседей — значит сдвинуть чужие адреса, а на
        /// них через DependsOn завязаны уже запечённые субсцены.
        ///
        /// Заявок нет вовсе (первая сборка, компакт) — раскладка ровно та же, что была всегда:
        /// группами по типу, сырые в хвост.
        /// </summary>
        byte[] Layout(int[] order, bool withDebug, out uint[] offsets)
        {
            offsets = new uint[_records.Count];
            var placed = new bool[_records.Count];

            var position = BlobchegFormat.HeaderSize;

            if (_claims.Count > 0)
            {
                var rank = new int[_records.Count];
                for (var i = 0; i < order.Length; i++)
                    rank[order[i]] = i;

                // По возрастанию адреса: наложение видно только в этом порядке. Одинаковые адреса
                // (склонированный носитель) разводит прежний детерминированный порядок.
                var claimed = new List<int>(_claims.Keys);
                claimed.Sort((a, b) => _claims[a] != _claims[b]
                    ? _claims[a].CompareTo(_claims[b])
                    : rank[a].CompareTo(rank[b]));

                foreach (var ticket in claimed)
                {
                    var claim = (int)_claims[ticket];
                    if (claim < position)
                        continue;

                    offsets[ticket] = (uint)claim;
                    placed[ticket] = true;
                    position = claim + SpanOf(ticket);
                }
            }

            for (var i = 0; i < order.Length; i++)
            {
                var ticket = order[i];
                if (placed[ticket])
                    continue;

                position = BlobchegFormat.AlignUp(position);
                offsets[ticket] = (uint)position;
                position += SpanOf(ticket);
            }

            var debugOffset = 0;
            byte[] debugSection = null;
            if (withDebug)
            {
                position = BlobchegFormat.AlignUp(position);
                debugOffset = position;
                debugSection = BuildDebugSection(order, offsets, (uint)debugOffset);
                position += debugSection.Length;
            }

            var file = new byte[position];
            for (var i = 0; i < order.Length; i++)
            {
                var record = _records[order[i]];
                Buffer.BlockCopy(record.Bytes, 0, file, (int)offsets[order[i]], record.Bytes.Length);
            }

            if (debugSection != null)
                Buffer.BlockCopy(debugSection, 0, file, debugOffset, debugSection.Length);

            DebugOffset = (uint)debugOffset;
            return file;
        }

        /// <summary>
        /// Сколько места запись занимает в раскладке. Запись нулевой длины занимает байт, а не ноль:
        /// иначе позиция после неё не двигается, следующее выравнивание возвращает тот же адрес, и
        /// две разные записи получают ОДИН адрес — а адрес и есть единственная личность записи.
        /// </summary>
        int SpanOf(int ticket)
        {
            var length = _records[ticket].Bytes.Length;
            return length > 0 ? length : 1;
        }

        uint DebugOffset { get; set; }

        /// <summary>
        /// Записи секции идут по возрастанию оффсета: <see cref="BlobchegDebugSection.Find"/> ищет
        /// двоичным поиском. Порядок раскладки для этого больше не годится — заявленный адрес
        /// ставит запись куда угодно, а не следом за предыдущей.
        /// </summary>
        byte[] BuildDebugSection(int[] layoutOrder, uint[] offsets, uint sectionOffset)
        {
            var order = (int[])layoutOrder.Clone();
            Array.Sort(order, (a, b) => offsets[a].CompareTo(offsets[b]));

            var count = order.Length;
            var namesStart = sectionOffset + BlobchegDebugSection.PrologSize + (uint)(count * BlobchegDebugSection.EntrySize);

            var names = new MemoryStream();
            var nameOffsets = new uint[count];
            for (var i = 0; i < count; i++)
            {
                nameOffsets[i] = namesStart + (uint)names.Length;
                var record = _records[order[i]];
                BlobchegBytes.WriteString(names, record.TypeName ?? string.Empty);
                BlobchegBytes.WriteString(names, record.NodeName);
            }

            var section = new MemoryStream();
            var w = new BinaryWriter(section);
            w.Write(BlobchegDebugSection.Magic);
            w.Write((uint)count);
            for (var i = 0; i < count; i++)
            {
                var index = order[i];
                w.Write(offsets[index]);
                w.Write((uint)_records[index].Bytes.Length);
                w.Write(_records[index].TypeHash);
                w.Write(nameOffsets[i]);
            }

            w.Write(names.ToArray());
            w.Flush();
            return section.ToArray();
        }

    }
}
