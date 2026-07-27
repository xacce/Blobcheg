using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Пикер поля-ссылки. Свой, а не нативный: нативный умеет фильтровать только по типу ассета, а
    /// ассет-тип у нас один на всю систему — в списке оказались бы записи всех доменов сразу.
    /// Здесь список собран по типу записи, поэтому положить чужое просто нечем.
    /// </summary>
    sealed class BlobchegRefPickerWindow : EditorWindow
    {
        const float RowHeight = 22f;
        const float Width = 340f;
        const float Height = 320f;

        List<BlobchegRefSo> _all;
        List<BlobchegRefSo> _shown;
        Action<BlobchegRefSo> _pick;
        Type _recordType;
        string _search = string.Empty;
        Vector2 _scroll;
        int _hot = -1;
        bool _focused;

        public static void Open(Rect fieldRect, Type recordType, BlobchegRefSo current, Action<BlobchegRefSo> pick)
        {
            var window = CreateInstance<BlobchegRefPickerWindow>();
            window._recordType = recordType;
            window._pick = pick;
            window._all = BlobchegRefCatalog.Candidates(recordType);
            window._shown = window._all;
            window._hot = current == null ? -1 : window._all.IndexOf(current);

            var screen = GUIUtility.GUIToScreenRect(fieldRect);
            window.ShowAsDropDown(screen, new Vector2(Mathf.Max(Width, fieldRect.width), Height));
        }

        void OnGUI()
        {
            DrawHeader();
            DrawSearch();
            DrawList();
        }

        void DrawHeader()
        {
            var title = _recordType == null ? "Сырые записи" : _recordType.Name;
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
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

            // Фокус ставится один раз: каждый кадр — и в поле не набрать ничего.
            EditorGUI.FocusTextInControl("blobcheg-search");
            _focused = true;
        }

        List<BlobchegRefSo> Filter()
        {
            if (string.IsNullOrEmpty(_search))
                return _all;

            var found = new List<BlobchegRefSo>();
            foreach (var reference in _all)
            {
                if (reference.name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0)
                    found.Add(reference);
            }

            return found;
        }

        void DrawList()
        {
            var picked = false;
            BlobchegRefSo choice = null;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (Row("Нет ссылки", null, _hot < 0))
                picked = true;

            var selected = Selected();
            foreach (var reference in _shown)
            {
                if (!Row(reference.name, reference, ReferenceEquals(reference, selected)))
                    continue;

                picked = true;
                choice = reference;
            }

            if (_shown.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    _all.Count == 0
                        ? "Записей этого типа в проекте нет. Нода, которая его пишет, ещё не создана — " +
                          "или пересборка не дошла до ref-ассетов."
                        : "По запросу ничего не нашлось.",
                    MessageType.Info);
            }

            EditorGUILayout.EndScrollView();

            // Закрываться только после EndScrollView: выход из середины layout'а роняет GUI.
            if (!picked)
                return;

            _pick(choice);
            Close();
        }

        BlobchegRefSo Selected() => _hot >= 0 && _hot < _all.Count ? _all[_hot] : null;

        bool Row(string label, BlobchegRefSo reference, bool selected)
        {
            var rect = GUILayoutUtility.GetRect(0, RowHeight, GUILayout.ExpandWidth(true));

            if (Event.current.type == EventType.Repaint)
            {
                if (selected)
                    EditorGUI.DrawRect(rect, new Color(0.24f, 0.37f, 0.59f, 1f));

                var content = reference == null
                    ? new GUIContent(label)
                    : new GUIContent(label, AssetPreview.GetMiniThumbnail(reference));

                EditorStyles.label.Draw(rect, content, false, false, selected, false);

                if (reference != null)
                {
                    var offset = new Rect(rect.xMax - 90f, rect.y, 88f, rect.height);
                    EditorStyles.miniLabel.Draw(offset, new GUIContent("offset " + reference.offset), false, false, false, false);
                }
            }

            return Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition);
        }
    }
}
