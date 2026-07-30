using System;
using UnityEngine;

namespace Blobcheg
{
    /// <summary>
    /// Носитель адреса из едитора в билд и единственный способ сохранить оффсет: sub-asset на пару
    /// (нода × домен), стабильный по identity. Тип ассета на всю систему один — типизировать сам
    /// ассет нельзя, у типа из кодогена нет MonoScript, а плодить руками класс на каждую запись
    /// потребитель не обязан. Типизировано поле, см. <see cref="BlobchegRef{T}"/>.
    /// </summary>
    public sealed class BlobchegRefSo : ScriptableObject
    {
        /// <summary>Абсолютный оффсет записи в файле базы. Перевыставляется каждой пересборкой.</summary>
        public uint offset;

        [SerializeField] internal string domainName;
        [SerializeField] internal string recordType;
        [SerializeField] internal long revision;

        public string DomainName => domainName;

        /// <summary>Полное имя типа записи. Пусто — сырые байты.</summary>
        public string RecordType => recordType;
    }

    /// <summary>
    /// Тип поля у потребителя: <c>public BlobchegRef&lt;GunData&gt; gun;</c>. Присвоить
    /// <c>BlobchegRef&lt;ShieldData&gt;</c> не даст компилятор, положить в пикере чужой ассет — драйвер,
    /// а несовпадение на бейке — ошибка, а не тихий ноль.
    /// </summary>
    [Serializable]
    public struct BlobchegRef<T> where T : unmanaged
    {
        [SerializeField] internal BlobchegRefSo asset;

        public BlobchegRef(BlobchegRefSo asset) => this.asset = asset;

        /// <summary>Сам ассет — для <c>DependsOn</c> в бейкере.</summary>
        public BlobchegRefSo Asset => asset;

        public bool IsSet => asset != null;

        /// <summary>Адрес записи. Пустой ref или ассет чужого типа — исключение.</summary>
        public uint Offset
        {
            get
            {
                if (asset == null)
                    throw new InvalidOperationException(
                        $"Blobcheg: пустой BlobchegRef<{typeof(T).Name}> — ассет записи не назначен");

                var expected = typeof(T).FullName;
                if (!string.Equals(asset.recordType, expected, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Blobcheg: в BlobchegRef<{typeof(T).Name}> лежит ассет '{asset.name}' с записью " +
                        $"'{asset.recordType}' — ожидалось '{expected}'");

                return asset.offset;
            }
        }

        /// <summary>
        /// Слот для компонента: тот же адрес, но в форме, которую патч импорта превратит в
        /// указатель. Проверка типа записи та же, что у <see cref="Offset"/>.
        /// </summary>
        public BlobchegReference<T> ToReference() => new BlobchegReference<T>(Offset);
    }

    /// <summary>
    /// То же поле без параметра — под записи из <c>AddBytes</c>. Типа у них нет, значит и проверять
    /// нечего: осознанная дыра ровно там, где потребитель сам отказался от типа.
    /// </summary>
    [Serializable]
    public struct BlobchegRawRef
    {
        [SerializeField] internal BlobchegRefSo asset;

        public BlobchegRawRef(BlobchegRefSo asset) => this.asset = asset;

        public BlobchegRefSo Asset => asset;

        public bool IsSet => asset != null;

        public uint Offset
        {
            get
            {
                if (asset == null)
                    throw new InvalidOperationException("Blobcheg: пустой BlobchegRawRef — ассет записи не назначен");

                return asset.offset;
            }
        }
    }
}
