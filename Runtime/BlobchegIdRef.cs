using System;
using UnityEngine;

namespace Blobcheg
{
    /// <summary>
    /// The carrier of a <see cref="BlobchegId"/> from the editor into the build: a sub-asset per
    /// (node × router) pair. A separate asset is needed for the same reason as <see cref="BlobchegRefSo"/>:
    /// the node lives in an editor-only assembly, and runtime authoring cannot reference it.
    /// </summary>
    public sealed class BlobchegIdSo : ScriptableObject
    {
        /// <summary>
        /// The value of a <see cref="BlobchegId"/>: the router tag and the row position. Re-stamped by
        /// every rebuild. Zero means "not assigned", also the value of a freshly created carrier.
        /// </summary>
        public uint id = BlobchegId.NoneValue;

        [SerializeField] internal string routerName;

        public string RouterName => routerName;
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

                var id = new BlobchegId(asset.id);
                if (!id.IsValid)
                    throw new InvalidOperationException(
                        $"Blobcheg: asset '{asset.name}' has no id — the rebuild never reached it");

                return id;
            }
        }
    }
}
