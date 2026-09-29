using System;

namespace Blobcheg
{
    // Hash table declared apart from the router, so a project without saves pays nothing for it.
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class BlobchegHashesAttribute : Attribute
    {
        public BlobchegHashesAttribute(Type router)
            => Router = router ?? throw new ArgumentNullException(nameof(router));

        public Type Router { get; }

        public bool AutoLoad { get; set; } // emits a boot system in BlobchegBootGroup; unset - load by hand
    }
}
