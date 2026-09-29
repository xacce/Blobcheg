using System;
using UnityEngine;

namespace Blobcheg
{
    // The anchor of identity for a (node x domain) pair: consumers point at it by GUID and fileID.
    public sealed class BlobchegRefSo : ScriptableObject
    {
        [SerializeField] internal string domainName;

        public string DomainName => domainName;

        public uint Offset => BlobchegStamps.OffsetOf(this);

        // The full name of the record type. Empty means raw bytes, null means never assembled.
        public string RecordType => BlobchegStamps.RecordTypeOf(this);
    }

    /// <summary>
    /// A postscript to "the reference is empty", for the editor only. An empty field where the carrier
    /// file has the reference in place is not about the data: Unity imported the carrier before it
    /// compiled its script, found no type and threw away the values of all its fields. That is what
    /// happens when a merge brings a new authoring script under a live editor. Without the postscript
    /// the message blames the data, and the investigation goes off to check the YAML anchors, which are
    /// perfectly fine.
    /// </summary>
    static class BlobchegRefHint
    {
#if UNITY_EDITOR
        public const string Empty =
            ". If the reference is in place in the carrier file, the carrier was imported without its " +
            "script (a merge brought a new .cs under a live editor) and the import threw away the values " +
            "of its fields: the carrier needs a Reimport, or rather the editor needs a restart";
#else
        public const string Empty = "";
#endif
    }

    /// <summary>
    /// The field type on the consumer: <c>public BlobchegRef&lt;GunData&gt; gun;</c>. Assigning a
    /// <c>BlobchegRef&lt;ShieldData&gt;</c> is refused by the compiler, putting a foreign asset into the
    /// picker is refused by the drawer, and a mismatch at bake time is an error, not a quiet zero.
    /// </summary>
    [Serializable]
    public struct BlobchegRef<T> where T : unmanaged
    {
        [SerializeField] internal BlobchegRefSo asset;

        public BlobchegRef(BlobchegRefSo asset) => this.asset = asset;

        /// <summary>The asset itself — for <c>DependsOn</c> in a baker.</summary>
        public BlobchegRefSo Asset => asset;

        public bool IsSet => asset != null;

        /// <summary>The address of the record. An empty ref or an asset of a foreign type — an exception.</summary>
        public uint Offset
        {
            get
            {
                if (asset == null)
                    throw new InvalidOperationException(
                        $"Blobcheg: an empty BlobchegRef<{typeof(T).Name}> — no record asset is assigned"
                        + BlobchegRefHint.Empty);

                // Asked first: with no address there is no type to check against either.
                var offset = asset.Offset;

                var expected = typeof(T).FullName;
                if (!string.Equals(asset.RecordType, expected, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Blobcheg: BlobchegRef<{typeof(T).Name}> holds asset '{asset.name}' carrying record " +
                        $"'{asset.RecordType}' — '{expected}' was expected");

                return offset;
            }
        }

        /// <summary>
        /// The slot for a component: the same address, but in the form the import patch will turn into
        /// a pointer. The record type check is the same as in <see cref="Offset"/>.
        /// </summary>
        public BlobchegReference<T> ToReference() => new BlobchegReference<T>(Offset);
    }

    /// <summary>
    /// The same field without a parameter — for records from <c>AddBytes</c>. They have no type, so
    /// there is nothing to check: a deliberate hole exactly where the consumer gave up the type
    /// themselves.
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
                    throw new InvalidOperationException(
                        "Blobcheg: an empty BlobchegRawRef — no record asset is assigned"
                        + BlobchegRefHint.Empty);

                return asset.Offset;
            }
        }
    }
}
