using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Magic.Kernel.Compilation.Ast;
using Magic.Kernel.Processor;

namespace Magic.Kernel.Compilation
{
    /// <summary>
    /// Type/class/schema lowering helpers extracted from <see cref="StatementLoweringCompiler"/>.
    /// </summary>
    internal sealed partial class StatementLoweringCompiler
    {
        private bool TryCompileSchemaDeclaration(string line, Dictionary<string, (string Kind, int Index)> vars, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var tableMatch = Regex.Match(
                line,
                @"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*(?:<>|>)?)\s*:\s*table\s*\{(?<body>.*)\}\s*;?\s*$",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (tableMatch.Success)
            {
                var tableName = tableMatch.Groups["name"].Value.Trim();
                var body = tableMatch.Groups["body"].Value;
                var tableSlot = memorySlotCounter++;
                vars[tableName] = ("memory", tableSlot);

                instructions.Add(CreatePushStringInstruction(tableName));
                instructions.Add(CreatePushStringInstruction("table"));
                instructions.Add(CreatePushIntInstruction(2));
                instructions.Add(new InstructionNode { Opcode = "def" });
                instructions.Add(CreatePopMemoryInstruction(tableSlot));

                foreach (var columnRaw in SplitTopLevelBySemicolon(body))
                {
                    if (!TryParseColumnSpec(columnRaw, out var columnName, out var columnType, out var modifiers, out var isRelation, out var isArrayRelation))
                        continue;

                    instructions.Add(CreatePushMemoryInstruction(tableSlot));
                    instructions.Add(CreatePushStringInstruction(columnName));
                    instructions.Add(CreatePushStringInstruction(columnType));
                    if (isRelation)
                    {
                        instructions.Add(CreatePushStringInstruction(isArrayRelation ? "array" : "single"));
                        instructions.Add(CreatePushStringInstruction("relation"));
                        instructions.Add(CreatePushIntInstruction(5));
                    }
                    else
                    {
                        foreach (var modifier in modifiers)
                            instructions.Add(CreatePushStringInstruction(modifier));
                        instructions.Add(CreatePushStringInstruction("column"));
                        instructions.Add(CreatePushIntInstruction(4 + modifiers.Count));
                    }
                    instructions.Add(new InstructionNode { Opcode = "def" });
                    instructions.Add(CreatePopMemoryInstruction(tableSlot));
                }

                return true;
            }

            var dbMatch = Regex.Match(
                line,
                @"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*(?:<>|>)?)\s*:\s*database\s*\{(?<body>.*)\}\s*;?\s*$",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!dbMatch.Success)
                return false;

            var dbName = dbMatch.Groups["name"].Value.Trim();
            var dbBody = dbMatch.Groups["body"].Value;
            var dbSlot = memorySlotCounter++;
            vars[dbName] = ("memory", dbSlot);

            instructions.Add(CreatePushStringInstruction(dbName));
            instructions.Add(CreatePushStringInstruction("database"));
            instructions.Add(CreatePushIntInstruction(2));
            instructions.Add(new InstructionNode { Opcode = "def" });
            instructions.Add(CreatePopMemoryInstruction(dbSlot));

            foreach (var tableRefRaw in SplitTopLevelBySemicolon(dbBody))
            {
                var tableRef = tableRefRaw.Trim();
                if (string.IsNullOrWhiteSpace(tableRef))
                    continue;
                if (!vars.TryGetValue(tableRef, out var tableVar))
                    throw new UndeclaredVariableException(tableRef);

                instructions.Add(CreatePushMemoryInstruction(dbSlot));
                instructions.Add(CreatePushMemoryInstruction(tableVar.Index));
                instructions.Add(CreatePushStringInstruction("table"));
                instructions.Add(CreatePushIntInstruction(3));
                instructions.Add(new InstructionNode { Opcode = "def" });
                instructions.Add(CreatePopMemoryInstruction(dbSlot));
            }

            return true;
        }

        /// <summary>
        /// Типы/классы уровня Prelude:
        ///   Point: type { ... }
        ///   Shape: class { ... }
        ///   Circle: Shape { ... }
        ///   CircleSquare: Circle, Square { ... }
        ///   CircleSquareList: CircleSquare[] { }
        /// </summary>
        private bool TryCompileTypeOrClassDeclaration(string line, Dictionary<string, (string Kind, int Index)> vars, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var colonIdx = line.IndexOf(':');
            var braceIdx = line.IndexOf('{');
            if (colonIdx <= 0 || braceIdx <= colonIdx)
                return false;

            // Быстрый фильтр: в заголовке до '{' не должно быть оператора присваивания.
            // Внутри тела типа/класса (constructor/method) ":=" допустим и не должен
            // ломать распознавание декларации.
            var headerSlice = line.Substring(0, braceIdx);
            if (headerSlice.Contains(":=", StringComparison.Ordinal))
                return false;

            var namePart = line.Substring(0, colonIdx).Trim();
            if (string.IsNullOrWhiteSpace(namePart))
                return false;

            // Имя типа
            var typeName = namePart;

            // Заголовок между ':' и '{'
            var header = line.Substring(colonIdx + 1, braceIdx - colonIdx - 1).Trim();
            if (string.IsNullOrWhiteSpace(header))
                return false;

            // Тело с фигурными скобками — может быть многострочным, но сюда уже пришёл
            // результат CoalesceMultilineStatements, т.е. single-line.
            var body = line.Substring(braceIdx);

            // Кейс 1: Point: type { ... }
            if (header.StartsWith("type", StringComparison.OrdinalIgnoreCase))
            {
                EmitSimpleTypeDef(typeName, ref memorySlotCounter, vars, instructions);
                EmitTypeBodyMembers(typeName, body, null, ref memorySlotCounter, vars, instructions);
                return true;
            }

            // Кейс 2: Shape: class { ... }
            if (header.StartsWith("class", StringComparison.OrdinalIgnoreCase))
            {
                EmitSimpleClassDef(typeName, null, ref memorySlotCounter, vars, instructions);
                EmitTypeBodyMembers(typeName, body, null, ref memorySlotCounter, vars, instructions);
                return true;
            }

            // Остальное трактуем как наследование:
            //   Circle: Shape { ... }
            //   CircleSquare: Circle, Square { ... }
            var baseTypes = header
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            if (baseTypes.Count == 0)
                return false;

            EmitSimpleClassDef(typeName, baseTypes, ref memorySlotCounter, vars, instructions);
            EmitTypeBodyMembers(typeName, body, baseTypes, ref memorySlotCounter, vars, instructions);
            return true;
        }

        private void EmitSimpleTypeDef(string typeName, ref int memorySlotCounter, Dictionary<string, (string Kind, int Index)> vars, List<InstructionNode> instructions)
        {
            // Тип как DefList-объект в памяти: slot хранит runtime-представление типа.
            if (!vars.TryGetValue(typeName, out var existing) || existing.Kind != "memory")
            {
                var slot = memorySlotCounter++;
                vars[typeName] = ("memory", slot);
                instructions.Add(CreatePushStringInstruction(QualifyDeclaredTypeName(typeName)));
                instructions.Add(CreatePushStringInstruction("type"));
                instructions.Add(CreatePushIntInstruction(2));
                instructions.Add(new InstructionNode { Opcode = "def" });
                instructions.Add(CreatePopMemoryInstruction(slot));
            }
        }

        private void EmitSimpleClassDef(string typeName, List<string>? baseTypes, ref int memorySlotCounter, Dictionary<string, (string Kind, int Index)> vars, List<InstructionNode> instructions)
        {
            // Класс без баз — тот же самый "type".
            if (baseTypes == null || baseTypes.Count == 0)
            {
                EmitSimpleTypeDef(typeName, ref memorySlotCounter, vars, instructions);
                return;
            }

            // Множественное наследование:
            // push qualified(class); push qualified(bases...); push N; def; pop slot;
            var slot = memorySlotCounter++;
            vars[typeName] = ("memory", slot);

            instructions.Add(CreatePushStringInstruction(QualifyDeclaredTypeName(typeName)));
            foreach (var b in baseTypes)
                instructions.Add(CreatePushStringInstruction(QualifyTypeNameForDefObj(b)));
            instructions.Add(CreatePushIntInstruction(1 + baseTypes.Count));
            instructions.Add(new InstructionNode { Opcode = "def" });
            instructions.Add(CreatePopMemoryInstruction(slot));
        }

        // Методы/конструкторы типов собираем как текстовые блоки и передаём наверх
        // через TakeTypeMethods(); поля продолжаем описывать через def "field".
        private readonly List<(string TypeName, string Name, List<string> BodyLines, Dictionary<string, string> FieldSpecs)> _typeMethods = new();

        // Простейший счётчик перегрузок: (TypeName, RawName) -> nextIndex.
        // Используется только для суффиксов "_1", "_2", ... в именах методов типов.
        private readonly Dictionary<(string TypeName, string RawName), int> _typeMethodOverloadCounters =
            new(DictionaryComparer.Instance);

        private sealed class DictionaryComparer : IEqualityComparer<(string TypeName, string RawName)>
        {
            public static readonly DictionaryComparer Instance = new();

            public bool Equals((string TypeName, string RawName) x, (string TypeName, string RawName) y) =>
                string.Equals(x.TypeName, y.TypeName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.RawName, y.RawName, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string TypeName, string RawName) obj) =>
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TypeName ?? string.Empty) * 397 ^
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.RawName ?? string.Empty);
        }

        public IReadOnlyList<(string TypeName, string Name, List<string> BodyLines, Dictionary<string, string> FieldSpecs)> TakeTypeMethods()
        {
            var snapshot = _typeMethods.ToList();
            _typeMethods.Clear();
            return snapshot;
        }

        /// <summary>
        /// Добавляет в <see cref="_declaredTypeFieldSpecs"/> поля из AST типов (основной файл и каждый <c>use</c>-модуль).
        /// </summary>
        public void MergeDeclaredTypeFieldsFromProgramTypes(IReadOnlyList<TypeNode>? types)
        {
            if (types == null || types.Count == 0)
                return;

            _declaredTypeFieldSpecs ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in types)
            {
                var line = node.Text?.Trim() ?? "";
                var colonIdx = line.IndexOf(':');
                var braceIdx = line.IndexOf('{');
                if (colonIdx <= 0 || braceIdx <= colonIdx)
                    continue;
                if (line.Substring(0, braceIdx).Contains(":=", StringComparison.Ordinal))
                    continue;
                var namePart = line.Substring(0, colonIdx).Trim();
                if (string.IsNullOrWhiteSpace(namePart))
                    continue;
                var bodyWithBraces = line.Substring(braceIdx);
                ScanTypeBodyLinesForDeclaredFieldSpecsOnly(namePart, bodyWithBraces);
            }
        }

        private void RecordDeclaredFieldSpec(string qualifiedHost, string fieldName, string fieldTypeSpec)
        {
            if (_declaredTypeFieldSpecs == null)
                return;
            if (!_declaredTypeFieldSpecs.TryGetValue(qualifiedHost, out var inner))
            {
                inner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _declaredTypeFieldSpecs[qualifiedHost] = inner;
            }

            inner[fieldName] = fieldTypeSpec.Trim();
        }

        /// <summary>
        /// Копирует карту полей из другого компилятора (например срез с namespace импортируемого модуля).
        /// </summary>
        internal void AbsorbDeclaredTypeFieldSpecsFrom(StatementLoweringCompiler source)
        {
            if (source._declaredTypeFieldSpecs == null)
                return;
            _declaredTypeFieldSpecs ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var hostKv in source._declaredTypeFieldSpecs)
            {
                foreach (var fieldKv in hostKv.Value)
                    RecordDeclaredFieldSpec(hostKv.Key, fieldKv.Key, fieldKv.Value);
            }
        }

        /// <summary>Только поля; методы/конструкторы пропускаются по тем же правилам, что <see cref="EmitTypeBodyMembers"/>.</summary>
        private void ScanTypeBodyLinesForDeclaredFieldSpecsOnly(string typeNameShort, string bodyWithBraces)
        {
            var fqHost = QualifyTypeNameForDefObj(typeNameShort.Trim());
            var trimmed = bodyWithBraces.Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal))
                trimmed = trimmed.Substring(1);
            if (trimmed.EndsWith("}", StringComparison.Ordinal))
                trimmed = trimmed.Substring(0, trimmed.Length - 1);

            var reader = new LineReader(trimmed);
            var visibility = "public";

            for (var li = 0; li < reader.Count; li++)
            {
                var (raw, _) = reader.GetLine(li);
                var text = raw.Trim();
                if (string.IsNullOrEmpty(text))
                    continue;

                var visPrefix = Regex.Match(text, @"^(?<vis>public|private|protected)\s+(?<rest>\S[\s\S]*)$", RegexOptions.IgnoreCase);
                if (visPrefix.Success)
                {
                    var rest = visPrefix.Groups["rest"].Value.Trim();
                    if (rest.Length > 0)
                    {
                        visibility = visPrefix.Groups["vis"].Value.ToLowerInvariant();
                        text = rest;
                    }
                }

                if (string.Equals(text, "public", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "private", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "protected", StringComparison.OrdinalIgnoreCase))
                {
                    visibility = text.ToLowerInvariant();
                    continue;
                }

                if (text.StartsWith("constructor ", StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith("method ", StringComparison.OrdinalIgnoreCase))
                {
                    var bodyLines = new List<string> { text };
                    var braceDepth = 0;
                    var braceIndex = text.IndexOf('{');
                    if (braceIndex >= 0)
                    {
                        braceDepth = 1;
                        var afterBrace = text.Substring(braceIndex + 1);
                        if (!string.IsNullOrWhiteSpace(afterBrace))
                            bodyLines.Add(afterBrace);
                    }

                    while (braceDepth > 0 && ++li < reader.Count)
                    {
                        var (innerRaw, _) = reader.GetLine(li);
                        var innerText = innerRaw;
                        foreach (var ch in innerText)
                        {
                            if (ch == '{') braceDepth++;
                            else if (ch == '}') braceDepth--;
                        }

                        if (braceDepth >= 0)
                            bodyLines.Add(innerText);
                    }

                    continue;
                }

                if (TryParseFieldDeclarations(text, out var fieldDecls))
                {
                    foreach (var (fieldName, fieldTypeSpec) in fieldDecls)
                        RecordDeclaredFieldSpec(fqHost, fieldName, fieldTypeSpec);
                }
            }
        }

        private void EmitTypeBodyMembers(string typeName, string bodyWithBraces, List<string>? baseTypes, ref int memorySlotCounter, Dictionary<string, (string Kind, int Index)> vars, List<InstructionNode> instructions)
        {
            var trimmed = bodyWithBraces.Trim();
            var fieldSpecs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (trimmed.StartsWith("{"))
                trimmed = trimmed.Substring(1);
            if (trimmed.EndsWith("}"))
                trimmed = trimmed.Substring(0, trimmed.Length - 1);

            var reader = new LineReader(trimmed);
            var visibility = "public";

            for (var li = 0; li < reader.Count; li++)
            {
                var (raw, _) = reader.GetLine(li);
                var text = raw.Trim();
                if (string.IsNullOrEmpty(text))
                    continue;

                // "public X: int;" / "private Foo: T;" на одной строке (не только "public" отдельной строкой).
                var visPrefix = Regex.Match(text, @"^(?<vis>public|private|protected)\s+(?<rest>\S[\s\S]*)$", RegexOptions.IgnoreCase);
                if (visPrefix.Success)
                {
                    var rest = visPrefix.Groups["rest"].Value.Trim();
                    if (rest.Length > 0)
                    {
                        visibility = visPrefix.Groups["vis"].Value.ToLowerInvariant();
                        text = rest;
                    }
                }

                // Модификаторы видимости.
                if (string.Equals(text, "public", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "private", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "protected", StringComparison.OrdinalIgnoreCase))
                {
                    visibility = text.ToLowerInvariant();
                    continue;
                }

                // Начало конструктора или метода: "constructor Name(...)" / "method Name(...)".
                if (text.StartsWith("constructor ", StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith("method ", StringComparison.OrdinalIgnoreCase))
                {
                    var isCtor = text.StartsWith("constructor ", StringComparison.OrdinalIgnoreCase);
                    var kwLen = isCtor ? "constructor".Length : "method".Length;
                    var rest = text.Substring(kwLen).TrimStart();
                    var nameEnd = rest.IndexOfAny(new[] { '(', ' ', '\t', '{' });
                    var rawName = nameEnd >= 0 ? rest.Substring(0, nameEnd).Trim() : rest;
                    if (string.IsNullOrEmpty(rawName) && isCtor)
                        rawName = typeName;

                    var ns = DefName.NamespaceFromDefaultTypePrefix(_defaultTypeNamespacePrefix);
                    var typeDef = new DefName(ns, typeName);
                    var manglingType = typeDef.FullName;
                    string methodName;
                    if (isCtor)
                    {
                        methodName = new DefName(ns, $"{typeName}_ctor_1").FullName;
                    }
                    else
                    {
                        var key = (typeName, rawName);
                        if (!_typeMethodOverloadCounters.TryGetValue(key, out var idx))
                            idx = 0;
                        idx++;
                        _typeMethodOverloadCounters[key] = idx;
                        methodName = new DefName(ns, $"{typeName}_{rawName}_{idx}").FullName;
                        var returnType = ParseMethodReturnTypeFromHeader(text);
                        var firstParamFq = TryQualifyFirstMethodParameterType(text);
                        EmitMethodDef(typeName, rawName, methodName, returnType, visibility, firstParamFq, ref memorySlotCounter, vars, instructions);
                    }

                    // Считываем тело метода/конструктора до закрывающей "}".
                    // В bodyLines первой строкой всегда кладём заголовок (constructor/method ...),
                    // чтобы ExtractTypeMethodSignatureAndBody могла разобрать параметры.
                    var bodyLines = new List<string> { text };
                    var braceDepth = 0;
                    var sawBrace = false;

                    // Если в строке уже есть "{", считаем, что это начало тела.
                    var braceIndex = text.IndexOf('{');
                    if (braceIndex >= 0)
                    {
                        sawBrace = true;
                        braceDepth = 1;
                        var afterBrace = text.Substring(braceIndex + 1);
                        if (!string.IsNullOrWhiteSpace(afterBrace))
                            bodyLines.Add(afterBrace);
                    }

                    while (braceDepth > 0 && ++li < reader.Count)
                    {
                        var (innerRaw, _) = reader.GetLine(li);
                        var innerText = innerRaw;
                        foreach (var ch in innerText)
                        {
                            if (ch == '{') braceDepth++;
                            else if (ch == '}') braceDepth--;
                        }
                        if (braceDepth >= 0)
                            bodyLines.Add(innerText);
                    }

                    _typeMethods.Add((manglingType, methodName, bodyLines, fieldSpecs));
                    continue;
                }

                // Поле: X: int; / Y: Shape; / W, H: float<decimal>;
                if (TryParseFieldDeclarations(text, out var fieldDecls))
                {
                    foreach (var (fieldName, fieldTypeSpec) in fieldDecls)
                    {
                        fieldSpecs[fieldName] = fieldTypeSpec;
                        EmitFieldDef(typeName, fieldName, fieldTypeSpec, visibility, ref memorySlotCounter, vars, instructions);
                    }

                    continue;
                }
            }
        }

        /// <summary>
        /// Одна или несколько деклараций полей с общим типом: <c>W, H: float&lt;decimal&gt;;</c>.
        /// </summary>
        internal static bool TryParseFieldDeclarations(string text, out List<(string FieldName, string FieldTypeSpec)> declarations)
        {
            declarations = new List<(string, string)>();
            var match = Regex.Match(
                text,
                @"^\s*(?<names>[A-Za-z_][A-Za-z0-9_]*(?:\s*,\s*[A-Za-z_][A-Za-z0-9_]*)*)\s*:\s*(?<type>[^;]+);?\s*$");
            if (!match.Success)
                return false;

            var typeSpec = match.Groups["type"].Value.Trim();
            if (string.IsNullOrEmpty(typeSpec))
                typeSpec = "any";

            foreach (var rawName in match.Groups["names"].Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = rawName.Trim();
                if (name.Length > 0)
                    declarations.Add((name, typeSpec));
            }

            return declarations.Count > 0;
        }

        private static string ParseMethodReturnTypeFromHeader(string headerLine)
        {
            var m = Regex.Match(headerLine ?? "", @"\)\s*(?::|->)\s*(?<ret>[^;{\)]+)", RegexOptions.IgnoreCase);
            if (!m.Success)
                return "void";
            var r = m.Groups["ret"].Value.Trim();
            return string.IsNullOrEmpty(r) ? "void" : r;
        }

        /// <summary>
        /// Метаданные метода: owner; short name; mangled name; return type; "method"; visibility; firstParamFq (может быть ""); push 7; def.
        /// </summary>
        private void EmitMethodDef(
            string typeName,
            string rawMethodName,
            string mangledProcedureFullName,
            string returnType,
            string visibility,
            string? firstUserParameterTypeFq,
            ref int memorySlotCounter,
            Dictionary<string, (string Kind, int Index)> vars,
            List<InstructionNode> instructions)
        {
            if (!vars.TryGetValue(typeName, out var ownerTypeVar) || ownerTypeVar.Kind != "memory")
                throw new InvalidOperationException($"Type '{typeName}' must be defined before its methods.");

            instructions.Add(CreatePushMemoryInstruction(ownerTypeVar.Index));
            instructions.Add(CreatePushStringInstruction(rawMethodName));
            instructions.Add(CreatePushStringInstruction(mangledProcedureFullName));
            instructions.Add(CreatePushStringInstruction(string.IsNullOrWhiteSpace(returnType) ? "void" : returnType));
            instructions.Add(CreatePushStringInstruction("method"));
            instructions.Add(CreatePushStringInstruction(visibility));
            instructions.Add(CreatePushStringInstruction(string.IsNullOrWhiteSpace(firstUserParameterTypeFq) ? "" : firstUserParameterTypeFq.Trim()));
            instructions.Add(CreatePushIntInstruction(7));
            instructions.Add(new InstructionNode { Opcode = "def" });
            instructions.Add(CreatePopInstruction());
        }

        private string? TryQualifyFirstMethodParameterType(string methodHeaderLine)
        {
            var t = (methodHeaderLine ?? "").Trim();
            if (!t.StartsWith("method ", StringComparison.OrdinalIgnoreCase))
                return null;
            var rest = t.Substring("method".Length).TrimStart();
            var nameEnd = 0;
            while (nameEnd < rest.Length && !char.IsWhiteSpace(rest[nameEnd]) && rest[nameEnd] != '(')
                nameEnd++;
            rest = rest.Substring(nameEnd).TrimStart();
            if (!TryExtractFormalParametersSlice(rest, out var inside))
                return null;
            var shortT = TryGetFirstFormalParameterTypeShort(inside);
            return string.IsNullOrWhiteSpace(shortT) ? null : QualifyTypeNameForDefObj(shortT.Trim());
        }

        private static bool TryExtractFormalParametersSlice(string textStartingAtParenGroup, out string insideParens)
        {
            insideParens = "";
            var open = textStartingAtParenGroup.IndexOf('(');
            if (open < 0)
                return false;
            var depth = 0;
            for (var j = open; j < textStartingAtParenGroup.Length; j++)
            {
                var c = textStartingAtParenGroup[j];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        insideParens = textStartingAtParenGroup.Substring(open + 1, j - open - 1);
                        return true;
                    }
                }
            }

            return false;
        }

        private static string? TryGetFirstFormalParameterTypeShort(string insideParens)
        {
            if (string.IsNullOrWhiteSpace(insideParens))
                return null;
            var s = insideParens.Trim();
            var idx = 0;
            while (idx < s.Length && char.IsWhiteSpace(s[idx])) idx++;
            var start = idx;
            while (idx < s.Length && !char.IsWhiteSpace(s[idx]) && s[idx] != ':') idx++;
            if (start >= idx)
                return null;
            while (idx < s.Length && char.IsWhiteSpace(s[idx])) idx++;
            if (idx >= s.Length || s[idx] != ':')
                return null;
            idx++;
            while (idx < s.Length && char.IsWhiteSpace(s[idx])) idx++;
            var tStart = idx;
            var depthAngle = 0;
            while (idx < s.Length)
            {
                var c = s[idx];
                if (c == '<') depthAngle++;
                else if (c == '>' && depthAngle > 0) depthAngle--;
                else if (depthAngle == 0 && c == ',')
                    break;
                idx++;
            }

            var ts = s.Substring(tStart, idx - tStart).Trim();
            return string.IsNullOrEmpty(ts) ? null : ts;
        }

        private void EmitFieldDef(string typeName, string fieldName, string fieldTypeSpec, string visibility, ref int memorySlotCounter, Dictionary<string, (string Kind, int Index)> vars, List<InstructionNode> instructions)
        {
            var fullFieldName = $"{typeName}.{fieldName}";
            if (!vars.TryGetValue(fullFieldName, out var existing) || existing.Kind != "memory")
            {
                if (!vars.TryGetValue(typeName, out var ownerTypeVar) || ownerTypeVar.Kind != "memory")
                    throw new InvalidOperationException($"Type '{typeName}' must be defined before its fields.");

                var slot = memorySlotCounter++;
                vars[fullFieldName] = ("memory", slot);
                instructions.Add(CreatePushMemoryInstruction(ownerTypeVar.Index));
                instructions.Add(CreatePushStringInstruction(fieldName));
                EmitFieldTypePush(fieldTypeSpec, instructions);
                instructions.Add(CreatePushStringInstruction("field"));
                instructions.Add(CreatePushStringInstruction(visibility));
                instructions.Add(CreatePushIntInstruction(5));
                instructions.Add(new InstructionNode { Opcode = "def" });
                instructions.Add(CreatePopMemoryInstruction(slot));
            }
        }

        /// <summary>
        /// Примитивы и спеки вида <c>float&lt;decimal&gt;</c> остаются <c>push string</c>;
        /// ссылка на объявленный тип/класс — <c>push class</c> с полным именем (как у <c>defobj</c>).
        /// Массивы классов: <c>push string</c> с квалифицированным <c>T[]</c> (стек <c>def</c> ждёт строку типа в метаданных).
        /// </summary>
        private void EmitFieldTypePush(string fieldTypeSpec, List<InstructionNode> instructions)
        {
            var spec = string.IsNullOrWhiteSpace(fieldTypeSpec) ? "any" : fieldTypeSpec.Trim();

            if (TryStripSimpleArraySuffix(spec, out var elem) &&
                IsSimpleTypeIdentifier(elem) &&
                !IsBuiltinFieldTypeName(elem))
            {
                instructions.Add(CreatePushStringInstruction(QualifyTypeNameForDefObj(elem) + "[]"));
                return;
            }

            if (IsSimpleTypeIdentifier(spec) && !IsBuiltinFieldTypeName(spec))
            {
                instructions.Add(CreatePushClassInstruction(QualifyTypeNameForDefObj(spec)));
                return;
            }

            instructions.Add(CreatePushStringInstruction(spec));
        }

        private static bool TryStripSimpleArraySuffix(string spec, out string elementType)
        {
            elementType = "";
            if (spec.Length <= 2 || !spec.EndsWith("[]", StringComparison.Ordinal))
                return false;
            elementType = spec.Substring(0, spec.Length - 2).Trim();
            return elementType.Length > 0;
        }

        private static bool IsSimpleTypeIdentifier(string spec) =>
            spec.Length > 0 &&
            spec.IndexOfAny(new[] { '<', '(', '.', ' ', '\t' }) < 0 &&
            Regex.IsMatch(spec, @"^[A-Za-z_][A-Za-z0-9_]*$");

        private static bool IsBuiltinFieldTypeName(string name) => BuiltinFieldTypeNames.Contains(name);

        private static readonly HashSet<string> BuiltinFieldTypeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "any", "void", "int", "long", "short", "byte", "sbyte", "ushort", "uint", "ulong",
            "float", "double", "decimal", "bool", "char", "string", "object", "json"
        };

        private static List<string> SplitTopLevelBySemicolon(string source)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(source))
                return result;

            var sb = new StringBuilder();
            var parenDepth = 0;
            var inString = false;
            var escaped = false;

            for (var index = 0; index < source.Length; index++)
            {
                var ch = source[index];
                if (inString)
                {
                    sb.Append(ch);
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (ch == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (ch == '"')
                        inString = false;
                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    sb.Append(ch);
                    continue;
                }

                if (ch == '(') parenDepth++;
                if (ch == ')') parenDepth = Math.Max(0, parenDepth - 1);

                if (ch == ';' && parenDepth == 0)
                {
                    var item = sb.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(item))
                        result.Add(item);
                    sb.Clear();
                    continue;
                }

                sb.Append(ch);
            }

            var last = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(last))
                result.Add(last);
            return result;
        }

        private static bool TryParseColumnSpec(
            string columnRaw,
            out string columnName,
            out string columnType,
            out List<string> modifiers,
            out bool isRelation,
            out bool isArrayRelation)
        {
            columnName = "";
            columnType = "";
            modifiers = new List<string>();
            isRelation = false;
            isArrayRelation = false;

            var idx = columnRaw.IndexOf(':');
            if (idx <= 0)
                return false;

            columnName = columnRaw.Substring(0, idx).Trim();
            var spec = columnRaw.Substring(idx + 1).Trim();
            if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(spec))
                return false;

            if (TryParseRelationSpec(spec, out var relationType, out var relationIsArray))
            {
                columnType = relationType;
                isRelation = true;
                isArrayRelation = relationIsArray;
                return true;
            }

            var nullable = spec.EndsWith("?", StringComparison.Ordinal);
            if (nullable)
                spec = spec.Substring(0, spec.Length - 1).Trim();

            var lengthMatch = Regex.Match(spec, @"^(?<type>[A-Za-z_][A-Za-z0-9_]*)\((?<len>\d+)\)\s*(?<rest>.*)$", RegexOptions.IgnoreCase);
            if (lengthMatch.Success)
            {
                columnType = lengthMatch.Groups["type"].Value.Trim().ToLowerInvariant();
                modifiers.Add($"length:{lengthMatch.Groups["len"].Value.Trim()}");
                spec = lengthMatch.Groups["rest"].Value.Trim();
            }
            else
            {
                var parts = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    return false;
                columnType = parts[0].Trim().ToLowerInvariant();
                spec = string.Join(" ", parts.Skip(1)).Trim();
            }

            // Support nullable marker directly on type token, e.g. "int? index".
            if (columnType.EndsWith("?", StringComparison.Ordinal))
            {
                nullable = true;
                columnType = columnType.Substring(0, columnType.Length - 1).Trim();
            }

            if (string.IsNullOrWhiteSpace(columnType))
                return false;

            if (Regex.IsMatch(spec, @"\bprimary\s+key\b", RegexOptions.IgnoreCase))
            {
                modifiers.Add("primary key");
                spec = Regex.Replace(spec, @"\bprimary\s+key\b", "", RegexOptions.IgnoreCase).Trim();
            }
            if (Regex.IsMatch(spec, @"\bidentity\b", RegexOptions.IgnoreCase))
            {
                modifiers.Add("identity");
                spec = Regex.Replace(spec, @"\bidentity\b", "", RegexOptions.IgnoreCase).Trim();
            }
            if (!string.IsNullOrWhiteSpace(spec))
                modifiers.Add(spec);

            modifiers.Add(nullable ? "nullable:1" : "nullable:0");
            return true;
        }

        private static bool TryParseRelationSpec(string spec, out string referencedTableType, out bool isArray)
        {
            referencedTableType = "";
            isArray = false;

            var s = (spec ?? "").Trim();
            if (s.Length == 0)
                return false;

            var nullableSuffix = false;
            if (s.EndsWith("?", StringComparison.Ordinal))
            {
                nullableSuffix = true;
                s = s.Substring(0, s.Length - 1).Trim();
            }

            if (s.EndsWith("[]", StringComparison.Ordinal))
            {
                isArray = true;
                s = s.Substring(0, s.Length - 2).Trim();
            }

            if (nullableSuffix && isArray)
                return false;

            if (!Regex.IsMatch(s, @"^[A-Za-z_][A-Za-z0-9_]*(?:<>|>)?$"))
                return false;

            var normalizedType = s.Trim().ToLowerInvariant();
            if (BuiltinSqlTypeNames.Contains(normalizedType))
                return false;

            referencedTableType = s;
            return true;
        }

        private static readonly HashSet<string> BuiltinSqlTypeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "bigint", "int", "integer", "smallint", "datetime", "timestamp", "date", "time",
            "bool", "boolean", "decimal", "double", "float", "uuid", "json", "jsonb", "text",
            "varchar", "nvarchar"
        };
    }
}

