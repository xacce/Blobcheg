using System;
using Unity.Entities;

namespace Blobcheg
{
    /// <summary>
    /// A patch pass over the world for whoever has just loaded a base — for the first time or again.
    /// After it, the slots of entities that arrived before the base become addresses; the slots that
    /// looked into the previous buffer move onto the new one through the retired generations of
    /// <see cref="BlobchegBases"/>.
    ///
    /// The work itself lives in Blobcheg.Entities.Patch, an optional assembly, so all that is here is
    /// the place it puts itself into (that is done by <c>BlobchegPatchInstall</c>). If the project has
    /// no patch, there are no address slots in the world and nothing to call.
    /// </summary>
    public static class BlobchegSweep
    {
        /// <summary>Set by installing the patch. Not set — there is no pass and none is needed.</summary>
        public static Action<EntityManager> Hook;

        public static void Run(EntityManager entityManager) => Hook?.Invoke(entityManager);
    }
}
