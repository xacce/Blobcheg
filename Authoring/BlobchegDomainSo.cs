using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// The manifest of a domain: the same thing that is in the header of the last assembled file, but
    /// from the editor side. It is not the source of truth about the contents — the contents are found
    /// by scanning the project; the manifest exists so that "exactly what lies in the assets was baked"
    /// can be checked, and so that it is visible to the eye.
    /// </summary>
    public sealed class BlobchegDomainSo : ScriptableObject
    {
        /// <summary>
        /// Which file is described. For a router and for a hash table the <c>nodes</c> run in id order,
        /// for a base in project traversal order.
        /// </summary>
        public BlobchegFileKind kind;

        /// <summary>The manifest of a router, not of a base. Kept for the eye and for the tests.</summary>
        public bool IsRouter => kind == BlobchegFileKind.Router;

        public string domainName;
        public string fileName;
        public string builtAt;
        public int recordCount;
        public BlobchegNodeSo[] nodes;

        [SerializeField] long contentHash;

        public ulong ContentHash
        {
            get => unchecked((ulong)contentHash);
            set => contentHash = unchecked((long)value);
        }
    }
}
