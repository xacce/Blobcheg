using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// The picker for a reference field. Our own and not the native one: the native one can only filter
    /// by asset type, and we have one asset type for the whole system — the records of every domain
    /// would end up in the list at once. Here the list is gathered beforehand (by record type or by
    /// router), so there is nothing to put something foreign in with.
    ///
    /// The list arrives ready-made rather than being gathered inside: there is one window both for
    /// records and for id carriers, and it has no business knowing about either of them.
    /// </summary>
    sealed class BlobchegRefPickerWindow : EditorWindow
    {
        const float RowHeight = 22f;
        const float Width = 340f;
        const float Height = 320f;

        List<ScriptableObject> _all;
        List<ScriptableObject> _shown;
        Action<ScriptableObject> _pick;
        Func<ScriptableObject, string> _hint;
        string _title;
        string _empty;
        string _search = string.Empty;
        Vector2 _scroll;
        int _hot = -1;
        bool _focused;

        public static void Open(Rect fieldRect, string title, string empty, List<ScriptableObject> candidates,
            ScriptableObject current, Func<ScriptableObject, string> hint, Action<ScriptableObject> pick)
        {
            var window = CreateInstance<BlobchegRefPickerWindow>();
            window._title = title;
            window._empty = empty;
            window._pick = pick;
            window._hint = hint;
            window._all = candidates;
            window._shown = candidates;
            window._hot = current == null ? -1 : candidates.IndexOf(current);

            var screen = GUIUtility.GUIToScreenRect(fieldRect);
            window.ShowAsDropDown(screen, new Vector2(Mathf.Max(Width, fieldRect.width), Height));
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField(_title, EditorStyles.boldLabel);
            DrawSearch();
            DrawList();
        }

        void DrawSearch()
        {
            GUI.SetNextControlName("blobcheg-search");
            var search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);
            if (search != _search)
            {
                _search = search;
                _shown = Filter();
            }

            if (_focused)
                return;

            // The focus is set once: do it every frame and nothing can be typed into the field.
            EditorGUI.FocusTextInControl("blobcheg-search");
            _focused = true;
        }

        List<ScriptableObject> Filter()
        {
            if (string.IsNullOrEmpty(_search))
                return _all;

            var found = new List<ScriptableObject>();
            foreach (var candidate in _all)
            {
                if (candidate.name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0)
                    found.Add(candidate);
            }

            return found;
        }

        void DrawList()
        {
            var picked = false;
            ScriptableObject choice = null;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (Row("No reference", null, _hot < 0))
                picked = true;

            var selected = Selected();
            foreach (var candidate in _shown)
            {
                if (!Row(candidate.name, candidate, ReferenceEquals(candidate, selected)))
                    continue;

                picked = true;
                choice = candidate;
            }

            if (_shown.Count == 0)
                EditorGUILayout.HelpBox(_all.Count == 0 ? _empty : "Nothing was found for that query.", MessageType.Info);

            EditorGUILayout.EndScrollView();

            // Close only after EndScrollView: leaving from the middle of a layout breaks the GUI.
            if (!picked)
                return;

            _pick(choice);
            Close();
        }

        ScriptableObject Selected() => _hot >= 0 && _hot < _all.Count ? _all[_hot] : null;

        bool Row(string label, ScriptableObject candidate, bool selected)
        {
            var rect = GUILayoutUtility.GetRect(0, RowHeight, GUILayout.ExpandWidth(true));

            if (Event.current.type == EventType.Repaint)
            {
                if (selected)
                    EditorGUI.DrawRect(rect, new Color(0.24f, 0.37f, 0.59f, 1f));

                var content = candidate == null
                    ? new GUIContent(label)
                    : new GUIContent(label, AssetPreview.GetMiniThumbnail(candidate));

                EditorStyles.label.Draw(rect, content, false, false, selected, false);

                if (candidate != null && _hint != null)
                {
                    var hint = new Rect(rect.xMax - 90f, rect.y, 88f, rect.height);
                    EditorStyles.miniLabel.Draw(hint, new GUIContent(_hint(candidate)), false, false, false, false);
                }
            }

            return Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition);
        }
    }
}
