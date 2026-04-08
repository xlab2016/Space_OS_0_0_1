using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Magic.Kernel.Core;

namespace Magic.Kernel.Types
{
    public sealed class DefField
    {
        public string Name { get; }
        public string Type { get; private set; }
        public object? Value { get; private set; }

        public DefField(string name, object? value)
        {
            Name = name;
            Value = value;
            Type = value?.GetType().Name ?? "null";
        }

        public void SetValue(object? value)
        {
            Value = value;
            Type = value?.GetType().Name ?? "null";
        }

        public override string ToString() => ToString(0);

        internal string ToString(int depth) =>
            $"{Name}={DefValueFormatter.Format(Value, depth)}";
    }

    /// <summary>
    /// Runtime object instance created by Def for custom AGI types.
    /// Stores object type and dynamic fields with inferred runtime field types.
    /// </summary>
    public sealed class DefObject
    {
        /// <summary>Runtime schema / identity for this instance (short <see cref="DefType.Name"/>, <see cref="DefType.Namespace"/>, <see cref="DefType.FullName"/>).</summary>
        public DefType Type { get; }

        /// <summary>Qualified type name (<see cref="DefType.FullName"/>).</summary>
        public string TypeName => Type.FullName;

        public Dictionary<string, DefField> Fields { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public DefObject(DefType type)
        {
            Type = type ?? throw new ArgumentNullException(nameof(type));
        }

        /// <summary>JSON object: keys are <see cref="DefTypeField.Name"/> from <see cref="Type"/> и цепочки <see cref="DefClass.Inheritances"/> (при <paramref name="typeCatalog"/>); nested <see cref="DefObject"/> — те же правила.</summary>
        public Dictionary<string, object?> ToJsonMapBySchemaFieldNames(bool includeTypeDiscriminator, IReadOnlyList<DefType>? typeCatalog = null)
        {
            var dict = new Dictionary<string, object?>();
            if (includeTypeDiscriminator)
                dict["$type"] = string.IsNullOrEmpty(Type.Name) ? Type.FullName : Type.Name;

            var keys = CollectSchemaFieldKeysForLayout(Type, typeCatalog);
            if (keys.Count > 0)
            {
                foreach (var key in keys)
                {
                    TryResolveFieldValue(key, out var val);
                    dict[key] = AgiValueToJsonTree(val, typeCatalog);
                }

                return dict;
            }

            foreach (var kv in Fields.OrderBy(x => x.Value.Name, StringComparer.OrdinalIgnoreCase))
                dict[kv.Value.Name] = AgiValueToJsonTree(kv.Value.Value, typeCatalog);

            return dict;
        }

        /// <summary>Compact JSON (no <c>$type</c>); для вывода в <c>format</c> см. <see cref="ToConstructorStyleString"/>.</summary>
        public string ToJsonStringBySchemaFieldNames(IReadOnlyList<DefType>? typeCatalog = null) =>
            JsonSerializer.Serialize(ToJsonMapBySchemaFieldNames(includeTypeDiscriminator: false, typeCatalog));

        /// <summary>
        /// Литерал в стиле AGI-конструктора для подстановки в <c>format</c>. Одна строка для простых случаев;
        /// несколько полей или вложенные <see cref="DefObject"/> — многострочно с табами, как в исходнике .agi.
        /// </summary>
        /// <param name="depth">Глубина вложенности (лимит развёртки).</param>
        /// <param name="multilineFieldIndent">Число табов перед именами полей при многострочном выводе (корень: 1).</param>
        /// <param name="typeCatalog">Каталог типов исполняемого юнита — чтобы подтянуть поля из <see cref="DefClass.Inheritances"/>.</param>
        public string ToConstructorStyleString(int depth = 0, int multilineFieldIndent = 1, IReadOnlyList<DefType>? typeCatalog = null)
        {
            const int maxDepth = 10;
            if (depth > maxDepth)
                return TypeDisplayName() + "(…)";

            if (!NeedsMultilineLayout(typeCatalog))
                return ToConstructorStyleStringSingleLine(depth, typeCatalog);

            return ToConstructorStyleStringMultiline(depth, multilineFieldIndent, typeCatalog);
        }

        /// <summary>Разрезает ссылку на базовый тип из <see cref="DefClass.Inheritances"/> по каталогу (полное имя, короткое, или <c>namespace</c> владельца + короткое).</summary>
        internal static DefType? TryResolveTypeInCatalog(IReadOnlyList<DefType>? catalog, string? rawRef, DefType anchorType)
        {
            if (catalog == null || catalog.Count == 0 || string.IsNullOrWhiteSpace(rawRef))
                return null;
            var name = rawRef.Trim();
            foreach (var t in catalog)
            {
                if (string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase))
                    return t;
            }

            var ns = (anchorType.Namespace ?? "").Trim();
            if (ns.Length > 0)
            {
                var fqCandidate = ns + ":" + name;
                foreach (var t in catalog)
                {
                    if (string.Equals(t.FullName, fqCandidate, StringComparison.OrdinalIgnoreCase))
                        return t;
                }
            }

            DefType? fallback = null;
            foreach (var t in catalog)
            {
                if (!string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (ns.Length > 0 && string.Equals((t.Namespace ?? "").Trim(), ns, StringComparison.OrdinalIgnoreCase))
                    return t;
                fallback ??= t;
            }

            return fallback;
        }

        /// <summary>Сначала поля объявленные на <paramref name="type"/>, затем поля баз (порядок <see cref="DefClass.Inheritances"/>, рекурсивно), без дубликатов имён.</summary>
        private static List<string> CollectSchemaFieldKeysForLayout(DefType type, IReadOnlyList<DefType>? catalog)
        {
            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AppendDeclared(DefType t)
            {
                if (t.Fields == null)
                    return;
                foreach (var f in t.Fields)
                {
                    var k = (f.Name ?? "").Trim();
                    if (k.Length == 0 || !seen.Add(k))
                        continue;
                    keys.Add(k);
                }
            }

            void AppendInheritedTree(DefType t)
            {
                if (t.Fields != null)
                {
                    foreach (var f in t.Fields)
                    {
                        var k = (f.Name ?? "").Trim();
                        if (k.Length == 0 || seen.Contains(k))
                            continue;
                        seen.Add(k);
                        keys.Add(k);
                    }
                }

                if (catalog == null || t is not DefClass dc)
                    return;
                foreach (var bn in dc.Inheritances)
                {
                    var bt = TryResolveTypeInCatalog(catalog, bn, t);
                    if (bt != null)
                        AppendInheritedTree(bt);
                }
            }

            AppendDeclared(type);
            if (catalog != null && type is DefClass rootDc)
            {
                foreach (var bn in rootDc.Inheritances)
                {
                    var bt = TryResolveTypeInCatalog(catalog, bn, type);
                    if (bt != null)
                        AppendInheritedTree(bt);
                }
            }

            return keys;
        }

        private List<(string Key, object? Value)> GetConstructorParts(IReadOnlyList<DefType>? typeCatalog)
        {
            var keys = CollectSchemaFieldKeysForLayout(Type, typeCatalog);
            if (keys.Count > 0)
            {
                var partList = new List<(string, object?)>();
                foreach (var key in keys)
                {
                    TryResolveFieldValue(key, out var val);
                    partList.Add((key, val));
                }

                return partList;
            }

            var dynamicParts = new List<(string, object?)>();
            foreach (var kv in Fields.OrderBy(x => x.Value.Name, StringComparer.OrdinalIgnoreCase))
                dynamicParts.Add((kv.Value.Name, kv.Value.Value));

            return dynamicParts;
        }

        private bool NeedsMultilineLayout(IReadOnlyList<DefType>? typeCatalog)
        {
            var parts = GetConstructorParts(typeCatalog);
            if (parts.Count == 0)
                return false;
            if (parts.Count == 1)
                return ValueNeedsMultiline(parts[0].Value, typeCatalog);

            // Несколько полей: одна строка, пока все значения «плоские» (как Point(X: 1, Y: 2)).
            var anyStructured = parts.Any(p => p.Value is DefObject or DefList);
            return anyStructured;
        }

        private static bool ValueNeedsMultiline(object? v, IReadOnlyList<DefType>? typeCatalog)
        {
            if (v is DefObject d)
                return d.NeedsMultilineLayout(typeCatalog);
            if (v is DefList list)
                return list.Items.Any(x => ValueNeedsMultiline(x, typeCatalog));
            return false;
        }

        private string ToConstructorStyleStringSingleLine(int depth, IReadOnlyList<DefType>? typeCatalog)
        {
            const int maxDepth = 10;
            if (depth > maxDepth)
                return TypeDisplayName() + "(…)";

            var typeLabel = TypeDisplayName();
            var partList = GetConstructorParts(typeCatalog);
            if (partList.Count == 0)
                return $"{typeLabel}()";

            var segments = partList.Select(p => $"{p.Key}: {FormatConstructorValue(p.Value, depth + 1, typeCatalog)}");
            return $"{typeLabel}({string.Join(", ", segments)})";
        }

        private string ToConstructorStyleStringMultiline(int depth, int fieldIndent, IReadOnlyList<DefType>? typeCatalog)
        {
            const int maxDepth = 10;
            if (depth > maxDepth)
                return TypeDisplayName() + "(…)";

            var typeLabel = TypeDisplayName();
            var partList = GetConstructorParts(typeCatalog);
            var closeIndent = Math.Max(0, fieldIndent - 1);
            var sb = new StringBuilder();
            sb.Append(typeLabel).Append('(').Append('\n');
            for (var i = 0; i < partList.Count; i++)
            {
                var (key, val) = partList[i];
                sb.Append('\t', fieldIndent);
                sb.Append(key).Append(": ");
                sb.Append(FormatConstructorValueForMultiline(val, depth + 1, fieldIndent + 1, typeCatalog));
                if (i < partList.Count - 1)
                    sb.Append(',');
                sb.Append('\n');
            }

            sb.Append('\t', closeIndent);
            sb.Append(')');
            return sb.ToString();
        }

        private string FormatConstructorValueForMultiline(object? v, int depth, int childFieldIndent, IReadOnlyList<DefType>? typeCatalog)
        {
            const int maxDepth = 10;
            if (depth > maxDepth)
                return "…";
            if (v is DefObject d)
            {
                if (d.NeedsMultilineLayout(typeCatalog))
                    return d.ToConstructorStyleStringMultiline(depth, childFieldIndent, typeCatalog);
                return d.ToConstructorStyleStringSingleLine(depth, typeCatalog);
            }

            return FormatConstructorValue(v, depth, typeCatalog);
        }

        private string TypeDisplayName()
        {
            if (!string.IsNullOrEmpty(Type.Name))
                return Type.Name.Trim();
            var fn = Type.FullName ?? "";
            var last = fn.LastIndexOf(':');
            return (last >= 0 ? fn[(last + 1)..] : fn).Trim();
        }

        private bool TryResolveFieldValue(string key, out object? val)
        {
            if (TryGetField(key, out val))
                return true;
            foreach (var kv in Fields)
            {
                if (!string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                    continue;
                val = kv.Value.Value;
                return true;
            }

            val = null;
            return false;
        }

        private static string FormatConstructorValue(object? v, int depth, IReadOnlyList<DefType>? typeCatalog)
        {
            const int maxDepth = 10;
            if (depth > maxDepth)
                return "…";
            if (v == null)
                return "null";

            if (v is DefObject d)
                return d.ToConstructorStyleStringSingleLine(depth, typeCatalog);

            if (v is DefList list)
            {
                var elem = string.IsNullOrWhiteSpace(list.Name) ? "" : list.Name.Trim();
                if (list.Items.Count == 0 && elem.Length > 0)
                    return $"{elem}[]";
                var inner = string.Join(", ", list.Items.Select(x => FormatConstructorValue(x, depth + 1, typeCatalog)));
                return elem.Length > 0 ? $"{elem}[{inner}]" : $"[{inner}]";
            }

            if (v is string s)
                return "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

            if (v is char c)
                return "'" + c + "'";

            if (v is bool b)
                return b ? "true" : "false";

            if (v is long or int or short or byte)
                return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";

            if (v is decimal or double or float)
                return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";

            return DefValueFormatter.Format(v, depth);
        }

        private static object? AgiValueToJsonTree(object? v, IReadOnlyList<DefType>? typeCatalog)
        {
            if (v == null)
                return null;
            if (v is DefObject d)
                return d.ToJsonMapBySchemaFieldNames(includeTypeDiscriminator: false, typeCatalog);
            if (v is DefList list)
                return list.Items.Select(x => AgiValueToJsonTree(x, typeCatalog)).ToList();
            if (v is string or bool or long or int or short or byte or decimal or double or float)
                return v;
            return v.ToString();
        }

        public void SetField(string fieldName, object? value)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
                return;

            if (Fields.TryGetValue(fieldName, out var field))
                field.SetValue(value);
            else
                Fields[fieldName] = new DefField(fieldName, value);
        }

        public bool TryGetField(string fieldName, out object? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(fieldName))
                return false;

            if (!Fields.TryGetValue(fieldName, out var field))
                return false;

            value = field.Value;
            return true;
        }

