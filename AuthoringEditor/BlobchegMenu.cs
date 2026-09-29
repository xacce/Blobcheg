using UnityEditor;

namespace Blobcheg.Authoring
{
    // Public so that -executeMethod calls the very same command a human clicks, not a copy of it.
    public static class BlobchegMenu
    {
        [MenuItem("Tools/Blobcheg/Rebuild bases", priority = 0)]
        public static void Rebuild() => BlobchegBuild.RebuildFull("the menu command");

        [MenuItem("Tools/Blobcheg/Why would it rebuild", priority = 1)]
        public static void Why() => UnityEngine.Debug.Log("Blobcheg: " + BlobchegFreshness.Explain());

        const string PlayerItem = "Tools/Blobcheg/Work as in a player";
        const string LogItem = "Tools/Blobcheg/Say what it does";
        const string LogKey = "Blobcheg.Log";

        // Remembered per machine: both questions are asked of every project a human opens.
        [InitializeOnLoadMethod]
        static void Restore()
        {
            BlobchegProfile.Enabled = EditorPrefs.GetBool(LogKey, true);
            BlobchegBuild.DebugContour = !BlobchegBuild.AsInPlayer;
        }

        // The player path in the editor: the dense layout of a release build, no debug contour.
        [MenuItem(PlayerItem, priority = 20)]
        static void ToggleAsInPlayer()
        {
            var wanted = !BlobchegBuild.AsInPlayer;

            BlobchegBuild.AsInPlayer = wanted;
            BlobchegBuild.RebuildAll(wanted ? "the player path in the editor" : "the editor path back");
        }

        [MenuItem(PlayerItem, true)]
        static bool ToggleAsInPlayerCheck()
        {
            Menu.SetChecked(PlayerItem, BlobchegBuild.AsInPlayer);
            return true;
        }

        [MenuItem(LogItem, priority = 40)]
        static void ToggleLog()
        {
            BlobchegProfile.Enabled = !BlobchegProfile.Enabled;
            EditorPrefs.SetBool(LogKey, BlobchegProfile.Enabled);
        }

        [MenuItem(LogItem, true)]
        static bool ToggleLogCheck()
        {
            Menu.SetChecked(LogItem, BlobchegProfile.Enabled);
            return true;
        }
    }
}
