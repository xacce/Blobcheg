using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>What the rebuild did. For the logs, the pre-build gate and the tests.</summary>
    public struct BlobchegBuildReport
    {
        public int Domains;
        public int Routers;
        public int Records;
        public int ChangedFiles;
        public int ChangedManifests;
        public int ChangedRefs;
        public int RemovedRefs;

        // Addresses, record types and ids written next to the bases; stale ones dropped there.
        public int ChangedStamps;
        public int RemovedStamps;

        /// <summary>Nodes whose empty name the rebuild filled in. A second run is obliged to give zero.</summary>
        public int NamedNodes;

        /// <summary>How many ids moved onto the row a node declared. Not an error — a migration.</summary>
        public int MovedIds;

        public bool Changed => ChangedFiles > 0 || ChangedRefs > 0 || RemovedRefs > 0
                               || ChangedManifests > 0 || ChangedStamps > 0 || RemovedStamps > 0
                               || NamedNodes > 0;

        // Only the carriers and the node names are assets; everything else the rebuild writes is a file.
        public bool AssetsTouched => ChangedRefs > 0 || RemovedRefs > 0 || NamedNodes > 0;

        public override string ToString()
            => $"domains {Domains}, routers {Routers}, records {Records}, files rewritten {ChangedFiles}, " +
               $"manifests {ChangedManifests}, stamps {ChangedStamps}, dropped {RemovedStamps}, " +
               $"carriers added {ChangedRefs}, removed {RemovedRefs}, " +
               $"nodes named {NamedNodes}, ids moved {MovedIds}";
    }

    // The layout is deterministic, so the rebuild is idempotent: nothing changed, nothing is rewritten.
    public static class BlobchegBuild
    {
        public static string OutputDirectory => BlobchegEditorOutput.Directory;

        /// <summary>
        /// Whether the debug contour is written into the files. In the editor always: the read-time type
        /// check stands on it, and without it the check exists only on paper. Exactly one place takes it
        /// off — the pre-build gate of a non-development player, see <see cref="BlobchegBuildGate"/>.
        /// </summary>
        public static bool WithDebug => DebugContour;

        internal static bool DebugContour = true;

        // The editor reads the bases by the player path: the same files, assembled the release way.
        public static bool AsInPlayer
        {
            get => EditorPrefs.GetBool(PlayerMode, false);
            set
            {
                EditorPrefs.SetBool(PlayerMode, value);
                DebugContour = !value;
            }
        }

        const string PlayerMode = "Blobcheg.AsInPlayer";

        public static bool Building { get; private set; }

        // Write is called on what the index says has moved; the rest hand back the bytes they wrote.
        public static BlobchegBuildReport RebuildAll(string trigger = null)
            => Rebuild(BlobchegFreshness.DirtyNow(), trigger);

        // Everything anew, from the assets and not from memory: the pre-build and the menu go this way.
        public static BlobchegBuildReport RebuildFull(string trigger = null) => Rebuild(null, trigger);

        internal static BlobchegBuildReport Rebuild(IReadOnlyCollection<string> dirty, string trigger)
        {
            // A nested run goes over a half-filled collector and half-handed-out ids.
            if (Building)
                throw new InvalidOperationException(
                    "Blobcheg: the rebuild entered itself — most likely a node calls RebuildAll " +
                    "from Write. There is obliged to be one rebuild: a nested one goes over half-handed-out " +
                    "addresses and ids");

            var report = new BlobchegBuildReport();
            var collector = new BlobchegCollector(OutputDirectory);

            var kind = dirty != null ? "rebuild" : "full rebuild";
            using var work = BlobchegProfile.Begin(trigger == null ? kind : kind + " (" + trigger + ")");

            Building = true;
            try
            {
                var done = Run(collector, dirty, ref report);
                work.Note(done.ToString());
                return done;
            }
            finally
            {
                Building = false;
            }
        }

        // Node files the rebuild wrote carriers or names into: the only ones it saves.
        static readonly HashSet<string> Written = new HashSet<string>(StringComparer.Ordinal);

        static BlobchegBuildReport Run(BlobchegCollector collector, IReadOnlyCollection<string> dirty,
            ref BlobchegBuildReport report)
        {
            List<BlobchegNodeEntry> entries;
            using (BlobchegProfile.Section("Node list"))
                entries = BlobchegNodes.Gather(dirty);

            BlobchegManifests.BeginRebuild();
            BlobchegStampStore.BeginRebuild();
            Written.Clear();

            var nodes = new List<BlobchegNodeSo>(entries.Count);
            var guids = new Dictionary<BlobchegNodeSo, string>(entries.Count);
            foreach (var entry in entries)
            {
                nodes.Add(entry.Node);
                guids[entry.Node] = entry.Guid;
            }

            // A node needs its name before it writes: a record may put the hash of its own name into
            // itself. There is deliberately no StartAssetEditing batch here — that one stands later and
            // exists for AddObjectToAsset, while a SetDirty on the node itself causes no reimport.
            using (BlobchegProfile.Section("Node names"))
            {
                foreach (var entry in entries)
                {
                    if (!entry.Node.EnsureName())
                        continue;

                    entry.Dirty = true;
                    EditorUtility.SetDirty(entry.Node);
                    Written.Add(entry.Guid);
                    report.NamedNodes++;
                }
            }

            // The carriers are read once for the whole rebuild: which pairs already have a sub-asset.
            BlobchegCarriers carriers;
            using (BlobchegProfile.Section("Reading the carriers"))
                carriers = BlobchegCarriers.Read(nodes);

            // The ids are handed out BEFORE the write: they are derived from OutTypes and not from what
            // the node wrote, so a node can put its own id straight into a record in one pass.
            BlobchegIdTable ids;
            using (BlobchegProfile.Section("Assigning ids"))
                ids = BlobchegIdTable.Assign(nodes, (node, router) => HeldRow(carriers.Id(node, router), guids[node]));

            // A writer is opened for EVERY declared domain, even an empty one: otherwise a domain whose
            // last node was deleted would stay on disk as the old file.
            foreach (var domain in BlobchegDomains.All)
                collector.WriterOf(domain);

            using (BlobchegProfile.Section("node.Write — changed nodes"))
            {
                foreach (var entry in entries)
                    WriteNode(entry, collector, ids);
            }

            using (BlobchegProfile.Section("Records to the writers"))
                collector.Handover();

            using (BlobchegProfile.Section("Flush of the bases"))
            {
                foreach (var pair in collector.Writers)
                {
                    pair.Value.Flush(WithDebug);
                    report.Domains++;
                    report.Records += pair.Value.RecordCount;
                    if (pair.Value.FileChanged)
                    {
                        report.ChangedFiles++;
                        BlobchegFileVersions.Bump(BlobchegNaming.FileName(BlobchegDomains.NameOf(pair.Key)));
                    }
                }
            }

            // The routers are assembled AFTER Flush: before it the offsets the rows are made of do not
            // exist at all.
            var offsets = new Dictionary<(BlobchegNodeSo, Type), uint>();
            foreach (var entry in collector.Entries)
                offsets[(entry.Node, entry.Domain)] = collector.Writers[entry.Domain].OffsetOf(entry.Ticket);

            using (BlobchegProfile.Section("BuildRouters"))
                BuildRouters(offsets, ids, ref report);

            // The derived files — the hash table and whatever gets added after it. The core knows nothing
            // about them: it hands over the finished layout and takes back the report counters.
            using (BlobchegProfile.Section("Post-passes"))
                RunPasses(new BlobchegBuildLayout(ids, offsets), ref report);

            // The carriers are written as a batch: a per-item AddObjectToAsset reimports the node for
            // every sub-asset, and on a large project that is what the whole rebuild is. A measurement on
            // 500 nodes: 34 ms per carrier without the batch against 9 ms with it.
            AssetDatabase.StartAssetEditing();
            try
            {
                using (BlobchegProfile.Section("SyncRefs"))
                    SyncRefs(collector, carriers, nodes, guids, ref report);

                using (BlobchegProfile.Section("SyncIds"))
                    SyncIds(ids, carriers, nodes, guids, ref report);
            }
            finally
            {
                using (BlobchegProfile.Section("StopAssetEditing — reimport of the batch"))
                    AssetDatabase.StopAssetEditing();
            }

            using (BlobchegProfile.Section("SyncStamps"))
                report.RemovedStamps += BlobchegStampStore.Flush();

            using (BlobchegProfile.Section("SyncManifests"))
            {
                SyncManifests(collector, nodes, ref report);
                report.ChangedManifests += BlobchegManifests.Flush();
            }

            if (report.AssetsTouched)
            {
                // Unity has no save of one file: this writes every dirty asset, as SaveAssets would.
                using (BlobchegProfile.Section("SaveAssets"))
                {
                    foreach (var guid in Written)
                        AssetDatabase.SaveAssetIfDirty(new GUID(guid));
                }

                using (BlobchegProfile.Section("Refresh"))
                    AssetDatabase.Refresh();
            }

            // The index is written last and only over what the rebuild has actually just handed out.
            BlobchegNodes.Keep(entries);
            BlobchegFreshness.Publish(entries);

            return report;
        }

        // A node nobody touched hands back its previous bytes: Write is not called on it at all.
        static void WriteNode(BlobchegNodeEntry entry, BlobchegCollector collector, BlobchegIdTable ids)
        {
            var node = entry.Node;
            var now = IdsNow(node, ids);

            if (!entry.Dirty && entry.Records != null && Same(entry.IdsAtWrite, now))
            {
                using (BlobchegProfile.Section("  node from the cache"))
                {
                    foreach (var written in entry.Records)
                        collector.Add(node, written.Domain, written.RecordType, written.TypeHash, written.Bytes);
                }

                return;
            }

            using var _ = BlobchegProfile.Section("  node computed anew");
            using var __ = BlobchegProfile.Section("    " + node.GetType().Name);

            var start = collector.Entries.Count;

            var writer = new BlobchegNodeWriter { Collector = collector, Node = node, Ids = ids };
            try
            {
                node.Write(ref writer);
            }
            catch
            {
                // The chunk memory is freed and the node's error arrives as it was.
                collector.ReleaseBuilders(node.name, nodeFailed: true);
                throw;
            }

            collector.ReleaseBuilders(node.name, nodeFailed: false);

            foreach (var domain in BlobchegDomains.DomainsOf(node))
            {
                if (!collector.Wrote(node, domain))
                    throw new InvalidOperationException(
                        $"Blobcheg: node '{node.name}' declared domain '{domain.Name}' in OutTypes but wrote nothing into it");
            }

            var records = new List<BlobchegWritten>();
            for (var i = start; i < collector.Entries.Count; i++)
            {
                var written = collector.Entries[i];
                records.Add(new BlobchegWritten
                {
                    Domain = written.Domain,
                    RecordType = written.RecordType,
                    TypeHash = written.TypeHash,
                    Bytes = written.Bytes,
                });
            }

            entry.Records = records;
            entry.IdsAtWrite = now;
        }

        /// <summary>The ids of a node in every router at once. A node outside a router gets <see cref="BlobchegId.NoneValue"/>.</summary>
        static uint[] IdsNow(BlobchegNodeSo node, BlobchegIdTable ids)
        {
            var routers = BlobchegRouters.All;
            var now = new uint[routers.Length];

            for (var i = 0; i < routers.Length; i++)
                now[i] = ids.TryOf(node, routers[i], out var id) ? id.Value : BlobchegId.NoneValue;

            return now;
        }

        static bool Same(uint[] were, uint[] are)
        {
            if (were == null || are == null || were.Length != are.Length)
                return false;

            for (var i = 0; i < are.Length; i++)
            {
                if (were[i] != are[i])
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The contents of a domain are found by scanning the project rather than taken from a
        /// hand-written list: a list is one more place to forget in.
        ///
        /// There is a state in which NO walk finds a node: right after a rename its path is already the
        /// new one and the GUID is known, while the type and the object at it do not load yet, and
        /// neither <c>ImportAsset(ForceSynchronousImport)</c> nor <c>Refresh</c> changes that (measured).
        /// Building in that state is not allowed: the node's record would leave the file and the ids of
        /// its neighbours would move, all of it silently. That is why the walk remembers the GUIDs it has
        /// already seen and refuses on a loss — see <see cref="Lost"/>.
        /// </summary>
        public static List<BlobchegNodeSo> FindNodes()
            => FindNodesByGuid().Values.OrderBy(AssetDatabase.GetAssetPath, StringComparer.Ordinal).ToList();

        /// <summary>The same thing, but with the GUIDs: the cache keeps them so as not to ask the database again.</summary>
        internal static Dictionary<string, BlobchegNodeSo> FindNodesByGuid()
        {
            var found = Walk();

            var lost = Lost(found);
            if (lost != null)
            {
                // The asset database has not digested the rename yet: the file lies on disk, the GUID is
                // known, and neither the type nor the object at it loads yet. We let it finish and go
                // again.
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                found = Walk();
                lost = Lost(found);
            }

            if (lost != null)
                throw new BlobchegTransientException(
                    $"Blobcheg: node '{lost}' disappeared from the walk while its file lies on disk — the " +
                    "asset database has not digested the rename yet. A rebuild in this state would throw " +
                    "its record out of the file and shift the ids of its neighbours, and silently at that, " +
                    "so it refuses. Try again once the editor has finished importing");

            Seen.UnionWith(found.Keys);
            return found;
        }

        // The nodes the walk has seen in this session. It dies with the domain — the rename window.
        static readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);

        static Dictionary<string, BlobchegNodeSo> Walk()
        {
            using var _ = BlobchegProfile.Section("walk over the project");

            var byGuid = new Dictionary<string, BlobchegNodeSo>(StringComparer.Ordinal);
            var guids = new List<string>();
            var paths = new List<string>();

            // Index guids follow a move; importer paths cover a lagging search. Sweep only with no index.
            if (BlobchegFreshness.Candidates(guids, paths))
            {
                foreach (var guid in guids)
                    Consider(AssetDatabase.GUIDToAssetPath(guid), guid, byGuid);

                foreach (var path in paths)
                    Consider(path, null, byGuid);
            }
            else
            {
                foreach (var path in AssetDatabase.GetAllAssetPaths())
                    Consider(path, null, byGuid);
            }

            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(BlobchegNodeSo)))
            {
                // The GUID first, the path second: on 10,000 nodes the second walk almost entirely hits
                // what was already found, and there is no reason to pay for it in native calls.
                if (byGuid.ContainsKey(guid))
                    continue;

                Consider(AssetDatabase.GUIDToAssetPath(guid), guid, byGuid);
            }

            return byGuid;
        }

        /// <summary>
        /// A node the walk knew and no longer sees, although its file is in place. The path of such a
        /// node, or <c>null</c> if there are no losses. As a bonus it cleans out of <see cref="Seen"/>
        /// those that really are gone.
        ///
        /// A loss can be told from a normal departure by three signs at once: the file lies on disk, the
        /// GUID still points at a path, and the type at that path cannot be asked for. A deleted node
        /// loses its file, one that stopped being a node hands out its new type, and both pass by.
        /// </summary>
        static string Lost(Dictionary<string, BlobchegNodeSo> found)
        {
            List<string> gone = null;

            foreach (var guid in Seen)
            {
                if (found.ContainsKey(guid))
                    continue;

                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path)
                    && AssetDatabase.GetMainAssetTypeAtPath(path) == null
                    && File.Exists(Path.Combine(Application.dataPath, "..", path)))
                    return path;

                (gone ?? (gone = new List<string>())).Add(guid);
            }

            if (gone != null)
            {
                foreach (var guid in gone)
                    Seen.Remove(guid);
            }

            return null;
        }

        static void Consider(string path, string knownGuid, Dictionary<string, BlobchegNodeSo> byGuid)
        {
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return;

            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (type == null || !typeof(BlobchegNodeSo).IsAssignableFrom(type))
                return;

            var guid = knownGuid ?? AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid) || byGuid.ContainsKey(guid))
                return;

            var node = AssetDatabase.LoadAssetAtPath<BlobchegNodeSo>(path);

            if (node == null)
            {
                // The path from the walk lagged behind the rename: we ask the database where the asset
                // lies now.
                var now = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.Equals(now, path, StringComparison.Ordinal))
                    node = AssetDatabase.LoadAssetAtPath<BlobchegNodeSo>(now);
            }

            if (node == null)
                throw new InvalidOperationException(
                    $"Blobcheg: '{path}' is declared a node ({type.Name}) but does not load. Skipping it " +
                    "silently is not allowed: its record would leave the base and the ids of its neighbours would move");

            byGuid.Add(guid, node);
        }

        static void SyncRefs(BlobchegCollector collector, BlobchegCarriers carriers,
            List<BlobchegNodeSo> nodes, Dictionary<BlobchegNodeSo, string> guids, ref BlobchegBuildReport report)
        {
            var wanted = new HashSet<BlobchegRefSo>();

            foreach (var entry in collector.Entries)
            {
                var writer = collector.Writers[entry.Domain];
                var reference = Upsert(entry, writer, carriers, guids, ref report);
                wanted.Add(reference);
            }

            // Over all the nodes and not only over those that wrote: a node may have stopped writing
            // entirely, and its ref asset is obliged to leave together with the record.
            foreach (var node in nodes)
            {
                var staleRefs = carriers.RefsOf(node).Where(r => !wanted.Contains(r)).ToList();

                foreach (var stale in staleRefs)
                {
                    Written.Add(guids[node]);
                    carriers.Forget(node, stale);
                    AssetDatabase.RemoveObjectFromAsset(stale);
                    UnityEngine.Object.DestroyImmediate(stale, true);
                    report.RemovedRefs++;
                }
            }
        }

        static BlobchegRefSo Upsert(BlobchegEntry entry, BlobchegWriter writer, BlobchegCarriers carriers,
            Dictionary<BlobchegNodeSo, string> guids, ref BlobchegBuildReport report)
        {
            var domainName = BlobchegDomains.NameOf(entry.Domain);
            var wantedName = entry.Node.name + "_" + domainName;

            if (BlobchegStampStore.SyncRecord(guids[entry.Node], domainName, entry.RecordType,
                    writer.OffsetOf(entry.Ticket), writer.RevisionOf(entry.Ticket)))
                report.ChangedStamps++;

            var reference = carriers.Ref(entry.Node, domainName);

            if (reference == null)
            {
                reference = ScriptableObject.CreateInstance<BlobchegRefSo>();
                reference.name = wantedName;
                reference.domainName = domainName;
                using (BlobchegProfile.Section("  refs: AddObjectToAsset"))
                    AssetDatabase.AddObjectToAsset(reference, entry.Node);

                carriers.Add(entry.Node, reference);
            }
            else if (reference.name == wantedName && reference.domainName == domainName)
            {
                return reference;
            }

            // Only a rename touches the asset: what was re-stamped here lies next to the bases now.
            Written.Add(guids[entry.Node]);
            reference.name = wantedName;
            reference.domainName = domainName;

            using (BlobchegProfile.Section("  refs: SetDirty"))
                EditorUtility.SetDirty(reference);

            report.ChangedRefs++;
            return reference;
        }

        // There are no labels on the carriers any more. Nobody read them — neither the picker (it walks
        // the nodes and looks at recordType) nor the bake — while every AssetDatabase.SetLabels cost
        // 4.7 ms: on 500 nodes that is 7.1 s out of a 14.4 s cold build.

        /// <summary>The ref assets of a node — one per domain it writes into.</summary>
        public static IEnumerable<BlobchegRefSo> RefsOf(BlobchegNodeSo node)
        {
            var path = AssetDatabase.GetAssetPath(node);
            if (string.IsNullOrEmpty(path))
                yield break;

            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is BlobchegRefSo reference)
                    yield return reference;
            }
        }

        /// <summary>
        /// The router file: one row per node in id order, and in the row the offsets in every base of the
        /// router the node is in. An empty row is allowed: a node may have joined the router through one
        /// base out of ten.
        /// </summary>
        static void BuildRouters(Dictionary<(BlobchegNodeSo, Type), uint> offsets, BlobchegIdTable ids,
            ref BlobchegBuildReport report)
        {
            if (BlobchegRouters.All.Length == 0)
                return;

            foreach (var router in BlobchegRouters.All)
            {
                // The check against the codegen happens before the file is written: assembling a file for
                // one bit numbering and reading it with code built for another is exactly the divergence
                // LayoutHash exists for.
                BlobchegRouters.RequireCodeGenAgrees(router);

                var domains = BlobchegRouters.DomainsOf(router);
                var name = BlobchegRouters.NameOf(router);
                var writer = BlobchegRouterWriter.Open(
                    OutputDirectory, name, domains.Length, BlobchegRouters.LayoutHashOf(router));

                var members = ids.NodesOf(router);
                var cells = new List<BlobchegRouterCell>();

                foreach (var node in members)
                {
                    cells.Clear();

                    // A hole from a deleted node: the row is there but empty. Removing it means shifting
                    // the ids of everyone standing after it, and those ids have already travelled into
                    // other people's saves and subscenes.
                    if (node != null)
                    {
                        for (var bit = 0; bit < domains.Length; bit++)
                        {
                            if (offsets.TryGetValue((node, domains[bit]), out var offset))
                                cells.Add(new BlobchegRouterCell(bit, offset));
                        }
                    }

                    writer.Append(node == null ? string.Empty : node.name, cells);
                }

                writer.Flush(WithDebug);

                report.Routers++;
                if (writer.FileChanged)
                {
                    report.ChangedFiles++;
                    BlobchegFileVersions.Bump(BlobchegNaming.FileName(name));
                }

                SyncRouterManifest(name, writer, members, ref report);
            }
        }

        static void SyncRouterManifest(string name, BlobchegRouterWriter writer,
            IReadOnlyList<BlobchegNodeSo> members, ref BlobchegBuildReport report)
        {
            // The order of the nodes in the manifest is id order. That is the "id → node" table for the eye.
            SyncManifest(name, BlobchegFileKind.Router, members.ToArray(), writer.RowCount,
                writer.ContentHash, writer.FileChanged, ref report);
        }

        /// <summary>
        /// The foreign passes over the finished layout. The order is by full type name: the rebuild is
        /// obliged to be deterministic, and the order in which <c>TypeCache</c> hands out types is not
        /// promised to it.
        /// </summary>
        static void RunPasses(BlobchegBuildLayout layout, ref BlobchegBuildReport report)
        {
            var passes = TypeCache.GetTypesDerivedFrom<IBlobchegBuildPass>()
                .Where(type => !type.IsAbstract && !type.IsInterface)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToList();

            foreach (var type in passes)
            {
                var pass = (IBlobchegBuildPass)Activator.CreateInstance(type);

                using (BlobchegProfile.Section("  " + type.Name))
                    pass.Run(layout, ref report);
            }
        }

        /// <summary>The id carriers: one sub-asset per (node × router) pair.</summary>
        static void SyncIds(BlobchegIdTable ids, BlobchegCarriers carriers,
            List<BlobchegNodeSo> nodes, Dictionary<BlobchegNodeSo, string> guids, ref BlobchegBuildReport report)
        {
            var wanted = new HashSet<BlobchegIdSo>();

            foreach (var router in BlobchegRouters.All)
            {
                var name = BlobchegRouters.NameOf(router);
                var members = ids.NodesOf(router);

                foreach (var node in members)
                {
                    if (node != null)
                        wanted.Add(UpsertId(node, name, ids.Of(node, router), carriers, guids, ref report));
                }
            }

            foreach (var node in nodes)
            {
                var staleIds = carriers.IdsOf(node).Where(id => !wanted.Contains(id)).ToList();

                foreach (var stale in staleIds)
                {
                    Written.Add(guids[node]);
                    carriers.Forget(node, stale);
                    AssetDatabase.RemoveObjectFromAsset(stale);
                    UnityEngine.Object.DestroyImmediate(stale, true);
                    report.RemovedRefs++;
                }
            }
        }

        static int HeldRow(BlobchegIdSo carrier, string guid)
        {
            if (carrier == null)
                return -1;

            // No owner is a carrier from before owners existed: its row was handed out to this very node.
            return string.IsNullOrEmpty(carrier.owner) || carrier.owner == guid ? carrier.row : -1;
        }

        static BlobchegIdSo UpsertId(BlobchegNodeSo node, string routerName, BlobchegId id,
            BlobchegCarriers carriers, Dictionary<BlobchegNodeSo, string> guids, ref BlobchegBuildReport report)
        {
            var wantedName = node.name + "_" + routerName;
            var guid = guids[node];

            // Not an error but a migration: everything baked against the old number is baked again.
            if (BlobchegStampStore.TryPriorId(guid, routerName, out var was) && was != id.Value
                && new BlobchegId(was).IsValid)
            {
                Debug.Log($"Blobcheg: node '{node.name}' in router '{routerName}' moved: " +
                          $"{new BlobchegId(was)} → {id}");

                report.MovedIds++;
            }

            if (BlobchegStampStore.SyncId(guid, routerName, id.Value))
                report.ChangedStamps++;

            var carrier = carriers.Id(node, routerName);

            if (carrier == null)
            {
                carrier = ScriptableObject.CreateInstance<BlobchegIdSo>();
                carrier.name = wantedName;
                carrier.routerName = routerName;
                carrier.row = (int)id.Index;
                carrier.owner = guid;
                using (BlobchegProfile.Section("  ids: AddObjectToAsset"))
                    AssetDatabase.AddObjectToAsset(carrier, node);

                carriers.Add(node, carrier);
            }
            else if (carrier.name == wantedName && carrier.routerName == routerName && carrier.row == (int)id.Index
                     && carrier.owner == guid)
            {
                return carrier;
            }

            Written.Add(guid);
            carrier.name = wantedName;
            carrier.routerName = routerName;
            carrier.row = (int)id.Index;
            carrier.owner = guid;

            using (BlobchegProfile.Section("  ids: SetDirty"))
                EditorUtility.SetDirty(carrier);

            report.ChangedRefs++;
            return carrier;
        }

        /// <summary>The id carriers of a node — one per router it belongs to.</summary>
        public static IEnumerable<BlobchegIdSo> IdsOf(BlobchegNodeSo node)
        {
            var path = AssetDatabase.GetAssetPath(node);
            if (string.IsNullOrEmpty(path))
                yield break;

            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is BlobchegIdSo carrier)
                    yield return carrier;
            }
        }

        static void SyncManifests(BlobchegCollector collector, List<BlobchegNodeSo> nodes, ref BlobchegBuildReport report)
        {
            // One walk over the nodes for every domain at once: a node's OutTypes is a property, and an
            // ordinary node builds the array anew on every ask. Asking it once per domain is the same
            // walk multiplied by the number of domains.
            var members = new Dictionary<Type, List<BlobchegNodeSo>>();
            foreach (var pair in collector.Writers)
                members.Add(pair.Key, new List<BlobchegNodeSo>());

            foreach (var node in nodes)
            {
                var declared = node.OutTypes;
                if (declared == null)
                    continue;

                foreach (var domain in declared)
                {
                    if (members.TryGetValue(domain, out var list))
                        list.Add(node);
                }
            }

            foreach (var pair in collector.Writers)
            {
                var domainName = BlobchegDomains.NameOf(pair.Key);

                SyncManifest(domainName, BlobchegFileKind.Database, members[pair.Key].ToArray(),
                    pair.Value.RecordCount, pair.Value.ContentHash, pair.Value.FileChanged, ref report);
            }
        }

        internal static void SyncManifest(string name, BlobchegFileKind kind, BlobchegNodeSo[] members,
            int recordCount, ulong contentHash, bool fileChanged, ref BlobchegBuildReport report)
        {
            if (BlobchegManifests.Sync(name, kind, GuidsOf(members), recordCount, contentHash, fileChanged))
                report.ChangedManifests++;
        }

        // A hole left by a deleted node keeps its place in the order and has no guid to name it by.
        static string[] GuidsOf(BlobchegNodeSo[] members)
        {
            var guids = new string[members.Length];

            for (var i = 0; i < members.Length; i++)
            {
                guids[i] = members[i] != null
                           && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(members[i], out var guid, out long _)
                    ? guid
                    : string.Empty;
            }

            return guids;
        }
    }
}
