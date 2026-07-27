using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Драйвер поля-обёртки. Тип поля держит компилятор, а этот слой не даёт положить в
    /// <c>BlobchegRef&lt;GunData&gt;</c> ассет чужой записи — ни пикером, ни перетаскиванием.
    /// Пустое поле и чужая запись подсвечиваются: молча нулевой оффсет не поедет.
    /// </summary>
    [CustomPropertyDrawer(typeof(BlobchegRef<>), true)]
    [CustomPropertyDrawer(typeof(BlobchegRawRef), true)]
    public sealed class BlobchegRefDrawer : PropertyDrawer
    {
        const float Gap = 2f;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var line = EditorGUIUtility.singleLineHeight;
            return Problem(property, out _) == null ? line : line * 2 + Gap;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var asset = property.FindPropertyRelative("asset");
            var expected = ExpectedRecordType(fieldInfo);

            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);

            EditorGUI.BeginProperty(position, label, property);
            EditorGUI.BeginChangeCheck();
            var picked = EditorGUI.ObjectField(line, label, asset.objectReferenceValue, typeof(BlobchegRefSo), false);
            if (EditorGUI.EndChangeCheck())
                asset.objectReferenceValue = Accept(picked as BlobchegRefSo, expected);

            var problem = Problem(property, out _);
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

        static BlobchegRefSo Accept(BlobchegRefSo picked, Type expected)
        {
            if (picked == null || expected == null)
                return picked;

            if (string.Equals(picked.recordType, expected.FullName, StringComparison.Ordinal))
                return picked;

            Debug.LogError(
                $"Blobcheg: ассет '{picked.name}' держит запись '{picked.recordType}', " +
                $"а поле ждёт '{expected.FullName}' — не назначено");
            return null;
        }

        string Problem(SerializedProperty property, out BlobchegRefSo asset)
        {
            asset = property.FindPropertyRelative("asset").objectReferenceValue as BlobchegRefSo;
            if (asset == null)
                return "запись не назначена";

            var expected = ExpectedRecordType(fieldInfo);
            if (expected != null && !string.Equals(asset.recordType, expected.FullName, StringComparison.Ordinal))
                return $"чужая запись: '{asset.recordType}' вместо '{expected.FullName}'";

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
