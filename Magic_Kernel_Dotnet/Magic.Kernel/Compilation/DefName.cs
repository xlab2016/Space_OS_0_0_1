using System;
using System.Collections.Generic;

namespace Magic.Kernel.Compilation
{
    /// <summary>
    /// Логическое имя символа в unit: пространство имён (system:module:program) и короткое имя символа.
    /// <see cref="FullName"/> — то, что попадает в bytecode / ключи функций.
    /// </summary>
    public readonly struct DefName : IEquatable<DefName>
    {
        public DefName(string? @namespace, string? name)
        {
            Namespace = string.IsNullOrWhiteSpace(@namespace) ? "" : @namespace.Trim();
            Name = string.IsNullOrWhiteSpace(name) ? "" : name!.Trim();
            FullName = string.IsNullOrEmpty(Namespace)
                ? Name
                : string.IsNullOrEmpty(Name)
                    ? Namespace
                    : Namespace + ":" + Name;
        }

        /// <summary>Часть до последнего <c>:</c> у квалифицированной строки; для неквалифицированной — пусто.</summary>
        public static DefName ParseQualified(string qualified)
        {
            if (string.IsNullOrWhiteSpace(qualified))
                return new DefName("", "");
            var q = qualified.Trim();
            var idx = q.LastIndexOf(':');
            if (idx < 0)
                return new DefName("", q);
            return new DefName(q.Substring(0, idx).Trim(), q.Substring(idx + 1).Trim());
        }

        /// <summary>system:module:program без завершающего двоеточия.</summary>
        public static string GetNamespace(ExecutableUnit unit) => JoinUnitParts(unit?.System, unit?.Module, unit?.Name);

        /// <summary>system:module:program без завершающего двоеточия.</summary>
        public static string GetNamespace(ProgramStructure programStructure) =>
            programStructure == null
                ? ""
                : JoinUnitParts(programStructure.System, programStructure.Module, programStructure.ProgramName);

        /// <summary>Префикс вида <c>sys:mod:prog:</c> из <see cref="GetNamespace(ProgramStructure)"/>.</summary>
        public static string? GetDefaultTypeNamespacePrefix(ProgramStructure programStructure)
        {
            var ns = GetNamespace(programStructure);
            return string.IsNullOrEmpty(ns) ? null : ns + ":";
        }

        /// <summary>
        /// Убирает завершающее <c>:</c> у legacy-префикса из компилятора типов.</summary>
        public static string NamespaceFromDefaultTypePrefix(string? prefixEndsWithColon)
        {
            if (string.IsNullOrWhiteSpace(prefixEndsWithColon))
                return "";
            var p = prefixEndsWithColon.Trim();
            return p.EndsWith(":", StringComparison.Ordinal) ? p[..^1].Trim() : p;
        }

        /// <summary>
        /// Полный адрес символа в unit: если <paramref name="symbolName"/> уже под этим namespace, не дублирует.
        /// </summary>
        public static string QualifyInUnit(ExecutableUnit unit, string symbolName)
        {
            if (string.IsNullOrWhiteSpace(symbolName))
                return symbolName;
            var name = symbolName.Trim();
            var ns = GetNamespace(unit);
            if (string.IsNullOrEmpty(ns))
                return name;
            if (name.Equals(ns, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(ns + ":", StringComparison.OrdinalIgnoreCase))
                return name;
            return new DefName(ns, name).FullName;
        }

        public string Namespace { get; }
        public string Name { get; }
        public string FullName { get; }

        public string NamespaceWithTrailingColon => Namespace.Length == 0 ? "" : Namespace + ":";

        public bool Equals(DefName other) =>
            string.Equals(Namespace, other.Namespace, StringComparison.Ordinal) &&
            string.Equals(Name, other.Name, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is DefName other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Namespace, Name);

        public override string ToString() => FullName;

        private static string JoinUnitParts(params string?[] parts)
        {
            var list = new List<string>();
            foreach (var p in parts)
            {
                if (string.IsNullOrWhiteSpace(p))
                    continue;
                var t = p.Trim();
                if (t.Length > 0)
                    list.Add(t);
            }

            return list.Count == 0 ? "" : string.Join(":", list);
        }
    }
}
