using System;

namespace Blobcheg
{
    /// <summary>
    /// Declares a hash table over a router. The generator adds a constructor, a
    /// <c>GetId</c>/<c>TryGetId</c>, a <c>HashOf</c> for an id and for an offset in each base of the
    /// router, and a <c>Dispose</c> to the partial; if <c>IComponentData</c> is declared it also emits
    /// a boot system.
    ///
    /// The table is declared apart from the router and lives in its own file: the main path of the
    /// package knows nothing about hashes, and a project that needs no saves does not pay a single byte
    /// for them.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class BlobchegHashesAttribute : Attribute
    {
        /// <param name="router">The router struct marked with <c>[BlobchegRouter]</c>.</param>
        public BlobchegHashesAttribute(Type router)
            => Router = router ?? throw new ArgumentNullException(nameof(router));

        public Type Router { get; }
    }
}
