using System;

namespace Blobcheg
{
    public interface IBlobchegStamps
    {
        bool TryOffset(BlobchegRefSo reference, out uint offset);

        bool TryRecordType(BlobchegRefSo reference, out string recordType);

        bool TryId(BlobchegIdSo carrier, out uint id);
    }

    // Only the editor installs a source: in a player addresses come from the bake, ids off the carrier.
    public static class BlobchegStamps
    {
        public static IBlobchegStamps Source { get; set; }

        public static uint OffsetOf(BlobchegRefSo reference)
        {
            if (reference == null)
                throw new ArgumentNullException(nameof(reference));

            if (Source == null)
                throw new InvalidOperationException(Outside("the address of record '" + reference.name + "'"));

            if (!Source.TryOffset(reference, out var offset))
                throw new InvalidOperationException(Unbuilt("record '" + reference.name + "'"));

            return offset;
        }

        public static string RecordTypeOf(BlobchegRefSo reference)
            => reference != null && Source != null && Source.TryRecordType(reference, out var recordType)
                ? recordType
                : null;

        // For the pickers: a carrier with no number yet is an answer to show, not a breakdown.
        public static bool TryOffsetOf(BlobchegRefSo reference, out uint offset)
        {
            offset = 0;
            return reference != null && Source != null && Source.TryOffset(reference, out offset);
        }

        public static bool TryIdOf(BlobchegIdSo carrier, out uint id)
        {
            if (carrier == null)
                throw new ArgumentNullException(nameof(carrier));

            if (Source != null)
                return Source.TryId(carrier, out id);

            id = IdFromRow(carrier);
            return true;
        }

        public static uint IdOf(BlobchegIdSo carrier)
        {
            if (carrier == null)
                throw new ArgumentNullException(nameof(carrier));

            if (Source == null)
                return IdFromRow(carrier);

            if (!Source.TryId(carrier, out var id))
                throw new InvalidOperationException(Unbuilt("node '" + carrier.name + "'"));

            return id;
        }

        // The row travels in the asset with the node, so a player needs no table to name the id.
        static uint IdFromRow(BlobchegIdSo carrier)
        {
            if (carrier.row < 0)
                throw new InvalidOperationException(Unbuilt("node '" + carrier.name + "'"));

            if (string.IsNullOrEmpty(carrier.routerName))
                throw new InvalidOperationException(
                    $"Blobcheg: id carrier '{carrier.name}' names no router, the asset is damaged");

            if (carrier.row > BlobchegId.MaxIndex)
                throw new InvalidOperationException(
                    $"Blobcheg: id carrier '{carrier.name}' holds row {carrier.row}, past the ceiling " +
                    $"of {BlobchegId.MaxIndex}, the asset is damaged");

            return BlobchegId.Make(BlobchegNaming.TagOf(carrier.routerName), (uint)carrier.row).Value;
        }

        static string Outside(string what)
            => $"Blobcheg: {what} is asked for outside the editor — the numbers are derived, they stay " +
               "next to the bases and travel into a player only inside what the bake wrote";

        static string Unbuilt(string what)
            => $"Blobcheg: {what} has no number in the artifact of the bases — they have not been " +
               "assembled in this project yet. Tools/Blobcheg/Rebuild bases";
    }
}
