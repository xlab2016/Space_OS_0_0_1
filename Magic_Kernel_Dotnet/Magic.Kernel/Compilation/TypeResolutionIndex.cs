using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Magic.Kernel.Compilation
{
    /// <summary>
    /// Короткое имя типа (последний сегмент <see cref="DefName"/>) → кандидаты с полным именем.
    /// Нужен для <c>Point(1, 2)</c> как конструктора и для диагностики коллизий между модулями.
    /// </summary>
    public sealed class TypeResolutionIndex
    {
        private readonly Dictionary<string, List<(string FullName, bool IsClass)>> _byShort =
            new(StringComparer.OrdinalIgnoreCase);

        public void AddCandidate(string fullName, bool isClass)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return;

            var fq = fullName.Trim();
            var shortName = SemanticAnalyzer.ExtractUnqualifiedTypeName(fq);
            if (string.IsNullOrEmpty(shortName))
                return;

            if (!_byShort.TryGetValue(shortName, out var list))
            {
                list = new List<(string, bool)>();
                _byShort[shortName] = list;
            }

            foreach (var x in list)
            {
                if (string.Equals(x.FullName, fq, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            list.Add((fq, isClass));
        }

        /// <summary>
        /// Однозначное разрешение короткого имени. Ноль кандидатов — <c>false</c>; больше одного — <see cref="CompilationException"/>.
        /// </summary>
        public bool TryGetUniqueType(string shortName, out string fullName, out bool isClass)
        {
            fullName = "";
            isClass = false;
            if (string.IsNullOrWhiteSpace(shortName) || shortName.IndexOf(':') >= 0)
                return false;

            var key = shortName.Trim();
            if (!_byShort.TryGetValue(key, out var list) || list.Count == 0)
                return false;

            if (list.Count > 1)
            {
                var names = string.Join(", ", list.Select(x => x.FullName).Distinct(StringComparer.OrdinalIgnoreCase));
                throw new CompilationException(
                    $"Ambiguous type name '{key}': multiple types match this short name between modules: {names}.");
            }

            fullName = list[0].FullName;
            isClass = list[0].IsClass;
            return true;
        }

        /// <summary>
        /// Карта только для однозначных коротких имён (совместимо с <see cref="StatementLoweringCompiler.SetExternalTypeQualificationMap"/>).
        /// </summary>
        public Dictionary<string, string> ToExternalQualificationDictionary()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _byShort)
            {
                if (kv.Value.Count == 1)
                    d[kv.Key] = kv.Value[0].FullName;
            }

            return d;
        }

        public static void AddLocalTypesFromProgram(ProgramStructure programStructure, TypeResolutionIndex index)
        {
            if (programStructure?.Types == null)
                return;

            var ns = DefName.NamespaceFromDefaultTypePrefix(DefName.GetDefaultTypeNamespacePrefix(programStructure));

            foreach (var node in programStructure.Types)
            {
                var line = node.Text?.Trim() ?? "";
                if (line.Length == 0)
                    continue;

                var colonIdx = line.IndexOf(':');
                if (colonIdx <= 0)
                    continue;

                var braceIdx = line.IndexOf('{');
                if (braceIdx <= colonIdx)
                    continue;

                var header = line.Substring(colonIdx + 1, braceIdx - colonIdx - 1).Trim();
                if (header.StartsWith("table", StringComparison.OrdinalIgnoreCase) ||
                    header.StartsWith("database", StringComparison.OrdinalIgnoreCase))
                    continue;

                var namePart = line.Substring(0, colonIdx).Trim();
                if (namePart.Length == 0)
                    continue;

                bool isClass;
                if (header.StartsWith("class", StringComparison.OrdinalIgnoreCase))
                    isClass = true;
                else if (header.StartsWith("type", StringComparison.OrdinalIgnoreCase))
                    isClass = false;
                else
                    isClass = true;

                var fq = new DefName(ns, namePart).FullName;
                index.AddCandidate(fq, isClass);
            }
        }

        internal static async Task<TypeResolutionIndex> BuildAsync(
            ProgramStructure programStructure,
            Linker linker,
            string? resolvedSourcePath,
            HashSet<string>? useStack)
        {
            var index = new TypeResolutionIndex();
            AddLocalTypesFromProgram(programStructure, index);
            if (resolvedSourcePath != null &&
                useStack != null &&
                programStructure.UseDirectives.Count > 0)
            {
                await linker
                    .PopulateTypeIndexFromUsesAsync(resolvedSourcePath, programStructure, index, useStack)
                    .ConfigureAwait(false);
            }

            return index;
        }
    }
}
