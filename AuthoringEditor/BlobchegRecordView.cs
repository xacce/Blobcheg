using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg.Authoring
{
    public sealed class BlobchegRecordNode
    {
        public string Name;
        public string TypeName;
        public string Value;
        public readonly List<BlobchegRecordNode> Children = new List<BlobchegRecordNode>();

        public bool IsLeaf => Children.Count == 0;
    }

    // Offsets via UnsafeUtility, not Marshal.OffsetOf (bool is 1 byte here); reads are bounds-checked.
    public static unsafe class BlobchegRecordView
    {
        public const int MaxElements = 64; // shown before the tail folds into one line

        public const int MaxDepth = 8; // a record is data, not a graph: deeper means a broken type

        public static BlobchegRecordNode Of(byte* file, int length, uint offset, Type type, string name)
        {
            var root = new BlobchegRecordNode { Name = name, TypeName = NameOf(type) };

            if (file == null || type == null)
            {
                root.Value = "нечего разбирать: нет ни буфера, ни типа";
                return root;
            }

            var size = SizeOf(type);
            if (offset + (long)size > length)
            {
                root.Value = $"запись на {offset} размером {size} не влезает в буфер {length} Б";
                return root;
            }

            Walk(root, file, length, file + offset, type, 0); // whole buffer: record arrays may lie anywhere
            return root;
        }

        public static string ToText(BlobchegRecordNode node)
        {
            var text = new StringBuilder();
            Print(text, node, 0);
            return text.ToString();
        }

        public static string Dump(byte* file, int length, uint offset, uint size, int columns = 16)
        {
            if (file == null || offset >= (uint)length)
                return string.Empty;

            var last = Math.Min((long)offset + size, length);
            var text = new StringBuilder();

            for (var at = (long)offset; at < last; at += columns)
            {
                text.Append(at.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");

                var row = Math.Min(columns, last - at);
                for (var i = 0; i < columns; i++)
                {
                    text.Append(i < row ? file[at + i].ToString("X2", CultureInfo.InvariantCulture) : "  ").Append(' ');
                }

                text.Append(' ');
                for (var i = 0; i < row; i++)
                {
                    var b = file[at + i];
                    text.Append(b >= 32 && b < 127 ? (char)b : '.');
                }

                text.Append('\n');
            }

            return text.ToString();
        }

        static void Walk(BlobchegRecordNode node, byte* file, int length, byte* at, Type type, int depth)
        {
            if (depth > MaxDepth)
            {
                node.Value = "…";
                return;
            }

            if (!Inside(file, length, at, SizeOf(type)))
            {
                node.Value = "— за границей буфера";
                return;
            }

            if (TryLeaf(at, type, out var value))
            {
                node.Value = value;
                return;
            }

            if (IsArray(type))
            {
                Elements(node, file, length, at, type, depth);
                return;
            }

            foreach (var field in FieldsOf(type))
            {
                var child = new BlobchegRecordNode { Name = field.Name, TypeName = NameOf(field.FieldType) };
                node.Children.Add(child);
                Walk(child, file, length, at + UnsafeUtility.GetFieldOffset(field), field.FieldType, depth + 1);
            }

            // A struct without a single field is not an error — it is a tag, and it has nothing to show.
            if (node.Children.Count == 0)
                node.Value = "{}";
        }

        static void Elements(BlobchegRecordNode node, byte* file, int length, byte* at, Type type, int depth)
        {
            var element = type.GetGenericArguments()[0];
            var size = SizeOf(element);
            var offset = *(int*)at;
            var count = *(int*)(at + 4);

            node.TypeName = $"BlobchegArray<{NameOf(element)}>";

            if (count < 0)
            {
                node.Value = $"длина {count} — это не массив";
                return;
            }

            if (count == 0)
            {
                node.Value = "[0]";
                return;
            }

            var first = at + offset; // self-relative: counted from the field, not the record start
            if (!Inside(file, length, first, (long)size * count))
            {
                node.Value = $"[{count}] за границей буфера (смещение {offset})";
                return;
            }

            node.Value = $"[{count}]";

            var shown = Math.Min(count, MaxElements);
            for (var i = 0; i < shown; i++)
            {
                var child = new BlobchegRecordNode { Name = "[" + i + "]", TypeName = NameOf(element) };
                node.Children.Add(child);
                Walk(child, file, length, first + (long)size * i, element, depth + 1);
            }

            if (shown < count)
                node.Children.Add(new BlobchegRecordNode { Name = "…", Value = $"ещё {count - shown}" });
        }

        static bool TryLeaf(byte* at, Type type, out string value)
        {
            if (type.IsEnum)
            {
                value = EnumText(at, type);
                return true;
            }

            if (type.IsPrimitive)
            {
                value = PrimitiveText(at, type);
                return true;
            }

            if (type.Name == "Hash128")
            {
                value = $"{(*(uint*)at):x8}{(*(uint*)(at + 4)):x8}{(*(uint*)(at + 8)):x8}{(*(uint*)(at + 12)):x8}";
                return true;
            }

            if (IsFixedString(type))
            {
                var used = *(ushort*)at;
                var room = SizeOf(type) - 2;
                value = "\"" + Encoding.UTF8.GetString(at + 2, Math.Min(used, room)) + "\"";
                return true;
            }

            if (type.Namespace == "Unity.Mathematics")
                return TryInline(at, type, out value);

            value = null;
            return false;
        }

        static bool TryInline(byte* at, Type type, out string value)
        {
            value = null;

            var fields = FieldsOf(type);
            if (fields.Length == 0)
                return false;

            var parts = new string[fields.Length];
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                var kind = field.FieldType;
                var address = at + UnsafeUtility.GetFieldOffset(field);

                if (kind.IsPrimitive)
                    parts[i] = PrimitiveText(address, kind);
                else if (kind.Namespace == "Unity.Mathematics" && TryInline(address, kind, out var nested))
                    parts[i] = nested;
                else
                    return false;
            }

            value = fields.Length == 1 ? parts[0] : "(" + string.Join(", ", parts) + ")"; // quaternion: no double brackets
            return true;
        }

        static string EnumText(byte* at, Type type)
        {
            var underlying = Enum.GetUnderlyingType(type);
            object raw;

            switch (Type.GetTypeCode(underlying))
            {
                case TypeCode.SByte: raw = *(sbyte*)at; break;
                case TypeCode.Byte: raw = *at; break;
                case TypeCode.Int16: raw = *(short*)at; break;
                case TypeCode.UInt16: raw = *(ushort*)at; break;
                case TypeCode.Int32: raw = *(int*)at; break;
                case TypeCode.UInt32: raw = *(uint*)at; break;
                case TypeCode.Int64: raw = *(long*)at; break;
                case TypeCode.UInt64: raw = *(ulong*)at; break;
                default: return "?";
            }

            // Unnamed values print as numbers, so a field baked before a member was added stays visible.
            return Enum.ToObject(type, raw).ToString();
        }

        static string PrimitiveText(byte* at, Type type)
        {
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Boolean: return *at != 0 ? "true" : "false";
                case TypeCode.Char: return "'" + (char)*(ushort*)at + "'";
                case TypeCode.SByte: return (*(sbyte*)at).ToString(CultureInfo.InvariantCulture);
                case TypeCode.Byte: return (*at).ToString(CultureInfo.InvariantCulture);
                case TypeCode.Int16: return (*(short*)at).ToString(CultureInfo.InvariantCulture);
                case TypeCode.UInt16: return (*(ushort*)at).ToString(CultureInfo.InvariantCulture);
                case TypeCode.Int32: return (*(int*)at).ToString(CultureInfo.InvariantCulture);
                case TypeCode.UInt32: return (*(uint*)at).ToString(CultureInfo.InvariantCulture);
                case TypeCode.Int64: return (*(long*)at).ToString(CultureInfo.InvariantCulture);
                case TypeCode.UInt64: return (*(ulong*)at).ToString(CultureInfo.InvariantCulture);
                case TypeCode.Single: return (*(float*)at).ToString("0.######", CultureInfo.InvariantCulture);
                case TypeCode.Double: return (*(double*)at).ToString("0.############", CultureInfo.InvariantCulture);
                default: return "?";
            }
        }

        static bool IsArray(Type type)
            => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(BlobchegArray<>);

        static bool IsFixedString(Type type)
            => type.Namespace == "Unity.Collections"
               && type.Name.StartsWith("FixedString", StringComparison.Ordinal)
               && type.Name.EndsWith("Bytes", StringComparison.Ordinal);

        static bool Inside(byte* file, int length, byte* at, long size)
            => at >= file && size >= 0 && at + size <= file + length;

        // Private fields included: the layout is sequential and they take bytes too.
        static FieldInfo[] FieldsOf(Type type)
        {
            if (s_Fields.TryGetValue(type, out var fields))
                return fields;

            fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_Fields.Add(type, fields);
            return fields;
        }

        static int SizeOf(Type type)
        {
            if (s_Sizes.TryGetValue(type, out var size))
                return size;

            size = UnsafeUtility.SizeOf(type);
            s_Sizes.Add(type, size);
            return size;
        }

        static string NameOf(Type type)
        {
            if (type == null)
                return "?";

            if (!type.IsGenericType)
                return type.Name;

            var arguments = type.GetGenericArguments();
            var names = new string[arguments.Length];
            for (var i = 0; i < arguments.Length; i++)
                names[i] = NameOf(arguments[i]);

            var bare = type.Name;
            var tick = bare.IndexOf('`');
            if (tick >= 0)
                bare = bare.Substring(0, tick);

            return bare + "<" + string.Join(", ", names) + ">";
        }

        static void Print(StringBuilder text, BlobchegRecordNode node, int depth)
        {
            text.Append(' ', depth * 2).Append(node.Name);

            if (!string.IsNullOrEmpty(node.TypeName))
                text.Append(" : ").Append(node.TypeName);

            if (!string.IsNullOrEmpty(node.Value))
                text.Append(" = ").Append(node.Value);

            text.Append('\n');

            for (var i = 0; i < node.Children.Count; i++)
                Print(text, node.Children[i], depth + 1);
        }

        static readonly Dictionary<Type, FieldInfo[]> s_Fields = new Dictionary<Type, FieldInfo[]>();
        static readonly Dictionary<Type, int> s_Sizes = new Dictionary<Type, int>();
    }
}
