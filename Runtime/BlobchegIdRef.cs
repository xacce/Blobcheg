using System;
using UnityEngine;

namespace Blobcheg
{
    // The anchor of identity for a (node x router) pair. The id itself lives next to the bases.
    public sealed class BlobchegIdSo : ScriptableObject
    {
        [SerializeField] internal string routerName;

        // The row travels in git with the node: an addition or a deletion elsewhere does not move it.
        [SerializeField] internal int row = -1;

        // Whose row it is: a duplicated asset carries a foreign owner and sits down as a newcomer.
        [SerializeField] internal string owner;

        public string RouterName => routerName;

        public BlobchegId Id => new BlobchegId(BlobchegStamps.IdOf(this));
    }

    /// <summary>
    /// The field on the consumer: <c>public BlobchegIdRef&lt;GameRouter&gt; gun;</c>. A foreign router
    /// will not be assigned by the compiler, a foreign asset is rejected by the drawer, and an empty
    /// field throws instead of returning zero.
    /// </summary>
    [Serializable]
    public struct BlobchegIdRef<TRouter> where TRouter : unmanaged, IBlobchegRouter
    {
        [SerializeField] internal BlobchegIdSo asset;

        public BlobchegIdRef(BlobchegIdSo asset) => this.asset = asset;

        /// <summary>The asset itself — for <c>DependsOn</c> in a baker.</summary>
        public BlobchegIdSo Asset => asset;

        public bool IsSet => asset != null;

        /// <summary>The router name of this field. Taken from the type parameter, not written by hand.</summary>
        public static string RouterName => default(TRouter).Name;

        /// <summary>The id of the node. An empty field, the asset of a foreign router or an unassigned id — an exception.</summary>
        public BlobchegId Id
        {
            get
            {
                if (asset == null)
                    throw new InvalidOperationException(
                        $"Blobcheg: an empty BlobchegIdRef<{typeof(TRouter).Name}> — no node is assigned");

                var expected = RouterName;
                if (!string.Equals(asset.routerName, expected, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Blobcheg: BlobchegIdRef<{typeof(TRouter).Name}> holds asset '{asset.name}' of router " +
                        $"'{asset.routerName}' — '{expected}' was expected");

                var id = asset.Id;
                if (!id.IsValid)
                    throw new InvalidOperationException(
                        $"Blobcheg: asset '{asset.name}' has no id — the rebuild never reached it");

                return id;
            }
        }
    }
}
