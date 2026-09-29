using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Blobcheg.Authoring
{
    // The debug contour is taken off for a release player only: a development one checks types.
    public sealed class BlobchegBuildGate : BuildPlayerProcessor
    {
        public override int callbackOrder => -10000;

        public override void PrepareForBuild(BuildPlayerContext context)
        {
            var development = (context.BuildPlayerOptions.options & BuildOptions.Development) != 0;

            Debug.Log($"Blobcheg: pre-build — the bases before the subscene bake, the debug contour " +
                      $"{(development ? "stays (development)" : "is taken off")}");

            BlobchegBuild.DebugContour = development;
            BlobchegBuild.RebuildFull("the pre-build");

            context.AddAdditionalPathToStreamingAssets(
                BlobchegEditorOutput.Directory, BlobchegNaming.DefaultFolder);
        }
    }

    // After the build the editor gets its debug contour back: the read-time type check stands on it.
    public sealed class BlobchegDebugContourRestore : IPostprocessBuildWithReport
    {
        public int callbackOrder => 10000;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (BlobchegBuild.DebugContour == !BlobchegBuild.AsInPlayer)
                return;

            BlobchegBuild.DebugContour = !BlobchegBuild.AsInPlayer;
            BlobchegBuild.RebuildAll("giving the debug contour back after the build");
            Debug.Log("Blobcheg: the debug contour has been given back to the editor");
        }
    }
}
