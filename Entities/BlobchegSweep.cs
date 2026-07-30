using System;
using Unity.Entities;

namespace Blobcheg
{
    /// <summary>
    /// Проход патча по миру для того, кто только что поднял базу — первый раз или заново.
    /// Слоты сущностей, приехавших раньше базы, после него становятся адресами; слоты, смотревшие
    /// в прежний буфер, переезжают на новый через отставные поколения <see cref="BlobchegBases"/>.
    ///
    /// Сама работа живёт в Blobcheg.Entities.Patch — сборке необязательной, поэтому здесь только
    /// место, куда она себя кладёт (это делает <c>BlobchegPatchInstall</c>). Патча в проекте нет —
    /// адресных слотов в мире не бывает, и звать нечего.
    /// </summary>
    public static class BlobchegSweep
    {
        /// <summary>Ставится установкой патча. Не поставлен — прохода нет и не нужно.</summary>
        public static Action<EntityManager> Hook;

        public static void Run(EntityManager entityManager) => Hook?.Invoke(entityManager);
    }
}
