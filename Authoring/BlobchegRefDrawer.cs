using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// The drawer for a wrapper field. The field type is held by the compiler, and this layer does not
    /// let the asset of a foreign record be put into a <c>BlobchegRef&lt;GunData&gt;</c> — neither by
    /// the picker nor by dragging. An empty field and a foreign record are highlighted: a zero offset
    /// will not travel silently.
    ///
    /// The field is drawn by us and not by <c>EditorGUI.ObjectField</c>: the native picker filters only
    /// by asset type, and we have one asset type for the whole system — the records of every domain
    /// would lie in the list.
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
                return new GUIContent("No reference (" + (expected == null ? "raw record" : expected.Name) + ")");

            return new GUIContent(current.name, AssetPreview.GetMiniThumbnail(current));
        }

        static void OpenPicker(Rect field, SerializedProperty property, Type expected, BlobchegRefSo current)
        {
            // A SerializedProperty lives until the end of the frame while the picker answers later —
            // that is why the object and the path are remembered and the property is looked up again at
            // the moment of the choice.
            var serialized = property.serializedObject;
            var path = property.propertyPath;

            var candidates = BlobchegRefCatalog.Candidates(expected).ConvertAll(r => (ScriptableObject)r);

            BlobchegRefPickerWindow.Open(field,
                expected == null ? "Raw records" : expected.Name,
                "There are no records of this type in the project. The node that writes it has not been " +
                "created yet — or the rebuild never reached the ref assets.",
                candidates,
                current,
                asset => "offset " + ((BlobchegRefSo)asset).offset,
                picked =>
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
                return "no record is assigned";

            var expected = ExpectedRecordType(fieldInfo);
            if (!BlobchegRefCatalog.Matches(asset, expected))
                return $"foreign record: '{asset.RecordType}' instead of '{expected?.FullName}'";

            return null;
        }

        /// <summary>The record type from the field parameter. <c>null</c> means a raw ref, nothing to check.</summary>
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