        public override string ToString() => ToString(0);

        internal string ToString(int depth)
        {
            const int maxDepth = 4;
            var label = Type.FullName;
            if (depth >= maxDepth)
                return $"{label}{{…}}";

            if (Fields.Count == 0)
                return $"{label}{{}}";

            const int maxFields = 16;
            var parts = new List<string>(Math.Min(Fields.Count, maxFields));
            foreach (var kv in Fields.Take(maxFields))
                parts.Add(kv.Value.ToString(depth + 1));

            var tail = Fields.Count > maxFields ? $", …+{Fields.Count - maxFields}" : "";
            return $"{label}{{{string.Join(", ", parts)}{tail}}}";
        }
    }

    /// <summary>Stable, debugger-friendly <c>ToString</c> for def field values (depth-limited nesting).</summary>
    internal static class DefValueFormatter
    {
        public static string Format(object? value, int depth = 0)
        {
            const int maxDepth = 4;
            if (depth > maxDepth)
                return "…";

            if (value == null)
                return "null";

            if (value is DefObject d)
                return d.ToString(depth);

            if (value is DefList list)
                return list.ToString();

            if (value is string s)
            {
                if (s.Length <= 64)
                    return "\"" + s.Replace("\"", "\\\"") + "\"";
                return "\"" + s.AsSpan(0, 61).ToString().Replace("\"", "\\\"") + "…\"";
            }

            if (value is char c)
                return "'" + c + "'";

            if (value is bool b)
                return b ? "true" : "false";

            if (value is IEnumerable seq and not string)
            {
                var items = new List<string>();
                var n = 0;
                foreach (var x in seq)
                {
                    if (n++ >= 8)
                    {
                        items.Add("…");
                        break;
                    }

                    items.Add(Format(x, depth + 1));
                }

                return "[" + string.Join(", ", items) + "]";
            }

            var t = value.GetType();
            if (t.IsPrimitive || value is decimal)
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";

            var asStr = value.ToString();
            if (!string.IsNullOrEmpty(asStr) && asStr != t.FullName)
                return asStr;

            return t.Name;
        }
    }
}
