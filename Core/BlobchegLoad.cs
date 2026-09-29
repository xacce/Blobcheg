using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;

namespace Blobcheg
{
    // Async by design: Android StreamingAssets sit in an archive and a blocking read stalls or hangs.
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


        public bool Poll() // blocks until loaded
        {
	        while (!PollLazy())
	        {
		        var handle = At == Stage.Size ? SizeHandle : BodyHandle;
		        handle.JobHandle.Complete();
	        }

	        return true;
        }

		public bool PollLazy() // non-blocking, call every frame
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

        // Blocking: tests and editor tools only, never the game thread.
        public void Complete()
        {
            while (!Poll())
            {
                var handle = At == Stage.Size ? SizeHandle : BodyHandle;
                handle.JobHandle.Complete();
            }
        }

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
            // Transient: in the editor a pulled domain may precede its rebuild; the load reruns later.
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
