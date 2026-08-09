using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// The rebuild happens by itself: a "save the blob" button would only give a chance to forget about
    /// it. We catch node imports, entering PlayMode and the pre-build.
    ///
    /// There is a menu command as well (<see cref="BlobchegMenu"/>) — but it is not about "build when I
    /// remember", it is about what those three events do not see: files can be lost past the assets, and
    /// then there are no dirty nodes while a rebuild is still needed.
    /// </summary>
    public sealed class BlobchegHooks : AssetPostprocessor
    {
        static bool _dirty;
        static bool _running;
        static bool _scheduled;

        [InitializeOnLoadMethod]
        static void Install()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            // The rebuild writes the carriers itself and knows what it wrote: counting those imports as
            // someone else's edit would mean declaring every node that was just built dirty.
            if (BlobchegBuild.Building)
                return;

            BlobchegCache.Touch(imported, deleted, moved, movedFrom);

            if (_running)
                return;

            if (TouchesNodes(imported) || TouchesDeleted(deleted) || TouchesNodes(moved) || TouchesDeleted(movedFrom))
                MarkDirty();
        }

        /// <summary>Mark the domains dirty by hand — from tests and tools.</summary>
        public static void MarkDirty()
        {
            _dirty = true;
            if (_scheduled)
                return;

            _scheduled = true;
            EditorApplication.delayCall += () =>
            {
                _scheduled = false;
                RebuildIfDirty();
            };
        }

        public static void RebuildIfDirty()
        {
            if (!_dirty || _running || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            _running = true;
            try
            {
                var report = BlobchegBuild.RebuildAll();
                _dirty = false;
                if (report.Changed)
                    Debug.Log($"Blobcheg: rebuilt — {report}");
            }
            finally
            {
                _running = false;
            }
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingEditMode || !_dirty)
                return;

            try
            {
                RebuildIfDirty();
            }
            catch (Exception e)
            {
                // Entering PlayMode with an unbuilt base is not allowed: it either comes up whole or not
                // at all.
                EditorApplication.isPlaying = false;
                Debug.LogException(e);
                throw;
            }
        }

        static bool TouchesNodes(string[] paths)
        {
            foreach (var path in paths)
            {
                if (IsOutput(path))
                    continue;

                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                if (type != null && typeof(BlobchegNodeSo).IsAssignableFrom(type))
                    return true;
            }

            return false;
        }

        static bool TouchesDeleted(string[] paths)
        {
            // A deleted asset can no longer be loaded and its type cannot be asked for. The rebuild is
            // idempotent, so an extra run over any deleted .asset is cheaper than a missed node.
            foreach (var path in paths)
            {
                if (!IsOutput(path) && path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        static bool IsOutput(string path)
            => path.Replace('\\', '/').Contains("/StreamingAssets/" + BlobchegNaming.DefaultFolder + "/");
    }

    /// <summary>
    /// The pre-build: the blob is obliged to be built, compacted and deterministic before it travels
    /// into the player.
    ///
    /// The compaction happens exactly here: the holes left by deleted nodes are bytes that travel into
    /// the build for nothing, while a re-sort moves every address, and it can only be allowed where
    /// everything gets rebaked right afterwards anyway. In the editor there is a separate command for
    /// it, it never happens by itself.
    ///
    /// The debug contour is taken off here too — but only for a release player. A development build has
    /// <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c>, that is, the record type check works there, and taking
    /// away what it stands on would mean switching it off in exactly the build that is made in order to
    /// catch things.
    /// </summary>
    public sealed class BlobchegBuildGate : BuildPlayerProcessor
    {
        // Not IPreprocessBuildWithReport: Entities bakes subscenes in PrepareForBuild
        // (EntitySceneBuildPlayerProcessor), and that phase runs earlier than the pre-build callbacks.
        // Compacting the base after the bake means carrying subscenes with the old addresses into the
        // build and never noticing. Hence both the phase and the order: earlier than everyone who bakes.
        public override int callbackOrder => -10000;

        public override void PrepareForBuild(BuildPlayerContext context)
        {
            var development = (context.BuildPlayerOptions.options & BuildOptions.Development) != 0;

            Debug.Log($"Blobcheg: pre-build — compaction before the subscene bake, the debug contour " +
                      $"{(development ? "stays (development)" : "is taken off")}");

            BlobchegBuild.DebugContour = development;
            BlobchegBuild.Compact();
            BlobchegBuild.RequireUpToDate("pre-build");
        }
    }

    /// <summary>
    /// After the build the editor gets its debug contour back: the files in StreamingAssets stayed
    /// assembled for the player, and in the editor the read-time type check stands on the contour.
    ///
    /// If the build failed the callback was not called, and there will be no contour until the next
    /// domain reload. That is not a silent loss: <see cref="BlobchegBuild.WithDebug"/> is a static and
    /// does not survive a reload, and the very first rebuild after it puts the section back.
    /// </summary>
    public sealed class BlobchegDebugContourRestore : IPostprocessBuildWithReport
    {
        public int callbackOrder => 10000;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (BlobchegBuild.DebugContour)
                return;

            BlobchegBuild.DebugContour = true;
            BlobchegBuild.RebuildFull();
            Debug.Log("Blobcheg: the debug contour has been given back to the editor");
        }
    }
}
