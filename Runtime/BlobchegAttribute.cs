using System;

namespace Blobcheg
{
    /// <summary>
    /// Declares a base over a domain. The generator adds a constructor, a <c>Read&lt;T&gt;</c> with the
    /// domain constraint and a <c>Dispose</c> over <see cref="BlobchegBlob"/> to the partial.
    ///
    /// The constraint is the only check that always works, because it is a compiler check: a foreign
    /// domain simply does not build.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class BlobchegAttribute : Attribute
    {
        /// <param name="domain">The marker interface of the domain.</param>
        /// <param name="member">
        /// The name of the member in the router row. Not given — the base does not join a router and
        /// lives on its own.
        /// </param>
        public BlobchegAttribute(Type domain, string member = null)
        {
            Domain = domain ?? throw new ArgumentNullException(nameof(domain));
            Member = member;
        }

        /// <summary>The marker interface of the domain. Also the name of the base file.</summary>
        public Type Domain { get; }

        /// <summary>The name of the router row member; <c>null</c> means a base outside any router.</summary>
        public string Member { get; }

        /// <summary>
        /// The router struct. Not given — the single router declared in the project; if there are zero
        /// of them or more than one, that is an error and not a guess.
        /// </summary>
        public Type Router { get; set; }
    }

    /// <summary>
    /// Declares a router: by a <see cref="BlobchegId"/> it hands out the offsets of a node in all of
    /// its bases. The generator adds a constructor, a <c>Get</c>, a <c>Get*</c>/<c>TryGet*</c> per
    /// base, the enum of its bits and a <c>Dispose</c> to the partial.
    ///
    /// Bases join a router by themselves — by the member name in <see cref="BlobchegAttribute"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class BlobchegRouterAttribute : Attribute
    {
        /// <summary>
        /// The row numbers of this router are declared by the nodes, a rebuild does not hand them out.
        /// Every node of the router is obliged to implement <c>IBlobchegIndexed</c>, and the id carrier
        /// stops being the source of truth and becomes derived: wipe every carrier, rebuild — the same
        /// ids come back.
        /// </summary>
        public bool FixedIndex { get; set; }
    }
}
