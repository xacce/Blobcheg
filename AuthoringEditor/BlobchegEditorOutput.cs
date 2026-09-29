using System.IO;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.Authoring
{
    // Derived output belongs in Library; the player keeps its StreamingAssets transport.
    public static class BlobchegEditorOutput
    {
        public static string Directory => Path.Combine(Library, BlobchegNaming.DefaultFolder);

        // Editor-only derived data: the pre-build copies the folder of the bases into the player as it is.
        public static string MetaDirectory => Path.Combine(Library, BlobchegNaming.DefaultFolder + "Editor");

        static string Library
            => Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? string.Empty, "Library");

        [InitializeOnLoadMethod]
        static void Install()
            => BlobchegTransport.Default = new BlobchegEditorTransport(Directory);
    }

    // The reader is the trigger: whoever opens a base gets the one the assets stand for right now.
    sealed class BlobchegEditorTransport : IBlobchegTransport
    {
        readonly BlobchegFileTransport _files;

        public BlobchegEditorTransport(string directory) => _files = new BlobchegFileTransport(directory);

        public string Directory => _files.Directory;

        public BlobchegLoad Read(string fileName, Allocator allocator)
        {
            BlobchegFreshness.Ensure("a base is being read");
            return _files.Read(fileName, allocator);
        }
    }
}
