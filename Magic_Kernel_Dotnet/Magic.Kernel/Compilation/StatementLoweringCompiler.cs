using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using Magic.Kernel.Compilation.Ast;
using Magic.Kernel.Processor;

namespace Magic.Kernel.Compilation
{
    /// <summary>
    /// Lowers high-level statement lines into instruction AST nodes.
    /// </summary>
    internal sealed partial class StatementLoweringCompiler
    {
        private Scanner? _scanner;
        private readonly Dictionary<string, int> _globalSlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _nextGlobalSlot;

        /// <summary>
        /// Logical local-slot counter (per compiler), used for future 0-based per-function locals.
        /// Physical indices still come from <see cref="_nextGlobalSlot"/> to preserve runtime semantics.
        /// </summary>
        private int _nextLocalSlot;

        private Scanner CurrentScanner =>
            _scanner ?? throw new InvalidOperationException("Scanner is not initialized.");

        /// <summary>The current value of the global slot counter (next slot index to be allocated).</summary>
        public int NextGlobalSlot => _nextGlobalSlot;

        /// <summary>Allocates a new global memory slot for the given name and returns its index.</summary>
        public int AllocateGlobalSlot(string name)
        {
            var slot = _nextGlobalSlot++;
            _globalSlots[name] = slot;
            return slot;
        }

        /// <summary>Allocates a new local (non-global) memory slot for the given name and returns its physical index.
        /// Unlike <see cref="AllocateGlobalSlot"/>, the slot is stored as "memory" kind so that body instructions
        /// use <c>push [N]</c> (local memory) instead of <c>push global: [N]</c>.</summary>
        public int AllocateLocalSlot(string name)
        {
            if (_locals.TryGetValue(name, out var existing))
                return existing.Physical;

            var physical = _nextGlobalSlot++;
            var logical = _nextLocalSlot++;
            _locals[name] = (physical, logical);
            _localSlots[name] = physical;
            return physical;
        }
        public bool TryResolveLocalSlot(string name, out int slot)
        {
            return _localSlots.TryGetValue(name, out slot);
        }

        private readonly Dictionary<string, int> _localSlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // имя локала -> (physicalIndex, logicalIndex)
        private readonly Dictionary<string, (int Physical, int Logical)> _locals =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Resets per-function local-slot state (logical indices). Physical slots remain driven by <see cref="_nextGlobalSlot"/>.</summary>
        public void ResetLocals()
        {
            _localSlots.Clear();
            _locals.Clear();
            _nextLocalSlot = 0;
        }
        public bool TryResolveLocalSlot(string name, out int physical, out int logical)
        {
            if (_locals.TryGetValue(name, out var tuple))
            {
                physical = tuple.Physical;
                logical = tuple.Logical;
                return true;
            }

            physical = logical = -1;
            return false;
        }

        private NestedCallScope? _nestedCallScope;
        private string? _defaultTypeNamespacePrefix;
        private HashSet<string>? _locallyDeclaredTypeNames;
        private Dictionary<string, string>? _externalTypeQualification;
        private TypeResolutionIndex? _typeResolutionIndex;

        /// <summary>Вложенные подпрограммы: короткие имена в call разворачиваются в манглированные ключи (процедура или функция).</summary>
        public void SetNestedCallScope(NestedCallScope? scope) => _nestedCallScope = scope;
        public void SetDefaultTypeNamespacePrefix(string? prefix) => _defaultTypeNamespacePrefix = prefix;

        /// <summary>Имена типов, объявленных в прелюдии <em>этого</em> файла (для <c>def</c> / манглинга методов).</summary>
        public void SetLocallyDeclaredTypeNames(HashSet<string>? names) =>
            _locallyDeclaredTypeNames = names is { Count: > 0 } ? names : null;

        /// <summary>Короткое имя → полное из скомпилированных <c>use</c>-модулей (для <c>defobj</c>, <c>new T</c>).</summary>
        public void SetExternalTypeQualificationMap(Dictionary<string, string>? map) =>
            _externalTypeQualification = map is { Count: > 0 } ? map : null;

        /// <summary>Локальные и импортированные типы для различения <c>T(...)</c> (конструктор) и вызова функции.</summary>
        public void SetTypeResolutionIndex(TypeResolutionIndex? index) =>
            _typeResolutionIndex = index;

        /// <summary>Квалификация типа, объявленного в unit (для сигнатур методов из <see cref="SemanticAnalyzer"/>).</summary>
        public string QualifyDeclaredUserTypeName(string shortName) => QualifyDeclaredTypeName(shortName);

        /// <summary>Квалификация типа из сигнатуры метода/поля (<c>Shape</c>, <c>mod:T</c>).</summary>
        public string QualifyTypeReferenceForDefObj(string typeSpec) => QualifyTypeNameForDefObj(typeSpec);

        /// <summary>Квалификация <em>объявляемого</em> в этом unit типа: префикс текущего program/system/module через <see cref="DefName"/>.</summary>
        private string QualifyDeclaredTypeName(string shortName)
        {
            if (string.IsNullOrWhiteSpace(shortName))
                return shortName;
            var trimmed = shortName.Trim();
            if (trimmed.IndexOf(':') >= 0)
                return trimmed;
            var ns = DefName.NamespaceFromDefaultTypePrefix(_defaultTypeNamespacePrefix);
            return new DefName(ns, trimmed).FullName;
        }

        /// <summary>Квалификация <em>ссылки</em> на тип (<c>new Room</c>, база из другого модуля): сначала типы из <c>use</c>, иначе локальный префикс.</summary>
        private string QualifyTypeNameForDefObj(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return typeName;
            if (typeName.IndexOf(':') >= 0)
                return typeName;
            if (_locallyDeclaredTypeNames != null && _locallyDeclaredTypeNames.Contains(typeName))
                return QualifyDeclaredTypeName(typeName);
            if (_externalTypeQualification != null &&
                _externalTypeQualification.TryGetValue(typeName, out var ext) &&
                !string.IsNullOrWhiteSpace(ext))
                return ext;
            return QualifyDeclaredTypeName(typeName);
        }

        private string ResolveUserCallTarget(string name)
        {
            if (name.IndexOf(':') >= 0)
                return name;
            if (_nestedCallScope != null && _nestedCallScope.TryResolve(name, out var m))
                return m;
            return name;
        }

        /// <summary>
        /// Named bindings (memory/stream/def/…) accumulated across per-line <see cref="Lower"/> calls in one procedure or block.
        /// Without this, lowering one statement line at a time loses <c>var x := …</c> forfollowing lines (regression after per-line SourceLine fix).
        /// </summary>
        private Dictionary<string, (string Kind, int Index)>? _crossLineVarState;

        /// <summary>
        /// Для memory-переменных, которым присвоен экземпляр def-типа: <see cref="DefType.FullName"/> (например <c>samples:modularity:module2:Point</c>),
        /// чтобы эмитить <c>callobj</c> с полным именем процедуры метода (<c>…:Point_Print_1</c>).
        /// </summary>
        private Dictionary<string, string>? _crossLineVarDefTypeFullName;

        /// <summary>Активная карта на время одного вызова <see cref="Lower"/> (снимок + обновления строки).</summary>
        private Dictionary<string, string>? _activeVarDefTypeFullName;

        /// <summary>
        /// hostTypeFullName → methodShort → список (манглированная процедура, короткий тип первого пользовательского параметра или null).
        /// Для <c>callobj</c> с перегрузками по типу первого аргумента.
        /// </summary>
        private Dictionary<string, Dictionary<string, List<(string MangledProcedure, string? FirstParamTypeShort)>>>? _instanceMethodOverloads;

        /// <summary>
        /// Объявленные поля классов/типов программы: FQ типа-носителя → поле → спецификация типа из исходника
        /// (для вывода def-типа у <c>var b := world.Board</c> и полного <c>callobj</c>).
        /// </summary>
        private Dictionary<string, Dictionary<string, string>>? _declaredTypeFieldSpecs;

        public void SetInstanceMethodOverloads(
            Dictionary<string, Dictionary<string, List<(string MangledProcedure, string? FirstParamTypeShort)>>>? map) =>
            _instanceMethodOverloads = map is { Count: > 0 } ? map : null;

        /// <summary>Сохраняет def-тип переменной между строками (параметры методов, ветки switch).</summary>
        public void RegisterPersistentVarDefType(string varName, string qualifiedDefTypeFullName)
        {
            if (string.IsNullOrWhiteSpace(varName) || string.IsNullOrWhiteSpace(qualifiedDefTypeFullName))
                return;
            var key = varName.Trim();
            var fq = qualifiedDefTypeFullName.Trim();
            _crossLineVarDefTypeFullName ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _crossLineVarDefTypeFullName[key] = fq;
            // Тот же Lower(), что компилирует switch: _activeVarDefTypeFullName уже снят с cross-line в начале Lower —
            // без записи сюда callobj не видит тип pattern-binding (напр. circle → Circle) до следующей строки.
            _activeVarDefTypeFullName ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _activeVarDefTypeFullName[key] = fq;
        }

        public void UnregisterPersistentVarDefType(string varName)
        {
            if (string.IsNullOrWhiteSpace(varName))
                return;
            var key = varName.Trim();
            _crossLineVarDefTypeFullName?.Remove(key);
            _activeVarDefTypeFullName?.Remove(key);
        }

        /// <summary>Start or reset cross-line variable state for the next sequence of lowered lines (prelude, entrypoint, a procedure body, one function, …).</summary>
        public void BeginStatementSequence()
        {
            _crossLineVarState ??= new Dictionary<string, (string Kind, int Index)>(StringComparer.OrdinalIgnoreCase);
            _crossLineVarState.Clear();
            _crossLineVarDefTypeFullName ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _crossLineVarDefTypeFullName.Clear();
        }

        /// <summary>Copies all global slots from <paramref name="source"/> into this compiler so that
        /// global variables declared in the entrypoint/prelude are accessible inside procedure bodies.</summary>
        public void InheritGlobalSlots(StatementLoweringCompiler source)
        {
            foreach (var kv in source._globalSlots)
                _globalSlots[kv.Key] = kv.Value;
            // Advance the slot counter past any slots already allocated by the source compiler
            // so local slots don't overlap with inherited global ones.
            if (source._nextGlobalSlot > _nextGlobalSlot)
                _nextGlobalSlot = source._nextGlobalSlot;
            _defaultTypeNamespacePrefix = source._defaultTypeNamespacePrefix;
            _locallyDeclaredTypeNames = source._locallyDeclaredTypeNames;
            _externalTypeQualification = source._externalTypeQualification;
            _typeResolutionIndex = source._typeResolutionIndex;
            _declaredTypeFieldSpecs = source._declaredTypeFieldSpecs;
        }

        /// <summary>Mangled procedure name for a method экземпляра def-типа, как в <see cref="StatementLoweringCompiler.Types"/>.</summary>
        private static string BuildDefTypeMethodProcedureFullName(string qualifiedDefTypeFullName, string methodShortName, int overloadIndex)
        {
            var parsed = DefName.ParseQualified((qualifiedDefTypeFullName ?? "").Trim());
            var typeShort = !string.IsNullOrEmpty(parsed.Name) ? parsed.Name : (qualifiedDefTypeFullName ?? "").Trim();
            var seg = (methodShortName ?? "").Trim();
            return new DefName(parsed.Namespace, $"{typeShort}_{seg}_{overloadIndex}").FullName;
        }

        private void RememberMemoryVarDefType(string varName, string qualifiedDefTypeFullName)
        {
            if (_activeVarDefTypeFullName == null || string.IsNullOrWhiteSpace(varName) || string.IsNullOrWhiteSpace(qualifiedDefTypeFullName))
                return;
            _activeVarDefTypeFullName[varName.Trim()] = qualifiedDefTypeFullName.Trim();
        }

        /// <summary>
        /// Цепочка вида <c>world.Board</c> без вызовов: выводит FQ def-типа значения по таблице полей из объявлений типов.
        /// </summary>
        private bool TryInferQualifiedDefTypeFromSimpleMemberRhs(
            string rhsText,
            Dictionary<string, (string Kind, int Index)> vars,
            out string qualifiedFq)
        {
            qualifiedFq = "";
            var t = (rhsText ?? "").Trim();
            if (t.EndsWith(";", StringComparison.Ordinal))
                t = t.Substring(0, t.Length - 1).Trim();
            if (t.Length == 0)
                return false;
            if (t.IndexOf('(') >= 0 || t.IndexOf('[') >= 0)
                return false;

            var parts = t.Split('.');
            if (parts.Length < 2)
                return false;
            foreach (var p in parts)
            {
                if (string.IsNullOrWhiteSpace(p))
                    return false;
            }

            var root = parts[0].Trim();
            if (!vars.TryGetValue(root, out var rv))
                return false;
            if (rv.Kind != "memory" && rv.Kind != "global")
                return false;
            if (_activeVarDefTypeFullName == null ||
                !_activeVarDefTypeFullName.TryGetValue(root, out var currentFq) ||
                string.IsNullOrWhiteSpace(currentFq))
                return false;

            if (_declaredTypeFieldSpecs == null)
                return false;

            for (var pi = 1; pi < parts.Length; pi++)
            {
                var field = parts[pi].Trim();
                if (!_declaredTypeFieldSpecs.TryGetValue(currentFq, out var byField) ||
                    !byField.TryGetValue(field, out var spec) ||
                    string.IsNullOrWhiteSpace(spec))
                    return false;

                var elem = spec.Trim();
                if (TryStripSimpleArraySuffix(elem, out var innerElem))
                    elem = innerElem;

                if (!IsSimpleTypeIdentifier(elem) || IsBuiltinFieldTypeName(elem))
                    return false;

                currentFq = QualifyTypeNameForDefObj(elem);
            }

            qualifiedFq = currentFq;
            return true;
        }

        /// <summary>
        /// Имя операнда <c>callobj</c>: полное манглированное имя из таблицы перегрузок или <c>Type_Method_1</c>, если известен def-тип приёмника; иначе короткое.
        /// </summary>
        private string ResolveCallObjProcedureNameForInstanceMethod(
            string receiverVarName,
            int pathSegmentCount,
            string receiverVarKind,
            string methodShortName,
            IReadOnlyList<string> argExpressionTexts)
        {
            if (pathSegmentCount != 0 ||
                !(string.Equals(receiverVarKind, "memory", StringComparison.Ordinal) ||
                  string.Equals(receiverVarKind, "global", StringComparison.Ordinal)))
                return methodShortName;
            if (_activeVarDefTypeFullName == null ||
                !_activeVarDefTypeFullName.TryGetValue(receiverVarName, out var receiverFq) ||
                string.IsNullOrWhiteSpace(receiverFq))
                return methodShortName;

            string? arg0Fq = null;
            if (argExpressionTexts.Count > 0)
            {
                var a0 = (argExpressionTexts[0] ?? "").Trim();
                if (_activeVarDefTypeFullName.TryGetValue(a0, out var t0) && !string.IsNullOrWhiteSpace(t0))
                    arg0Fq = t0;
                else
                    arg0Fq = TryInferDefTypeFqFromCtorLeadingType(a0);
            }

            if (_instanceMethodOverloads != null &&
                _instanceMethodOverloads.TryGetValue(receiverFq, out var byMethod) &&
                byMethod.TryGetValue(methodShortName, out var overloads) &&
                overloads.Count > 0)
            {
                if (overloads.Count == 1)
                    return overloads[0].MangledProcedure;

                if (!string.IsNullOrWhiteSpace(arg0Fq))
                {
                    foreach (var (mangled, p0short) in overloads)
                    {
                        if (string.IsNullOrWhiteSpace(p0short))
                            continue;
                        var p0fq = QualifyTypeNameForDefObj(p0short.Trim());
                        if (string.Equals(p0fq, arg0Fq, StringComparison.OrdinalIgnoreCase))
                            return mangled;
                    }
                }

                return overloads[0].MangledProcedure;
            }

            return BuildDefTypeMethodProcedureFullName(receiverFq, methodShortName, overloadIndex: 1);
        }

        /// <summary>Для аргумента вида <c>Circle(...)</c> возвращает квалифицированное имя типа, если это известный тип.</summary>
        private string? TryInferDefTypeFqFromCtorLeadingType(string argText)
        {
            var t = (argText ?? "").Trim();
            var m = Regex.Match(t, @"^([A-Za-z_][\w]*)\s*\(");
            if (!m.Success)
                return null;
            var shortName = m.Groups[1].Value;
            if (string.Equals(shortName, "new", StringComparison.OrdinalIgnoreCase))
                return null;
            return QualifyTypeNameForDefObj(shortName);
        }

        public List<InstructionNode> Lower(IEnumerable<string> sourceLines, bool registerGlobals = false, int statementStartSourceLine = 0)
        {
            var vars = new Dictionary<string, (string Kind, int Index)>(StringComparer.OrdinalIgnoreCase);
            foreach (var global in _globalSlots)
                vars[global.Key] = ("global", global.Value);
            if (_crossLineVarState != null)
            {
                foreach (var kv in _crossLineVarState)
                    vars[kv.Key] = kv.Value;
            }

            _activeVarDefTypeFullName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_crossLineVarDefTypeFullName != null)
            {
                foreach (var kv in _crossLineVarDefTypeFullName)
                    _activeVarDefTypeFullName[kv.Key] = kv.Value;
            }
            // Local slots (procedure parameters allocated via AllocateLocalSlot) use "memory" kind
            // so that instructions use push [N] (local memory) instead of push global: [N].
            foreach (var local in _localSlots)
                vars[local.Key] = ("memory", local.Value);
            var vertexCounter = 1;
            var relationCounter = 1;
            var shapeCounter = 1;
            var memorySlotCounter = _nextGlobalSlot;
            var streamLoopCounter = 0;
            var instructions = CompileStatementLines(sourceLines, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter, statementStartSourceLine);

