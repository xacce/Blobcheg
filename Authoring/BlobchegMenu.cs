using UnityEditor;
using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// The package menu. A human calls both commands themselves, and both exist for exactly one
    /// reason: the rebuild cannot call them itself — it has no right to compact without asking, and it
    /// has nothing to start a full rebuild with when the assets have not changed.
    /// </summary>
    static class BlobchegMenu
    {
        /// <summary>
        /// A rebuild by hand. On its own the rebuild arrives on a node import, on entering PlayMode and
        /// on a pre-build — that is, on a change of assets and on nothing else. Files can be lost past
        /// the assets: artifacts wiped while the Library is warm (<c>git clean -X</c>, a fresh worktree)
        /// leave not a single node dirty, so there is nothing to rebuild, and the editor stands broken
        /// until the first edit.
        ///
        /// Full and not incremental: it is called exactly when what was assembled from memory is not
        /// trusted. The addresses and the ids stay in place — only a compaction moves them.
        /// </summary>
        [MenuItem("Tools/Blobcheg/Rebuild bases", priority = 0)]
        static void Rebuild()
        {
            var report = BlobchegBuild.RebuildFull();
            Debug.Log($"Blobcheg: rebuilt — {report}");
        }

        /// <summary>
        /// The compaction command. It exists for exactly one reason: a compaction is what a rebuild has
        /// no right to do on its own — it moves every address and every id, and baked subscenes and
        /// other people's saves already remember them. A human calls it when they are ready to rebake.
        /// </summary>
        [MenuItem("Tools/Blobcheg/Compact bases", priority = 20)]
        static void Compact()
        {
            var ok = EditorUtility.DisplayDialog(
                "Blobcheg: compaction",
                "The holes left by deleted nodes will disappear, but the addresses and the ids will be " +
                "handed out anew — all of them. Everything that remembered them (baked subscenes, saves) " +
                "will point at the wrong place afterwards.\n\n" +
                "The subscenes will have to be rebaked by hand.",
                "Compact", "Cancel");

            if (!ok)
                return;

            var report = BlobchegBuild.Compact();
            Debug.Log($"Blobcheg: compaction — {report}");
        }
    }
}
