using System.Collections.Generic;
using UnityEditor;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Носители нод, прочитанные один раз на пересборку: ref-ассеты с адресами записей и носители
    /// id. Это и есть журнал выданных адресов — он лежит на самих нодах, едет в гит вместе с ними и
    /// переживает чекаут без .bcheg, поэтому отдельного файла-манифеста «нода → адрес» нет: он был
    /// бы дублем и вечным вопросом, кто из двоих прав.
    ///
    /// Читается до раскладки: адрес нужен писателю ДО Flush, а раньше носители доставались уже
    /// после — по одному <c>LoadAllAssetsAtPath</c> на каждую запись вместо одного на ноду.
    /// </summary>
    sealed class BlobchegCarriers
    {
        readonly Dictionary<BlobchegNodeSo, List<BlobchegRefSo>> _refs =
            new Dictionary<BlobchegNodeSo, List<BlobchegRefSo>>();

        readonly Dictionary<BlobchegNodeSo, List<BlobchegIdSo>> _ids =
            new Dictionary<BlobchegNodeSo, List<BlobchegIdSo>>();

        public static BlobchegCarriers Read(IReadOnlyList<BlobchegNodeSo> nodes)
        {
            var carriers = new BlobchegCarriers();

            foreach (var node in nodes)
                carriers.ReadOne(node);

            return carriers;
        }

        /// <summary>
        /// То же самое, но носители нетронутых нод берутся из кеша: сабассеты ноды меняет только
        /// пересборка, и она же кладёт в кеш то, что записала.
        /// </summary>
        public static BlobchegCarriers Read(IReadOnlyList<BlobchegCache.Entry> entries)
        {
            var carriers = new BlobchegCarriers();

            foreach (var entry in entries)
            {
                if (!entry.Dirty && Alive(entry.Refs) && Alive(entry.Ids))
                {
                    carriers._refs[entry.Node] = entry.Refs;
                    carriers._ids[entry.Node] = entry.Ids;
                    continue;
                }

                carriers.ReadOne(entry.Node);
            }

            return carriers;
        }

        /// <summary>
        /// Переимпорт мог уничтожить объекты, на которые кеш держит ссылки. Уничтоженный носитель
        /// сравнится с null, пересборка сочтёт, что носителя нет, и заведёт второй — поэтому такой
        /// список не годится целиком.
        /// </summary>
        static bool Alive<T>(List<T> carriers) where T : UnityEngine.Object
        {
            if (carriers == null)
                return false;

            foreach (var carrier in carriers)
            {
                if (carrier == null)
                    return false;
            }

            return true;
        }

        void ReadOne(BlobchegNodeSo node)
        {
            var refs = new List<BlobchegRefSo>();
            var ids = new List<BlobchegIdSo>();

            var path = AssetDatabase.GetAssetPath(node);
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    switch (asset)
                    {
                        case BlobchegRefSo reference:
                            refs.Add(reference);
                            break;
                        case BlobchegIdSo carrier:
                            ids.Add(carrier);
                            break;
                    }
                }
            }

            _refs[node] = refs;
            _ids[node] = ids;
        }

        public IReadOnlyList<BlobchegRefSo> RefsOf(BlobchegNodeSo node)
            => _refs.TryGetValue(node, out var found) ? found : (IReadOnlyList<BlobchegRefSo>)new List<BlobchegRefSo>();

        public IReadOnlyList<BlobchegIdSo> IdsOf(BlobchegNodeSo node)
            => _ids.TryGetValue(node, out var found) ? found : (IReadOnlyList<BlobchegIdSo>)new List<BlobchegIdSo>();

        public BlobchegRefSo Ref(BlobchegNodeSo node, string domainName)
        {
            foreach (var reference in RefsOf(node))
            {
                if (reference.domainName == domainName)
                    return reference;
            }

            return null;
        }

        public BlobchegIdSo Id(BlobchegNodeSo node, string routerName)
        {
            foreach (var carrier in IdsOf(node))
            {
                if (carrier.RouterName == routerName)
                    return carrier;
            }

            return null;
        }

        /// <summary>Свежесозданный носитель попадает в журнал сразу: его ещё нет в файле ноды.</summary>
        public void Add(BlobchegNodeSo node, BlobchegRefSo reference)
        {
            if (!_refs.TryGetValue(node, out var refs))
                _refs[node] = refs = new List<BlobchegRefSo>();

            refs.Add(reference);
        }

        public void Add(BlobchegNodeSo node, BlobchegIdSo carrier)
        {
            if (!_ids.TryGetValue(node, out var ids))
                _ids[node] = ids = new List<BlobchegIdSo>();

            ids.Add(carrier);
        }

        /// <summary>Носитель уехал с ассета — из журнала он обязан уехать тем же движением.</summary>
        public void Forget(BlobchegNodeSo node, BlobchegRefSo reference)
        {
            if (_refs.TryGetValue(node, out var refs))
                refs.Remove(reference);
        }

        public void Forget(BlobchegNodeSo node, BlobchegIdSo carrier)
        {
            if (_ids.TryGetValue(node, out var ids))
                ids.Remove(carrier);
        }

        /// <summary>Списки носителей ноды — их же кладёт себе кеш, чтобы не читать ассет заново.</summary>
        public List<BlobchegRefSo> RefListOf(BlobchegNodeSo node)
            => _refs.TryGetValue(node, out var found) ? found : new List<BlobchegRefSo>();

        public List<BlobchegIdSo> IdListOf(BlobchegNodeSo node)
            => _ids.TryGetValue(node, out var found) ? found : new List<BlobchegIdSo>();
    }
}
