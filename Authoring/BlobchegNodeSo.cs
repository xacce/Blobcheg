using System;
using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// The unit of data in the editor. It decides which bases to write into itself; into each one
    /// exactly one record. It has no Save button: save the asset and the pipeline rebuilds the domain
    /// on its own.
    /// </summary>
    public abstract class BlobchegNodeSo : ScriptableObject
    {
        [Tooltip("The stable name of the node. It outlives a rename of the asset, a compaction and the "
                 + "deletion of neighbours — the hash a save addresses the record with is computed from "
                 + "it. An empty one is filled once with the asset name; after that the name must not be "
                 + "changed, other people's saves already remember it.")]
        [SerializeField] string blobchegName;

        /// <summary>
        /// The stable name of the node. Everything else about it — the GUID, the file name, the offsets,
        /// the id — is either invisible to the consumer or does not outlive everything: addresses and
        /// ids move on a compaction, and the file name is changed by a human with the mouse.
        /// </summary>
        public string BlobchegName => blobchegName;

        /// <summary>The domains the node promises to write into. A disagreement with the fact is a build error.</summary>
        public abstract Type[] OutTypes { get; }

        public abstract void Write(ref BlobchegNodeWriter writer);

        /// <summary>
        /// An empty name is filled with the asset name. Called only by the rebuild and only before
        /// <see cref="Write"/>: a record may put the hash of its own name into itself.
        /// Returns whether the field was touched.
        /// </summary>
        internal bool EnsureName()
        {
            if (!string.IsNullOrEmpty(blobchegName))
                return false;

            if (string.IsNullOrEmpty(name))
                return false;

            blobchegName = name;
            return true;
        }
    }

    /// <summary>
    /// A node of a router with <c>FixedIndex</c>: it declares the number of its own row. Where it takes
    /// it from is its own business: a serialised field, a const, an enum, a row of a table. The package
    /// only asks.
    ///
    /// An interface and not a member of the base class: only the nodes of deterministic routers
    /// implement it, and "did not implement" is a type check, not a sentinel like -1.
    /// </summary>
    public interface IBlobchegIndexed
    {
        /// <summary>The row of the node in the router file, 0..<see cref="BlobchegId.MaxIndex"/>.</summary>
        uint Index { get; }
    }
}
