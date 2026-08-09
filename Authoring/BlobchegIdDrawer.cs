using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// The drawer for an id field. The router is held by the compiler as a type parameter, and this
    /// layer does not let the carrier of a foreign router be put into a
    /// <c>BlobchegIdRef&lt;GameRouter&gt;</c> — neither by the picker nor by dragging.
    /// </summary>
    [CustomPropertyDrawer(typeof(BlobchegIdRef<>), true)]
    public sealed class BlobchegIdDrawer : PropertyDrawer
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
            var router = ExpectedRouterName(fieldInfo);
            var current = asset.objectReferenceValue as BlobchegIdSo;

            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);

            EditorGUI.BeginProperty(position, label, property);

            var body = EditorGUI.PrefixLabel(line, label);
            var field = new Rect(body.x, body.y, body.width - PingWidth - Gap, body.height);
            var ping = new Rect(field.xMax + Gap, body.y, PingWidth, body.height);

            DragAndDropInto(field, router, asset);

            if (GUI.Button(field, Caption(current, router), EditorStyles.objectField))
                OpenPicker(field, property, router, current);

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

        static GUIContent Caption(BlobchegIdSo current, string router)
        {
            if (current == null)
                return new GUIContent("No node (" + (router ?? "router undetermined") + ")");

            return new GUIContent(current.name, AssetPreview.GetMiniThumbnail(current));
        }

        static void OpenPicker(Rect field, SerializedProperty property, string router, BlobchegIdSo current)
        {
            // A SerializedProperty lives until the end of the frame while the picker answers later —
            // that is why the object and the path are remembered and the property is looked up again at
            // the moment of the choice.
            var serialized = property.serializedObject;
            var path = property.propertyPath;
            var candidates = BlobchegIdCatalog.Candidates(router).ConvertAll(c => (ScriptableObject)c);

            BlobchegRefPickerWindow.Open(field,
                router ?? "Router nodes",
                "There are no nodes of this router in the project — or the rebuild never reached the id carriers.",
                candidates,
                current,
                asset => "row " + new BlobchegId(((BlobchegIdSo)asset).id).Index,
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

        static void DragAndDropInto(Rect field, string router, SerializedProperty asset)
        {
            var evt = Event.current;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)
                return;

            if (!field.Contains(evt.mousePosition))
                return;

            var dragged = DragAndDrop.objectReferences.Length == 1
                ? DragAndDrop.objectReferences[0] as BlobchegIdSo
                : null;

            var fits = BlobchegIdCatalog.Matches(dragged, router);
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
            var asset = property.FindPropertyRelative("asset").objectReferenceValue as BlobchegIdSo;
            if (asset == null)
                return "no node is assigned";

            var router = ExpectedRouterName(fieldInfo);
            if (!BlobchegIdCatalog.Matches(asset, router))
                return $"foreign router: '{asset.RouterName}' instead of '{router}'";

            return null;
        }

        /// <summary>The router name from the field parameter.</summary>
        static string ExpectedRouterName(FieldInfo field)
        {
            var type = field.FieldType;

            if (type.IsArray)
                type = type.GetElementType();
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                type = type.GetGenericArguments()[0];

            if (type != null && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(BlobchegIdRef<>))
                return BlobchegIdCatalog.RouterNameOf(type.GetGenericArguments()[0]);

            return null;
        }
    }
}
