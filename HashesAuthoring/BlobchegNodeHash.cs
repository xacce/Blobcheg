using System;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// The hash of a node at bake time. It lives apart from <see cref="BlobchegNodeWriter"/> on
    /// purpose: the main writer knows nothing about hashes, and a node that needs a hash in its record
    /// references this assembly itself.
    /// </summary>
    public static class BlobchegNodeHash
    {
        /// <summary>
        /// The hash of the node name in this router — it can be put straight into a record, like
        /// <c>writer.Id</c>. The name is known before <see cref="BlobchegNodeSo.Write"/>: the rebuild
        /// stamps it earlier.
        /// </summary>
        public static ulong HashIn<TRouter>(this BlobchegNodeSo node)
            where TRouter : unmanaged, IBlobchegRouter
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node), "Blobcheg: the hash of a node that does not exist");

            var routerName = default(TRouter).Name;

            var name = node.BlobchegName;
            if (string.IsNullOrEmpty(name))
                throw new InvalidOperationException(
                    $"Blobcheg: node '{node.name}' has an empty name — there is nothing to compute a hash from. " +
                    "The rebuild stamps it itself, so this call is going around it");

            // A node outside the router would get a hash the table has nothing to hand back for: it has
            // no row there. Staying silent about that is not an option — the error would only surface at
            // runtime, while loading a save.
            if (!BlobchegRouters.RoutersOf(node).Contains(typeof(TRouter)))
                throw new InvalidOperationException(
                    $"Blobcheg: node '{node.name}' writes into no base of router '{routerName}' — " +
                    "it has no row in its table, and the hash would lead nowhere");

            return BlobchegHashKey.Of(routerName, name);
        }
    }
}
