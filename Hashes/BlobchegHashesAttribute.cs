using System;

namespace Blobcheg
{
    /// <summary>
    /// Объявляет таблицу хешей над роутером. Генератор дописывает партиалу конструктор,
    /// <c>GetId</c>/<c>TryGetId</c>, <c>HashOf</c> на id и на оффсет в каждой базе роутера, и
    /// <c>Dispose</c>; объявлена <c>IComponentData</c> — выпускает ещё и бут-систему.
    ///
    /// Таблица объявляется отдельно от роутера и живёт отдельным файлом: основной путь пакета о
    /// хешах не знает, и проект, которому сейвы не нужны, не платит за них ни байтом.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class BlobchegHashesAttribute : Attribute
    {
        /// <param name="router">Структура роутера, помеченная <c>[BlobchegRouter]</c>.</param>
        public BlobchegHashesAttribute(Type router)
            => Router = router ?? throw new ArgumentNullException(nameof(router));

        public Type Router { get; }
    }
}