            if (memorySlotCounter > _nextGlobalSlot)
                _nextGlobalSlot = memorySlotCounter;

            if (registerGlobals)
            {
                foreach (var kv in vars)
                {
                    if (kv.Value.Kind == "memory" || kv.Value.Kind == "global")
                        _globalSlots[kv.Key] = kv.Value.Index;
                }
            }

            if (_crossLineVarState != null)
            {
                foreach (var kv in vars)
                    _crossLineVarState[kv.Key] = kv.Value;
            }

            if (_crossLineVarDefTypeFullName != null && _activeVarDefTypeFullName != null)
            {
                foreach (var kv in _activeVarDefTypeFullName)
                    _crossLineVarDefTypeFullName[kv.Key] = kv.Value;
            }

            _activeVarDefTypeFullName = null;

            return instructions;
        }

        private int AllocateAnonymousLocalTemp(ref int memorySlotCounter)
        {
            var name = $"__tmp_{_nextLocalSlot}";
            var physical = AllocateLocalSlot(name);
            // AllocateLocalSlot уже продвинул _nextGlobalSlot и _nextLocalSlot.
            // Для согласованности с внешним счётчиком памяти синхронизируем memorySlotCounter.
            if (physical >= memorySlotCounter)
                memorySlotCounter = physical + 1;
            return physical;
        }

        /// <summary>
        /// Понижает присваивание полей внутри методов типов:
        /// X := x; / Y := y; в конструкторах Point-объектов.
        /// </summary>
        public bool TryLowerFieldAssign(
            string rawLine,
            IReadOnlyDictionary<string, string> instanceFieldSpecs,
            Assembler assembler,
            ExecutionBlock body)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                return false;

            var idx = line.IndexOf(":=", StringComparison.Ordinal);
            if (idx <= 0)
                return false;

            var left = line.Substring(0, idx).Trim();
            if (!instanceFieldSpecs.ContainsKey(left))
                return false;

            var rhs = line.Substring(idx + 2).Trim().TrimEnd(';').Trim();
            if (rhs.Length == 0)
                return false;

            // Пока поддерживаем только простой случай: RHS — имя параметра/локальной переменной,
            // без сложных выражений. Для ctor Point(x,y): "X:= x;" / "Y:= y;".
            if (!TryResolveLocalSlot(rhs, out var rhsPhysical, out var rhsLogical))
                return false;
            if (!TryResolveLocalSlot("_this", out var thisPhysical, out var thisLogical))
                return false;

