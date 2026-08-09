using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;

namespace Blobcheg
{
    /// <summary>
    /// A base file read in progress. Unmanaged — which is why it lies as a field right inside an
    /// <c>ISystem</c>. The read is asynchronous by construction: on Android StreamingAssets lies inside
    /// an archive, and a blocking wait on the main thread there either stalls the frame or hangs the
    /// game for good. Errors are not returned but thrown: either the base came up whole, or the game
    /// did not start.
    /// </summary>
    public unsafe struct BlobchegLoad : IDisposable
    {
        internal enum Stage : byte
        {
            Size = 0,
            Body = 1,
            Ready = 2,
            Taken = 3,
        }

        internal FixedString512Bytes Path;
        internal Allocator Allocator;
        internal FileInfoResult* Info;
        internal ReadCommand* Command;
        internal ReadHandle SizeHandle;
        internal ReadHandle BodyHandle;
        internal BlobchegBuffer Buffer;
        internal Stage At;

        /// <summary>
        /// Advances the read state machine and tells whether the buffer is ready. A method and not a
        /// property on purpose: without the call the machine does not move, and an "IsDone" that never
        /// turns true would be a trap.
        /// </summary>
        public bool Poll()
        {
            switch (At)
            {
                case Stage.Size:
                {
                    if (SizeHandle.Status == ReadStatus.InProgress)
                        return false;

                    var status = SizeHandle.Status;
                    SizeHandle.Dispose();
                    // The handle is gone: if the line below throws, Dispose must not touch it.
                    At = Stage.Taken;
                    RequireStatus(status, "size");
                    StartBody();
                    return false;
                }

                case Stage.Body:
                {
                    if (BodyHandle.Status == ReadStatus.InProgress)
                        return false;

                    var status = BodyHandle.Status;
                    BodyHandle.Dispose();
                    At = Stage.Taken;
                    RequireStatus(status, "body");
                    At = Stage.Ready;
                    return true;
                }

                case Stage.Ready:
                    return true;

                default:
                    throw new InvalidOperationException(
                        $"Blobcheg: the read of '{Path}' is already over — the buffer was taken or the read broke off");
            }
        }

        /// <summary>A blocking wait — tests and editor tools, not the game thread.</summary>
        public void Complete()
        {
            while (!Poll())
            {
                var handle = At == Stage.Size ? SizeHandle : BodyHandle;
                handle.JobHandle.Complete();
            }
        }

        /// <summary>Hands out the buffer and the ownership of it. Before it is ready — an error.</summary>
        public BlobchegBuffer Acquire()
        {
            if (At != Stage.Ready)
                throw new InvalidOperationException(
                    $"Blobcheg: Acquire of the '{Path}' buffer before it is ready — Poll or Complete first");

            var buffer = Buffer;
            Buffer = default;
            At = Stage.Taken;
            FreeScratch();
            return buffer;
        }

        public void Dispose()
        {
            if (At == Stage.Size)
            {
                SizeHandle.JobHandle.Complete();
                SizeHandle.Dispose();
            }
            else if (At == Stage.Body)
            {
                BodyHandle.JobHandle.Complete();
                BodyHandle.Dispose();
            }

            Buffer.Dispose();
            FreeScratch();
            At = Stage.Taken;
        }

        void StartBody()
        {
            // Transient: in the editor this is what a domain that arrived with a pull ahead of its own
            // rebuild looks like. The file will appear and the load will run again — see
            // BlobchegTransientException.
            if (Info->FileState != FileState.Exists)
                throw new BlobchegTransientException($"Blobcheg: there is no base file '{Path}'");

            var size = Info->FileSize;
            if (size < BlobchegFormat.HeaderSize)
                throw new InvalidOperationException(
                    $"Blobcheg: base file '{Path}' of {size} B is shorter than the header");

            if (size > int.MaxValue)
                throw new InvalidOperationException($"Blobcheg: base file '{Path}' of {size} B does not fit into a buffer");

            Buffer = BlobchegBuffer.Alloc((int)size, Allocator);
            *Command = new ReadCommand { Buffer = Buffer.Ptr, Offset = 0, Size = size };
            BodyHandle = AsyncReadManager.Read(Path.ToString(), Command, 1);
            At = Stage.Body;
        }

        void RequireStatus(ReadStatus status, string what)
        {
            if (status != ReadStatus.Complete)
                throw new InvalidOperationException(
                    $"Blobcheg: the read ({what}) of base file '{Path}' failed: {status}");
        }

        void FreeScratch()
        {
            if (Info != null)
            {
                UnsafeUtility.Free(Info, Unity.Collections.Allocator.Persistent);
                Info = null;
            }

            if (Command != null)
            {
                UnsafeUtility.Free(Command, Unity.Collections.Allocator.Persistent);
                Command = null;
            }
        }
    }
}
