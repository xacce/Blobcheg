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
        /// <param name="domain">Маркер-интерфейс домена.</param>
        /// <param name="member">
        /// Имя члена в строке роутера. Не указано — база в роутер не входит и живёт сама по себе.
        /// </param>
        public BlobchegAttribute(Type domain, string member = null)
        {
            Domain = domain ?? throw new ArgumentNullException(nameof(domain));
            Member = member;
        }

        /// <summary>Маркер-интерфейс домена. Он же имя файла базы.</summary>
        public Type Domain { get; }

        /// <summary>Имя члена строки роутера; <c>null</c> — база вне роутеров.</summary>
        public string Member { get; }

        /// <summary>
        /// Структура роутера. Не указана — единственный объявленный роутер проекта; если их ноль или
        /// больше одного, это ошибка, а не догадка.
        /// </summary>
        public Type Router { get; set; }
    }

    /// <summary>
    /// Объявляет роутер: по <see cref="BlobchegId"/> отдаёт оффсеты ноды во всех своих базах.
    /// Генератор дописывает партиалу конструктор, <c>Get</c>, <c>Get*</c>/<c>TryGet*</c> на каждую
    /// базу, enum её бит и <c>Dispose</c>.
    ///
    /// Базы вступают в роутер сами — именем члена в <see cref="BlobchegAttribute"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class BlobchegRouterAttribute : Attribute
    {
        /// <summary>
        /// Номера строк этого роутера объявляют ноды, пересборка их не раздаёт. Каждая нода роутера
        /// обязана реализовать <c>IBlobchegIndexed</c>, а носитель id перестаёт быть источником
        /// правды и становится производным: снеси все носители, пересобери — id вернутся те же.
        /// </summary>
        public bool FixedIndex { get; set; }
    }
}
