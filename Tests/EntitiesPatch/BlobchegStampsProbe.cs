using System;
using System.Collections.Generic;

namespace Blobcheg.PatchTests
{
    // A carrier fabricated by a test lies in no asset: such a test names the numbers itself.
    sealed class BlobchegStampsProbe : IBlobchegStamps, IDisposable
    {
        readonly IBlobchegStamps _was;

        readonly Dictionary<BlobchegRefSo, KeyValuePair<uint, string>> _records =
            new Dictionary<BlobchegRefSo, KeyValuePair<uint, string>>();

        readonly Dictionary<BlobchegIdSo, uint> _ids = new Dictionary<BlobchegIdSo, uint>();

        public BlobchegStampsProbe()
        {
            _was = BlobchegStamps.Source;
            BlobchegStamps.Source = this;
        }

        public void Dispose() => BlobchegStamps.Source = _was;

        public void Say(BlobchegRefSo reference, uint offset, string recordType)
            => _records[reference] = new KeyValuePair<uint, string>(offset, recordType);

        public void Say(BlobchegIdSo carrier, uint id) => _ids[carrier] = id;

        public bool TryOffset(BlobchegRefSo reference, out uint offset)
        {
            if (_records.TryGetValue(reference, out var stamp))
            {
                offset = stamp.Key;
                return true;
            }

            offset = 0;
            return _was != null && _was.TryOffset(reference, out offset);
        }

        public bool TryRecordType(BlobchegRefSo reference, out string recordType)
        {
            if (_records.TryGetValue(reference, out var stamp))
            {
                recordType = stamp.Value;
                return true;
            }

            recordType = null;
            return _was != null && _was.TryRecordType(reference, out recordType);
        }

        public bool TryId(BlobchegIdSo carrier, out uint id)
        {
            if (_ids.TryGetValue(carrier, out id))
                return true;

            id = BlobchegId.NoneValue;
            return _was != null && _was.TryId(carrier, out id);
        }
    }
}
