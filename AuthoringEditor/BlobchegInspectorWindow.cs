using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.Authoring
{
    // Live process memory; disk files stay a separate list, since a rebuild desyncs them from buffers.
    sealed class BlobchegInspectorWindow : EditorWindow
    {
        const float MinPaneWidth = 220f;
        const float DividerWidth = 4f;
        const int HexLimit = 4096;
        const double ScanSeconds = 2.0;

        [MenuItem("Tools/Blobcheg/Inspector", priority = 10)]
        static void Open()
        {
            var window = GetWindow<BlobchegInspectorWindow>();
            window.titleContent = new GUIContent("Blobcheg");
            window.minSize = new Vector2(2 * MinPaneWidth + DividerWidth, 240f);
            window.Show();
        }

        readonly HashSet<ulong> _openFiles = new HashSet<ulong>();
        readonly HashSet<string> _openNodes = new HashSet<string>();
        readonly Dictionary<ulong, Cached> _records = new Dictionary<ulong, Cached>();

        List<BlobchegLiveFile> _files;
        List<BlobchegDiskFile> _disk = new List<BlobchegDiskFile>();
        double _scanned;
        string _selectedDisk;
        ulong _selectedFile;
        uint _selectedRecord;
        bool _hasRecord;
        bool _hex;
        bool _composition;
        string _search = string.Empty;
        float _split = 340f;
        bool _dragging;
        Vector2 _treeScroll;
        Vector2 _detailScroll;

        struct Cached
        {
            public ulong Address;
            public int Length;
            public List<BlobchegLiveRecord> Records;
        }

        // A rebuild under PlayMode swaps buffers without input, so repaint on the inspector tick.
        void OnInspectorUpdate() => Repaint();

        void OnFocus() => BlobchegFreshness.Ensure("the blobcheg window");

        void OnGUI()
        {
            _files = BlobchegLive.Files();
            Scan();

            DrawToolbar();

            var body = new Rect(0f, EditorGUIUtility.singleLineHeight + 2f, position.width,
                position.height - EditorGUIUtility.singleLineHeight - 2f);

            _split = Mathf.Clamp(_split, MinPaneWidth, Mathf.Max(MinPaneWidth, body.width - MinPaneWidth));

            DrawTree(new Rect(body.x, body.y, _split, body.height));
            DrawDivider(new Rect(body.x + _split, body.y, DividerWidth, body.height));
            DrawDetails(new Rect(body.x + _split + DividerWidth, body.y,
                body.width - _split - DividerWidth, body.height));
        }

        void Scan() // on a clock, not per repaint: this hits the disk; headers only, full check by hand
        {
            if (EditorApplication.timeSinceStartup - _scanned < ScanSeconds)
                return;

            _scanned = EditorApplication.timeSinceStartup;
            _disk = BlobchegOnDisk.Files();
        }

        void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField, GUILayout.Width(220f));

                GUILayout.Label(Summary(), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(!_hasRecord))
                {
                    _hex = GUILayout.Toggle(_hex, "Hex", EditorStyles.toolbarButton, GUILayout.Width(44f));

                    if (GUILayout.Button("Копировать", EditorStyles.toolbarButton, GUILayout.Width(90f)))
                        CopySelection();
                }
            }
        }

        string Summary()
        {
            var bases = 0;
            var routers = 0;
            var tables = 0;
            var bytes = 0L;

            foreach (var file in _files)
            {
                bytes += file.Length;

                switch (file.Kind)
                {
                    case BlobchegFileKind.Router:
                        routers++;
                        break;
                    case BlobchegFileKind.Hashes:
                        tables++;
                        break;
                    default:
                        bases++;
                        break;
                }
            }

            return $"баз {bases} · роутеров {routers} · таблиц {tables} · {Size(bytes)}";
        }

        void DrawTree(Rect area)
        {
            GUILayout.BeginArea(area);
            _treeScroll = EditorGUILayout.BeginScrollView(_treeScroll);

            if (_files.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Реестр пуст: ни одна база не поднята. Базы поднимает boot-система своего мира — "
                    + "войди в PlayMode либо подними базу руками.", MessageType.Info);
            }

            Type router = null;
            foreach (var file in _files)
            {
                if (file.Kind == BlobchegFileKind.Database && file.Router != null && file.Router != router)
                {
                    router = file.Router;
                    EditorGUILayout.LabelField(BlobchegRouters.NameOf(router), EditorStyles.miniBoldLabel);
                }

                DrawFile(file);
            }

            DrawDisk();

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // Kept apart from loaded bases: a file is the last build, a buffer is what the game reads.
        void DrawDisk()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("На диске", EditorStyles.miniBoldLabel);

            if (_disk.Count == 0)
            {
                EditorGUILayout.LabelField(
                    BlobchegOnDisk.Directory == null ? "транспорт не файловый" : "файлов нет",
                    EditorStyles.miniLabel);
                return;
            }

            foreach (var file in _disk)
            {
                if (!string.IsNullOrEmpty(_search)
                    && file.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var live = BlobchegLive.Find(_files, file.Name);
                var state = live == null
                    ? "не поднят"
                    : BlobchegOnDisk.SameContent(file, live) ? "поднят" : "поднят, но байты другие";

                var style = _selectedDisk == file.Path ? EditorStyles.boldLabel : EditorStyles.label;
                if (GUILayout.Button($"{file.Name}   {Size(file.Length)}   · {state}", style))
                    SelectDisk(file.Path);
            }
        }

        void DrawFile(BlobchegLiveFile file)
        {
            var open = _openFiles.Contains(file.Key);

            // Size in the label: a foldout owns its whole line, a second column lands under the arrow.
            var next = EditorGUILayout.Foldout(
                open, $"{Mark(file.Kind)} {file.Name}   {Size(file.Length)}", true);

            if (next != open)
            {
                if (next)
                    _openFiles.Add(file.Key);
                else
                    _openFiles.Remove(file.Key);

                Select(file.Key);
            }

            if (!_openFiles.Contains(file.Key))
                return;

            EditorGUI.indentLevel++;

            if (!file.Readable)
                EditorGUILayout.HelpBox(file.Trouble, MessageType.Error);
            else if (file.Kind == BlobchegFileKind.Database)
                DrawRecords(file);
            else if (file.Kind == BlobchegFileKind.Router)
                EditorGUILayout.LabelField("строки роутера — справа", EditorStyles.miniLabel);
            else
                EditorGUILayout.LabelField("таблица хэшей — справа", EditorStyles.miniLabel);

            EditorGUI.indentLevel--;
        }

        void DrawRecords(BlobchegLiveFile file)
        {
            if (!file.HasDebug)
            {
                EditorGUILayout.HelpBox(
                    "У файла нет debug-контура — он собран для релизного плеера. Список записей "
                    + "существует только в контуре: в самом файле таблицы записей нет.", MessageType.Info);
                return;
            }

            var records = RecordsOf(file);
            var shown = 0;

            foreach (var record in records)
            {
                if (!Matches(record))
                    continue;

                shown++;
                var selected = _hasRecord && _selectedFile == file.Key && _selectedRecord == record.Offset;

                using (new EditorGUILayout.HorizontalScope())
                {
                    var name = record.NodeName;
                    if (string.IsNullOrEmpty(name))
                        name = "—";

                    var style = selected ? EditorStyles.boldLabel : EditorStyles.label;
                    if (GUILayout.Button($"{name}  ·  {Short(record.TypeName)}", style))
                        Select(file.Key, record.Offset);

                    GUILayout.FlexibleSpace();
                    GUILayout.Label("@" + record.Offset, EditorStyles.miniLabel);
                }
            }

            if (shown == 0)
                EditorGUILayout.LabelField(records.Count == 0 ? "записей нет" : "ничего не найдено",
                    EditorStyles.miniLabel);
        }

        void DrawDivider(Rect area)
        {
            EditorGUI.DrawRect(area, new Color(0f, 0f, 0f, 0.25f));
            EditorGUIUtility.AddCursorRect(area, MouseCursor.ResizeHorizontal);

            var e = Event.current;
            if (e.type == EventType.MouseDown && area.Contains(e.mousePosition))
                _dragging = true;
            else if (e.type == EventType.MouseUp)
                _dragging = false;

            if (_dragging && e.type == EventType.MouseDrag)
            {
                _split = e.mousePosition.x;
                Repaint();
            }
        }

        void DrawDetails(Rect area)
        {
            GUILayout.BeginArea(area);
            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);

            var disk = SelectedDisk();
            var file = Selected(_selectedFile);

            if (disk != null)
                DrawDiskDetails(disk);
            else if (file == null)
                EditorGUILayout.LabelField("выбери файл слева", EditorStyles.miniLabel);
            else if (_hasRecord)
                DrawRecordDetails(file);
            else
                DrawFileDetails(file);

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void DrawDiskDetails(BlobchegDiskFile file)
        {
            EditorGUILayout.LabelField(file.Name, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("путь", file.Path);
            EditorGUILayout.LabelField("размер", $"{file.Length} Б");
            EditorGUILayout.LabelField("записан", file.Written.ToString("yyyy-MM-dd HH:mm:ss"));

            if (!file.Readable)
            {
                EditorGUILayout.HelpBox(file.Trouble, MessageType.Error);
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Шапка", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("вид", BlobchegFormat.NameOf(file.Kind));
            EditorGUILayout.LabelField("версия формата",
                file.Header.Version.ToString(CultureInfo.InvariantCulture));
            EditorGUILayout.LabelField("длина по шапке", $"{file.Header.FileLength} Б");
            EditorGUILayout.LabelField("debug-контур",
                file.Header.HasDebug ? "@" + file.Header.DebugOffset : "нет (собран для релиза)");
            EditorGUILayout.LabelField("хэш содержимого", file.Header.ContentHash.ToString("X16"));
            EditorGUILayout.LabelField("хэш имени", file.Header.NameHash.ToString("X16"));

            DrawManifest(file.Name);

            EditorGUILayout.Space();
            var live = BlobchegLive.Find(_files, file.Name);
            if (live == null)
            {
                EditorGUILayout.HelpBox(
                    "База этого домена в процессе не поднята — читать её записи неоткуда.",
                    MessageType.Info);
            }
            else if (BlobchegOnDisk.SameContent(file, live))
            {
                EditorGUILayout.HelpBox("Поднятый буфер несёт то же содержимое.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Поднятый буфер несёт ДРУГОЕ содержимое: файл пересобрали после того, как мир его "
                    + "поднял. Игра читает старые байты.", MessageType.Warning);
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Проверить целостность"))
                BlobchegOnDisk.Verify(file);

            if (file.Whole == null)
                EditorGUILayout.LabelField("целостность", "не проверена");
            else if (file.Whole.Value)
                EditorGUILayout.LabelField("целостность", "сходится");
            else
                EditorGUILayout.HelpBox(
                    $"Файл не сходится с собственной шапкой: посчитано {file.ComputedHash:X16}, "
                    + $"в шапке {file.Header.ContentHash:X16}. Такой файл не поднимется и в игре.",
                    MessageType.Error);
        }

        // The composition of a built file: the only place where "which asset got which id" is written.
        void DrawManifest(string name)
        {
            var manifest = BlobchegManifests.Of(name);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Manifest", EditorStyles.boldLabel);

            if (manifest == null)
            {
                EditorGUILayout.LabelField("built", "never in this checkout");
                return;
            }

            EditorGUILayout.LabelField("built", manifest.builtAt);
            EditorGUILayout.LabelField("records", manifest.recordCount.ToString(CultureInfo.InvariantCulture));
            EditorGUILayout.LabelField("content hash", manifest.ContentHash.ToString("X16"));

            _composition = EditorGUILayout.Foldout(_composition,
                manifest.IsRouter ? "rows by id" : "nodes", true);

            if (!_composition)
                return;

            using (new EditorGUI.IndentLevelScope())
            {
                for (var i = 0; i < manifest.NodeCount; i++)
                {
                    EditorGUILayout.ObjectField(i.ToString(CultureInfo.InvariantCulture),
                        manifest.NodeAt(i), typeof(BlobchegNodeSo), false);
                }
            }
        }

        void DrawFileDetails(BlobchegLiveFile file)
        {
            EditorGUILayout.LabelField(file.Name, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("вид", BlobchegFormat.NameOf(file.Kind));
            EditorGUILayout.LabelField("адрес буфера", "0x" + file.Address.ToString("X16"));
            EditorGUILayout.LabelField("длина", $"{file.Length} Б");

            if (file.Router != null)
                EditorGUILayout.LabelField("бит в роутере", $"{file.Bit} ({BlobchegRouters.NameOf(file.Router)})");

            if (!file.Readable)
            {
                EditorGUILayout.HelpBox(file.Trouble, MessageType.Error);
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Шапка", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("версия формата", file.Header.Version.ToString(CultureInfo.InvariantCulture));
            EditorGUILayout.LabelField("флаги", "0x" + file.Header.Flags.ToString("X4"));
            EditorGUILayout.LabelField("длина по шапке", $"{file.Header.FileLength} Б");
            EditorGUILayout.LabelField("debug-контур",
                file.HasDebug ? "@" + file.DebugOffset : "нет (собран для релиза)");
            EditorGUILayout.LabelField("хэш содержимого", file.Header.ContentHash.ToString("X16"));
            EditorGUILayout.LabelField("хэш имени", file.Header.NameHash.ToString("X16"));

            DrawRetired(file);

            if (file.Kind == BlobchegFileKind.Router)
                DrawRows(file);
            else if (file.Kind == BlobchegFileKind.Hashes)
                DrawTable(file);
        }

        // Prolog only, no rows: a row is a name hash that unfolds into no name anywhere.
        unsafe void DrawTable(BlobchegLiveFile file)
        {
            if (file.Length < BlobchegHashesFormat.PrologOffset + BlobchegHashesFormat.PrologSize)
            {
                EditorGUILayout.HelpBox("файл короче пролога таблицы", MessageType.Error);
                return;
            }

            var prolog = *(BlobchegHashesProlog*)((byte*)file.Address + BlobchegHashesFormat.PrologOffset);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Таблица", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("строк", prolog.Count.ToString(CultureInfo.InvariantCulture));
            EditorGUILayout.LabelField("ёмкость", prolog.Capacity.ToString(CultureInfo.InvariantCulture));
            EditorGUILayout.LabelField("баз роутера", prolog.DomainCount.ToString(CultureInfo.InvariantCulture));
            EditorGUILayout.LabelField("хэш раскладки", prolog.LayoutHash.ToString("X16"));
            EditorGUILayout.LabelField("обратных полос", prolog.Total.ToString(CultureInfo.InvariantCulture));
        }

        void DrawRetired(BlobchegLiveFile file) // old pointers land here; remapped while still remembered
        {
            if (file.Retired.Count == 0)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                $"Отставные поколения ({file.Retired.Count} из {BlobchegBases.RetiredGenerations})",
                EditorStyles.boldLabel);

            for (var i = 0; i < file.Retired.Count; i++)
            {
                var generation = file.Retired[i];
                EditorGUILayout.LabelField($"−{i + 1}",
                    $"0x{generation.Address:X16}   {generation.Length} Б");
            }
        }

        void DrawRows(BlobchegLiveFile file)
        {
            List<BlobchegLiveRow> rows;
            try
            {
                rows = BlobchegLive.RowsOf(file);
            }
            catch (Exception e)
            {
                EditorGUILayout.HelpBox(e.Message, MessageType.Error);
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Строки ({rows.Count})", EditorStyles.boldLabel);

            foreach (var row in rows)
            {
                var name = string.IsNullOrEmpty(row.NodeName) ? "—" : row.NodeName;
                if (!string.IsNullOrEmpty(_search)
                    && name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                EditorGUILayout.LabelField($"{row.Id.Index}  {name}", EditorStyles.miniBoldLabel);

                EditorGUI.indentLevel++;
                foreach (var entry in row.Entries)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"{entry.Domain}", GUILayout.Width(180f));

                        if (GUILayout.Button("@" + entry.Offset, EditorStyles.miniButton, GUILayout.Width(80f)))
                            Jump(entry.Domain, entry.Offset);
                    }
                }

                EditorGUI.indentLevel--;
            }
        }

        void DrawRecordDetails(BlobchegLiveFile file)
        {
            var record = RecordAt(file, _selectedRecord);
            if (record == null)
            {
                EditorGUILayout.HelpBox(
                    $"Записи на смещении {_selectedRecord} больше нет — базу пересобрали.", MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(record.NodeName) ? "—" : record.NodeName, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("тип", record.TypeName);
            EditorGUILayout.LabelField("смещение", $"@{record.Offset}  (0x{record.Offset:X})");
            EditorGUILayout.LabelField("размер", $"{record.Size} Б");
            EditorGUILayout.LabelField("база", $"{file.Name}  ·  0x{file.Address:X16}");

            if (record.Type == null)
            {
                EditorGUILayout.HelpBox(
                    "Типа с таким именем в проекте нет — запись собрана кодом, которого больше не "
                    + "существует. Остаются только байты.", MessageType.Warning);
            }
            else if (!record.TypeAgrees)
            {
                EditorGUILayout.HelpBox(
                    "Хэш типа в контуре не сходится с текущей структурой: файл собран другой версией "
                    + "кода. Раскладка полей уехала, разбор соврал бы — остаются байты.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space();

            var byBytes = _hex || record.Type == null || !record.TypeAgrees;
            if (byBytes)
                DrawHex(file, record);
            else
                DrawFields(file, record);
        }

        void DrawFields(BlobchegLiveFile file, BlobchegLiveRecord record)
        {
            var root = Decode(file, record);
            for (var i = 0; i < root.Children.Count; i++)
                DrawNode(root.Children[i], root.Children[i].Name, 0);

            if (root.Children.Count == 0 && !string.IsNullOrEmpty(root.Value))
                EditorGUILayout.LabelField(root.Value);
        }

        void DrawNode(BlobchegRecordNode node, string path, int depth)
        {
            if (node.IsLeaf)
            {
                EditorGUI.indentLevel += depth;
                EditorGUILayout.LabelField(node.Name, node.Value ?? string.Empty);
                EditorGUI.indentLevel -= depth;
                return;
            }

            var open = depth == 0 || _openNodes.Contains(path);
            var header = string.IsNullOrEmpty(node.Value)
                ? $"{node.Name}   {node.TypeName}"
                : $"{node.Name}   {node.TypeName} {node.Value}";

            EditorGUI.indentLevel += depth;
            var next = EditorGUILayout.Foldout(open, header, true);
            EditorGUI.indentLevel -= depth;

            if (depth > 0)
            {
                if (next)
                    _openNodes.Add(path);
                else
                    _openNodes.Remove(path);
            }

            if (!next)
                return;

            for (var i = 0; i < node.Children.Count; i++)
                DrawNode(node.Children[i], path + "/" + node.Children[i].Name, depth + 1);
        }

        unsafe void DrawHex(BlobchegLiveFile file, BlobchegLiveRecord record)
        {
            var size = Math.Min(record.Size, (uint)HexLimit);
            var dump = BlobchegRecordView.Dump((byte*)file.Address, file.Length, record.Offset, size);

            EditorGUILayout.TextArea(dump, EditorStyles.miniLabel);

            if (size < record.Size)
                EditorGUILayout.LabelField($"…показаны первые {size} из {record.Size} Б", EditorStyles.miniLabel);
        }

        unsafe BlobchegRecordNode Decode(BlobchegLiveFile file, BlobchegLiveRecord record)
            => BlobchegRecordView.Of((byte*)file.Address, file.Length, record.Offset, record.Type,
                string.IsNullOrEmpty(record.NodeName) ? record.TypeName : record.NodeName);

        void CopySelection()
        {
            var file = Selected(_selectedFile);
            var record = file != null ? RecordAt(file, _selectedRecord) : null;
            if (record == null)
                return;

            EditorGUIUtility.systemCopyBuffer = record.Type != null && record.TypeAgrees
                ? BlobchegRecordView.ToText(Decode(file, record))
                : HexOf(file, record);
        }

        unsafe string HexOf(BlobchegLiveFile file, BlobchegLiveRecord record)
            => BlobchegRecordView.Dump((byte*)file.Address, file.Length, record.Offset, record.Size);

        // An unloaded domain has nothing to jump to: the row knows the offset, the bytes live in the base.
        void Jump(string domain, uint offset)
        {
            var file = BlobchegLive.Find(_files, domain);
            if (file == null)
                return;

            _openFiles.Add(file.Key);
            Select(file.Key, offset);
        }

        void Select(ulong key)
        {
            _selectedFile = key;
            _selectedDisk = null;
            _hasRecord = false;
            _openNodes.Clear();
        }

        void Select(ulong key, uint offset)
        {
            _selectedFile = key;
            _selectedDisk = null;
            _selectedRecord = offset;
            _hasRecord = true;
            _openNodes.Clear();
        }

        void SelectDisk(string path)
        {
            _selectedDisk = path;
            _selectedFile = 0;
            _hasRecord = false;
            _openNodes.Clear();
        }

        BlobchegDiskFile SelectedDisk()
        {
            if (_selectedDisk == null)
                return null;

            foreach (var file in _disk)
            {
                if (file.Path == _selectedDisk)
                    return file;
            }

            return null;
        }

        BlobchegLiveFile Selected(ulong key)
        {
            foreach (var file in _files)
            {
                if (file.Key == key)
                    return file;
            }

            return null;
        }

        BlobchegLiveRecord RecordAt(BlobchegLiveFile file, uint offset)
        {
            if (file.Kind != BlobchegFileKind.Database)
                return null;

            foreach (var record in RecordsOf(file))
            {
                if (record.Offset == offset)
                    return record;
            }

            return null;
        }

        // Keyed by address and length: a rebuild may land the new generation where the old one lay.
        List<BlobchegLiveRecord> RecordsOf(BlobchegLiveFile file)
        {
            if (_records.TryGetValue(file.Key, out var cached)
                && cached.Address == file.Address && cached.Length == file.Length)
                return cached.Records;

            cached = new Cached
            {
                Address = file.Address,
                Length = file.Length,
                Records = BlobchegLive.RecordsOf(file),
            };

            _records[file.Key] = cached;
            return cached.Records;
        }

        bool Matches(BlobchegLiveRecord record)
        {
            if (string.IsNullOrEmpty(_search))
                return true;

            return (record.NodeName != null
                    && record.NodeName.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0)
                   || (record.TypeName != null
                       && record.TypeName.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static string Mark(BlobchegFileKind kind)
        {
            switch (kind)
            {
                case BlobchegFileKind.Router: return "◆";
                case BlobchegFileKind.Hashes: return "#";
                default: return "▪";
            }
        }

        static string Short(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return "?";

            var dot = typeName.LastIndexOf('.');
            return dot >= 0 ? typeName.Substring(dot + 1) : typeName;
        }

        static string Size(long bytes)
            => bytes < 1024
                ? bytes + " Б"
                : (bytes / 1024f).ToString("0.#", CultureInfo.InvariantCulture) + " КБ";

    }
}
