using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Пост-проход пересборки: на каждый роутер собирает файл таблицы хешей. Ядро о нём не знает —
    /// оно находит его через <see cref="IBlobchegBuildPass"/> и отдаёт готовую раскладку.
    ///
    /// Здесь же живёт вся политика имён: дубли и коллизии ключей валят пересборку, потому что и то,
    /// и другое означает два предмета с одним адресом в сейве.
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

                // Дырка от удалённой ноды: строка есть, но пустая, и хеша у неё нет.
                if (node == null)
                {
                    writer.Append(0);
                    continue;
                }

                var name = node.BlobchegName;
                if (string.IsNullOrEmpty(name))
                    throw new InvalidOperationException(
                        $"Blobcheg: у ноды '{Path(node)}' пустое имя — хеш считать не от чего. " +
                        "Пересборка заполняет его именем ассета, значит имя у ассета тоже пустое");

                if (byName.TryGetValue(name, out var twin))
                    throw new InvalidOperationException(
                        $"Blobcheg: в роутере '{routerName}' имя '{name}' занято дважды — " +
                        $"'{Path(twin)}' и '{Path(node)}'. Хеш у них один, а строки разные: сейв " +
                        "адресовал бы обе ноды одним числом");

                byName.Add(name, node);

                var key = BlobchegHashKey.Of(routerName, name);

                if (byKey.TryGetValue(key, out var clash))
                    throw new InvalidOperationException(
                        $"Blobcheg: в роутере '{routerName}' имена '{byName.First(p => p.Value == clash).Key}' " +
                        $"и '{name}' сошлись на одном хеше {key:X16} — '{Path(clash)}' и '{Path(node)}'. " +
                        "Переименуй одну из них");

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

            // Ноды в манифесте лежат в порядке строк — это и есть таблица «хеш → нода» для глаз.
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
