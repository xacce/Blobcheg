using System.Collections.Generic;
using UnityEditor;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// The carriers of the nodes, read once per rebuild: the ref assets with record addresses and the
    /// id carriers. That is the journal of handed-out addresses — it lies on the nodes themselves,
    /// travels into git with them and outlives a checkout without a .bcheg, which is why there is no
    /// separate "node → address" manifest file: it would be a duplicate and an eternal question of
    /// which of the two is right.
    ///
    /// It is read before the layout: the writer needs the address BEFORE Flush, and earlier the carriers
    /// were fetched after it — one <c>LoadAllAssetsAtPath</c> per record instead of one per node.
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
        /// The same thing, but the carriers of untouched nodes are taken from the cache: the sub-assets
        /// of a node are only changed by the rebuild, and it is the rebuild that puts what it wrote into
        /// the cache.
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
        /// A reimport may have destroyed the objects the cache holds references to. A destroyed carrier
        /// compares equal to null, the rebuild decides that there is no carrier and creates a second
        /// one — which is why such a list is unfit as a whole.
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

        /// <summary>The carrier lists of a node — the cache keeps the same ones so as not to read the asset again.</summary>
        public List<BlobchegRefSo> RefListOf(BlobchegNodeSo node)
            => _refs.TryGetValue(node, out var found) ? found : new List<BlobchegRefSo>();

        public List<BlobchegIdSo> IdListOf(BlobchegNodeSo node)
            => _ids.TryGetValue(node, out var found) ? found : new List<BlobchegIdSo>();
    }
}
