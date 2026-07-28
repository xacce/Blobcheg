using System;
using System.Collections.Generic;
using UnityEditor;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Что пересборка помнит с прошлого раза: список нод, их записи в байтах и их носители.
    /// Живёт в памяти и умирает вместе с доменом — переживать перезагрузку ему незачем, а файл на
    /// диске был бы вторым источником правды о том, что и так лежит в ассетах.
    ///
    /// Смысл один: пересборка обязана стоить столько, сколько изменилось, а не сколько нод в
    /// проекте. Без кеша каждый импорт любой ноды заново обходит проект, зовёт Write у всех и
    /// читает носители всех — на 10 000 нодах это секунды на каждое сохранение.
    ///
    /// Грязной нода становится тремя путями: её переимпортировали (это видит тот же хук, который
    /// пересборку и запускает), её правят в инспекторе и она грязная в памяти, или ей выдали
    /// другой id, чем был на момент записи — свой id нода могла положить прямо в запись.
    ///
    /// Чего кеш НЕ умеет: заметить правку чужого ассета, от которого нода зависит. Пересборка и
    /// раньше на неё не срабатывала — хук запускается только на импорт самой ноды, — поэтому кеш
    /// здесь ничего не ухудшает и не притворяется, что умеет больше.
    /// </summary>
    static class BlobchegCache
    {
        /// <summary>Одна запись ноды: то же самое, что нода отдала коллектору.</summary>
        public struct Written
        {
            public Type Domain;
            public string RecordType;
            public uint TypeHash;
            public byte[] Bytes;
        }

        public sealed class Entry
        {
            public string Path;

            /// <summary>Личность ноды: путь у неё меняется, GUID — нет.</summary>
            public string Guid;

            public BlobchegNodeSo Node;

            /// <summary>Нужно звать Write заново.</summary>
            public bool Dirty = true;

            /// <summary>Что нода написала в прошлый раз. <c>null</c> — не писала ни разу.</summary>
            public List<Written> Records;

            /// <summary>Id, с которыми она это писала: по индексу <see cref="BlobchegRouters.All"/>.</summary>
            public uint[] IdsAtWrite;

            /// <summary>Носители ноды, прочитанные с ассета. <c>null</c> — не читаны.</summary>
            public List<BlobchegRefSo> Refs;

            public List<BlobchegIdSo> Ids;
        }

        static readonly List<Entry> Entries = new List<Entry>();
        static readonly Dictionary<string, Entry> ByPath = new Dictionary<string, Entry>(StringComparer.Ordinal);

        static bool _filled;

        /// <summary>Ноды в порядке пути. Первый заход обходит проект, дальше список правится точечно.</summary>
        public static IReadOnlyList<Entry> Fill()
        {
            if (_filled)
            {
                // Ассет мог быть уничтожен мимо хука (например, откатом версии на диске).
                // Пустая обёртка в списке — это молчаливо пропущенная нода, поэтому список
                // собирается заново.
                foreach (var entry in Entries)
                {
                    if (entry.Node == null)
                    {
                        Drop();
                        break;
                    }
                }
            }

            if (_filled)
                return Entries;

            // Список сортируется разом и набирается в хвост: вставка поиском места превратила бы
            // наполнение на 10 000 нодах в квадрат. GUID берётся у обхода, а не спрашивается заново:
            // он там уже посчитан.
            var found = BlobchegBuild.FindNodesByGuid();
            var byPath = new List<KeyValuePair<string, Entry>>(found.Count);

            foreach (var pair in found)
            {
                var path = AssetDatabase.GetAssetPath(pair.Value);
                byPath.Add(new KeyValuePair<string, Entry>(path,
                    new Entry { Path = path, Guid = pair.Key, Node = pair.Value }));
            }

            byPath.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            foreach (var pair in byPath)
            {
                Entries.Add(pair.Value);
                ByPath[pair.Key] = pair.Value;
            }

            _filled = true;
            return Entries;
        }

        /// <summary>
        /// Носители ноды: из памяти, если пересборка их уже читала, иначе с ассета. Этим живут
        /// пикеры — им нужен весь проект сразу, а спрашивать базу ассетов по разу на ноду на
        /// 10 000 нодах стоит секунды на каждое открытие поля.
        /// </summary>
        public static IEnumerable<BlobchegRefSo> RefsOf(Entry entry)
            => entry.Refs ?? BlobchegBuild.RefsOf(entry.Node);

        public static IEnumerable<BlobchegIdSo> IdsOf(Entry entry)
            => entry.Ids ?? BlobchegBuild.IdsOf(entry.Node);

        /// <summary>Забыть всё. Зовут гейт пре-билда и правки, после которых верить кешу нельзя.</summary>
        public static void Drop()
        {
            Entries.Clear();
            ByPath.Clear();
            _filled = false;
        }

        /// <summary>Что принёс импорт. Пути — из <c>OnPostprocessAllAssets</c>.</summary>
        public static void Touch(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (!_filled)
                return;

            foreach (var path in deleted)
                Remove(path);

            // Переезд берётся ПАРОЙ, а не двумя независимыми списками: сам ассет никуда не девался,
            // и перечитывать его по новому пути нельзя — база ассетов о переименовании в этом заходе
            // ещё не знает, и нода молча выпала бы из пересборки вместе со своей записью.
            for (var i = 0; i < moved.Length; i++)
            {
                if (i < movedFrom.Length && ByPath.TryGetValue(movedFrom[i], out var entry))
                {
                    Rekey(entry, moved[i]);
                    continue;
                }

                if (i < movedFrom.Length)
                    Remove(movedFrom[i]);

                Mark(moved[i]);
            }

            foreach (var path in imported)
                Mark(path);
        }

        /// <summary>Тот же ассет по новому пути: запись остаётся, место в списке пересчитывается.</summary>
        static void Rekey(Entry entry, string path)
        {
            ByPath.Remove(entry.Path);
            Entries.Remove(entry);

            entry.Path = path;
            entry.Dirty = true;

            var at = Entries.Count;
            for (var i = 0; i < Entries.Count; i++)
            {
                if (string.CompareOrdinal(Entries[i].Path, path) > 0)
                {
                    at = i;
                    break;
                }
            }

            Entries.Insert(at, entry);
            ByPath[path] = entry;
        }

        static void Mark(string path)
        {
            if (ByPath.TryGetValue(path, out var entry))
            {
                entry.Dirty = true;
                return;
            }

            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return;

            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (type == null || !typeof(BlobchegNodeSo).IsAssignableFrom(type))
                return;

            var node = AssetDatabase.LoadAssetAtPath<BlobchegNodeSo>(path);
            if (node != null)
                Put(path, node);
        }

        static void Remove(string path)
        {
            if (!ByPath.TryGetValue(path, out var entry))
                return;

            ByPath.Remove(path);
            Entries.Remove(entry);
        }

        /// <summary>Порядок нод — по пути: он же порядок, в котором их отдаёт полный обход.</summary>
        static void Put(string path, BlobchegNodeSo node)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);

            // Нода, созданная между полными обходами, известна только кешу. Обходу о ней надо
            // сказать: иначе она пропадёт из него незаметно — ему не с чем будет сверить.
            BlobchegBuild.Remember(guid);

            var entry = new Entry { Path = path, Guid = guid, Node = node };

            var at = Entries.Count;
            for (var i = 0; i < Entries.Count; i++)
            {
                if (string.CompareOrdinal(Entries[i].Path, path) > 0)
                {
                    at = i;
                    break;
                }
            }

            Entries.Insert(at, entry);
            ByPath.Add(path, entry);
        }
    }
}