            // push this
            body.Add(assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = thisPhysical,
                        LogicalIndex = thisLogical
                    }
                }));

            // push field name (string: "X" / "Y")
            body.Add(assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.StringParameterNode
                    {
                        Name = "string",
                        Value = left
                    }
                }));

            // push rhs (значение параметра/локала)
            body.Add(assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = rhsPhysical,
                        LogicalIndex = rhsLogical
                    }
                }));

            // setobj (Operand1 можно оставить для красоты AGIASM, но рантайм его не использует)
            var set = assembler.Emit(Opcodes.SetObj, null);
            body.Add(set);
            body.Add(assembler.Emit(Opcodes.Pop, null));

            return true;
        }

        /// <summary>
        /// Понижает сложение полей типа: X += p.X; / Y += p.Y; в методах типов.
        /// </summary>
        public bool TryLowerFieldPlusAssign(
            string rawLine,
            IReadOnlyDictionary<string, string> instanceFieldSpecs,
            Assembler assembler,
            ExecutionBlock body)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                return false;

            // Ищем "X += p.X;" / "Y += p.Y;"
            var idx = line.IndexOf("+=", StringComparison.Ordinal);
            if (idx <= 0)
                return false;

            var left = line.Substring(0, idx).Trim();  // "X"
            if (!instanceFieldSpecs.ContainsKey(left))
                return false;

            var rhs = line.Substring(idx + 2).Trim().TrimEnd(';').Trim(); // "p.X"
            if (!TryResolveLocalSlot("_this", out var thisPhys, out var thisLog))
                return false;

            // RHS вида "<var>.<field>"
            var dot = rhs.IndexOf('.');
            if (dot <= 0)
                return false;
            var rhsVar = rhs.Substring(0, dot).Trim();           // "p"
            var rhsField = rhs.Substring(dot + 1).Trim();        // "X"

            if (!TryResolveLocalSlot(rhsVar, out var rhsPhys, out var rhsLog))
                return false;

            // this.<left> + rhsVar.<rhsField>:

            // push this
            body.Add(assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = thisPhys,
                        LogicalIndex = thisLog
                    }
                }));
            body.Add(assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.StringParameterNode
                    {
                        Name = "string",
                        Value = left      // "X" или "Y"
                    }
                }));
            var getThisField = assembler.Emit(Opcodes.GetObj, null);
            body.Add(getThisField);

            // p.<rhsField>
            body.Add(assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = rhsPhys,
                        LogicalIndex = rhsLog
                    }
                }));
            body.Add(assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.StringParameterNode
                    {
                        Name = "string",
                        Value = rhsField   // тоже "X" или "Y"
                    }
                }));
            var getOtherField = assembler.Emit(Opcodes.GetObj, null);
            body.Add(getOtherField);

            // add
            body.Add(assembler.Emit(Opcodes.Add, null));

            // tmp слот под результат
            var tmpName = $"__tmp_{left}";
            if (!TryResolveLocalSlot(tmpName, out var tmpPhys, out var tmpLog))
            {
                tmpPhys = AllocateLocalSlot(tmpName);
                TryResolveLocalSlot(tmpName, out tmpPhys, out tmpLog);
            }

            // pop tmp
            body.Add(assembler.Emit(Opcodes.Pop,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = tmpPhys,
                        LogicalIndex = tmpLog
                    }
                }));

            // push this
            body.Add(assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = thisPhys,
                        LogicalIndex = thisLog
                    }
                }));

            // push field name
            body.Add(assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.StringParameterNode
                    {
                        Name = "string",
                        Value = left
                    }
                }));

            // push tmp
            body.Add(assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = tmpPhys,
                        LogicalIndex = tmpLog
                    }
                }));

            // setobj "X"/"Y"
            var set = assembler.Emit(Opcodes.SetObj, null);
            body.Add(set);
            body.Add(assembler.Emit(Opcodes.Pop, null));

            return true;
        }

        /// <summary>
        /// Понижает простой "+=" для локальных переменных (включая поля, поднятые в локалы методом):
        /// left += right;
        /// -> push [left]; push [right]; add; pop [left];
        /// Для полей типа <c>T[]</c> (рантайм-список DefList) — <c>callobj add</c> вместо арифметического <c>add</c>.
        /// </summary>
        public bool TryLowerLocalPlusAssign(
            string rawLine,
            Assembler assembler,
            ExecutionBlock body,
            IReadOnlyDictionary<string, string>? instanceFieldTypeSpecs = null)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                return false;

            var opIdx = line.IndexOf("+=", StringComparison.Ordinal);
            if (opIdx <= 0)
                return false;

            var left = line.Substring(0, opIdx).Trim();
            var right = line.Substring(opIdx + 2).Trim();
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            if (right.EndsWith(";", StringComparison.Ordinal))
                right = right.Substring(0, right.Length - 1).Trim();

            // Не лезем в сложные выражения/доступы через точку.
            if (left.Contains('.') || right.Contains('.'))
                return false;

            // Оба должны быть локалами в текущем методе.
            if (!TryResolveLocalSlot(left, out var leftPhys, out var leftLog))
                return false;
            if (!TryResolveLocalSlot(right, out var rightPhys, out var rightLog))
                return false;

            var useListAppend = instanceFieldTypeSpecs != null &&
                                instanceFieldTypeSpecs.TryGetValue(left, out var leftTypeSpec) &&
                                TypeSpecLooksLikeElementCollection(leftTypeSpec);

            // push [left]
            body.Add(assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = leftPhys,
                        LogicalIndex = leftLog
                    }
                }));

            // push [right]
            body.Add(assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = rightPhys,
                        LogicalIndex = rightLog
                    }
                }));

            if (useListAppend)
            {
                body.Add(assembler.EmitPushIntLiteral(1));
                body.Add(assembler.EmitCallObj(new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = "add" }
                }));
            }
            else
                body.Add(assembler.Emit(Opcodes.Add, null));

            // pop [left]
            body.Add(assembler.Emit(Opcodes.Pop,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = leftPhys,
                        LogicalIndex = leftLog
                    }
                }));

            return true;
        }

        /// <summary>AGI тип поля вида <c>Shape[]</c> / <c>T[]</c> компилируется в DefList; для <c>+=</c> нужен <c>callobj</c>, не opcode <c>add</c>.</summary>
        internal static bool TypeSpecLooksLikeElementCollection(string? typeSpec)
        {
            var t = (typeSpec ?? "").Trim();
            return t.Length > 0 && t.EndsWith("]", StringComparison.Ordinal);
        }

        /// <summary>
        /// Понижает вызов базового конструктора в конструкторе дочернего типа:
        /// Shape(origin);  ->  push this; push origin; push 1; call {nsPrefix}Shape_ctor_1; pop
        /// </summary>
        public bool TryLowerBaseCtorCall(
            string rawLine,
            Assembler assembler,
            ExecutionBlock body,
            string nsPrefix)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                return false;

            // Ожидаем простой вызов вида BaseType(args...);
            if (!line.EndsWith(";", StringComparison.Ordinal))
                return false;
            line = line.Substring(0, line.Length - 1).Trim();
            var parenIdx = line.IndexOf('(');
            var lastParenIdx = line.LastIndexOf(')');
            if (parenIdx <= 0 || lastParenIdx <= parenIdx)
                return false;

            var baseName = line.Substring(0, parenIdx).Trim();
            if (string.IsNullOrEmpty(baseName))
                return false;

            var argsText = line.Substring(parenIdx + 1, lastParenIdx - parenIdx - 1).Trim();
            var args = new List<string>();
            if (!string.IsNullOrEmpty(argsText))
            {
                var depth = 0;
                var sb = new StringBuilder();
                foreach (var ch in argsText)
                {
                    if (ch == ',' && depth == 0)
                    {
                        var arg = sb.ToString().Trim();
                        if (!string.IsNullOrEmpty(arg))
                            args.Add(arg);
                        sb.Clear();
                        continue;
                    }
                    if (ch is '(' or '{' or '[') depth++;
                    if (ch is ')' or '}' or ']') depth--;
                    sb.Append(ch);
                }
                var last = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(last))
                    args.Add(last);
            }

            // Нужен _this
            if (!TryResolveLocalSlot("_this", out var thisPhys, out var thisLog))
                return false;

            // push this
            body.Add(assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = thisPhys,
                        LogicalIndex = thisLog
                    }
                }));

            // push args (простые имена локальных переменных или литералы)
            foreach (var argExpr in args)
            {
                var ae = argExpr.Trim();
                if (ae.Length == 0)
                    continue;

                // локальная переменная / параметр
                if (TryResolveLocalSlot(ae, out var phys, out var log))
                {
                    body.Add(assembler.Emit(Opcodes.Push,
                        new List<ParameterNode>
                        {
                            new Ast.MemoryParameterNode
                            {
                                Name = "index",
                                Index = phys,
                                LogicalIndex = log
                            }
                        }));
                    continue;
                }

                // целочисленный литерал
                if (int.TryParse(ae, out var intVal))
                {
                    body.Add(assembler.Emit(Opcodes.Push,
                        new List<ParameterNode>
                        {
                            new Ast.IndexParameterNode { Name = "int", Value = intVal }
                        }));
                    continue;
                }

                // строковый литерал "..."
                if (ae.Length >= 2 && ae[0] == '"' && ae[^1] == '"')
                {
                    var s = ae.Substring(1, ae.Length - 2);
                    body.Add(assembler.Emit(Opcodes.Push,
                        new List<ParameterNode>
                        {
                            new Ast.StringParameterNode { Name = "string", Value = s }
                        }));
                    continue;
                }

                // Сложные выражения пока не поддерживаем
                return false;
            }

            // arity = args (this не входит в arity для callobj)
            body.Add(assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.IndexParameterNode { Name = "int", Value = args.Count }
                }));

            // Вызов базового конструктора: callobj "{nsPrefix}Base_ctor_1" (nsPrefix вида "sys:mod:prog:")
            var methodName = string.IsNullOrEmpty(nsPrefix)
                ? $"{baseName}_ctor_1"
                : $"{nsPrefix}{baseName}_ctor_1";
            var callCmd = assembler.Emit(Opcodes.CallObj, null);
            callCmd.Operand1 = methodName;
            body.Add(callCmd);

            // pop (результат конструктора, если есть)
            body.Add(assembler.Emit(Opcodes.Pop, null));

            return true;
        }

        /// <summary>
        /// Понижает вызов базового метода в методе дочернего типа:
        /// Shape.Draw(); -> push this; push 0; call {nsPrefix}Shape_Draw; pop
        /// </summary>
        public bool TryLowerBaseMethodCall(
            string rawLine,
            Assembler assembler,
            ExecutionBlock body,
            string nsPrefix)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                return false;

            if (!line.EndsWith(";", StringComparison.Ordinal))
                return false;
            line = line.Substring(0, line.Length - 1).Trim();

            // Ожидаем "BaseType.Method(args...)"
            var dotIdx = line.IndexOf('.');
            if (dotIdx <= 0)
                return false;

            var baseType = line.Substring(0, dotIdx).Trim();
            var rest = line.Substring(dotIdx + 1).Trim();

            var parenIdx = rest.IndexOf('(');
            var lastParenIdx = rest.LastIndexOf(')');
            if (parenIdx <= 0 || lastParenIdx <= parenIdx)
                return false;

            var methodName = rest.Substring(0, parenIdx).Trim();
            var argsText = rest.Substring(parenIdx + 1, lastParenIdx - parenIdx - 1).Trim();

            // Нужен _this
            if (!TryResolveLocalSlot("_this", out var thisPhys, out var thisLog))
                return false;

            // Разбор аргументов (простые имена/литералы)
            var args = new List<string>();
            if (!string.IsNullOrEmpty(argsText))
            {
                var depth = 0;
                var sb = new StringBuilder();
                foreach (var ch in argsText)
                {
                    if (ch == ',' && depth == 0)
                    {
                        var arg = sb.ToString().Trim();
                        if (!string.IsNullOrEmpty(arg))
                            args.Add(arg);
                        sb.Clear();
                        continue;
                    }
                    if (ch is '(' or '{' or '[') depth++;
                    if (ch is ')' or '}' or ']') depth--;
                    sb.Append(ch);
                }
                var last = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(last))
                    args.Add(last);
            }

            // push this
            body.Add(assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = thisPhys,
                        LogicalIndex = thisLog
                    }
                }));

            // push args
            foreach (var argExpr in args)
            {
                var ae = argExpr.Trim();
                if (ae.Length == 0)
                    continue;

                if (TryResolveLocalSlot(ae, out var phys, out var log))
                {
                    body.Add(assembler.Emit(Opcodes.Push,
                        new List<ParameterNode>
                        {
                            new Ast.MemoryParameterNode
                            {
                                Name = "index",
                                Index = phys,
                                LogicalIndex = log
                            }
                        }));
                    continue;
                }

                if (int.TryParse(ae, out var intVal))
                {
                    body.Add(assembler.Emit(Opcodes.Push,
                        new List<ParameterNode>
                        {
                            new Ast.IndexParameterNode { Name = "int", Value = intVal }
                        }));
                    continue;
                }

                if (ae.Length >= 2 && ae[0] == '"' && ae[^1] == '"')
                {
                    var s = ae.Substring(1, ae.Length - 2);
                    body.Add(assembler.Emit(Opcodes.Push,
                        new List<ParameterNode>
                        {
                            new Ast.StringParameterNode { Name = "string", Value = s }
                        }));
                    continue;
                }

                return false;
            }

            // arity = args (this не входит в arity для callobj)
            body.Add(assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.IndexParameterNode { Name = "int", Value = args.Count }
                }));

            var objMethodName = string.IsNullOrEmpty(nsPrefix)
                ? $"{baseType}_{methodName}"
                : $"{nsPrefix}{baseType}_{methodName}";
            var callObjCmd = assembler.Emit(Opcodes.CallObj, null);
            callObjCmd.Operand1 = objMethodName;
            body.Add(callObjCmd);

            // pop возможного возвращаемого значения
            body.Add(assembler.Emit(Opcodes.Pop, null));

            return true;
        }

        internal static IEnumerable<string> CoalesceMultilineStatements(IEnumerable<string> sourceLines)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            var collecting = false;
            var parenDepth = 0;
            var braceDepth = 0;
            var bracketDepth = 0;
            var inString = false;
            var escape = false;

            foreach (var raw in sourceLines)
            {
                var line = raw ?? "";
                var trimmed = line.Trim();

                if (!collecting)
                {
                    if (trimmed.Length == 0)
                    {
                        result.Add(line);
                        continue;
                    }

                    var (parenDelta, braceDelta, bracketDelta) = ComputeDepthDelta(trimmed, ref inString, ref escape);
                    parenDepth = Math.Max(0, parenDelta);
                    braceDepth = Math.Max(0, braceDelta);
                    bracketDepth = Math.Max(0, bracketDelta);
                    if (parenDepth > 0 || braceDepth > 0 || bracketDepth > 0)
                    {
                        collecting = true;
                        sb.Clear();
                        sb.Append(trimmed);
                        continue;
                    }

                    result.Add(line);
                    continue;
                }

                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(trimmed);

                var (parenDeltaAcc, braceDeltaAcc, bracketDeltaAcc) = ComputeDepthDelta(trimmed, ref inString, ref escape);
                parenDepth = Math.Max(0, parenDepth + parenDeltaAcc);
                braceDepth = Math.Max(0, braceDepth + braceDeltaAcc);
                bracketDepth = Math.Max(0, bracketDepth + bracketDeltaAcc);
                if (parenDepth <= 0 && braceDepth <= 0 && bracketDepth <= 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                    collecting = false;
                    parenDepth = 0;
                    braceDepth = 0;
                    bracketDepth = 0;
                }
            }

            if (collecting && sb.Length > 0)
                result.Add(sb.ToString());

            return result;
        }

        private static (int ParenDelta, int BraceDelta, int BracketDelta) ComputeDepthDelta(string text, ref bool inString, ref bool escape)
        {
            var parenDelta = 0;
            var braceDelta = 0;
            var bracketDelta = 0;

            foreach (var ch in text)
            {
                if (inString)
                {
                    if (escape)
                    {
                        escape = false;
                        continue;
                    }

                    if (ch == '\\')
                    {
                        escape = true;
                        continue;
                    }

                    if (ch == '"')
                        inString = false;

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    continue;
                }

                if (ch == '(') parenDelta++;
                else if (ch == ')') parenDelta--;
                else if (ch == '{') braceDelta++;
                else if (ch == '}') braceDelta--;
                else if (ch == '[') bracketDelta++;
                else if (ch == ']') bracketDelta--;
            }

            return (parenDelta, braceDelta, bracketDelta);
        }

        private sealed class VertexInitSpec
        {
            public List<long> Dimensions { get; } = new List<long> { 1, 0, 0, 0 };
            public double Weight { get; set; } = 0.5d;
            public string? TextData { get; set; }
            public string? BinaryData { get; set; }
        }

        private sealed class RelationInitSpec
        {
            public string? From { get; set; }
            public string? To { get; set; }
            public double Weight { get; set; } = 0.5d;
        }

        private sealed class InlineVertexSpec
        {
            public List<long> Dimensions { get; } = new List<long> { 1, 0, 0, 0 };
            public double? Weight { get; set; }
        }

        private sealed class ShapeInitSpec
        {
            public List<string> VertexNames { get; } = new List<string>();
            public List<InlineVertexSpec> InlineVertices { get; } = new List<InlineVertexSpec>();
            public bool UsesInlineVertices => InlineVertices.Count > 0;
        }

        private List<InstructionNode> CompileVarBlock(List<string> varLines, Dictionary<string, (string Kind, int Index)> vars, ref int vertexCounter, ref int relationCounter, ref int shapeCounter, ref int memorySlotCounter)
        {
            var instructions = new List<InstructionNode>();

            foreach (var line in varLines)
            {
                if (TryParseVaultDeclaration(line, out var vaultName))
                {
                    EmitVaultDeclaration(vaultName, vars, ref memorySlotCounter, instructions);
                    continue;
                }

                if (TryParseStreamDeclaration(line, out var streamName, out var elementTypes))
                {
                    EmitStreamDeclaration(streamName, elementTypes, vars, ref memorySlotCounter, instructions);
                    continue;
                }

                if (!TryParseEntityDeclaration(line, out var varName, out var varType, out var initText))
                {
                    // Fallback: untyped var inside var-block, e.g. "var p := new Point(1, 2);"
                    // Delegate to generic assignment lowering so ctor-sugar and other cases work.
                    if (TryCompileAssignment(line, vars, ref shapeCounter, ref vertexCounter, ref memorySlotCounter, instructions))
                        continue;
                    continue;
                }

                if (varType == "vertex")
                {
                    var index = vertexCounter++;
                    vars[varName] = ("vertex", index);
                    instructions.AddRange(CompileVertexInit(index, initText));
                }
                else if (varType == "relation")
                {
                    var index = relationCounter++;
                    vars[varName] = ("relation", index);
                    instructions.AddRange(CompileRelationInit(index, initText, vars));
                }
                else if (varType == "shape")
                {
                    var index = shapeCounter++;
                    vars[varName] = ("shape", index);
                    instructions.AddRange(CompileShapeInit(index, initText, vars, ref vertexCounter));
                }
                else
                {
                    // Fallback: var <name> := <initText> без специального типа (ctor-sugar и обычное присваивание).
                    // Ожидаем initText вроде "new Point(1, 2)".
                    if (!string.IsNullOrWhiteSpace(initText)
                        && TryCompileConstructorCall(varName, initText, vars, ref memorySlotCounter, instructions))
                    {
                        // ctor-sugar обработан, переходим к следующей строке var-блока
                        continue;
                    }

                    // Иначе — обычное var <name> := expr; через общий Assignment lowering.
                    // Здесь используем уже существующий CompileAssignment, но в var-блоке.
                    var assignmentLine = $"{varName} := {initText};";
                    if (TryCompileAssignment(assignmentLine, vars, ref shapeCounter, ref vertexCounter, ref memorySlotCounter, instructions))
                        continue;
                }
            }

            return instructions;
        }

        private List<InstructionNode> CompileVertexInit(int index, string initValue)
        {
            var instructions = new List<InstructionNode>();
            if (!TryParseVertexObject(initValue, out var spec))
                return instructions;

            var parameters = new List<ParameterNode>
            {
                new IndexParameterNode { Name = "index", Value = index },
                new DimensionsParameterNode { Name = "dimensions", Values = spec.Dimensions.Select(v => (float)v).ToList() },
                new WeightParameterNode { Name = "weight", Value = (float)spec.Weight }
            };

            if (!string.IsNullOrEmpty(spec.BinaryData))
            {
                parameters.Add(new DataParameterNode
                {
                    Name = "data",
                    Type = "binary:base64",
                    Types = new List<string> { "binary", "base64" },
                    Value = spec.BinaryData,
                    HasColon = true
                });
            }
            else if (!string.IsNullOrEmpty(spec.TextData))
            {
                parameters.Add(new DataParameterNode
                {
                    Name = "data",
                    Type = "text",
                    Types = new List<string> { "text" },
                    Value = spec.TextData,
                    HasColon = true
                });
            }

            instructions.Add(new InstructionNode { Opcode = "addvertex", Parameters = parameters });
            return instructions;
        }

        private List<InstructionNode> CompileRelationInit(int index, string initValue, Dictionary<string, (string Kind, int Index)> vars)
        {
            var instructions = new List<InstructionNode>();
            if (!TryParseRelationObject(initValue, out var spec) || string.IsNullOrEmpty(spec.From) || string.IsNullOrEmpty(spec.To))
                return instructions;

            if (!vars.TryGetValue(spec.From, out var fromVar))
                throw new UndeclaredVariableException(spec.From);
            if (!vars.TryGetValue(spec.To, out var toVar))
                throw new UndeclaredVariableException(spec.To);

            var fromType = fromVar.Kind == "relation" ? "relation" : "vertex";
            var toType = toVar.Kind == "relation" ? "relation" : "vertex";

            instructions.Add(new InstructionNode
            {
                Opcode = "addrelation",
                Parameters = new List<ParameterNode>
                {
                    new IndexParameterNode { Name = "index", Value = index },
                    new FromParameterNode { Name = "from", EntityType = fromType, Index = fromVar.Index },
                    new ToParameterNode { Name = "to", EntityType = toType, Index = toVar.Index },
                    new WeightParameterNode { Name = "weight", Value = (float)spec.Weight }
                }
            });
            return instructions;
        }

        private List<InstructionNode> CompileShapeInit(int index, string initValue, Dictionary<string, (string Kind, int Index)> vars, ref int vertexCounter)
        {
            var instructions = new List<InstructionNode>();
            if (!TryParseShapeObject(initValue, out var spec))
                return instructions;

            var indices = new List<long>();
            if (spec.UsesInlineVertices)
            {
                foreach (var vertex in spec.InlineVertices)
                {
                    var tempIndex = vertexCounter++;
                    var parameters = new List<ParameterNode>
                    {
                        new IndexParameterNode { Name = "index", Value = tempIndex },
                        new DimensionsParameterNode { Name = "dimensions", Values = vertex.Dimensions.Select(v => (float)v).ToList() }
                    };
                    if (vertex.Weight.HasValue)
                        parameters.Add(new WeightParameterNode { Name = "weight", Value = (float)vertex.Weight.Value });
                    instructions.Add(new InstructionNode { Opcode = "addvertex", Parameters = parameters });
                    indices.Add(tempIndex);
                }
            }
            else
            {
                foreach (var name in spec.VertexNames)
                {
                    if (vars.TryGetValue(name, out var vertexVar) && vertexVar.Kind == "vertex")
                        indices.Add(vertexVar.Index);
                }
            }

            if (indices.Count > 0)
            {
                instructions.Add(new InstructionNode
                {
                    Opcode = "addshape",
                    Parameters = new List<ParameterNode>
                    {
                        new IndexParameterNode { Name = "index", Value = index },
                        new VerticesParameterNode { Name = "vertices", Indices = indices }
                    }
                });
            }

            return instructions;
        }

        private static bool TryParseTypedCastPrefix(string expr, out string outerTypeLiteral, out string innerTypeName, out string innerExpr)
        {
            outerTypeLiteral = innerTypeName = innerExpr = "";
            var s = expr.Trim();
            if (s.Length == 0)
                return false;
            var scanner = new Scanner(s);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            var outer = scanner.Scan().Value;
            if (scanner.Current.Kind != TokenKind.LessThan)
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            var inner = scanner.Scan().Value;
            if (scanner.Current.Kind != TokenKind.GreaterThan)
                return false;
            scanner.Scan();
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Colon)
                return false;
            scanner.Scan();
            var restStart = scanner.Current.Start;
            innerExpr = restStart < s.Length ? s.Substring(restStart).Trim() : "";
            if (string.IsNullOrWhiteSpace(innerExpr))
                return false;
            outerTypeLiteral = $"{outer.ToLowerInvariant()}<{inner.ToLowerInvariant()}>";
            innerTypeName = inner.ToLowerInvariant();
            return true;
        }


        private static string TrimBalancedOuterParentheses(string text)
        {
            var trimmed = text?.Trim() ?? "";
            while (trimmed.Length >= 2 && trimmed[0] == '(' && trimmed[^1] == ')' && IsWrappedBySingleBalancedParentheses(trimmed))
                trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
            return trimmed;
        }

        private static bool IsWrappedBySingleBalancedParentheses(string text)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;
            var quote = '\0';

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (inString)
                {
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

                    if (ch == quote)
                        inString = false;

                    continue;
                }

                if (ch == '"' || ch == '\'')
                {
                    inString = true;
                    quote = ch;
                    continue;
                }

                if (ch == '(')
                {
                    depth++;
                    continue;
                }

                if (ch != ')')
                    continue;

                depth--;
                if (depth == 0 && i < text.Length - 1)
                    return false;
            }

            return depth == 0;
        }

        private static bool TryParseIfStatement(string line, out string condText, out bool negated, out string bodyText, out string elseText, out bool elseIsBlock)
        {
            condText = "";
            bodyText = "";
            elseText = "";
            negated = false;
            elseIsBlock = false;
            var t = line?.Trim() ?? "";
            if (t.Length < 3 || !t.StartsWith("if", StringComparison.OrdinalIgnoreCase))
                return false;
            var i = 2;
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
            if (i >= t.Length) return false;
            if (t[i] == '!')
            {
                negated = true;
                i++;
                while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
            }
            if (i >= t.Length) return false;

            var condStart = i;
            var parenDepth = 0;
            var inString = false;
            var quote = '\0';
            var braceStart = -1;
            while (i < t.Length)
            {
                var ch = t[i];
                if (inString)
                {
                    if (ch == '\\' && i + 1 < t.Length) { i += 2; continue; }
                    if (ch == quote) { inString = false; i++; continue; }
                    i++;
                    continue;
                }
                if (ch == '"' || ch == '\'') { inString = true; quote = ch; i++; continue; }
                if (ch == '(') { parenDepth++; i++; continue; }
                if (ch == ')') { parenDepth--; i++; continue; }
                if (ch == '{' && parenDepth == 0) { braceStart = i; break; }
                i++;
            }
            // Support braceless single-statement form: "if condition singleStatement;"
            // e.g. "if !auth.isAuthenticated return;"
            if (braceStart < 0)
            {
                var rest = t.Substring(condStart).Trim();
                if (string.IsNullOrEmpty(rest)) return false;

                // Preferred split: explicit parenthesized condition.
                // Example: if (replyOriginal) message.ReplyId := replyOriginal.Id;
                if (rest[0] == '(')
                {
                    var depth = 0;
                    var inStr = false;
                    var strQuote = '\0';
                    var closeIdx = -1;
                    for (var k = 0; k < rest.Length; k++)
                    {
                        var ch = rest[k];
                        if (inStr)
                        {
                            if (ch == '\\' && k + 1 < rest.Length) { k++; continue; }
                            if (ch == strQuote) inStr = false;
                            continue;
                        }

                        if (ch == '"' || ch == '\'') { inStr = true; strQuote = ch; continue; }
                        if (ch == '(') { depth++; continue; }
                        if (ch == ')')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                closeIdx = k;
                                break;
                            }
                        }
                    }

                    if (closeIdx <= 0)
                        return false;

                    condText = rest.Substring(0, closeIdx + 1).Trim();
                    bodyText = rest.Substring(closeIdx + 1).Trim();
                }
                else
                {
                    // Fallback for non-parenthesized form: "if cond statement;"
                    var splitIdx = -1;
                    var scanDepth = 0;
                    var scanInStr = false;
                    var scanQuote = '\0';
                    for (var si = 0; si < rest.Length; si++)
                    {
                        var ch = rest[si];
                        if (scanInStr)
                        {
                            if (ch == '\\' && si + 1 < rest.Length) { si++; continue; }
                            if (ch == scanQuote) scanInStr = false;
                            continue;
                        }

                        if (ch == '"' || ch == '\'') { scanInStr = true; scanQuote = ch; continue; }
                        if (ch == '(') { scanDepth++; continue; }
                        if (ch == ')') { scanDepth--; continue; }
                        if (scanDepth == 0 && char.IsWhiteSpace(ch))
                        {
                            splitIdx = si;
                            break;
                        }
                    }

                    if (splitIdx < 0) return false;
                    condText = rest.Substring(0, splitIdx).Trim();
                    bodyText = rest.Substring(splitIdx).Trim();
                }

                if (string.IsNullOrEmpty(condText) || string.IsNullOrEmpty(bodyText)) return false;
                elseText = "";
                elseIsBlock = false;
                return true;
            }
            condText = t.Substring(condStart, braceStart - condStart).Trim();
            if (string.IsNullOrEmpty(condText)) return false;

            var bodyDepth = 1;
            var j = braceStart + 1;
            while (j < t.Length && bodyDepth > 0)
            {
                var c = t[j];
                if (c == '"' || c == '\'')
                {
                    var q = c;
                    j++;
                    while (j < t.Length && (t[j] != q || (j > 0 && t[j - 1] == '\\'))) j++;
                    if (j < t.Length) j++;
                    continue;
                }
                if (c == '{') { bodyDepth++; j++; continue; }
                if (c == '}') { bodyDepth--; if (bodyDepth == 0) break; j++; continue; }
                j++;
            }
            if (bodyDepth != 0) return false;
            bodyText = t.Substring(braceStart + 1, j - braceStart - 1);

            var tailIndex = j + 1;
            while (tailIndex < t.Length && char.IsWhiteSpace(t[tailIndex]))
                tailIndex++;
            while (tailIndex < t.Length && t[tailIndex] == ';')
            {
                tailIndex++;
                while (tailIndex < t.Length && char.IsWhiteSpace(t[tailIndex]))
                    tailIndex++;
            }

            if (tailIndex >= t.Length)
                return true;

            const string elseKeyword = "else";
            if (tailIndex + elseKeyword.Length > t.Length ||
                !t.AsSpan(tailIndex, elseKeyword.Length).Equals(elseKeyword.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return false;

            var elseValueIndex = tailIndex + elseKeyword.Length;
            if (elseValueIndex < t.Length && !char.IsWhiteSpace(t[elseValueIndex]) && t[elseValueIndex] != '{')
                return false;

            while (elseValueIndex < t.Length && char.IsWhiteSpace(t[elseValueIndex]))
                elseValueIndex++;
            if (elseValueIndex >= t.Length)
                return false;

            if (t[elseValueIndex] == '{')
            {
                var elseBraceStart = elseValueIndex;
                var elseDepth = 1;
                var k = elseBraceStart + 1;
                while (k < t.Length && elseDepth > 0)
                {
                    var c = t[k];
                    if (c == '"' || c == '\'')
                    {
                        var q = c;
                        k++;
                        while (k < t.Length && (t[k] != q || (k > 0 && t[k - 1] == '\\'))) k++;
                        if (k < t.Length) k++;
                        continue;
                    }
                    if (c == '{') { elseDepth++; k++; continue; }
                    if (c == '}') { elseDepth--; if (elseDepth == 0) break; k++; continue; }
                    k++;
                }
                if (elseDepth != 0)
                    return false;

                elseText = t.Substring(elseBraceStart + 1, k - elseBraceStart - 1);
                elseIsBlock = true;

                var tailAfterElse = k + 1;
                while (tailAfterElse < t.Length && char.IsWhiteSpace(t[tailAfterElse]))
                    tailAfterElse++;
                while (tailAfterElse < t.Length && t[tailAfterElse] == ';')
                {
                    tailAfterElse++;
                    while (tailAfterElse < t.Length && char.IsWhiteSpace(t[tailAfterElse]))
                        tailAfterElse++;
                }

                return tailAfterElse >= t.Length;
            }

            elseText = t.Substring(elseValueIndex).Trim();
            return !string.IsNullOrWhiteSpace(elseText);
        }

        private static bool TryParseForStatement(string line, out string initText, out string condText, out string incrText, out string bodyText, out int endExclusiveInTrimmed)
        {
            initText = condText = incrText = bodyText = "";
            endExclusiveInTrimmed = 0;
            var t = line.Trim();
            if (t.Length < 5 || !t.StartsWith("for", StringComparison.OrdinalIgnoreCase))
                return false;
            // Reject "foreach" etc.
            if (t.Length > 3 && char.IsLetterOrDigit(t[3]))
                return false;

            var pos = 3;
            while (pos < t.Length && char.IsWhiteSpace(t[pos])) pos++;
            if (pos >= t.Length || t[pos] != '(')
                return false;
            pos++;

            var parts = new List<string>();
            var partStart = pos;
            var depth = 1;
            var inString = false;
            char quote = '\0';
            for (; pos < t.Length; pos++)
            {
                var ch = t[pos];
                if (inString)
                {
                    if (ch == '\\' && pos + 1 < t.Length) { pos++; continue; }
                    if (ch == quote) inString = false;
                    continue;
                }
                if (ch == '"' || ch == '\'') { inString = true; quote = ch; continue; }
                if (ch == '(') { depth++; continue; }
                if (ch == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        parts.Add(t.Substring(partStart, pos - partStart).Trim());
                        pos++;
                        break;
                    }
                    continue;
                }
                if (ch == ';' && depth == 1)
                {
                    parts.Add(t.Substring(partStart, pos - partStart).Trim());
                    partStart = pos + 1;
                }
            }

            if (parts.Count != 3)
                return false;
            initText = parts[0];
            condText = parts[1];
            incrText = parts[2];

            while (pos < t.Length && char.IsWhiteSpace(t[pos])) pos++;
            if (pos >= t.Length || t[pos] != '{')
                return false;

            var braceStart = pos;
            var bodyDepth = 1;
            var j = braceStart + 1;
            while (j < t.Length && bodyDepth > 0)
            {
                var c = t[j];
                if (c == '"' || c == '\'')
                {
                    var q = c;
                    j++;
                    while (j < t.Length && (t[j] != q || (j > 0 && t[j - 1] == '\\'))) j++;
                    if (j < t.Length) j++;
                    continue;
                }
                if (c == '{') { bodyDepth++; j++; continue; }
                if (c == '}')
                {
                    bodyDepth--;
                    if (bodyDepth == 0) { j++; break; }
                    j++;
                    continue;
                }
                j++;
            }
            if (bodyDepth != 0)
                return false;
            bodyText = t.Substring(braceStart + 1, j - braceStart - 2);

            while (j < t.Length && (char.IsWhiteSpace(t[j]) || t[j] == ';')) j++;
            endExclusiveInTrimmed = j;
            return true;
        }

        private static bool LooksLikeInstanceMethodBareName(string name) =>
            !string.IsNullOrEmpty(name) &&
            char.IsLetter(name[0]) &&
            char.IsUpper(name[0]);

        private static bool TryParsePostIncrement(string text, out string variableName)
        {
            variableName = "";
            text = text.Trim().TrimEnd(';');
            if (text.Length < 3 || !text.EndsWith("++", StringComparison.Ordinal))
                return false;
            var name = text.Substring(0, text.Length - 2).Trim();
            if (string.IsNullOrEmpty(name))
                return false;
            foreach (var c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_'))
                    return false;
            }
            if (!char.IsLetter(name[0]) && name[0] != '_')
                return false;
            variableName = name;
            return true;
        }

        private bool TryCompileFunctionCall(string line, Dictionary<string, (string Kind, int Index)> vars, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var scanner = new Scanner(line);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;

            var functionName = scanner.Scan().Value;
            var isStreamWait = false;

            // "for (...)" is not a call; print-arg parsing would treat "var" in the header as an identifier.
            if (string.Equals(functionName, "for", StringComparison.OrdinalIgnoreCase))
                return false;

            // streamwait <handler> [ (args) ];
            if (string.Equals(functionName, "streamwait", StringComparison.OrdinalIgnoreCase))
            {
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                functionName = scanner.Scan().Value; // handler name, e.g. debug/print
                isStreamWait = true;

                // streamwait <handler>;  (no args)
                if (scanner.Current.Kind != TokenKind.LParen)
                {
                    while (scanner.Current.Kind == TokenKind.Semicolon)
                        scanner.Scan();
                    if (!scanner.Current.IsEndOfInput)
                        return false;

                    instructions.Add(CreatePushStringInstruction(functionName));
                    instructions.Add(CreatePushIntInstruction(0));
                    instructions.Add(new InstructionNode { Opcode = "streamwait" });
                    return true;
                }
            }

            var isPrintln = string.Equals(functionName, "println", StringComparison.OrdinalIgnoreCase);
            var isPrint = string.Equals(functionName, "print", StringComparison.OrdinalIgnoreCase);
            var isPrintd = string.Equals(functionName, "printd", StringComparison.OrdinalIgnoreCase);

            // В теле метода типа: вызов с PascalCase-именем без префикса (Draw(x)) трактуем как _this.Draw(x).
            if (!isPrint && !isPrintln && !isPrintd &&
                !isStreamWait &&
                TryResolveLocalSlot("_this", out _) &&
                TryParseBareCall(line, out var instBareName, out _) &&
                LooksLikeInstanceMethodBareName(instBareName) &&
                (line ?? "").IndexOf('.') < 0)
            {
                var syn = "_this." + (line ?? "").Trim().TrimEnd(';').Trim() + ";";
                if (TryCompileMethodCall(syn, vars, ref memorySlotCounter, instructions))
                    return true;
            }

            // mod1:f(x), mod1:a.b(x), box.inner(x): '(' is not immediately after the first identifier — parse full line first.
            // Не манглировать vault1.read → vault1_read, если vault1 — слот экземпляра (см. ShouldDeferBareCallToInstanceMethod).
            if (!isPrint && !isPrintln && !isPrintd && !isStreamWait &&
                ShouldDeferBareCallToInstanceMethod((line ?? "").Trim(), vars) &&
                TryCompileMethodCall(line, vars, ref memorySlotCounter, instructions))
                return true;

            if (!isPrint && !isPrintln && !isPrintd && TryParseBareCall(line, out var bareName, out var argExprs))
            {
                for (var ai = 0; ai < argExprs.Count; ai++)
                {
                    var arg = argExprs[ai].Trim();
                    if (long.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numVal))
                        instructions.Add(CreatePushIntInstruction(numVal));
                    else if (arg.Length >= 2 && ((arg.StartsWith("\"", StringComparison.Ordinal) && arg.EndsWith("\"", StringComparison.Ordinal)) || (arg.StartsWith("'", StringComparison.Ordinal) && arg.EndsWith("'", StringComparison.Ordinal))))
                        instructions.Add(CreatePushStringInstruction(arg.Substring(1, arg.Length - 2).Replace("\\\"", "\"", StringComparison.Ordinal)));
                    else if (TryParseTypeLiteralArgument(arg, out var typeLiteral))
                        instructions.Add(CreatePushStringInstruction(typeLiteral));
                    else if (vars.TryGetValue(arg, out var argVar) && (argVar.Kind == "memory" || argVar.Kind == "global"))
                        instructions.Add(argVar.Kind == "global" ? CreatePushGlobalMemoryInstruction(argVar.Index) : CreatePushMemoryInstruction(argVar.Index));
                    else
                        return false;
                }

                instructions.Add(CreatePushIntInstruction(argExprs.Count));
                instructions.Add(CreateCallInstruction(ResolveUserCallTarget(bareName)));
                instructions.Add(CreatePopInstruction());
                return true;
            }

            if (scanner.Current.Kind != TokenKind.LParen)
                return false;
            scanner.Scan();

            var printArgs = new List<Action>();
            if (scanner.Current.Kind != TokenKind.RParen)
            {
                while (true)
                {
                    if (scanner.Current.Kind == TokenKind.Colon)
                    {
                        scanner.Scan();
                        if (scanner.Current.Kind != TokenKind.Identifier)
                            return false;
                        var symbolicName = scanner.Scan().Value;
                        printArgs.Add(() =>
                        {
                            instructions.Add(CreatePushStringInstruction(":" + symbolicName));
                            instructions.Add(CreatePushIntInstruction(1));
                            instructions.Add(CreateCallInstruction("get"));
                        });
                    }
                    else if (scanner.Current.Kind == TokenKind.Identifier)
                    {
                        var identifier = scanner.Scan().Value;
                        // Handle format string: #"template {expr}" used as a print argument
                        if (identifier == "#" && scanner.Current.Kind == TokenKind.StringLiteral)
                        {
                            var rawStr = scanner.Scan().Value;
                            var formatLiteral = "#\"" + rawStr + "\"";
                            if (TryParseFormatStringLiteral(formatLiteral, out var fmtTemplate, out var fmtExprs))
                            {
                                var fmtParams = new List<ParameterNode>
                                {
                                    new FunctionNameParameterNode { Name = "function", FunctionName = "format" },
                                    new StringParameterNode { Name = "0", Value = fmtTemplate }
                                };
                                for (var fi = 0; fi < fmtExprs.Count; fi++)
                                {
                                    var argSlot = memorySlotCounter++;
                                    if (!TryCompileExpressionToSlot(fmtExprs[fi], vars, argSlot, ref memorySlotCounter, instructions))
                                        return false;
                                    var capturedSlot = argSlot;
                                    fmtParams.Add(new FunctionParameterNode
                                    {
                                        Name = (fi + 1).ToString(),
                                        ParameterName = (fi + 1).ToString(),
                                        EntityType = "memory",
                                        Index = capturedSlot
                                    });
                                }
                                printArgs.Add(() => instructions.Add(new InstructionNode { Opcode = "call", Parameters = fmtParams }));
                            }
                            else
                            {
                                printArgs.Add(() => instructions.Add(CreatePushStringInstruction(rawStr)));
                            }
                        }
                        else if (vars.TryGetValue(identifier, out var argVar))
                        {
                            if (argVar.Kind == "global")
                                printArgs.Add(() => instructions.Add(CreatePushGlobalMemoryInstruction(argVar.Index)));
                            else
                                printArgs.Add(() => instructions.Add(CreatePushMemoryInstruction(argVar.Index)));
                        }
                        else
                            throw new UndeclaredVariableException(identifier);
                    }
                    else if (scanner.Current.Kind == TokenKind.StringLiteral)
                    {
                        var stringLiteral = scanner.Scan().Value;
                        printArgs.Add(() => instructions.Add(CreatePushStringInstruction(stringLiteral)));
                    }
                    else if (scanner.Current.Kind == TokenKind.Number)
                    {
                        var raw = scanner.Scan().Value;
                        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                            return false;
                        printArgs.Add(() => instructions.Add(CreatePushIntInstruction(number)));
                    }
                    else if (scanner.Current.Kind == TokenKind.Float)
                    {
                        var raw = scanner.Scan().Value;
                        // Push does not support float literal yet; keep user-visible value lossless.
                        printArgs.Add(() => instructions.Add(CreatePushStringInstruction(raw)));
                    }
                    else
                    {
                        return false;
                    }

                    if (scanner.Current.Kind == TokenKind.Comma)
                    {
                        scanner.Scan();
                        continue;
                    }

                    break;
                }
            }

            if (scanner.Current.Kind != TokenKind.RParen)
                return false;
            scanner.Scan();
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
            if (!scanner.Current.IsEndOfInput)
                return false;

            if (isStreamWait)
            {
                // streamwait print(...): push function name, then args, then arity, then streamwait.
                instructions.Add(CreatePushStringInstruction(functionName));
                foreach (var emit in printArgs)
                    emit();
                instructions.Add(CreatePushIntInstruction(printArgs.Count));
                instructions.Add(new InstructionNode { Opcode = "streamwait" });
                return true;
            }

            foreach (var emit in printArgs)
                emit();
            instructions.Add(CreatePushIntInstruction(printArgs.Count));
            instructions.Add(CreateCallInstruction(isPrintln ? "println" : (isPrintd ? "printd" : "print")));
            // Результат print/println в виде statement не используется — очищаем стек.
            instructions.Add(CreatePopInstruction());
            return true;
        }

        private bool TryCompileAwaitStatement(string line, Dictionary<string, (string Kind, int Index)> vars, List<InstructionNode> instructions)
        {
            var scanner = new Scanner(line);
            if (!IsIdentifier(scanner.Current, "await"))
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            var target = scanner.Scan().Value;
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
            if (!scanner.Current.IsEndOfInput)
                return false;

            if (!vars.TryGetValue(target, out var targetVar))
                throw new UndeclaredVariableException(target);
            if (targetVar.Kind != "stream" && targetVar.Kind != "def" && targetVar.Kind != "memory")
                return true;

            instructions.Add(CreatePushMemoryInstruction(targetVar.Index));
            instructions.Add(new InstructionNode { Opcode = "awaitobj" });
            // await как отдельный statement — результат не используется.
            instructions.Add(CreatePopInstruction());
            return true;
        }

        private sealed class ObjectLiteralArgumentSpec
        {
            public List<(string Key, string ValueIdentifier)> Properties { get; } = new List<(string Key, string ValueIdentifier)>();
        }

        private abstract class JsonArgumentNode { }

        private sealed class JsonObjectNode : JsonArgumentNode
        {
            public List<(string Key, JsonArgumentNode Value)> Properties { get; } = new List<(string Key, JsonArgumentNode Value)>();
        }

        private sealed class JsonArrayNode : JsonArgumentNode
        {
            public List<JsonArgumentNode> Items { get; } = new List<JsonArgumentNode>();
        }

        private sealed class JsonIdentifierNode : JsonArgumentNode
        {
            public string Name { get; set; } = "";
        }

        private sealed class JsonPrimitiveNode : JsonArgumentNode
        {
            public object? Value { get; set; }
        }

        /// <summary>Represents a symbolic variable reference in a JSON literal, e.g. <c>:time</c>.</summary>
        private sealed class JsonSymbolicNode : JsonArgumentNode
        {
            public string SymbolicName { get; set; } = "";
        }

        private void EmitMethodCallStatementArgument(
            string argText,
            bool argIsStringLiteral,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            if (argIsStringLiteral)
            {
                instructions.Add(CreatePushStringInstruction(argText));
                return;
            }

            var lookupExpr = argText.Trim();
            if (TryUnwrapClawJsonNamedArg(lookupExpr, out var jsonNamedIdent))
                lookupExpr = jsonNamedIdent;

            if (vars.TryGetValue(lookupExpr, out var argVar))
            {
                instructions.Add(argVar.Kind == "global" ? CreatePushGlobalMemoryInstruction(argVar.Index) : CreatePushMemoryInstruction(argVar.Index));
                return;
            }

            if (TryParseAddressLiteral(lookupExpr, out var address))
            {
                instructions.Add(CreatePushAddressInstruction(address));
                return;
            }

            var tempSlot = (long)AllocateAnonymousLocalTemp(ref memorySlotCounter);
            if (!TryCompileExpressionToSlot(lookupExpr, vars, tempSlot, ref memorySlotCounter, instructions))
                throw new CompilationException($"Cannot compile method call argument: {lookupExpr}", -1);

            instructions.Add(CreatePushMemoryInstruction(tempSlot));
        }

        private bool TryCompileMethodCall(string line, Dictionary<string, (string Kind, int Index)> vars, ref int memorySlotCounter, List<InstructionNode> instructions)
        {

            // <c>callobj</c> already awaits <see cref="Hal.CallObjAsync"/>; strip statement-level <c>await</c> so
            // <c>await socket.write(...)</c> compiles (otherwise the line is silently skipped — no instructions).
            var workLine = (line ?? "").Trim();
            if (workLine.Length >= 6 &&
                workLine.StartsWith("await", StringComparison.OrdinalIgnoreCase) &&
                char.IsWhiteSpace(workLine[5]))
            {
                workLine = workLine.Substring(6).TrimStart();
            }

            var scanner = new Scanner(workLine);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            var objectName = scanner.Scan().Value;
            if (string.Equals(objectName, "this", StringComparison.OrdinalIgnoreCase))
                objectName = "_this";
            if (scanner.Current.Kind != TokenKind.Dot)
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;

            // Collect intermediate property path segments (for chains like obj.prop.method(...))
            var pathSegments = new List<string>();
            var firstIdent = scanner.Scan().Value;

            // Check for multi-dot chain: obj.prop1.prop2...method(args)
            while (scanner.Current.Kind == TokenKind.Dot)
            {
                scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                pathSegments.Add(firstIdent);
                firstIdent = scanner.Scan().Value;
            }
            var methodName = firstIdent;

            if (scanner.Current.Kind != TokenKind.LParen)
                return false;
            scanner.Scan();

            // Collect all argument texts (comma-separated), respecting nesting
            var argTexts = new List<string>();
            var argIsStringLiterals = new List<bool>();

            while (scanner.Current.Kind != TokenKind.RParen && !scanner.Current.IsEndOfInput)
            {
                if (scanner.Current.Kind == TokenKind.StringLiteral)
                {
                    argTexts.Add(scanner.Scan().Value);
                    argIsStringLiterals.Add(true);
                }
                else
                {
                    var start = scanner.Current.Start;
                    var end = start;
                    var depth = 0;
                    while (!scanner.Current.IsEndOfInput)
                    {
                        if (scanner.Current.Kind == TokenKind.LParen || scanner.Current.Kind == TokenKind.LBrace || scanner.Current.Kind == TokenKind.LBracket) depth++;
                        if (scanner.Current.Kind == TokenKind.RParen || scanner.Current.Kind == TokenKind.RBrace || scanner.Current.Kind == TokenKind.RBracket)
                        {
                            if (depth == 0) break;
                            depth--;
                        }
                        if (scanner.Current.Kind == TokenKind.Comma && depth == 0) break;
                        end = scanner.Current.End;
                        scanner.Scan();
                    }
                    argTexts.Add(start < end ? workLine.Substring(start, end - start).Trim() : "");
                    argIsStringLiterals.Add(false);
                }

                if (scanner.Current.Kind == TokenKind.Comma)
                    scanner.Scan();
            }

            if (scanner.Current.Kind != TokenKind.RParen)
                return false;
            scanner.Scan();
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
            if (!scanner.Current.IsEndOfInput)
                return false;

            if (!vars.TryGetValue(objectName, out var objVar))
                throw new UndeclaredVariableException(objectName);

            var callObjProcedureName = ResolveCallObjProcedureNameForInstanceMethod(
                objectName,
                pathSegments.Count,
                objVar.Kind,
                methodName,
                argTexts);

            // For single-argument JSON literal case (backward compatible)
            if (argTexts.Count == 1 && pathSegments.Count == 0)
            {
                var argText = argTexts[0];
                var argIsStringLiteral = argIsStringLiterals[0];
                if (LooksLikeJsonLiteral(argText))
                {
                    if (TryParseObjectLiteralWithVarRefs(argText, out var keyVars) && keyVars != null && keyVars.Count > 0)
                    {
                        var objectSlot = AllocateAnonymousLocalTemp(ref memorySlotCounter);
                        instructions.Add(CreatePushStringInstruction("{}"));
                        instructions.Add(CreatePopMemoryInstruction(objectSlot));
                        foreach (var (key, varName) in keyVars)
                        {
                            if (!vars.TryGetValue(varName, out var valVar))
                                throw new UndeclaredVariableException(varName);
                            if (valVar.Kind != "memory" && valVar.Kind != "stream" && valVar.Kind != "def")
                                return false;
                            EmitOpJsonCall(objectSlot, "set", key, instructions, dataRef: new FunctionParameterNode { Name = "data", ParameterName = "data", EntityType = "memory", Index = valVar.Index });
                        }
                        instructions.Add(CreatePushMemoryInstruction(objVar.Index));
                        instructions.Add(CreatePushMemoryInstruction(objectSlot));
                        instructions.Add(CreatePushIntInstruction(1));
                        instructions.Add(new InstructionNode { Opcode = "callobj", Parameters = new List<ParameterNode> { new FunctionNameParameterNode { FunctionName = callObjProcedureName } } });
                        instructions.Add(CreatePopInstruction());
                        return true;
                    }
                    if (HasSemicolonInsideJsonLiteral(argText))
                        throw new CompilationException("JSON literal cannot contain ';' inside object or array.", -1);
                    if (TryParseJsonArgument(argText, out var jsonArg))
                    {
                        var objectSlot = AllocateAnonymousLocalTemp(ref memorySlotCounter);
                        var init = jsonArg is JsonArrayNode ? "[]" : "{}";
                        instructions.Add(CreatePushStringInstruction(init));
                        instructions.Add(CreatePopMemoryInstruction(objectSlot));
                        EmitBuildJsonNode(jsonArg, objectSlot, "", vars, instructions, ref memorySlotCounter);
                        instructions.Add(CreatePushMemoryInstruction(objVar.Index));
                        instructions.Add(CreatePushMemoryInstruction(objectSlot));
                        instructions.Add(CreatePushIntInstruction(1));
                        instructions.Add(new InstructionNode { Opcode = "callobj", Parameters = new List<ParameterNode> { new FunctionNameParameterNode { FunctionName = callObjProcedureName } } });
                        instructions.Add(CreatePopInstruction());
                        return true;
                    }
                }
            }

            // Общее поведение: obj.method(...) через callobj.
            // Push the base object, then navigate property path via getobj
            instructions.Add(CreatePushMemoryInstruction(objVar.Index));
            foreach (var seg in pathSegments)
            {
                instructions.Add(CreatePushStringInstruction(seg));
                instructions.Add(new InstructionNode { Opcode = "getobj" });
            }
            // If there were path segments, the traversed object is now on top of the stack.
            // We need to store it in a temp slot to push it before args.
            if (pathSegments.Count > 0)
            {
                var tempSlot = AllocateAnonymousLocalTemp(ref memorySlotCounter);
                instructions.Add(CreatePopMemoryInstruction(tempSlot));
                // Push args first, then the object — actually callobj pops arity, then args, then object
                // Order: push object, then push args, then push arity → callobj pops arity, args, object
                instructions.Add(CreatePushMemoryInstruction(tempSlot));
            }

            // Push all arguments
            foreach (var (argText, argIsStr) in argTexts.Zip(argIsStringLiterals))
                EmitMethodCallStatementArgument(argText, argIsStr, vars, ref memorySlotCounter, instructions);

            instructions.Add(CreatePushIntInstruction(argTexts.Count));
            instructions.Add(new InstructionNode { Opcode = "callobj", Parameters = new List<ParameterNode> { new FunctionNameParameterNode { FunctionName = callObjProcedureName } } });
            // Method call as statement — discard result
            instructions.Add(CreatePopInstruction());
            return true;
        }

        private bool TryEmitMethodCallExpression(
            string expressionText,
            Dictionary<string, (string Kind, int Index)> vars,
            long targetSlot,
            ref int memorySlotCounter,
            List<InstructionNode> instructions,
            out string resultKind)
        {
            resultKind = "memory";

            // Специализированный разбор db.Table<>.where(_ => _.Col = var).max(_ => _.OtherCol)
            if (TryEmitWhereMaxExpression(expressionText, vars, targetSlot, ref memorySlotCounter, instructions, out var whereMaxKind))
            {
                resultKind = whereMaxKind;
                return true;
            }
            var scanner = new Scanner(expressionText);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;

            var objectName = scanner.Scan().Value;
            if (string.Equals(objectName, "this", StringComparison.OrdinalIgnoreCase))
                objectName = "_this";
            var pathSegments = new List<string>();
            var methodName = "";
            while (scanner.Current.Kind == TokenKind.Dot)
            {
                scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                var segment = scanner.Scan().Value;
                if (scanner.Current.Kind == TokenKind.LessThan)
                {
                    scanner.Scan();
                    if (scanner.Current.Kind == TokenKind.GreaterThan)
                        scanner.Scan();
                    segment += "<>";
                }
                if (scanner.Current.Kind == TokenKind.Dot)
                    pathSegments.Add(segment);
                else
                {
                    methodName = segment;
                    break;
                }
            }
            if (string.IsNullOrEmpty(methodName))
                return false;
            if (string.Equals(methodName, "history", StringComparison.OrdinalIgnoreCase))
                resultKind = "history";

            if (scanner.Current.Kind != TokenKind.LParen)
                return false;
            scanner.Scan();

            var argIsStringLiteral = false;
            var hasArgument = scanner.Current.Kind != TokenKind.RParen;
            var argText = "";

            if (hasArgument)
            {
                if (scanner.Current.Kind == TokenKind.StringLiteral)
                {
                    argText = scanner.Scan().Value;
                    argIsStringLiteral = true;
                }
                else
                {
                    var start = scanner.Current.Start;
                    var end = start;
                    while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.RParen)
                    {
                        end = scanner.Current.End;
                        scanner.Scan();
                    }
                    argText = start < end ? expressionText.Substring(start, end - start).Trim() : "";
                }
            }

            if (scanner.Current.Kind != TokenKind.RParen)
                return false;
            scanner.Scan();
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
            if (!scanner.Current.IsEndOfInput)
                return false;

            // Нет приёмника в области — не callobj; отдаём bare-call (напр. calculate.add → calculate_add из use-модуля).
            if (!vars.TryGetValue(objectName, out var objVar))
                return false;

            var exprArgTexts = hasArgument
                ? (IReadOnlyList<string>)new[] { argText }
                : Array.Empty<string>();
            var exprCallObjProcedureName = ResolveCallObjProcedureNameForInstanceMethod(
                objectName,
                pathSegments.Count,
                objVar.Kind,
                methodName,
                exprArgTexts);

            instructions.Add(objVar.Kind == "global" ? CreatePushGlobalMemoryInstruction(objVar.Index) : CreatePushMemoryInstruction(objVar.Index));
            foreach (var segment in pathSegments)
            {
                instructions.Add(CreatePushStringInstruction(segment));
                instructions.Add(new InstructionNode { Opcode = "getobj" });
            }

            if (!hasArgument)
            {
                instructions.Add(CreatePushIntInstruction(0));
                instructions.Add(new InstructionNode
                {
                    Opcode = "callobj",
                    Parameters = new List<ParameterNode>
                    {
                        new FunctionNameParameterNode { FunctionName = exprCallObjProcedureName }
                    }
                });
                instructions.Add(CreatePopMemoryInstruction(targetSlot));
                return true;
            }

            var resolverArg = argText;
            if (!argIsStringLiteral && TryUnwrapClawJsonNamedArg(argText.Trim(), out var jsonNamedForExpr))
                resolverArg = jsonNamedForExpr;

            if ((string.Equals(methodName, "any", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(methodName, "where", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(methodName, "find", StringComparison.OrdinalIgnoreCase)) &&
                TryParseLambdaExpression(argText, out var lambdaParameters, out var lambdaBody))
            {
                if (TryEmitLambdaBodyEqualsMember(lambdaParameters, lambdaBody, vars, ref memorySlotCounter, instructions))
                {
                    instructions.Add(CreatePushIntInstruction(1));
                    instructions.Add(new InstructionNode
                    {
                        Opcode = "callobj",
                        Parameters = new List<ParameterNode>
                        {
                            new FunctionNameParameterNode { FunctionName = methodName }
                        }
                    });
                    instructions.Add(CreatePopMemoryInstruction(targetSlot));
                    return true;
                }
            }

            if (string.Equals(methodName, "max", StringComparison.OrdinalIgnoreCase) &&
                TryParseLambdaExpression(argText, out var maxLambdaParameters, out var maxLambdaBody))
            {
                if (TryEmitLambdaBodyMemberAccess(maxLambdaParameters, maxLambdaBody, instructions))
                {
                    instructions.Add(CreatePushIntInstruction(1));
                    instructions.Add(new InstructionNode
                    {
                        Opcode = "callobj",
                        Parameters = new List<ParameterNode>
                        {
                            new FunctionNameParameterNode { FunctionName = "max" }
                        }
                    });
                    instructions.Add(CreatePopMemoryInstruction(targetSlot));
                    return true;
                }
            }

            if (LooksLikeJsonLiteral(resolverArg))
            {
                if (TryParseObjectLiteralWithVarRefs(resolverArg, out var keyVars) && keyVars != null && keyVars.Count > 0)
                {
                    var objectSlot = memorySlotCounter++;
                    instructions.Add(CreatePushStringInstruction("{}"));
                    instructions.Add(CreatePopMemoryInstruction(objectSlot));
                    foreach (var (key, varName) in keyVars)
                    {
                        if (!vars.TryGetValue(varName, out var valVar))
                            throw new UndeclaredVariableException(varName);
                        if (valVar.Kind != "memory" && valVar.Kind != "stream" && valVar.Kind != "def")
                            return false;
                        EmitOpJsonCall(
                            objectSlot,
                            "set",
                            key,
                            instructions,
                            dataRef: new FunctionParameterNode
                            {
                                Name = "data",
                                ParameterName = "data",
                                EntityType = "memory",
                                Index = valVar.Index
                            });
                    }
                    instructions.Add(CreatePushMemoryInstruction(objectSlot));
                    instructions.Add(CreatePushIntInstruction(1));
                    instructions.Add(new InstructionNode
                    {
                        Opcode = "callobj",
                        Parameters = new List<ParameterNode>
                        {
                            new FunctionNameParameterNode { FunctionName = exprCallObjProcedureName }
                        }
                    });
                    instructions.Add(CreatePopMemoryInstruction(targetSlot));
                    return true;
                }

                if (HasSemicolonInsideJsonLiteral(resolverArg))
                    throw new CompilationException("JSON literal cannot contain ';' inside object or array.", -1);

                if (TryParseJsonArgument(resolverArg, out var jsonArg))
                {
                    var objectSlot = memorySlotCounter++;
                    var init = jsonArg is JsonArrayNode ? "[]" : "{}";
                    instructions.Add(CreatePushStringInstruction(init));
                    instructions.Add(CreatePopMemoryInstruction(objectSlot));
                    EmitBuildJsonNode(jsonArg, objectSlot, "", vars, instructions, ref memorySlotCounter);
                    instructions.Add(CreatePushMemoryInstruction(objectSlot));
                    instructions.Add(CreatePushIntInstruction(1));
                    instructions.Add(new InstructionNode
                    {
                        Opcode = "callobj",
                        Parameters = new List<ParameterNode>
                        {
                            new FunctionNameParameterNode { FunctionName = exprCallObjProcedureName }
                        }
                    });
                    instructions.Add(CreatePopMemoryInstruction(targetSlot));
                    return true;
                }
            }

            if (argIsStringLiteral)
                instructions.Add(CreatePushStringInstruction(argText));
            else if (vars.TryGetValue(resolverArg, out var argVar))
                instructions.Add(argVar.Kind == "global" ? CreatePushGlobalMemoryInstruction(argVar.Index) : CreatePushMemoryInstruction(argVar.Index));
            else if (TryParseAddressLiteral(resolverArg, out var address))
                instructions.Add(CreatePushAddressInstruction(address));
            else
            {
                var argExprSlot = (long)AllocateAnonymousLocalTemp(ref memorySlotCounter);
                if (!TryCompileExpressionToSlot(resolverArg, vars, argExprSlot, ref memorySlotCounter, instructions))
                    return false;
                instructions.Add(CreatePushMemoryInstruction(argExprSlot));
            }

            instructions.Add(CreatePushIntInstruction(1));
            instructions.Add(new InstructionNode
            {
                Opcode = "callobj",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = exprCallObjProcedureName }
                }
            });
            instructions.Add(CreatePopMemoryInstruction(targetSlot));
            return true;
        }

        /// <summary>
        /// Специализированная поддержка конструкций:
        /// db1.Message<>.where(_ => _.ChatId = channelTitle).max(_ => _.MessageId)
        /// Компилируется в отдельные вызовы Table.where(whereLambda) и Table.max(selectorLambda).
        /// </summary>
        private bool TryEmitWhereMaxExpression(
            string expressionText,
            Dictionary<string, (string Kind, int Index)> vars,
            long targetSlot,
            ref int memorySlotCounter,
            List<InstructionNode> instructions,
            out string resultKind)
        {
            resultKind = "memory";
            if (string.IsNullOrWhiteSpace(expressionText))
                return false;

            var trimmed = expressionText.Trim();
            while (trimmed.EndsWith(";", StringComparison.Ordinal))
                trimmed = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();

            if (!trimmed.Contains(".where", StringComparison.OrdinalIgnoreCase) ||
                !trimmed.Contains(".max", StringComparison.OrdinalIgnoreCase))
                return false;

            var whereSplit = trimmed.Split(new[] { ".where", ".Where" }, 2, StringSplitOptions.None);
            if (whereSplit.Length != 2)
                return false;
            var head = whereSplit[0].Trim();
            var rest = whereSplit[1];

            var maxSplit = rest.Split(new[] { ".max", ".Max" }, 2, StringSplitOptions.None);
            if (maxSplit.Length != 2)
                return false;

            var whereCall = maxSplit[0].Trim();
            var maxCall = maxSplit[1].Trim();

            if (!whereCall.StartsWith("(", StringComparison.Ordinal))
                whereCall = "(" + whereCall;
            if (!whereCall.EndsWith(")", StringComparison.Ordinal))
                whereCall += ")";
            if (!maxCall.StartsWith("(", StringComparison.Ordinal))
                maxCall = "(" + maxCall;
            if (!maxCall.EndsWith(")", StringComparison.Ordinal))
                maxCall += ")";

            var headScanner = new Scanner(head);
            if (headScanner.Current.Kind != TokenKind.Identifier)
                return false;
            var objectName = headScanner.Scan().Value;
            var pathSegments = new List<string>();
            while (headScanner.Current.Kind == TokenKind.Dot)
            {
                headScanner.Scan();
                if (headScanner.Current.Kind != TokenKind.Identifier)
                    return false;
                var segment = headScanner.Scan().Value;
                if (headScanner.Current.Kind == TokenKind.LessThan)
                {
                    headScanner.Scan();
                    if (headScanner.Current.Kind == TokenKind.GreaterThan)
                        headScanner.Scan();
                    segment += "<>";
                }
                pathSegments.Add(segment);
            }

            if (!vars.TryGetValue(objectName, out var objVar))
                throw new UndeclaredVariableException(objectName);

            if (!TryParseLambdaExpression(whereCall.Trim(' ', '\t', '\r', '\n', '(', ')'), out var whereParams, out var whereBody))
                return false;

            // table, затем where-лямбда
            instructions.Add(objVar.Kind == "global" ? CreatePushGlobalMemoryInstruction(objVar.Index) : CreatePushMemoryInstruction(objVar.Index));
            foreach (var segment in pathSegments)
            {
                instructions.Add(CreatePushStringInstruction(segment));
                instructions.Add(new InstructionNode { Opcode = "getobj" });
            }

            if (!TryEmitLambdaBodyEqualsMember(whereParams, whereBody, vars, ref memorySlotCounter, instructions))
                return false;

            instructions.Add(CreatePushIntInstruction(1));
            instructions.Add(new InstructionNode
            {
                Opcode = "callobj",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = "where" }
                }
            });

            if (!TryParseLambdaExpression(maxCall.Trim(' ', '\t', '\r', '\n', '(', ')'), out var maxParams, out var maxBody))
                return false;
            if (!TryEmitLambdaBodyMemberAccess(maxParams, maxBody, instructions))
                return false;

            instructions.Add(CreatePushIntInstruction(1));
            instructions.Add(new InstructionNode
            {
                Opcode = "callobj",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = "max" }
                }
            });
            instructions.Add(CreatePopMemoryInstruction(targetSlot));

            resultKind = "memory";
            return true;
        }

        private static bool TryParseLambdaExpression(string text, out List<string> parameters, out string body)
        {
            parameters = new List<string>();
            body = "";
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var parts = text.Split(new[] { "=>" }, 2, StringSplitOptions.None);
            if (parts.Length != 2)
                return false;

            var left = parts[0].Trim();
            body = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(body))
                return false;

            if (left.StartsWith("(", StringComparison.Ordinal) && left.EndsWith(")", StringComparison.Ordinal) && left.Length >= 2)
                left = left.Substring(1, left.Length - 2).Trim();

            foreach (var rawParameter in left.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Regex.IsMatch(rawParameter, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                    return false;
                parameters.Add(rawParameter);
            }

            return parameters.Count > 0;
        }

        /// <summary>
        /// Emits lambda body for pattern <c>param.Member = rhsExpr</c>.
        /// Rhs is compiled as a full expression (variable/member/literal/etc), so constructs like <c>reply.id</c> are preserved.
        /// </summary>
        private bool TryEmitLambdaBodyEqualsMember(
            List<string> lambdaParameters,
            string body,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            if (string.IsNullOrWhiteSpace(body) || lambdaParameters.Count != 1)
                return false;

            var parameterName = Regex.Escape(lambdaParameters[0]);
            var m = Regex.Match(
                body.Trim(),
                $@"^\s*{parameterName}\s*\.\s*(\w+)\s*=\s*(.+?)\s*$",
                RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            var memberName = m.Groups[1].Value;
            var rightExpression = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(rightExpression))
                return false;

            var tempSlot = memorySlotCounter++;
            var rightSlot = memorySlotCounter++;

            // Compute non-lambda RHS before expr/defexpr so ExprTree stays pure.
            if (!TryCompileExpressionToSlot(rightExpression, vars, rightSlot, ref memorySlotCounter, instructions))
                return false;

            instructions.Add(new InstructionNode { Opcode = "expr" });
            instructions.Add(CreatePushLambdaArgInstruction(0));
            instructions.Add(CreatePushLambdaArgInstruction(0));
            instructions.Add(CreatePushStringInstruction(memberName));
            instructions.Add(new InstructionNode { Opcode = "getobj" });
            instructions.Add(CreatePopMemoryInstruction(tempSlot));
            instructions.Add(CreatePushMemoryInstruction(tempSlot));
            instructions.Add(CreatePushMemoryInstruction(rightSlot));

            instructions.Add(new InstructionNode { Opcode = "equals" });
            instructions.Add(new InstructionNode { Opcode = "lambda" });
            instructions.Add(new InstructionNode { Opcode = "defexpr" });

            return true;
        }

        private static bool TryEmitLambdaBodyMemberAccess(
            List<string> lambdaParameters,
            string body,
            List<InstructionNode> instructions)
        {
            if (string.IsNullOrWhiteSpace(body) || lambdaParameters.Count != 1)
                return false;

            var parameterName = Regex.Escape(lambdaParameters[0]);
            var m = Regex.Match(
                body.Trim(),
                $@"^{parameterName}\s*\.\s*(\w+)\s*$",
                RegexOptions.IgnoreCase);
            if (!m.Success)
                return false;

            var memberName = m.Groups[1].Value;
            instructions.Add(new InstructionNode { Opcode = "expr" });
            instructions.Add(CreatePushLambdaArgInstruction(0));
            instructions.Add(CreatePushLambdaArgInstruction(0));
            instructions.Add(CreatePushStringInstruction(memberName));
            instructions.Add(new InstructionNode { Opcode = "getobj" });
            instructions.Add(new InstructionNode { Opcode = "lambda" });
            instructions.Add(new InstructionNode { Opcode = "defexpr" });
            return true;
        }

        /// <summary>Parses #"text {expr1} more {expr2}" into template "text {0} more {1}" and list [expr1, expr2].</summary>
        private static bool TryParseFormatStringLiteral(string trimmed, out string formatTemplate, out List<string> expressions)
        {
            formatTemplate = "";
            expressions = new List<string>();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length < 4)
                return false;
            if (!trimmed.StartsWith("#\"", StringComparison.Ordinal) || trimmed[trimmed.Length - 1] != '"')
                return false;

            var inner = trimmed.Substring(2, trimmed.Length - 3);
            var templateBuilder = new StringBuilder();
            var expressionBuilder = new StringBuilder();
            var inExpression = false;
            var parenDepth = 0;
            var bracketDepth = 0;
            var inString = false;
            var quote = '\0';
            var escaped = false;

            for (var i = 0; i < inner.Length; i++)
            {
                var ch = inner[i];
                if (!inExpression)
                {
                    if (ch == '{')
                    {
                        inExpression = true;
                        expressionBuilder.Clear();
                        parenDepth = 0;
                        bracketDepth = 0;
                        inString = false;
                        quote = '\0';
                        escaped = false;
                        continue;
                    }

                    templateBuilder.Append(ch);
                    continue;
                }

                if (inString)
                {
                    expressionBuilder.Append(ch);
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

                    if (ch == quote)
                        inString = false;

                    continue;
                }

                if (ch == '"' || ch == '\'')
                {
                    inString = true;
                    quote = ch;
                    expressionBuilder.Append(ch);
                    continue;
                }

                if (ch == '(')
                {
                    parenDepth++;
                    expressionBuilder.Append(ch);
                    continue;
                }

                if (ch == ')')
                {
                    if (parenDepth == 0)
                        return false;

                    parenDepth--;
                    expressionBuilder.Append(ch);
                    continue;
                }

                if (ch == '[')
                {
                    bracketDepth++;
                    expressionBuilder.Append(ch);
                    continue;
                }

                if (ch == ']')
                {
                    if (bracketDepth == 0)
                        return false;

                    bracketDepth--;
                    expressionBuilder.Append(ch);
                    continue;
                }

                if (ch == '}' && parenDepth == 0 && bracketDepth == 0)
                {
                    var expression = expressionBuilder.ToString().Trim();
                    if (string.IsNullOrEmpty(expression))
                        return false;

                    expressions.Add(expression);
                    templateBuilder.Append('{').Append(expressions.Count - 1).Append('}');
                    inExpression = false;
                    continue;
                }

                expressionBuilder.Append(ch);
            }

            if (inExpression || inString || parenDepth != 0 || bracketDepth != 0)
                return false;

            formatTemplate = templateBuilder.ToString();
            return true;
        }

        /// <summary>Claw / HTTP-style named arg: <c>json: varName</c> → push <c>varName</c>.</summary>
        private static bool TryUnwrapClawJsonNamedArg(string trimmedArgument, out string variableIdentifier)
        {
            variableIdentifier = trimmedArgument;
            var m = Regex.Match(trimmedArgument, @"^json\s*:\s*(\w+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!m.Success)
                return false;
            variableIdentifier = m.Groups[1].Value;
            return true;
        }

        private static bool TryParseObjectLiteralArgument(string text, out ObjectLiteralArgumentSpec spec)
        {
            spec = new ObjectLiteralArgumentSpec();
            var trimmed = (text ?? "").Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
                return false;

            var inner = trimmed.Substring(1, trimmed.Length - 2).Trim();
            if (inner.Length == 0)
                return true;

            var parts = inner.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var kv = part.Split(':', 2, StringSplitOptions.RemoveEmptyEntries);
                if (kv.Length != 2)
                    return false;
                var key = kv[0].Trim();
                var valueIdentifier = kv[1].Trim();
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(valueIdentifier))
                    return false;
                spec.Properties.Add((key, valueIdentifier));
            }

            return true;
        }

        private void EmitStreamDeclaration(string streamName, IReadOnlyList<string> elementTypes, Dictionary<string, (string Kind, int Index)> vars, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var baseSlot = memorySlotCounter++;
            var streamSlot = memorySlotCounter++;
            vars[streamName] = ("stream", streamSlot);
            instructions.Add(CreatePushTypeInstruction("stream"));
            instructions.Add(CreatePushIntInstruction(1));
            instructions.Add(new InstructionNode { Opcode = "def" });
            instructions.Add(CreatePopMemoryInstruction(baseSlot));
            instructions.Add(CreatePushMemoryInstruction(baseSlot));
            foreach (var elementType in elementTypes)
                instructions.Add(CreatePushTypeInstruction(elementType));
            instructions.Add(CreatePushIntInstruction(elementTypes.Count));
            instructions.Add(new InstructionNode { Opcode = "defgen" });
            instructions.Add(CreatePopMemoryInstruction(streamSlot));
        }

        private static bool TryParseVaultDeclaration(string line, out string vaultName)
        {
            vaultName = "";
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var text = line.Trim();
            if (text.StartsWith("var ", StringComparison.Ordinal))
                text = text.Substring("var ".Length).TrimStart();

            var assignIdx = text.IndexOf(":=", StringComparison.Ordinal);
            if (assignIdx < 0)
                return false;

            var left = text.Substring(0, assignIdx).Trim();
            var right = text.Substring(assignIdx + 2).Trim().TrimEnd(';').Trim();
            if (string.IsNullOrEmpty(left) || !string.Equals(right, "vault", StringComparison.OrdinalIgnoreCase))
                return false;

            vaultName = left;
            return true;
        }

        private void EmitVaultDeclaration(string vaultName, Dictionary<string, (string Kind, int Index)> vars, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var slot = memorySlotCounter++;
            vars[vaultName] = ("memory", slot);
            instructions.Add(CreatePushTypeInstruction("vault"));
            instructions.Add(CreatePushIntInstruction(1));
            instructions.Add(new InstructionNode { Opcode = "def" });
            instructions.Add(CreatePopMemoryInstruction(slot));
        }

        private static bool TryParseStreamWaitForLoop(
            string line,
            out bool isSync,
            out string streamName,
            out string waitType,
            out string deltaVarName,
            out string aggregateVarName,
            out string bodyText,
            out int openBraceIndex)
        {
            isSync = false;
            streamName = "";
            waitType = "";
            deltaVarName = "";
            aggregateVarName = "";
            bodyText = "";
            openBraceIndex = -1;

            var scanner = new Scanner(line);
            if (!IsIdentifier(scanner.Current, "for"))
                return false;
            scanner.Scan();
            if (IsIdentifier(scanner.Current, "sync"))
            {
                isSync = true;
                scanner.Scan();
            }
            if (!IsIdentifier(scanner.Current, "streamwait"))
                return false;
            scanner.Scan();
            if (!IsIdentifier(scanner.Current, "by"))
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            waitType = scanner.Scan().Value;

            if (scanner.Current.Kind != TokenKind.LParen)
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            streamName = scanner.Scan().Value;

            if (scanner.Current.Kind != TokenKind.Comma)
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            deltaVarName = scanner.Scan().Value;

            if (scanner.Current.Kind == TokenKind.Comma)
            {
                scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                aggregateVarName = scanner.Scan().Value;
            }

            if (scanner.Current.Kind != TokenKind.RParen)
                return false;
            scanner.Scan();

            if (scanner.Current.Kind != TokenKind.LBrace)
                return false;

            openBraceIndex = scanner.Current.Start;
            var braceStart = scanner.Current.Start;
            var braceDepth = 0;
            var bodyStart = -1;
            var bodyEnd = -1;
            var inString = false;
            var escaped = false;
            var quote = '\0';

            for (var i = braceStart; i < line.Length; i++)
            {
                var ch = line[i];
                if (inString)
                {
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

                    if (ch == quote)
                        inString = false;

                    continue;
                }

                if (ch == '"' || ch == '\'')
                {
                    inString = true;
                    quote = ch;
                    continue;
                }

                if (ch == '{')
                {
                    braceDepth++;
                    if (braceDepth == 1)
                        bodyStart = i + 1;
                    continue;
                }

                if (ch != '}')
                    continue;

                braceDepth--;
                if (braceDepth == 0)
                {
                    bodyEnd = i;
                    break;
                }
            }

            if (bodyStart < 0 || bodyEnd < bodyStart)
                return false;

            for (var i = bodyEnd + 1; i < line.Length; i++)
            {
                if (!char.IsWhiteSpace(line[i]) && line[i] != ';')
                    return false;
            }

            bodyText = line.Substring(bodyStart, bodyEnd - bodyStart);
            return true;
        }

        private static List<string> SplitStatementLines(string source)
        {
            var lines = new List<string>();
            if (string.IsNullOrWhiteSpace(source))
                return lines;

            var sb = new StringBuilder();
            var inString = false;
            var escaped = false;
            var parenDepth = 0;
            var braceDepth = 0;
            var bracketDepth = 0;

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
                if (ch == '{') braceDepth++;
                if (ch == '}') braceDepth = Math.Max(0, braceDepth - 1);
                if (ch == '[') bracketDepth++;
                if (ch == ']') bracketDepth = Math.Max(0, bracketDepth - 1);

                var topLevel = parenDepth == 0 && braceDepth == 0 && bracketDepth == 0;
                if ((ch == ';' || ch == '\n' || ch == '\r') && topLevel)
                {
                    var statement = sb.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(statement))
                        lines.Add(statement);
                    sb.Clear();
                    continue;
                }

                sb.Append(ch);

                if (ch == '}' && topLevel)
                {
                    var nextIndex = index + 1;
                    while (nextIndex < source.Length && char.IsWhiteSpace(source[nextIndex]))
                        nextIndex++;

                    if (nextIndex < source.Length && source[nextIndex] != ';')
                    {
                        var tail = source.Substring(nextIndex);
                        if (!tail.StartsWith("else", StringComparison.OrdinalIgnoreCase))
                        {
                            var statement = sb.ToString().Trim();
                            if (!string.IsNullOrWhiteSpace(statement))
                                lines.Add(statement);
                            sb.Clear();
                        }
                    }
                }
            }

            var last = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(last))
                lines.Add(last);

            return lines;
        }

        private static InstructionNode CreateCallInstruction(string functionName, params FunctionParameterNode[] args)
        {
            var parameters = new List<ParameterNode>
            {
                new FunctionNameParameterNode { Name = "function", FunctionName = functionName }
            };
            foreach (var arg in args)
                parameters.Add(arg);
            return new InstructionNode { Opcode = "call", Parameters = parameters };
        }

        private static InstructionNode CreateAsyncCallInstruction(string functionName, params FunctionParameterNode[] args)
        {
            var parameters = new List<ParameterNode>
            {
                new FunctionNameParameterNode { Name = "function", FunctionName = functionName }
            };
            foreach (var arg in args)
                parameters.Add(arg);
            return new InstructionNode { Opcode = "acall", Parameters = parameters };
        }

        /// <summary>streamwait loop: стек aggregate, delta, затем push 2 (arity); acall/call снимает аргументы и привязывает к слотам нового кадра.</summary>
        private static InstructionNode CreateStreamWaitLoopDeltaCallInstruction(string loopBodyLabel, bool async, long aggregateSlot, long deltaSlot, long[] captureToSlots)
        {
            var parameters = new List<ParameterNode>
            {
                new FunctionNameParameterNode { Name = "function", FunctionName = loopBodyLabel },
                new StreamWaitDeltaBindSlotsParameterNode
                {
                    Name = "streamwait_delta_bind",
                    AggregateSlot = aggregateSlot,
                    DeltaSlot = deltaSlot,
                    CaptureToSlots = captureToSlots ?? System.Array.Empty<long>()
                }
            };
            return new InstructionNode { Opcode = async ? "acall" : "call", Parameters = parameters };
        }

        private static InstructionNode CreateLabelInstruction(string label)
        {
            return new InstructionNode
            {
                Opcode = "label",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = label }
                }
            };
        }

        private static InstructionNode CreateCmpInstruction(long leftMemoryIndex, long rightLiteral)
        {
            return new InstructionNode
            {
                Opcode = "cmp",
                Parameters = new List<ParameterNode>
                {
                    new MemoryParameterNode { Name = "left", Index = leftMemoryIndex },
                    new IndexParameterNode { Name = "int", Value = rightLiteral }
                }
            };
        }

        private static InstructionNode CreateJumpIfEqualInstruction(string label)
        {
            return new InstructionNode
            {
                Opcode = "je",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = label }
                }
            };
        }

        private static InstructionNode CreateJumpInstruction(string label)
        {
            return new InstructionNode
            {
                Opcode = "jmp",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = label }
                }
            };
        }

        private static FunctionParameterNode CreateEntityCallParameter(string paramName, string entityType, long index)
        {
            return new FunctionParameterNode
            {
                Name = paramName,
                ParameterName = paramName,
                EntityType = entityType,
                Index = index
            };
        }

        private static InstructionNode CreatePushMemoryInstruction(long index)
        {
            return new InstructionNode
            {
                Opcode = "push",
                Parameters = new List<ParameterNode>
                {
                    new MemoryParameterNode { Name = "index", Index = index }
                }
            };
        }

        private static InstructionNode CreatePushGlobalMemoryInstruction(long index)
        {
            return new InstructionNode
            {
                Opcode = "push",
                Parameters = new List<ParameterNode>
                {
                    new MemoryParameterNode { Name = "global", Index = index }
                }
            };
        }

        private static InstructionNode CreatePushStringInstruction(string value)
        {
            return new InstructionNode
            {
                Opcode = "push",
                Parameters = new List<ParameterNode>
                {
                    new StringParameterNode { Name = "string", Value = value }
                }
            };
        }

        private static InstructionNode CreatePushAddressInstruction(string address)
        {
            return new InstructionNode
            {
                Opcode = "push",
                Parameters = new List<ParameterNode>
                {
                    new AddressLiteralParameterNode { Name = "address", Address = address }
                }
            };
        }

        private static InstructionNode CreatePushIntInstruction(long value)
        {
            return new InstructionNode
            {
                Opcode = "push",
                Parameters = new List<ParameterNode>
                {
                    new IndexParameterNode { Name = "int", Value = value }
                }
            };
        }

        private static InstructionNode CreatePushTypeInstruction(string typeName)
        {
            return new InstructionNode
            {
                Opcode = "push",
                Parameters = new List<ParameterNode>
                {
                    new TypeLiteralParameterNode { TypeName = typeName }
                }
            };
        }

        private static InstructionNode CreatePushClassInstruction(string className)
        {
            return new InstructionNode
            {
                Opcode = "push",
                Parameters = new List<ParameterNode>
                {
                    new ClassLiteralParameterNode { Name = "class", ClassName = className }
                }
            };
        }

        private static bool TryParseAddressLiteral(string text, out string address)
        {
            address = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var trimmed = text.Trim();
            if (!trimmed.StartsWith("&", StringComparison.Ordinal) || trimmed.Length <= 1)
                return false;

            var candidate = trimmed.Substring(1);
            if (candidate.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
                return false;

            address = candidate;
            return true;
        }

        private static InstructionNode CreatePushLambdaArgInstruction(int argIndex)
        {
            return new InstructionNode
            {
                Opcode = "push",
                Parameters = new List<ParameterNode>
                {
                    new LambdaArgParameterNode { Name = "lambda", Index = argIndex }
                }
            };
        }

        private static InstructionNode CreatePopInstruction()
        {
            return new InstructionNode
            {
                Opcode = "pop",
                Parameters = new List<ParameterNode>()
            };
        }

        private static InstructionNode CreatePopMemoryInstruction(long index)
        {
            return new InstructionNode
            {
                Opcode = "pop",
                Parameters = new List<ParameterNode>
                {
                    new MemoryParameterNode { Name = "index", Index = index }
                }
            };
        }

        private bool IsDeclarationStatement(string line)
        {
            return TryParseStreamDeclaration(line, out _, out _) ||
                   TryParseEntityDeclaration(line, out _, out _, out _);
        }

        private bool TryParseVarKeywordOnly(string line)
        {
            var scanner = new Scanner(line);
            if (!IsIdentifier(scanner.Current, "var"))
                return false;
            scanner.Scan();
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
            return scanner.Current.IsEndOfInput;
        }

        private bool TryStripInlineVarPrefix(string line, out string declarationLine)
        {
            declarationLine = "";
            var scanner = new Scanner(line);
            if (!IsIdentifier(scanner.Current, "var"))
                return false;
            scanner.Scan();
            if (scanner.Current.IsEndOfInput)
                return false;
            var start = scanner.Current.Start;
            var end = start;
            while (!scanner.Current.IsEndOfInput)
            {
                end = scanner.Current.End;
                scanner.Scan();
            }
            if (end <= start || start >= line.Length)
                return false;
            declarationLine = line.Substring(start, end - start).Trim();
            return !string.IsNullOrEmpty(declarationLine);
        }

        private bool TryParseStreamDeclaration(string line, out string streamName, out List<string> elementTypes)
        {
            streamName = string.Empty;
            elementTypes = new List<string>();
            var previous = _scanner;
            _scanner = new Scanner(line);
            try
            {
                if (IsIdentifier(CurrentScanner.Current, "var"))
                    CurrentScanner.Scan();

                if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                    return false;
                streamName = CurrentScanner.Scan().Value;

                // Accept both ":= stream<>" (single-line) and ": stream<>" (multi-line var block)
                if (CurrentScanner.Current.Kind != TokenKind.Colon)
                    return false;
                CurrentScanner.Scan(); // consume ":"
                if (CurrentScanner.Current.Kind == TokenKind.Assign)
                    CurrentScanner.Scan(); // consume optional "=" from ":="

                if (!IsIdentifier(CurrentScanner.Current, "stream"))
                    return false;
                CurrentScanner.Scan();

                if (CurrentScanner.Current.Kind != TokenKind.LessThan)
                    return false;
                CurrentScanner.Scan();
                if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                    return false;
                elementTypes.Add(CurrentScanner.Scan().Value.ToLowerInvariant());

                while (CurrentScanner.Current.Kind == TokenKind.Comma)
                {
                    CurrentScanner.Scan();
                    if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                        return false;
                    elementTypes.Add(CurrentScanner.Scan().Value.ToLowerInvariant());
                }

                if (CurrentScanner.Current.Kind != TokenKind.GreaterThan)
                    return false;
                CurrentScanner.Scan();
                SkipSemicolon();
                return CurrentScanner.Current.IsEndOfInput && elementTypes.Count > 0;
            }
            finally
            {
                _scanner = previous;
            }
        }

        private bool TryParseEntityDeclaration(string sourceLine, out string varName, out string varType, out string initText)
        {
            varName = "";
            varType = "";
            initText = "";
            var previous = _scanner;
            _scanner = new Scanner(sourceLine);
            try
            {
                if (IsIdentifier(CurrentScanner.Current, "var"))
                    CurrentScanner.Scan();

                if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                    return false;
                varName = CurrentScanner.Scan().Value;

                if (CurrentScanner.Current.Kind != TokenKind.Colon)
                    return false;
                CurrentScanner.Scan();
                if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                    return false;
                varType = CurrentScanner.Scan().Value.ToLowerInvariant();
                if (varType != "vertex" && varType != "relation" && varType != "shape")
                    return false;
                if (CurrentScanner.Current.Kind != TokenKind.Assign)
                    return false;
                CurrentScanner.Scan();

                if (CurrentScanner.Current.IsEndOfInput)
                    return false;
                var start = CurrentScanner.Current.Start;
                var end = start;
                while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.Semicolon)
                {
                    end = CurrentScanner.Current.End;
                    CurrentScanner.Scan();
                }
                initText = start < end ? sourceLine.Substring(start, end - start).Trim() : "";
                if (string.IsNullOrWhiteSpace(initText))
                    return false;

                SkipSemicolon();
                return CurrentScanner.Current.IsEndOfInput;
            }
            finally
            {
                _scanner = previous;
            }
        }

        private bool TryParseVertexObject(string initValue, out VertexInitSpec spec)
        {
            spec = new VertexInitSpec();
            var previous = _scanner;
            _scanner = new Scanner(initValue);
            try
            {
                if (CurrentScanner.Current.Kind != TokenKind.LBrace)
                    return false;
                CurrentScanner.Scan();

                while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.RBrace)
                {
                    if (CurrentScanner.Current.Kind == TokenKind.Comma)
                    {
                        CurrentScanner.Scan();
                        continue;
                    }

                    if (IsIdentifier(CurrentScanner.Current, "DIM"))
                    {
                        CurrentScanner.Scan();
                        TryConsumeColon();
                        var dims = ParseLongArray();
                        if (dims.Count > 0)
                        {
                            spec.Dimensions.Clear();
                            spec.Dimensions.AddRange(dims);
                        }
                        continue;
                    }

                    if (IsIdentifier(CurrentScanner.Current, "W"))
                    {
                        CurrentScanner.Scan();
                        TryConsumeColon();
                        if (TryParseDoubleToken(out var weight))
                            spec.Weight = weight;
                        continue;
                    }

                    if (IsIdentifier(CurrentScanner.Current, "DATA"))
                    {
                        CurrentScanner.Scan();
                        TryConsumeColon();
                        if (IsIdentifier(CurrentScanner.Current, "BIN"))
                        {
                            CurrentScanner.Scan();
                            TryConsumeColon();
                            if (CurrentScanner.Current.Kind == TokenKind.StringLiteral)
                                spec.BinaryData = CurrentScanner.Scan().Value;
                        }
                        else if (CurrentScanner.Current.Kind == TokenKind.StringLiteral)
                        {
                            spec.TextData = CurrentScanner.Scan().Value;
                        }
                        continue;
                    }

                    CurrentScanner.Scan();
                }

                if (CurrentScanner.Current.Kind != TokenKind.RBrace)
                    return false;
                CurrentScanner.Scan();
                return CurrentScanner.Current.IsEndOfInput;
            }
            finally
            {
                _scanner = previous;
            }
        }

        private bool TryParseRelationObject(string initValue, out RelationInitSpec spec)
        {
            spec = new RelationInitSpec();
            var previous = _scanner;
            _scanner = new Scanner(initValue);
            try
            {
                if (CurrentScanner.Current.Kind != TokenKind.LBrace)
                    return false;
                CurrentScanner.Scan();

                if (CurrentScanner.Current.Kind == TokenKind.Identifier)
                    spec.From = CurrentScanner.Scan().Value;
                if (CurrentScanner.Current.Kind != TokenKind.Assign || CurrentScanner.Watch(1)?.Kind != TokenKind.GreaterThan)
                    return false;
                CurrentScanner.Scan();
                CurrentScanner.Scan();
                if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                    return false;
                spec.To = CurrentScanner.Scan().Value;

                while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.RBrace)
                {
                    if (CurrentScanner.Current.Kind == TokenKind.Comma)
                    {
                        CurrentScanner.Scan();
                        continue;
                    }
                    if (IsIdentifier(CurrentScanner.Current, "W"))
                    {
                        CurrentScanner.Scan();
                        TryConsumeColon();
                        if (TryParseDoubleToken(out var weight))
                            spec.Weight = weight;
                        continue;
                    }
                    CurrentScanner.Scan();
                }

                if (CurrentScanner.Current.Kind != TokenKind.RBrace)
                    return false;
                CurrentScanner.Scan();
                return CurrentScanner.Current.IsEndOfInput;
            }
            finally
            {
                _scanner = previous;
            }
        }

        private bool TryParseShapeObject(string initValue, out ShapeInitSpec spec)
        {
            spec = new ShapeInitSpec();
            var previous = _scanner;
            _scanner = new Scanner(initValue);
            try
            {
                if (CurrentScanner.Current.Kind != TokenKind.LBrace)
                    return false;
                CurrentScanner.Scan();

                if (IsIdentifier(CurrentScanner.Current, "VERT"))
                {
                    CurrentScanner.Scan();
                    TryConsumeColon();
                    if (CurrentScanner.Current.Kind != TokenKind.LBracket)
                        return false;
                    CurrentScanner.Scan();
                    while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.RBracket)
                    {
                        if (CurrentScanner.Current.Kind == TokenKind.Comma)
                        {
                            CurrentScanner.Scan();
                            continue;
                        }
                        if (CurrentScanner.Current.Kind == TokenKind.LBrace && TryParseInlineVertex(out var inlineVertex))
                        {
                            spec.InlineVertices.Add(inlineVertex);
                            continue;
                        }
                        CurrentScanner.Scan();
                    }
                    if (CurrentScanner.Current.Kind != TokenKind.RBracket)
                        return false;
                    CurrentScanner.Scan();
                }
                else if (CurrentScanner.Current.Kind == TokenKind.LBracket)
                {
                    CurrentScanner.Scan();
                    while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.RBracket)
                    {
                        if (CurrentScanner.Current.Kind == TokenKind.Comma)
                        {
                            CurrentScanner.Scan();
                            continue;
                        }
                        if (CurrentScanner.Current.Kind == TokenKind.Identifier)
                            spec.VertexNames.Add(CurrentScanner.Scan().Value);
                        else
                            CurrentScanner.Scan();
                    }
                    if (CurrentScanner.Current.Kind != TokenKind.RBracket)
                        return false;
                    CurrentScanner.Scan();
                }
                else
                {
                    return false;
                }

                while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.RBrace)
                    CurrentScanner.Scan();
                if (CurrentScanner.Current.Kind != TokenKind.RBrace)
                    return false;
                CurrentScanner.Scan();
                return CurrentScanner.Current.IsEndOfInput;
            }
            finally
            {
                _scanner = previous;
            }
        }

        private bool TryParseInlineVertex(out InlineVertexSpec spec)
        {
            spec = new InlineVertexSpec();
            if (CurrentScanner.Current.Kind != TokenKind.LBrace)
                return false;
            CurrentScanner.Scan();

            while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.RBrace)
            {
                if (CurrentScanner.Current.Kind == TokenKind.Comma)
                {
                    CurrentScanner.Scan();
                    continue;
                }
                if (IsIdentifier(CurrentScanner.Current, "DIM"))
                {
                    CurrentScanner.Scan();
                    TryConsumeColon();
                    var dims = ParseLongArray();
                    if (dims.Count > 0)
                    {
                        spec.Dimensions.Clear();
                        spec.Dimensions.AddRange(dims);
                    }
                    continue;
                }
                if (IsIdentifier(CurrentScanner.Current, "W"))
                {
                    CurrentScanner.Scan();
                    TryConsumeColon();
                    if (TryParseDoubleToken(out var weight))
                        spec.Weight = weight;
                    continue;
                }
                CurrentScanner.Scan();
            }

            if (CurrentScanner.Current.Kind != TokenKind.RBrace)
                return false;
            CurrentScanner.Scan();
            return true;
        }

        private List<long> ParseLongArray()
        {
            var values = new List<long>();
            if (CurrentScanner.Current.Kind != TokenKind.LBracket)
                return values;
            CurrentScanner.Scan();
            while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.RBracket)
            {
                if (CurrentScanner.Current.Kind == TokenKind.Comma)
                {
                    CurrentScanner.Scan();
                    continue;
                }
                if ((CurrentScanner.Current.Kind == TokenKind.Number || CurrentScanner.Current.Kind == TokenKind.Float) &&
                    long.TryParse(CurrentScanner.Current.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
                {
                    values.Add(num);
                }
                CurrentScanner.Scan();
            }
            if (CurrentScanner.Current.Kind == TokenKind.RBracket)
                CurrentScanner.Scan();
            return values;
        }

        private static string ExtractRemainingExpressionText(string sourceLine, int startIndex)
        {
            if (string.IsNullOrEmpty(sourceLine) || startIndex < 0 || startIndex >= sourceLine.Length)
                return "";

            // Многострочный RHS: идём от startIndex до первого ';' на нулевой глубине
            // скобок/фигурных/квадратных. Это позволяет поддерживать выражения вида:
            //   var world := Room(
            //       Board: Board(...),
            //       Persons: Persons[]
            //   );
            // даже если внутри есть переводы строк.
            var depthParen = 0;
            var depthBrace = 0;
            var depthBracket = 0;
            var inString = false;
            char stringQuote = '\0';

            var endIndex = sourceLine.Length;
            for (var i = startIndex; i < sourceLine.Length; i++)
            {
                var ch = sourceLine[i];

                if (inString)
                {
                    if (ch == '\\')
                    {
                        // Пропускаем экранированный символ в строке
                        if (i + 1 < sourceLine.Length)
                            i++;
                        continue;
                    }
                    if (ch == stringQuote)
                    {
                        inString = false;
                        stringQuote = '\0';
                    }
                    continue;
                }

                if (ch == '"' || ch == '\'')
                {
                    inString = true;
                    stringQuote = ch;
                    continue;
                }

                switch (ch)
                {
                    case '(':
                        depthParen++;
                        break;
                    case ')':
                        if (depthParen > 0) depthParen--;
                        break;
                    case '{':
                        depthBrace++;
                        break;
                    case '}':
                        if (depthBrace > 0) depthBrace--;
                        break;
                    case '[':
                        depthBracket++;
                        break;
                    case ']':
                        if (depthBracket > 0) depthBracket--;
                        break;
                    case ';':
                        if (depthParen == 0 && depthBrace == 0 && depthBracket == 0)
                        {
                            endIndex = i;
                            i = sourceLine.Length; // выходим из цикла
                        }
                        break;
                }
            }

            var length = endIndex - startIndex;
            if (length <= 0)
                return "";

            var rhs = sourceLine.Substring(startIndex, length).Trim();
            // На всякий случай удаляем финальные ';', если они попали внутрь.
            while (rhs.EndsWith(";", StringComparison.Ordinal))
                rhs = rhs.Substring(0, rhs.Length - 1).TrimEnd();
            return rhs;
        }

        private bool TryParseDoubleToken(out double value)
        {
            value = 0d;
            if (CurrentScanner.Current.Kind != TokenKind.Number && CurrentScanner.Current.Kind != TokenKind.Float)
                return false;
            var ok = double.TryParse(CurrentScanner.Current.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
            CurrentScanner.Scan();
            return ok;
        }

        private static bool TryParseAwaitExpressionText(string text, out string expressionText)
        {
            expressionText = "";
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var trimmed = text.Trim();
            if (!trimmed.StartsWith("await", StringComparison.OrdinalIgnoreCase))
                return false;
            if (trimmed.Length <= 5 || !char.IsWhiteSpace(trimmed[5]))
                return false;

            expressionText = trimmed.Substring(5).Trim();
            while (expressionText.EndsWith(";", StringComparison.Ordinal))
                expressionText = expressionText.Substring(0, expressionText.Length - 1).TrimEnd();
            return expressionText.Length > 0;
        }

        private static bool TryParseProcedureName(string line, out string procedureName)
        {
            procedureName = "";
            var scanner = new Scanner(line);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            procedureName = scanner.Scan().Value;
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
            return scanner.Current.IsEndOfInput;
        }

        /// <summary>
        /// Parses switch body into arms. Each arm starts with "if" or "else if" followed by match value(s) then a body.
        /// Supported forms:
        ///   if "value"  statement;
        ///   if "value" { block }
        ///   else if "value" …  (эквивалентно if — только для читаемости)
        ///   if 0: val0, 1: val1 { block }
        /// <paramref name="bodyFirstSourceLine"/> — номер строки исходника первого символа внутри <c>{</c> (после нормализации с \n).
        /// </summary>
        /// <summary>«if …» или «else if …» в теле switch; <paramref name="restAfterIf"/> — хвост после ключевого слова <c>if</c> (как раньше <c>line.Substring(2)</c>).</summary>
        private static bool StartsSwitchArmIfKeyword(string line, out string restAfterIf)
        {
            restAfterIf = "";
            var t = line.TrimStart();
            if (t.Length >= 2 && t.StartsWith("if", StringComparison.OrdinalIgnoreCase))
            {
                if (t.Length > 2 && char.IsLetterOrDigit(t[2]))
                    return false;
                restAfterIf = t.Length > 2 ? t.Substring(2) : "";
                return true;
            }

            if (!t.StartsWith("else", StringComparison.OrdinalIgnoreCase) || t.Length < 6)
                return false;
            var i = 4;
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
            if (i + 2 > t.Length || !t.AsSpan(i).StartsWith("if", StringComparison.OrdinalIgnoreCase))
                return false;
            if (i + 2 < t.Length && char.IsLetterOrDigit(t[i + 2]))
                return false;
            restAfterIf = i + 2 < t.Length ? t.Substring(i + 2) : "";
            return true;
        }

        /// <summary>
        /// Парсинг ветки <c>if x is T: binding</c> для switch по типу (<c>call is</c>).
        /// Допускает хвост на той же строке (после coalesce/split по «;» получается
        /// <c>if shape is Circle: circle Draw(circle);</c> одним фрагментом).
        /// </summary>
        private static bool TryParseSwitchTypePatternLine(
            string restAfterIf,
            out string subjectVar,
            out string typeName,
            out string bindingVar,
            out string sameLineBodyTail)
        {
            subjectVar = typeName = bindingVar = sameLineBodyTail = "";
            var t = (restAfterIf ?? "").Trim();
            var m = Regex.Match(
                t,
                @"^([A-Za-z_][\w]*)\s+is\s+([A-Za-z_][\w]*)\s*:\s*([A-Za-z_][\w]*)(?:\s+(.+))?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!m.Success)
                return false;
            subjectVar = m.Groups[1].Value;
            typeName = m.Groups[2].Value;
            bindingVar = m.Groups[3].Value;
            sameLineBodyTail = m.Groups[4].Success ? m.Groups[4].Value.Trim() : "";
            return true;
        }

        /// <summary>
        /// Номера строк для веток switch; <paramref name="bodyFirstSourceLine"/> — строка первого символа тела (сразу после <c>{</c>).
        /// </summary>
        private static List<(List<string> MatchValues, string Body, int IfLine, int BodyStartLine, string? TypePatSubject, string? TypePatType, string? TypePatBinding)> ParseSwitchArmsWithSourceLines(
            string bodyText,
            int bodyFirstSourceLine)
        {
            var arms = new List<(List<string>, string, int, int, string?, string?, string?)>();
            var lines = SplitStatementLines(bodyText);
            var useLines = bodyFirstSourceLine > 0;
            var searchFrom = 0;

            foreach (var rawLine in CoalesceMultilineStatements(lines))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var idx = bodyText.IndexOf(line, searchFrom, StringComparison.Ordinal);
                if (idx < 0) idx = searchFrom;
                var physicalLine = useLines ? bodyFirstSourceLine + CountNewlinesInRange(bodyText, 0, idx) : 0;
                searchFrom = idx + Math.Max(1, line.Length);

                // Строка без нового «if» — продолжение тела последней ветки (иначе «if "b"\n stmt» теряет stmt,
                // когда предыдущая ветка уже имеет тело на отдельной строке).
                if (!StartsSwitchArmIfKeyword(line, out var lineAfterIfKeyword))
                {
                    if (arms.Count > 0)
                    {
                        var last = arms[^1];
                        var merged = string.IsNullOrWhiteSpace(last.Item2)
                            ? line
                            : last.Item2 + "\n" + line;
                        var bodyStartLine = string.IsNullOrWhiteSpace(last.Item2) ? physicalLine : last.Item4;
                        arms[^1] = (last.Item1, merged, last.Item3, bodyStartLine, last.Item5, last.Item6, last.Item7);
                    }

                    continue;
                }

                var rest = lineAfterIfKeyword.TrimStart();
                if (string.IsNullOrEmpty(rest)) continue;

                var matchValues = new List<string>();
                string armBody;

                if (rest[0] == '"' || rest[0] == '\'')
                {
                    var q = rest[0];
                    var k = 1;
                    while (k < rest.Length && rest[k] != q) k++;
                    if (k >= rest.Length) continue;
                    matchValues.Add(rest.Substring(1, k - 1));
                    armBody = rest.Substring(k + 1).Trim();
                    if (armBody.StartsWith("{", StringComparison.Ordinal) && armBody.EndsWith("}", StringComparison.Ordinal))
                        armBody = armBody.Substring(1, armBody.Length - 2);

                    var ifLine = physicalLine;
                    var bodyStartLine = string.IsNullOrEmpty(armBody) ? 0 : physicalLine;
                    arms.Add((matchValues, armBody, ifLine, bodyStartLine, null, null, null));
                }
                else
                {
                    if (TryParseSwitchTypePatternLine(rest, out var tpSubj, out var tpType, out var tpBind, out var tpTail))
                    {
                        var initialBody = tpTail;
                        var bodyStart = string.IsNullOrEmpty(initialBody) ? 0 : physicalLine;
                        arms.Add((matchValues, initialBody, physicalLine, bodyStart, tpSubj, tpType, tpBind));
                        continue;
                    }

                    var braceIdx = rest.IndexOf('{');
                    if (braceIdx < 0)
                    {
                        arms.Add((matchValues, rest, physicalLine, physicalLine, null, null, null));
                        continue;
                    }

                    var condPart = rest.Substring(0, braceIdx).Trim();
                    var braceBody = rest.Substring(braceIdx).Trim();
                    if (braceBody.StartsWith("{", StringComparison.Ordinal) && braceBody.EndsWith("}", StringComparison.Ordinal))
                        braceBody = braceBody.Substring(1, braceBody.Length - 2);

                    foreach (var part in condPart.Split(','))
                    {
                        var p = part.Trim();
                        var colonIdx = p.IndexOf(':');
                        var val = colonIdx >= 0 ? p.Substring(colonIdx + 1).Trim() : p;
                        if (val.StartsWith("\"", StringComparison.Ordinal) && val.EndsWith("\"", StringComparison.Ordinal))
                            val = val.Substring(1, val.Length - 2);
                        matchValues.Add(val);
                    }

                    arms.Add((matchValues, braceBody, physicalLine, physicalLine, null, null, null));
                }
            }

            return arms;
        }

        private static bool IsIdentifier(Token token, string value) =>
            token.Kind == TokenKind.Identifier && string.Equals(token.Value, value, StringComparison.OrdinalIgnoreCase);

        private void SkipSemicolon()
        {
            while (CurrentScanner.Current.Kind == TokenKind.Semicolon)
                CurrentScanner.Scan();
        }

        private void TryConsumeColon()
        {
            if (CurrentScanner.Current.Kind == TokenKind.Colon)
                CurrentScanner.Scan();
        }

        /// <summary>
        /// Попытка скомпилировать выражение вида `new TypeName(arg1, arg2, ...)` как вызов конструктора:
        /// samples:modularity:module2:TypeName_ctor_1(this, args...).
        ///
        /// Также поддерживаем object-initializer в круглых скобках:
        ///   new TypeName(Field1: expr1, Field2: expr2)  -> def TypeName + setobj по каждому полю.
        /// </summary>
        private bool TryCompileConstructorCall(
            string targetVarName,
            string rhsExpression,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            var trimmed = (rhsExpression ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                return false;

            // Конструкторы только через keyword `new`.
            // Это убирает неоднозначность с вызовами функций/процедур без `new`.
            if (!trimmed.StartsWith("new", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Length < 4 ||
                !char.IsWhiteSpace(trimmed[3]))
            {
                return false;
            }

            var afterNew = trimmed.Substring(3).TrimStart();
            if (afterNew.Length == 0)
                return false;

            // Ожидаем что-то вроде: TypeName(1, 2)  в afterNew
            var parenIdx = afterNew.IndexOf('(');
            if (parenIdx <= 0)
                return false;

            var typeName = afterNew.Substring(0, parenIdx).Trim();
            if (string.IsNullOrEmpty(typeName))
                return false;

            // Heuristic: тип для ctor/object-init должен начинаться с заглавной.
            // Это дополнительно защищает от кейсов вроде `new calculate(...)`.
            if (typeName.Length > 0 && char.IsLetter(typeName[0]) && char.IsLower(typeName[0]))
                return false;

            // RHS должен быть ровно `new TypeName(...)` без хвоста.
            var lastParenIdx = afterNew.LastIndexOf(')');
            if (lastParenIdx <= parenIdx || lastParenIdx != afterNew.Length - 1)
                return false;

            var argsText = afterNew.Substring(parenIdx + 1, lastParenIdx - parenIdx - 1).Trim();

            // object-initializer: new TypeName(Field: expr, ...)
            if (argsText.Contains(":", StringComparison.Ordinal))
            {
                return TryCompileTypeObjectInitializer(typeName, targetVarName, argsText, vars, ref memorySlotCounter, instructions);
            }

            // positional ctor args
            var argList = new List<string>();
            if (!string.IsNullOrEmpty(argsText))
            {
                var depth = 0;
                var sb = new System.Text.StringBuilder();
                var inString = false;
                char quote = '\0';
                var escaped = false;

                foreach (var ch in argsText)
                {
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
                        if (ch == quote)
                            inString = false;
                        continue;
                    }

                    if (ch == '"' || ch == '\'')
                    {
                        inString = true;
                        quote = ch;
                        sb.Append(ch);
                        continue;
                    }

                    if (ch == ',' && depth == 0)
                    {
                        var arg = sb.ToString().Trim();
                        if (!string.IsNullOrEmpty(arg))
                            argList.Add(arg);
                        sb.Clear();
                        continue;
                    }

                    if (ch is '(' or '{' or '[') depth++;
                    if (ch is ')' or '}' or ']') depth--;
                    sb.Append(ch);
                }

                var last = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(last))
                    argList.Add(last);
            }

            // Валидация аргументов до эмиссии инструкций.
            // Это важно: иначе при возврате false могли остаться "половинчатые" def/setobj.
            var preparedArgs = new List<(string Kind, int MemIndex, long IntValue, string? StrValue)>(argList.Count);
            foreach (var argExpr in argList)
            {
                var ae = argExpr.Trim();
                if (ae.Length == 0)
                    continue;

                if (vars.TryGetValue(ae, out var v) && v.Kind == "memory")
                {
                    preparedArgs.Add(("memory", v.Index, 0L, null));
                    continue;
                }

                if (int.TryParse(ae, out var intVal))
                {
                    preparedArgs.Add(("int", -1, intVal, null));
                    continue;
                }

                if ((ae.Length >= 2 && ae[0] == '"' && ae[^1] == '"') ||
                    (ae.Length >= 2 && ae[0] == '\'' && ae[^1] == '\''))
                {
                    var s = ae.Substring(1, ae.Length - 2);
                    preparedArgs.Add(("string", -1, 0L, s));
                    continue;
                }

                return false;
            }

            var qualifiedForObj = QualifyTypeNameForDefObj(typeName);
            var ctorName = $"{qualifiedForObj}_ctor_1";

            // 1) Выделяем слот под переменную targetVarName (this)
            if (!vars.TryGetValue(targetVarName, out var targetVar) || targetVar.Kind != "memory")
            {
                var thisSlot = memorySlotCounter++;
                vars[targetVarName] = ("memory", thisSlot);

                instructions.Add(CreatePushClassInstruction(qualifiedForObj));
                instructions.Add(new InstructionNode { Opcode = "defobj" });
                instructions.Add(CreatePopMemoryInstruction(thisSlot));

                targetVar = ("memory", thisSlot);
            }

            // 2) Вызов конструктора: this + args...
            // push this
            instructions.Add(CreatePushMemoryInstruction(targetVar.Index));

            // push args (пока очень простой случай: литералы / имена переменных)
            foreach (var prepared in preparedArgs)
            {
                switch (prepared.Kind)
                {
                    case "memory":
                        instructions.Add(CreatePushMemoryInstruction(prepared.MemIndex));
                        break;
                    case "int":
                        instructions.Add(CreatePushIntInstruction(prepared.IntValue));
                        break;
                    case "string":
                        instructions.Add(CreatePushStringInstruction(prepared.StrValue ?? ""));
                        break;
                    default:
                        return false;
                }
            }

            // callobj: arity = только аргументы (this на стеке под аргументами)
            instructions.Add(CreatePushIntInstruction(preparedArgs.Count));
            instructions.Add(new InstructionNode
            {
                Opcode = "callobj",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = ctorName }
                }
            });
            // callobj ctor кладёт DefObject на стек; объект уже в слоте this — сбрасываем дубликат.
            instructions.Add(CreatePopInstruction());

            RememberMemoryVarDefType(targetVarName, qualifiedForObj);
            return true;
        }

        /// <summary>
        /// Очень простой object-initializer для типов без явного конструктора:
        ///   Type(Field1: expr1, Field2: expr2)
        /// Разбираем список "Field: expr" через запятую и компилируем как:
        ///   new Type; setobj по каждому полю.
        /// Пока поддерживаем только однострочный инициализатор (весь внутри innerArgs).
        /// </summary>
        private bool TryCompileTypeObjectInitializer(
            string typeName,
            string targetVarName,
            string innerArgs,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            if (string.IsNullOrWhiteSpace(innerArgs))
                return false;

            // Разбиваем на top-level аргументы с учётом вложенных скобок.
            var argItems = new List<string>();
            {
                var depth = 0;
                var sb = new System.Text.StringBuilder();
                foreach (var ch in innerArgs)
                {
                    if (ch == ',' && depth == 0)
                    {
                        var item = sb.ToString().Trim();
                        if (!string.IsNullOrEmpty(item))
                            argItems.Add(item);
                        sb.Clear();
                        continue;
                    }
                    if (ch is '(' or '{' or '[') depth++;
                    if (ch is ')' or '}' or ']') depth--;
                    sb.Append(ch);
                }
                var last = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(last))
                    argItems.Add(last);
            }

            if (argItems.Count == 0)
                return false;

            var qualifiedDefType = QualifyTypeNameForDefObj(typeName);

            // Выделяем или находим слот под this (targetVarName).
            if (!vars.TryGetValue(targetVarName, out var targetVar) || targetVar.Kind != "memory")
            {
                var thisSlot = memorySlotCounter++;
                vars[targetVarName] = ("memory", thisSlot);

                instructions.Add(CreatePushClassInstruction(qualifiedDefType));
                instructions.Add(new InstructionNode { Opcode = "defobj" });
                instructions.Add(CreatePopMemoryInstruction(thisSlot));

                targetVar = ("memory", thisSlot);
            }

            // Для каждого Field: expr — компилируем expr в отдельный слот и делаем setobj.
            foreach (var item in argItems)
            {
                var colonIdx = item.IndexOf(':');
                if (colonIdx <= 0 || colonIdx >= item.Length - 1)
                    return false;

                var fieldName = item.Substring(0, colonIdx).Trim();
                var exprText = item.Substring(colonIdx + 1).Trim();
                if (string.IsNullOrEmpty(fieldName) || string.IsNullOrEmpty(exprText))
                    return false;

                var fieldSlot = memorySlotCounter++;
                if (!TryCompileExpressionToSlot(exprText, vars, fieldSlot, ref memorySlotCounter, instructions))
                    return false;

                var discardSlot = memorySlotCounter++;
                instructions.Add(CreatePushMemoryInstruction(targetVar.Index));
                instructions.Add(CreatePushStringInstruction(fieldName));
                instructions.Add(CreatePushMemoryInstruction(fieldSlot));
                instructions.Add(new InstructionNode { Opcode = "setobj" });
                instructions.Add(CreatePopMemoryInstruction(discardSlot));
            }

            RememberMemoryVarDefType(targetVarName, qualifiedDefType);
            return true;
        }
    }
}
