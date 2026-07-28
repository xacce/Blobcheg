using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;

namespace Blobcheg.Authoring
{
    /// <summary>Что нода отдала в домен: тикет писателя плюс всё, что нужно ref-ассету.</summary>
    sealed class BlobchegEntry
    {
        public BlobchegNodeSo Node;
        public Type Domain;
        public int Ticket;
        public string RecordType;

        /// <summary>Байты записи и хеш типа — их же кладёт в кеш пересборка, чтобы не звать Write.</summary>
        public byte[] Bytes;

        public uint TypeHash;
    }

    /// <summary>
    /// Набор открытых писателей на одну пересборку. Прослойки-коллектора между нодой и писателем
    /// нет: Authoring — editor-only сборка и зовёт <see cref="BlobchegWriter"/> напрямую.
    /// </summary>
    sealed class BlobchegCollector
    {
        readonly string _directory;
        readonly Dictionary<Type, BlobchegWriter> _writers = new Dictionary<Type, BlobchegWriter>();
        readonly HashSet<string> _written = new HashSet<string>(StringComparer.Ordinal);

        // Про ноду всё спрашивается один раз за пересборку. GUID и имя — нативные вызовы в базу
        // ассетов, OutTypes у обычной ноды собирает массив заново на каждый спрос, а спрашивают их
        // на КАЖДУЮ запись: на 10 000 нод это десятки тысяч вызовов ради трёх неизменных значений.
        readonly Dictionary<BlobchegNodeSo, About> _about = new Dictionary<BlobchegNodeSo, About>();

        readonly Dictionary<Type, List<BlobchegRecord>> _pending = new Dictionary<Type, List<BlobchegRecord>>();

        struct About
        {
            public string Guid;
            public string Name;
            public Type[] OutTypes;
        }

        public BlobchegCollector(string directory) => _directory = directory;

        public IReadOnlyDictionary<Type, BlobchegWriter> Writers => _writers;

        public List<BlobchegEntry> Entries { get; } = new List<BlobchegEntry>();

        public BlobchegWriter WriterOf(Type domain)
        {
            if (!_writers.TryGetValue(domain, out var writer))
            {
                writer = BlobchegWriter.Open(_directory, BlobchegDomains.NameOf(domain));
                _writers.Add(domain, writer);
            }

            return writer;
        }

        public void Add(BlobchegNodeSo node, Type domain, string recordTypeName, uint typeHash, byte[] bytes)
        {
            var about = AboutOf(node);

            // Текст ошибки собирается только когда ошибка есть: на пустом заходе через Add проходят
            // все записи проекта, и интерполяция на каждую — это и есть цена «ничего не менялось».
            if (Array.IndexOf(BlobchegDomains.All, domain) < 0)
                BlobchegDomains.RequireDeclared(domain, $"запись ноды '{about.Name}'");

            if (Array.IndexOf(about.OutTypes, domain) < 0)
                throw new InvalidOperationException(
                    $"Blobcheg: нода '{about.Name}' пишет в домен '{domain.Name}', которого нет в её OutTypes");

            if (!_written.Add(domain.FullName + " " + about.Guid))
                throw new InvalidOperationException(
                    $"Blobcheg: нода '{about.Name}' пишет в домен '{domain.Name}' второй раз — " +
                    "одна нода даёт базе ровно одну запись");

            // Записи копятся пачкой и уезжают писателю в Handover: позиция в пачке — это тикет.
            if (!_pending.TryGetValue(domain, out var pending))
                _pending[domain] = pending = new List<BlobchegRecord>();

            pending.Add(new BlobchegRecord(recordTypeName, about.Guid, typeHash, about.Name, bytes));
            var ticket = pending.Count - 1;

            Entries.Add(new BlobchegEntry
            {
                Node = node,
                Domain = domain,
                Ticket = ticket,
                RecordType = recordTypeName ?? string.Empty,
                Bytes = bytes,
                TypeHash = typeHash,
            });
        }

        /// <summary>Накопленные записи уезжают писателям. Зовётся один раз, перед Flush.</summary>
        public void Handover()
        {
            foreach (var pair in _pending)
                WriterOf(pair.Key).AppendAll(pair.Value);
        }

        public bool Wrote(BlobchegNodeSo node, Type domain)
            => _written.Contains(domain.FullName + " " + AboutOf(node).Guid);

        About AboutOf(BlobchegNodeSo node)
        {
            if (_about.TryGetValue(node, out var about))
                return about;

            about = new About { Guid = GuidOf(node), Name = node.name, OutTypes = node.OutTypes ?? Type.EmptyTypes };
            _about.Add(node, about);
            return about;
        }

        public static string GuidOf(BlobchegNodeSo node)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(node, out var guid, out long _))
                throw new InvalidOperationException(
                    $"Blobcheg: нода '{node.name}' не ассет проекта — раскладке нужен стабильный ключ порядка");

            return guid;
        }
    }

    /// <summary>
    /// То, что нода видит в <see cref="BlobchegNodeSo.Write"/>. Домен выводится из маркер-интерфейса
    /// записи — руками его называть не нужно.
    /// </summary>
    public struct BlobchegNodeWriter
    {
        internal BlobchegCollector Collector;
        internal BlobchegNodeSo Node;
        internal BlobchegIdTable Ids;

        /// <summary>
        /// Свой <see cref="BlobchegId"/> — его можно положить прямо в запись. Известен уже здесь,
        /// потому что раздаётся по OutTypes, до записи. Роутеров у ноды ноль или несколько —
        /// исключение, а не догадка.
        /// </summary>
        public BlobchegId Id => Ids.Single(Node);

        /// <summary>Свой id в конкретном роутере — форма для ноды, входящей сразу в несколько.</summary>
        public BlobchegId IdIn<TRouter>() where TRouter : unmanaged, IBlobchegRouter
            => Ids.Of(Node, typeof(TRouter));

        /// <summary>Id чужой ноды — так одна запись ссылается на другую, не зная её оффсетов.</summary>
        public BlobchegId IdOf(BlobchegNodeSo other)
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other), "Blobcheg: id несуществующей ноды");

            return Ids.Single(other);
        }

        public BlobchegId IdOf<TRouter>(BlobchegNodeSo other) where TRouter : unmanaged, IBlobchegRouter
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other), "Blobcheg: id несуществующей ноды");

            return Ids.Of(other, typeof(TRouter));
        }

        /// <summary>Типизированная запись. Домен берётся из маркер-интерфейса <typeparamref name="T"/>.</summary>
        public unsafe void Add<T>(in T record) where T : unmanaged
        {
            BlobchegRecordTypes.Require(typeof(T));

            var bytes = new byte[UnsafeUtility.SizeOf<T>()];
            var copy = record;
            fixed (byte* destination = bytes)
                UnsafeUtility.CopyStructureToPtr(ref copy, destination);

            Collector.Add(Node, BlobchegDomains.DomainOf(typeof(T)), typeof(T).FullName,
                unchecked((uint)BurstRuntime.GetHashCode32<T>()), bytes);
        }

        /// <summary>Сырой путь: типа у записи нет, значит нет и проверок по нему.</summary>
        public void AddBytes<TDomain>(ReadOnlySpan<byte> record)
        {
            Collector.Add(Node, typeof(TDomain), null, 0, record.ToArray());
        }
    }
}
