using System.Collections.Generic;
using UnityEditor;

namespace Blobcheg.Authoring
{
    // Which carrier a node already has: read once, so the rebuild breeds no second one for a pair.
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

        /// <summary>A freshly created carrier enters the journal at once: it is not in the node file yet.</summary>
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

        /// <summary>A carrier left the asset — it is obliged to leave the journal in the same motion.</summary>
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
    }
}
