using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.Authoring
{
    // The numbers as a dependency of the node asset: a moved number reimports it, the file stays put.
    public static class BlobchegDependencies
    {
        public const string Build = "blobcheg/build";

        const string NodePrefix = "blobcheg/node/";

        static bool _retrying;

        public static string NameOf(string guid) => NodePrefix + guid;

        // A custom dependency is read by an import, not a lookup: a node whose value moved is reimported.
        internal static List<string> Publish(IReadOnlyDictionary<string, string> were, Dictionary<string, string> are,
            Func<string, bool> held)
        {
            var fresh = new List<string>();

            if (AssetDatabase.IsAssetImportWorkerProcess())
                return fresh;

            using var _ = BlobchegProfile.Section("dependencies");

            try
            {
                AssetDatabase.RegisterCustomDependency(Build, BlobchegFreshness.Key);

                foreach (var pair in BlobchegStampStore.NodeKeys())
                {
                    var value = pair.Value.ToString();
                    are[pair.Key] = value;

                    if (were.TryGetValue(pair.Key, out var was) && was == value)
                        continue;

                    // A new value makes Unity reimport the node on its own, and that writes an unsaved one.
                    if (held(pair.Key))
                    {
                        are.Remove(pair.Key);
                        continue;
                    }

                    AssetDatabase.RegisterCustomDependency(NameOf(pair.Key), pair.Value);
                    fresh.Add(pair.Key);
                }
            }
            catch (Exception e)
            {
                // Registering is forbidden inside an import: a bake that started one waits for its turn.
                are.Clear();
                fresh.Clear();
                Retry(e);
            }

            return fresh;
        }

        internal static void Retry(Exception e)
        {
            if (_retrying)
                return;

            _retrying = true;
            EditorApplication.delayCall += () =>
            {
                _retrying = false;
                BlobchegFreshness.Redeclare();
            };

            Debug.Log($"Blobcheg: the dependencies are registered after the import ({e.Message})");
        }

        internal static Hash128 KeyOf(IEnumerable<string> parts)
        {
            var text = new StringBuilder();
            foreach (var part in parts)
                text.Append(part).Append('\n');

            var bytes = Encoding.UTF8.GetBytes(text.ToString());
            var hash = BlobchegHash.Of(bytes, 0, bytes.Length);
            return new Hash128((uint)(hash >> 32), (uint)hash, 0, 0);
        }
    }

    // A node asset declares its numbers; any other asset is left alone.
    sealed class BlobchegNodeDependency : AssetPostprocessor
    {
        void OnPreprocessAsset()
        {
            if (!assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return;

            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (BlobchegFreshness.IsNode(guid))
                context.DependsOnCustomDependency(BlobchegDependencies.NameOf(guid));
        }
    }
}
