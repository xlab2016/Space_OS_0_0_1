using Magic.Kernel.Compilation;
using Magic.Kernel.Devices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Magic.Kernel.Core
{
    public sealed class DefTypeField
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "any";
        public string Visibility { get; set; } = "public";

        public override string ToString()
        {
            var vis = string.IsNullOrWhiteSpace(Visibility) || string.Equals(Visibility, "public", StringComparison.OrdinalIgnoreCase)
                ? ""
                : Visibility.Trim() + " ";
            return $"{vis}{Name}:{Type}";
        }
    }

    /// <summary>Declared type method: short <see cref="Name"/>, mangled procedure key in <see cref="FullName"/> (e.g. <c>Type_Method_1</c> in unit namespace).</summary>
    public sealed class DefTypeMethod
    {
        public string Name { get; set; } = "";
        public string FullName { get; set; } = "";
        public string ReturnType { get; set; } = "void";
        public string Visibility { get; set; } = "public";

        /// <summary>Полное имя типа первого пользовательского параметра (для выбора перегрузки <c>callobj</c>).</summary>
        public string? FirstParameterTypeFq { get; set; }

        public override string ToString()
        {
            var vis = string.IsNullOrWhiteSpace(Visibility) || string.Equals(Visibility, "public", StringComparison.OrdinalIgnoreCase)
                ? ""
                : Visibility.Trim() + " ";
            return $"{vis}{Name} -> {ReturnType} ({FullName})";
        }
    }

    public class DefType : IDefType
    {
        public long? Index { get; set; }

        /// <summary>Logical namespace for the type, e.g. <c>system:module:eu</c> (same segment rules as <see cref="DefName"/>).</summary>
        public string Namespace { get; set; } = "";

        /// <summary>Short type name within <see cref="Namespace"/>.</summary>
        public string Name { get; set; } = "";

        /// <summary><see cref="Namespace"/> and <see cref="Name"/> joined with <c>:</c> when namespace is non-empty; otherwise <see cref="Name"/>.</summary>
        public string FullName
        {
            get
            {
                var ns = (Namespace ?? "").Trim();
                var nm = (Name ?? "").Trim();
                if (ns.Length == 0)
                    return nm;
                return nm.Length == 0 ? ns : ns + ":" + nm;
            }
        }

        public StructurePosition? Position { get; set; }
        public List<IDefType> Generalizations { get; set; } = new List<IDefType>();
        public List<DefTypeField> Fields { get; set; } = new List<DefTypeField>();
        public List<DefTypeMethod> Methods { get; set; } = new List<DefTypeMethod>();
        public ExecutionCallContext? ExecutionCallContext { get; set; }

        /// <summary>
        /// If <see cref="Namespace"/> is empty and <see cref="Name"/> contains <c>:</c>, splits into namespace + short name (legacy single-string storage).
        /// </summary>
        public void NormalizeLegacyQualifiedName()
        {
            if (!string.IsNullOrEmpty((Namespace ?? "").Trim()))
                return;
            var n = (Name ?? "").Trim();
            if (n.IndexOf(':') < 0)
                return;
            var p = DefName.ParseQualified(n);
            Namespace = p.Namespace;
            Name = p.Name;
        }

        /// <summary>Builds a <see cref="DefType"/> or <see cref="DefClass"/> from a <c>def</c> bytecode name plus optional unit namespace.</summary>
        public static DefType FromDefBytecode(string rawTypeName, string? unitNamespace, bool asClass, IEnumerable<string>? classBaseNames)
        {
            var parsed = DefName.ParseQualified((rawTypeName ?? "").Trim());
            var ns = string.IsNullOrEmpty(parsed.Namespace)
                ? (unitNamespace ?? "").Trim()
                : parsed.Namespace.Trim();
            var shortName = string.IsNullOrEmpty(parsed.Name)
                ? (rawTypeName ?? "").Trim()
                : parsed.Name.Trim();

            if (asClass)
                return new DefClass(shortName, classBaseNames) { Namespace = ns };

            return new DefType { Namespace = ns, Name = shortName };
        }

        public virtual async Task<object?> Await()
        {
            throw new NotImplementedException();
        }

        public virtual async Task<object?> AwaitObjAsync()
        {
            throw new NotImplementedException();
        }

        public virtual async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            throw new NotImplementedException();
        }

        public virtual async Task<(bool IsEnd, object? Delta, object? Aggregate)> StreamWaitAsync(string streamWaitType)
        {
            throw new NotImplementedException();
        }

        /// <summary>Short label for another <see cref="IDefType"/> inside <see cref="ToString"/> (avoids deep recursion).</summary>
        protected static string DefTypeRefToString(IDefType? t)
        {
            if (t == null) return "null";
            var n = t.Name?.Trim();
            if (!string.IsNullOrEmpty(n))
                return n;
            return t.GetType().Name;
        }

        public override string ToString()
        {
            var kind = GetType().Name;
            var fq = FullName;
            var label = string.IsNullOrWhiteSpace(fq) ? kind : fq.Trim();
            var idx = Index.HasValue ? $" index={Index}" : "";

            string fieldsPart = "";
            if (Fields is { Count: > 0 })
            {
                const int maxFields = 12;
                var shown = Fields.Take(maxFields).Select(f => f.ToString());
                var tail = Fields.Count > maxFields ? $", …+{Fields.Count - maxFields}" : "";
                fieldsPart = $" fields=[{string.Join(", ", shown)}{tail}]";
            }

            string methodsPart = "";
            if (Methods is { Count: > 0 })
            {
                const int maxM = 8;
                var shown = Methods.Take(maxM).Select(m => m.ToString());
                var tail = Methods.Count > maxM ? $", …+{Methods.Count - maxM}" : "";
                methodsPart = $" methods=[{string.Join(", ", shown)}{tail}]";
            }

            string gensPart = "";
            if (Generalizations is { Count: > 0 })
                gensPart = $" gens=[{string.Join(", ", Generalizations.Select(DefTypeRefToString))}]";

            return $"{kind}({label}{idx}{fieldsPart}{methodsPart}{gensPart})";
        }
    }
}
