using System;

namespace Blobcheg
{
    // The generated Read<T> domain constraint is the one always-on check: a foreign domain won't build.
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class BlobchegAttribute : Attribute
    {
        public BlobchegAttribute(Type domain, string member = null) // member null: the base joins no router
        {
            Domain = domain ?? throw new ArgumentNullException(nameof(domain));
            Member = member;
        }

        public Type Domain { get; } // also names the base file

        public string Member { get; }

        public Type Router { get; set; } // null: the project's single router; zero or several is an error

        public bool AutoLoad { get; set; } // emits a boot system in BlobchegBootGroup; unset = load by hand
    }

    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class BlobchegRouterAttribute : Attribute // BlobchegId -> node offsets in all its bases; bases join by member name
    {
        public bool FixedIndex { get; set; } // rows come from IBlobchegIndexed nodes, so ids survive a wiped carrier

        public bool AutoLoad { get; set; } // emits a boot system in BlobchegBootGroup; unset = load by hand
    }
}
