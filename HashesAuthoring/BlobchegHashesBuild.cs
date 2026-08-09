using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// A post-pass of the rebuild: it assembles a hash table file per router. The core knows nothing
    /// about it — it finds it through <see cref="IBlobchegBuildPass"/> and hands over the finished
    /// layout.
    ///
    /// The whole naming policy lives here as well: duplicates and key collisions fail the rebuild,
    /// because both mean two subjects with one address in a save.
    /// </summary>
    public sealed class BlobchegHashesBuild : IBlobchegBuildPass
    {
        public void Run(BlobchegBuildLayout layout, ref BlobchegBuildReport report)
        {
            foreach (var router in layout.Routers)
                Build(layout, router, ref report);
        }

        static void Build(BlobchegBuildLayout layout, Type router, ref BlobchegBuildReport report)
        {
            var routerName = layout.NameOf(router);
            var domains = layout.DomainsOf(router);
            var rows = layout.NodesOf(router);

            var writer = BlobchegHashesWriter.Open(
                layout.OutputDirectory, routerName, domains.Count, layout.LayoutHashOf(router));

            var byName = new Dictionary<string, BlobchegNodeSo>(StringComparer.Ordinal);
            var byKey = new Dictionary<ulong, BlobchegNodeSo>();

            for (var row = 0; row < rows.Count; row++)
            {
                var node = rows[row];

                // A hole from a deleted node: the row is there but empty, and it has no hash.
                if (node == null)
                {
                    writer.Append(0);
                    continue;
                }

                var name = node.BlobchegName;
                if (string.IsNullOrEmpty(name))
                    throw new InvalidOperationException(
                        $"Blobcheg: node '{Path(node)}' has an empty name — there is nothing to compute a hash from. " +
                        "The rebuild fills it with the asset name, so the asset name is empty too");

                if (byName.TryGetValue(name, out var twin))
                    throw new InvalidOperationException(
                        $"Blobcheg: in router '{routerName}' the name '{name}' is taken twice — " +
                        $"'{Path(twin)}' and '{Path(node)}'. Their hash is one and their rows are different: a save " +
                        "would address both nodes with one number");

                byName.Add(name, node);

                var key = BlobchegHashKey.Of(routerName, name);

                if (byKey.TryGetValue(key, out var clash))
                    throw new InvalidOperationException(
                        $"Blobcheg: in router '{routerName}' the names '{byName.First(p => p.Value == clash).Key}' " +
                        $"and '{name}' met on one hash {key:X16} — '{Path(clash)}' and '{Path(node)}'. " +
                        "Rename one of them");

                byKey.Add(key, node);
                writer.Append(key);

                for (var bit = 0; bit < domains.Count; bit++)
                {
                    if (layout.TryOffset(node, domains[bit], out var offset))
                        writer.Track(bit, offset, (uint)row);
                }
            }

            writer.Flush();

            if (writer.FileChanged)
            {
                report.ChangedFiles++;
                BlobchegFileVersions.Bump(BlobchegNaming.FileName(writer.Identity));
            }

            // The nodes lie in the manifest in row order — that is the "hash → node" table for the eye.
            layout.SyncManifest(writer.Identity, BlobchegFileKind.Hashes, rows.ToArray(),
                writer.RowCount, writer.ContentHash, writer.FileChanged, ref report);
        }

        static string Path(BlobchegNodeSo node)
        {
            var path = AssetDatabase.GetAssetPath(node);
            return string.IsNullOrEmpty(path) ? node.name : path;
        }
    }
}
