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
            BlobchegDomains.RequireDeclared(domain, $"запись ноды '{node.name}'");

            if (Array.IndexOf(node.OutTypes, domain) < 0)
                throw new InvalidOperationException(
                    $"Blobcheg: нода '{node.name}' пишет в домен '{domain.Name}', которого нет в её OutTypes");

            var guid = GuidOf(node);
            if (!_written.Add(domain.FullName + " " + guid))
                throw new InvalidOperationException(
                    $"Blobcheg: нода '{node.name}' пишет в домен '{domain.Name}' второй раз — " +
                    "одна нода даёт базе ровно одну запись");

            var ticket = WriterOf(domain).Append(
                new BlobchegRecord(recordTypeName, guid, typeHash, node.name, bytes));

            Entries.Add(new BlobchegEntry
            {
                Node = node,
                Domain = domain,
                Ticket = ticket,
                RecordType = recordTypeName ?? string.Empty,
            });
        }

        public bool Wrote(BlobchegNodeSo node, Type domain)
            => _written.Contains(domain.FullName + " " + GuidOf(node));

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

        /// <summary>Типизированная запись. Домен берётся из маркер-интерфейса <typeparamref name="T"/>.</summary>
        public unsafe void Add<T>(in T record) where T : unmanaged
        {
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
