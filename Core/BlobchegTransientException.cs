using System;

namespace Blobcheg
{
    /// <summary>
    /// A load failure with an expiry date: the base file is not there yet, or the read caught it
    /// mid-rewrite. The cause here is not in the bytes but in time — the same read a frame later
    /// goes through.
    ///
    /// In the editor this is a transient moment and heals itself: a rebuild finishes writing the file
    /// and bumps its number in <see cref="BlobchegFileVersions"/>, on which the load runs again. In
    /// the player the same failure is terminal — there is nobody there to rewrite the file.
    ///
    /// A separate type rather than text in the message: the one who decides is not the one who throws
    /// but the one who catches, and they are obliged to decide by type, not by parsing a string.
    /// </summary>
    public sealed class BlobchegTransientException : InvalidOperationException
    {
        public BlobchegTransientException(string message) : base(message)
        {
        }
    }
}
