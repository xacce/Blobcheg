using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Драйвер поля-id. Роутер держит компилятор параметром типа, а этот слой не даёт положить в
    /// <c>BlobchegIdRef&lt;GameRouter&gt;</c> носитель чужого роутера — ни пикером, ни перетаскиванием.
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
                return new GUIContent("Нет ноды (" + (router ?? "роутер не определён") + ")");

            return new GUIContent(current.name, AssetPreview.GetMiniThumbnail(current));
        }

        static void OpenPicker(Rect field, SerializedProperty property, string router, BlobchegIdSo current)
        {
            // SerializedProperty живёт до конца кадра, а пикер отвечает позже — поэтому запоминаем
            // объект и путь, а свойство ищем заново в момент выбора.
            var serialized = property.serializedObject;
            var path = property.propertyPath;
            var candidates = BlobchegIdCatalog.Candidates(router).ConvertAll(c => (ScriptableObject)c);

            BlobchegRefPickerWindow.Open(field,
                router ?? "Ноды роутера",
                "Нод этого роутера в проекте нет — или пересборка не дошла до носителей id.",
                candidates,
                current,
                asset => "id " + ((BlobchegIdSo)asset).id,
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
                return "нода не назначена";

            var router = ExpectedRouterName(fieldInfo);
            if (!BlobchegIdCatalog.Matches(asset, router))
                return $"чужой роутер: '{asset.RouterName}' вместо '{router}'";

            return null;
        }

        /// <summary>Имя роутера из параметра поля.</summary>
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
