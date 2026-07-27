using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Драйвер поля-обёртки. Тип поля держит компилятор, а этот слой не даёт положить в
    /// <c>BlobchegRef&lt;GunData&gt;</c> ассет чужой записи — ни пикером, ни перетаскиванием.
    /// Пустое поле и чужая запись подсвечиваются: молча нулевой оффсет не поедет.
    ///
    /// Поле рисуется своё, а не <c>EditorGUI.ObjectField</c>: нативный пикер фильтрует только по
    /// типу ассета, а ассет-тип у нас один на всю систему — в списке лежали бы записи всех доменов.
    /// </summary>
    [CustomPropertyDrawer(typeof(BlobchegRef<>), true)]
    [CustomPropertyDrawer(typeof(BlobchegRawRef), true)]
    public sealed class BlobchegRefDrawer : PropertyDrawer
    {
        const float Gap = 2f;
        const float PingWidth = 26f;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var line = EditorGUIUtility.singleLineHeight;
            return Problem(property) == null ? line : line * 2 + Gap;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var asset = property.FindPropertyRelative("asset");
            var expected = ExpectedRecordType(fieldInfo);
            var current = asset.objectReferenceValue as BlobchegRefSo;

            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);

            EditorGUI.BeginProperty(position, label, property);

            var body = EditorGUI.PrefixLabel(line, label);
            var field = new Rect(body.x, body.y, body.width - PingWidth - Gap, body.height);
            var ping = new Rect(field.xMax + Gap, body.y, PingWidth, body.height);

            DragAndDropInto(field, expected, asset);

            if (GUI.Button(field, Caption(current, expected), EditorStyles.objectField))
                OpenPicker(field, property, expected, current);

            using (new EditorGUI.DisabledScope(current == null))
            {
                if (GUI.Button(ping, EditorGUIUtility.IconContent("d_Search Icon"), EditorStyles.miniButton))
                    EditorGUIUtility.PingObject(current);
            }

            var problem = Problem(property);
            if (problem != null)
            {
                var hint = new Rect(position.x, line.yMax + Gap, position.width, EditorGUIUtility.singleLineHeight);
                var color = GUI.color;
                GUI.color = new Color(1f, 0.5f, 0.4f);
                EditorGUI.LabelField(hint, " ", problem);
                GUI.color = color;
            }

            EditorGUI.EndProperty();
        }

        static GUIContent Caption(BlobchegRefSo current, Type expected)
        {
            if (current == null)
                return new GUIContent("Нет ссылки (" + (expected == null ? "сырая запись" : expected.Name) + ")");

            return new GUIContent(current.name, AssetPreview.GetMiniThumbnail(current));
        }

        static void OpenPicker(Rect field, SerializedProperty property, Type expected, BlobchegRefSo current)
        {
            // SerializedProperty живёт до конца кадра, а пикер отвечает позже — поэтому запоминаем
            // объект и путь, а свойство ищем заново в момент выбора.
            var serialized = property.serializedObject;
            var path = property.propertyPath;

            BlobchegRefPickerWindow.Open(field, expected, current, picked =>
            {
                var found = serialized.FindProperty(path);
                if (found == null)
                    return;

                found.FindPropertyRelative("asset").objectReferenceValue = picked;
                serialized.ApplyModifiedProperties();
                InternalEditorUtility.RepaintAllViews();
            });
        }

        static void DragAndDropInto(Rect field, Type expected, SerializedProperty asset)
        {
            var evt = Event.current;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)
                return;

            if (!field.Contains(evt.mousePosition))
                return;

            var dragged = DragAndDrop.objectReferences.Length == 1
                ? DragAndDrop.objectReferences[0] as BlobchegRefSo
                : null;

            var fits = BlobchegRefCatalog.Matches(dragged, expected);
            DragAndDrop.visualMode = fits ? DragAndDropVisualMode.Link : DragAndDropVisualMode.Rejected;

            if (fits && evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                asset.objectReferenceValue = dragged;
            }

            evt.Use();
        }

        string Problem(SerializedProperty property)
        {
            var asset = property.FindPropertyRelative("asset").objectReferenceValue as BlobchegRefSo;
            if (asset == null)
                return "запись не назначена";

            var expected = ExpectedRecordType(fieldInfo);
            if (!BlobchegRefCatalog.Matches(asset, expected))
                return $"чужая запись: '{asset.RecordType}' вместо '{expected?.FullName}'";

            return null;
        }

        /// <summary>Тип записи из параметра поля. <c>null</c> — сырой ref, проверять нечего.</summary>
        static Type ExpectedRecordType(FieldInfo field)
        {
            var type = field.FieldType;

            if (type.IsArray)
                type = type.GetElementType();
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                type = type.GetGenericArguments()[0];

            if (type != null && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(BlobchegRef<>))
                return type.GetGenericArguments()[0];

            return null;
        }
    }
}
