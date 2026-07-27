using System;

namespace Blobcheg
{
    /// <summary>
    /// Объявляет базу над доменом. Генератор дописывает партиалу конструктор, <c>Read&lt;T&gt;</c> с
    /// констрейнтом домена и <c>Dispose</c> поверх <see cref="BlobchegBlob"/>.
    ///
    /// Констрейнт — единственная проверка, которая работает всегда, потому что она компиляторная:
    /// чужой домен просто не соберётся.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class BlobchegAttribute : Attribute
    {
        public BlobchegAttribute(Type domain)
        {
            Domain = domain ?? throw new ArgumentNullException(nameof(domain));
        }

        /// <summary>Маркер-интерфейс домена. Он же имя файла базы.</summary>
        public Type Domain { get; }
    }
}
