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

                carriers._refs[node] = refs;
                carriers._ids[node] = ids;
            }

            return carriers;
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
    }
}
