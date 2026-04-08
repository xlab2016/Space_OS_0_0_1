using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Magic.Kernel.Compilation;
using Magic.Kernel.Core;
using Magic.Kernel.Data;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Store;
using Magic.Kernel.Devices.Streams;
using Magic.Kernel.Devices.Streams.Inference;
using Magic.Kernel.Interpretation;
using Magic.Kernel.Types;
using Magic.Kernel;

namespace Magic.Kernel.Core.OS
{
    /// <summary>Hardware/OS abstraction layer: resolves type names to device instances (Def) and generalization types (DefGen).</summary>
    public static class Hal
    {
        /// <summary>DefObj: builds a <see cref="DefObject"/> from a compiled <see cref="DefType"/> schema (field names and declared types).</summary>
        public static DefObject DefObj(DefType type)
        {
            ArgumentNullException.ThrowIfNull(type);
            var obj = new DefObject(type);
            if (type.Fields == null)
                return obj;
            foreach (var f in type.Fields)
            {
                if (string.IsNullOrWhiteSpace(f.Name))
                    continue;
                obj.SetField(f.Name.Trim(), DefaultValueForDeclaredAgiType(f.Type));
            }
            return obj;
        }

        /// <summary>Resolves a user type from <paramref name="executableUnit"/> or builds a minimal <see cref="DefType"/> stub (namespace + short name).</summary>
        public static DefType ResolveOrStubDefType(string customTypeName, ExecutableUnit? executableUnit)
        {
            var trimmed = (customTypeName ?? "").Trim();
            var parsed = DefName.ParseQualified(trimmed);
            var unitNs = DefName.GetNamespace(executableUnit ?? new ExecutableUnit());
            var ns = string.IsNullOrEmpty(parsed.Namespace) ? unitNs : parsed.Namespace.Trim();
            var nm = string.IsNullOrEmpty(parsed.Name) ? trimmed : parsed.Name.Trim();

            var catalog = executableUnit?.Types;
            if (catalog != null)
            {
                foreach (var t in catalog)
                {
                    if (string.Equals(t.FullName, trimmed, StringComparison.OrdinalIgnoreCase))
                        return t;
                }

                foreach (var t in catalog)
                {
                    if (string.Equals(t.Name, nm, StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrEmpty(ns) ||
                         string.Equals((t.Namespace ?? "").Trim(), ns, StringComparison.OrdinalIgnoreCase)))
                        return t;
                }

                foreach (var t in catalog)
                {
                    if (string.Equals(t.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                        return t;
                }
            }

            return new DefType { Namespace = ns, Name = nm };
        }

        private static object? DefaultValueForDeclaredAgiType(string? typeName)
        {
            var t = (typeName ?? "any").Trim();
            if (t.Length == 0 || string.Equals(t, "any", StringComparison.OrdinalIgnoreCase))
                return null;
            if (string.Equals(t, "int", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "long", StringComparison.OrdinalIgnoreCase))
                return 0L;
            if (string.Equals(t, "string", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "str", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (string.Equals(t, "bool", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "boolean", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(t, "float", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "double", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "number", StringComparison.OrdinalIgnoreCase))
                return 0.0;
            if (string.Equals(t, "null", StringComparison.OrdinalIgnoreCase))
                return null;
            return null;
        }

        /// <summary>Def: creates instance by type name. E.g. "stream" → StreamDevice, "vault" → Vault (with executableUnit).</summary>
        /// <exception cref="DefTypeUnknownException">When type name is not supported.</exception>
        public static object Def(object? typeObj, ExecutableUnit? executableUnit)
        {
            if (typeObj is string customTypeName &&
                !string.IsNullOrWhiteSpace(customTypeName) &&
                !string.Equals(customTypeName, "stream", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(customTypeName, "execution", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(customTypeName, "vault", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(customTypeName, "database", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(customTypeName, "list", StringComparison.OrdinalIgnoreCase))
            {
                return new DefObject(ResolveOrStubDefType(customTypeName, executableUnit));
            }

            if (string.Equals(typeObj as string, "stream", StringComparison.OrdinalIgnoreCase))
                return new StreamDevice();
            if (string.Equals(typeObj as string, "execution", StringComparison.OrdinalIgnoreCase))
                return new Devices.ExecutionDevice(executableUnit);
            if (string.Equals(typeObj as string, "vault", StringComparison.OrdinalIgnoreCase))
            {
                var vault = new Vault
                {
                    ExecutableUnit = executableUnit
                };

                var indexNames = new List<string?>
                {
                    executableUnit?.System,
                    executableUnit?.Module,
                    executableUnit?.Name
                };

                var indices = new List<int>();
                if (executableUnit?.InstanceIndex is int instanceIndex)
                    indices.Add(instanceIndex);

                vault.Position = new StructurePosition
                {
                    IndexNames = indexNames.Where(n => n != null).Select(n => n!).ToList(),
                    Indices = indices
                };
                return vault;
            }
            if (string.Equals(typeObj as string, "database", StringComparison.OrdinalIgnoreCase))
            {
                return new Devices.Store.DatabaseDevice();
            }
            if (string.Equals(typeObj as string, "list", StringComparison.OrdinalIgnoreCase))
            {
                return new DefList();
            }
            throw new DefTypeUnknownException(typeObj);
        }

        /// <summary>Def with explicit arity arguments for schema-like declarations.</summary>
        public static object? Def(IReadOnlyList<object?> args, ExecutableUnit? executableUnit)
        {
            if (args == null || args.Count == 0)
                return null;
            if (args.Count == 1)
                return Def(args[0], executableUnit);

            if (args.Count == 2 && args[0] is string name && args[1] is string kind)
            {
                var sanitizedName = SanitizeDefName(name);
                if (string.Equals(kind, "table", StringComparison.OrdinalIgnoreCase))
                    return new Table { Name = sanitizedName };

                if (string.Equals(kind, "database", StringComparison.OrdinalIgnoreCase))
                    return new Data.Database { Name = sanitizedName };
            }

            // Type def: args = [typeName, "type"].
            if (args.Count == 2 &&
                args[0] is string typeName &&
                args[1] is string typeKind &&
                string.Equals(typeKind, "type", StringComparison.OrdinalIgnoreCase))
            {
                var normalizedTypeName = typeName.Trim();
                var unitNs = DefName.GetNamespace(executableUnit ?? new ExecutableUnit());
                var parsed = DefName.ParseQualified(normalizedTypeName);
                var ns = string.IsNullOrEmpty(parsed.Namespace) ? unitNs : parsed.Namespace.Trim();
                var shortNm = string.IsNullOrEmpty(parsed.Name) ? normalizedTypeName : parsed.Name.Trim();

                if (!string.IsNullOrWhiteSpace(normalizedTypeName))
                {
                    var existing = executableUnit?.Types?.FirstOrDefault(t =>
                        string.Equals(t.FullName, normalizedTypeName, StringComparison.OrdinalIgnoreCase) ||
                        (string.Equals(t.Name, shortNm, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals((t.Namespace ?? "").Trim(), ns, StringComparison.OrdinalIgnoreCase)));
                    if (existing != null)
                        return existing;
                }

                var created = new DefType
                {
                    Namespace = ns,
                    Name = shortNm
                };
                executableUnit?.Types?.Add(created);
                return created;
            }

            // Class def with inheritance:
            // args = [className, base1, base2, ...] → <see cref="DefClass.Inheritances"/> (not Generalizations).
            // push "CircleSquare"; push "Circle"; push "Square"; push 3; def
            if (args.Count >= 2 && args.All(a => a is string))
            {
                var names = args.Cast<string>()
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim())
                    .ToList();
                if (names.Count >= 2)
                {
                    var className = names[0];
                    var baseNames = names.Skip(1)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    return new DefClass(className, baseNames);
                }
            }

            // List def: args = [initialData, "list"] — creates a DefList pre-populated with items.
            if (args.Count == 2 &&
                args[1] is string listKind &&
                string.Equals(listKind, "list", StringComparison.OrdinalIgnoreCase))
            {
                var defList = new DefList();
                if (args[0] is System.Collections.Generic.IEnumerable<object?> typedItems)
                {
                    foreach (var item in typedItems)
                        defList.Items.Add(item);
                }
                else if (args[0] is System.Collections.IEnumerable nonGeneric && args[0] is not string)
                {
                    foreach (var item in nonGeneric)
                        defList.Items.Add(item);
                }
                return defList;
            }

            // Table column + database/table binding must run before generic DefType field/method branches:
            // <see cref="Table"/> inherits <see cref="DefType"/>, so [table, name, type, …, "column"] must not be
            // misclassified as type-field metadata (would return wrong shape or miss column → null from no match).

            // Column def: args = [parentTable, columnName, columnType, ...modifiers, "column"].
            if (args.Count >= 4 &&
                args[args.Count - 1] is string colKind &&
                string.Equals(colKind, "column", StringComparison.OrdinalIgnoreCase) &&
                args[0] is Table parentTable &&
                args[1] is string columnName &&
                args[2] is string columnType)
            {
                var modifiers = args.Skip(3).Take(args.Count - 4).ToList();
                var column = new Column
                {
                    Name = columnName,
                    Type = columnType,
                    Modifiers = modifiers
                };
                parentTable.Columns.Add(column);
                return parentTable;
            }

            // Table relation def:
            // args = [parentTable, relationName, referencedTableType, "single|array", "relation"].
            if (args.Count == 5 &&
                args[4] is string relKind &&
                string.Equals(relKind, "relation", StringComparison.OrdinalIgnoreCase) &&
                args[0] is Table relationParentTable &&
                args[1] is string relationName &&
                args[2] is string referencedTableType &&
                args[3] is string relationCardinality)
            {
                var isArray = string.Equals(relationCardinality.Trim(), "array", StringComparison.OrdinalIgnoreCase);
                relationParentTable.Relations.Add(new TableRelation
                {
                    Name = relationName,
                    ReferencedTableType = referencedTableType,
                    IsArray = isArray
                });
                return relationParentTable;
            }

            // Database add table: args = [db, tableRef, "table"].
            if (args.Count == 3 &&
                args[args.Count - 1] is string tableDefKind &&
                string.Equals(tableDefKind, "table", StringComparison.OrdinalIgnoreCase) &&
                args[0] is Data.Database db &&
                args[1] is Table tableRef)
            {
                tableRef.Database = db;
                db.AddTable(tableRef);
                return db;
            }

            // Type field def:
            // legacy: args = [ownerType, fieldName, "field", visibility]
            // typed:  args = [ownerType, fieldName, fieldType, "field", visibility]
            if (args.Count >= 4 &&
                args[0] is DefType ownerType &&
                args[1] is string fieldName)
            {
                var hasExplicitFieldType =
                    args.Count >= 5 &&
                    args[3] is string fkTyped &&
                    string.Equals(fkTyped, "field", StringComparison.OrdinalIgnoreCase) &&
                    (args[2] is string || args[2] is DefType);
                var isLegacyField =
                    args[2] is string fkLegacy &&
                    string.Equals(fkLegacy, "field", StringComparison.OrdinalIgnoreCase);
                if (!hasExplicitFieldType && !isLegacyField)
                    goto SkipTypeFieldDef;

                var fieldType = hasExplicitFieldType
                    ? args[2] switch
                    {
                        string s => s.Trim(),
                        DefType dt => (dt.Name ?? "").Trim(),
                        _ => "any"
                    }
                    : "any";
                var visibility = (hasExplicitFieldType ? args[4] : args[3]) as string;
                var normalizedFieldName = fieldName.Trim();
                if (!string.IsNullOrWhiteSpace(normalizedFieldName))
                {
                    var existingField = ownerType.Fields.FirstOrDefault(f =>
                        string.Equals(f.Name, normalizedFieldName, StringComparison.OrdinalIgnoreCase));
                    if (existingField == null)
                    {
                        ownerType.Fields.Add(new DefTypeField
                        {
                            Name = normalizedFieldName,
                            Type = string.IsNullOrWhiteSpace(fieldType) ? "any" : fieldType,
                            Visibility = string.IsNullOrWhiteSpace(visibility) ? "public" : visibility!.Trim().ToLowerInvariant()
                        });
                    }
                    else
                    {
                        if (hasExplicitFieldType && !string.IsNullOrWhiteSpace(fieldType))
                            existingField.Type = fieldType;
                        if (!string.IsNullOrWhiteSpace(visibility))
                            existingField.Visibility = visibility!.Trim().ToLowerInvariant();
                    }
                }

                return ownerType;
            }
            SkipTypeFieldDef:

            // Type method metadata: [ownerType, shortName, procedureFullName, returnType, "method", visibility, firstParamFq?].
            if ((args.Count == 6 || args.Count == 7) &&
                args[0] is DefType ownerForMethod &&
                args[1] is string methodShortName &&
                args[2] is string methodProcedureFullName &&
                args[3] is string methodReturnType &&
                args[4] is string methodKind &&
                string.Equals(methodKind, "method", StringComparison.OrdinalIgnoreCase) &&
                args[5] is string methodVisibility)
            {
                var shortN = (methodShortName ?? "").Trim();
                var fullN = (methodProcedureFullName ?? "").Trim();
                var firstParamFq = args.Count == 7 && args[6] is string fp && !string.IsNullOrWhiteSpace(fp)
                    ? fp.Trim()
                    : null;
                if (!string.IsNullOrWhiteSpace(shortN) && !string.IsNullOrWhiteSpace(fullN))
                {
                    var existingM = ownerForMethod.Methods.FirstOrDefault(m =>
                        string.Equals(m.FullName, fullN, StringComparison.OrdinalIgnoreCase));
                    if (existingM == null)
                    {
                        ownerForMethod.Methods.Add(new DefTypeMethod
                        {
                            Name = shortN,
                            FullName = fullN,
                            ReturnType = string.IsNullOrWhiteSpace(methodReturnType) ? "void" : methodReturnType.Trim(),
                            Visibility = string.IsNullOrWhiteSpace(methodVisibility) ? "public" : methodVisibility.Trim().ToLowerInvariant(),
                            FirstParameterTypeFq = firstParamFq
                        });
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(methodReturnType))
                            existingM.ReturnType = methodReturnType.Trim();
                        if (!string.IsNullOrWhiteSpace(methodVisibility))
                            existingM.Visibility = methodVisibility.Trim().ToLowerInvariant();
                        if (firstParamFq != null)
                            existingM.FirstParameterTypeFq = firstParamFq;
                    }
                }

                return ownerForMethod;
            }

            return null;
        }

        /// <summary>Strips angle brackets and similar symbols from Def name (table/database).</summary>
        private static string SanitizeDefName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            return name.Replace("<", "").Replace(">", "");
        }

        /// <summary>DefGen: adds generalizations to a def-object (IStreamDevice + IDefType). E.g. "file" → FileStreamDevice. Returns the same defObj.</summary>
        /// <exception cref="DefGenTypeUnknownException">When defObj is not a valid def-type or a generalization name is not supported.</exception>
        public static object DefGen(object? defObj, IReadOnlyList<object?> genValues)
        {
            var streamDevice = defObj as IStreamDevice;
            var defType = defObj as IDefType;

            if (streamDevice == null && defType == null)
                throw new DefGenTypeUnknownException(defObj);

            var genSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in genValues)
            {
                if (g is string s)
                    genSet.Add(s);
            }
            if (streamDevice != null && genSet.Contains("claw"))
            {
                defType.Generalizations.Add(new ClawStreamDevice());
                return defObj;
            }
            if (streamDevice != null && genSet.Contains("inference") && genSet.Contains("openai"))
            {
                defType.Generalizations.Add(new OpenAIInference());
                return defObj;
            }
            if (streamDevice != null && genSet.Contains("inference") && genSet.Contains("deepseek"))
            {
                defType.Generalizations.Add(new DeepSeekInference());
                return defObj;
            }
            if (streamDevice != null && genSet.Contains("messenger") && genSet.Contains("telegram") && genSet.Contains("client"))
            {
                defType.Generalizations.Add(new Devices.Streams.WTelegramStreamDevice());
                return defObj;
            }
            if (streamDevice != null && genSet.Contains("network") && genSet.Contains("file") && genSet.Contains("telegram"))
            {
                if (genSet.Contains("client"))
                {
                    defType.Generalizations.Add(new Devices.Streams.WTelegramNetworkFileStreamDevice());
                    return defObj;
                }

                defType.Generalizations.Add(new Devices.Streams.TelegramNetworkFileStreamDevice());
                return defObj;
            }

            foreach (var g in genValues)
            {
                if (string.Equals(g as string, "file", StringComparison.OrdinalIgnoreCase))
                    defType.Generalizations.Add(new FileStreamDevice());
                else if (string.Equals(g as string, "messenger", StringComparison.OrdinalIgnoreCase))
                    ;
                else if (string.Equals(g as string, "network", StringComparison.OrdinalIgnoreCase))
                    ;
                else if (string.Equals(g as string, "telegram", StringComparison.OrdinalIgnoreCase))
                    defType.Generalizations.Add(new Devices.Streams.Telegram());
                else if (string.Equals(g as string, "client", StringComparison.OrdinalIgnoreCase))
                    ;
                else if (string.Equals(g as string, "claw", StringComparison.OrdinalIgnoreCase))
                    ; // handled by combined genSet check above
                else if (string.Equals(g as string, "inference", StringComparison.OrdinalIgnoreCase))
                    ; // handled by combined genSet checks above
                else if (string.Equals(g as string, "openai", StringComparison.OrdinalIgnoreCase))
                    ; // handled by combined genSet checks above
                else if (string.Equals(g as string, "deepseek", StringComparison.OrdinalIgnoreCase))
                    ; // handled by combined genSet checks above
                else if (string.Equals(g as string, "postgres", StringComparison.OrdinalIgnoreCase))
                    defType.Generalizations.Add(new Devices.Store.Postgres());
                else if (g is Data.Database database)
                {
                    database.Device = defObj as IDatabaseDevice;
                    defType.Generalizations.Add(database);
                }
                else
                    throw new DefGenTypeUnknownException(g);
            }

            return defObj;
        }

        /// <summary>CallObj: invokes methodName on obj with args. Obj must implement IDefType.</summary>
        /// <exception cref="UnknownTypeException">When obj does not implement IDefType.</exception>
        public static async Task<object?> CallObjAsync(object? obj, string? methodName, object?[] args, ExecutionCallContext? context = null)
        {
            if (obj is not IDefType type)
                throw new UnknownTypeException(obj);
            DefType? defType = obj as DefType;
            DefStream? defStream = obj as DefStream;
            var previousDefTypeContext = defType?.ExecutionCallContext;
            var previousDefStreamContext = defStream?.ExecutionCallContext;
            if (defType != null)
                defType.ExecutionCallContext = context;
            if (defStream != null)
                defStream.ExecutionCallContext = context;
            try
            {
                return await type.CallObjAsync(methodName ?? "", args ?? Array.Empty<object?>()).ConfigureAwait(false);
            }
            finally
            {
                if (defType != null)
                    defType.ExecutionCallContext = previousDefTypeContext;
                if (defStream != null)
                    defStream.ExecutionCallContext = previousDefStreamContext;
            }
        }

        /// <summary>
        /// AwaitObj opcode: unwrap <see cref="Task"/> results, await <see cref="IDefType"/> (streams, db wrappers), or return plain CLR values as-is.
        /// Statement compiler emits awaitobj for <c>await expr</c> in assignments; SQL <c>read</c> returns lists/dictionaries, not IDefType.
        /// </summary>
        public static async Task<object?> ExecuteAwaitObjAsync(object? obj)
        {
            while (true)
            {
                if (obj is Task task)
                {
                    await task.ConfigureAwait(false);
                    var taskType = task.GetType();
                    if (taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(Task<>))
                    {
                        var resultProperty = taskType.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
                        obj = resultProperty?.GetValue(task);
                        continue;
                    }

                    return null;
                }

                if (obj is IDefType type)
                    return await type.AwaitObjAsync().ConfigureAwait(false);

                return obj;
            }
        }

        /// <summary>Await: generic await behavior for def-type object.</summary>
        public static async Task<object?> ExecuteAwaitAsync(object? obj)
        {
            if (obj is not IDefType type)
                throw new UnknownTypeException(obj);
            return await type.Await().ConfigureAwait(false);
        }

        /// <summary>StreamWait output: route stream items to "print"/"printd"/"println".</summary>
        public static void StreamWaitAsync(ExecutableUnit? unit, string? functionName, object? value)
        {
            // Interpreter passes "arg0..argN-1" as a single object (the args array).
            // For the common case streamwait print(x) where arity=1, unwrap so output matches print(x).
            if (value is object?[] singleArgs && singleArgs.Length == 1)
                value = singleArgs[0];

            switch (functionName?.ToLowerInvariant())
            {
                case "print":
                {
                    // "print" during streamwait is runtime-only (no console output).
                    var display = value is string s
                        ? s
                        : (value is System.Collections.IDictionary or System.Collections.IEnumerable && value is not string
                            ? JsonSerializer.Serialize(value)
                            : value?.ToString() ?? "");
                    if (unit != null)
                        unit.RuntimeOutputs.Add(value);
                    break;
                }

                case "println":
                {
                    // "println" during streamwait should be clean and should include new line.
                    var display = value is string s
                        ? s
                        : (value is System.Collections.IDictionary or System.Collections.IEnumerable && value is not string
                            ? JsonSerializer.Serialize(value)
                            : value?.ToString() ?? "");
                    Console.WriteLine(display);
                    if (unit != null)
                        unit.RuntimeOutputs.Add(value);
                    break;
                }

                case "printd":
                {
                    // Debug variant: includes execution prefix and a clear wrapper.
                    var prefix = ExecutionCallContext.GetPrefix(unit);
                    var display = value is string s
                        ? s
                        : (value is System.Collections.IDictionary or System.Collections.IEnumerable && value is not string
                            ? JsonSerializer.Serialize(value)
                            : value?.ToString() ?? "");
                    Console.Write(prefix + "Streamwait output: " + display);
                    Console.Out.Flush();
                    if (unit != null)
                        unit.RuntimeOutputs.Add(value);
                    break;
                }
            }
        }

        /// <summary>StreamWaitObj: waits next stream item according to mode.</summary>
        public static async Task<(bool IsEnd, object? Delta, object? Aggregate)> StreamWaitObjAsync(object? obj, string? streamWaitType)
        {
            if (obj is not IDefType type)
                throw new UnknownTypeException(obj);
            return await type.StreamWaitAsync(streamWaitType ?? "delta").ConfigureAwait(false);
        }

        /// <summary>GetObj: reads property/indexed member from object by name (case-insensitive for CLR properties).</summary>
        public static object? GetObj(object? obj, string? memberName)
        {
            if (obj == null || string.IsNullOrWhiteSpace(memberName))
                return null;

            if (obj is DeltaWeakDisposable weakDelta)
                return GetObj(weakDelta.Value, memberName);

            if (obj is Devices.Store.DatabaseDevice runtimeDb)
            {
                var databaseDevice = TryGetDatabaseDevice(runtimeDb);
                return databaseDevice?.FindTable(runtimeDb, memberName);
            }

            if (obj is Data.Database schemaDb)
                return schemaDb.Tables.FirstOrDefault(t => string.Equals(t.Name, memberName, StringComparison.OrdinalIgnoreCase));

            if (obj is StreamChunk chunk)
            {
                if (string.Equals(memberName, "data", StringComparison.OrdinalIgnoreCase))
                    return ConvertStreamChunkData(chunk);

                // Convenience: allow accessing JSON fields directly from StreamChunk,
                // e.g. delta.text when chunk.DataFormat == Json and Data is {"text":"..."}.
                var converted = ConvertStreamChunkData(chunk);
                if (converted is IDictionary rawDictChunk)
                {
                    foreach (DictionaryEntry entry in rawDictChunk)
                    {
                        if (entry.Key is string s && string.Equals(s, memberName, StringComparison.Ordinal))
                            return entry.Value;
                    }
                }
            }

            if (obj is DefObject defObject)
            {
                if (string.Equals(memberName, nameof(DefObject.Type), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(memberName, "Type", StringComparison.OrdinalIgnoreCase))
                    return defObject.Type;

                if (string.Equals(memberName, nameof(DefObject.TypeName), StringComparison.OrdinalIgnoreCase))
                    return defObject.TypeName;

                if (string.Equals(memberName, nameof(DefObject.Fields), StringComparison.OrdinalIgnoreCase))
                    return defObject.Fields;

                if (string.Equals(memberName, "FieldTypes", StringComparison.OrdinalIgnoreCase))
                {
                    return defObject.Fields.ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value.Type,
                        StringComparer.OrdinalIgnoreCase);
                }

                return defObject.TryGetField(memberName, out var fieldValue) ? fieldValue : null;
            }

            if (obj is IDictionary<string, object> dict)
                return TryGetDictionaryValue(dict, memberName);

            if (obj is IDictionary rawDict)
            {
                foreach (DictionaryEntry entry in rawDict)
                {
                    if (entry.Key is string s && string.Equals(s, memberName, StringComparison.Ordinal))
                        return entry.Value;
                }
                return null;
            }

            var type = obj.GetType();
            var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property != null)
                return property.GetValue(obj);

            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            return field?.GetValue(obj);
        }

        private static object? ConvertStreamChunkData(StreamChunk chunk)
        {
            var bytes = chunk.Data ?? Array.Empty<byte>();

            if (bytes.Length == 0)
            {
                return chunk.DataFormat switch
                {
                    DataFormat.Json => new Dictionary<string, object?>(),
                    DataFormat.Text or DataFormat.Code or DataFormat.Xml or DataFormat.Html or DataFormat.Css or DataFormat.JavaScript => string.Empty,
                    _ => bytes
                };
            }

            if (chunk.DataFormat == DataFormat.Json)
            {
                try
                {
                    using var doc = JsonDocument.Parse(bytes);
                    return ConvertJsonElement(doc.RootElement);
                }
                catch
                {
                    // Keep raw text when malformed JSON arrives to avoid breaking stream consumers.
                    return Encoding.UTF8.GetString(bytes);
                }
            }

            if (chunk.DataFormat is DataFormat.Text or DataFormat.Code or DataFormat.Xml or DataFormat.Html or DataFormat.Css or DataFormat.JavaScript)
                return Encoding.UTF8.GetString(bytes);

            return bytes;
        }

        private static object? ConvertJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Object => ConvertJsonObject(element),
                JsonValueKind.Array => ConvertJsonArray(element),
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => TryGetJsonNumber(element),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => element.GetRawText()
            };
        }

        private static Dictionary<string, object?> ConvertJsonObject(JsonElement element)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                dict[property.Name] = ConvertJsonElement(property.Value);
            return dict;
        }

        private static List<object?> ConvertJsonArray(JsonElement element)
        {
            var list = new List<object?>();
            foreach (var item in element.EnumerateArray())
                list.Add(ConvertJsonElement(item));
            return list;
        }

        private static object TryGetJsonNumber(JsonElement element)
        {
            if (element.TryGetInt64(out var longValue))
                return longValue;
            if (element.TryGetDecimal(out var decimalValue))
                return decimalValue;
            return element.GetDouble();
        }

        /// <summary>SetObj: writes property/indexed member on object and returns the updated object.
        /// The <c>setobj</c> opcode pushes that return value onto the interpreter stack — emit <c>pop</c> after statement-level assignments unless the value is consumed.</summary>
        public static object? SetObj(object? obj, string? memberName, object? value)
        {
            if (obj == null || string.IsNullOrWhiteSpace(memberName))
                return obj;

            if (obj is DefObject defObject)
            {
                defObject.SetField(memberName, value);
                return defObject;
            }

            if (obj is DeltaWeakDisposable weakDelta)
                return SetObj(weakDelta.Value, memberName, value);

            if (obj is Devices.Store.DatabaseDevice runtimeDb && value is Data.Table runtimeTable)
            {
                var databaseDevice = TryGetDatabaseDevice(runtimeDb);
                databaseDevice?.UpsertTable(runtimeDb, memberName, runtimeTable);
                return obj;
            }

            if (obj is Data.Database schemaDb && value is Data.Table schemaTable)
            {
                for (var i = 0; i < schemaDb.Tables.Count; i++)
                {
                    if (!string.Equals(schemaDb.Tables[i].Name, memberName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    schemaTable.Database = schemaDb;
                    schemaDb.Tables[i] = schemaTable;
                    return obj;
                }

                schemaDb.AddTable(schemaTable);
                return obj;
            }

            if (obj is IDictionary<string, object> dict)
            {
                dict[memberName] = value!;
                return obj;
            }

            if (obj is IDictionary rawDict)
            {
                rawDict[memberName] = value;
                return obj;
            }

            var type = obj.GetType();
            var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property != null && property.CanWrite)
            {
                property.SetValue(obj, ConvertValue(value, property.PropertyType));
                return obj;
            }

            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (field != null)
                field.SetValue(obj, ConvertValue(value, field.FieldType));

            return obj;
        }

        private static object? TryGetDictionaryValue(IDictionary<string, object> dict, string key)
        {
            if (dict.TryGetValue(key, out var direct))
                return direct;

            foreach (var kv in dict)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
            return null;
        }

        private static object? ConvertValue(object? value, Type targetType)
        {
            if (value == null)
                return null;
            if (targetType.IsInstanceOfType(value))
                return value;

            try
            {
                if (targetType.IsEnum && value is string enumName)
                    return Enum.Parse(targetType, enumName, ignoreCase: true);
                return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
            }
            catch
            {
                return value;
            }
        }

        /// <summary>
        /// Applies JSON-shaped data (tree from <c>opjson</c> / memory slot, or a JSON object string) onto a <see cref="DefObject"/>
        /// already created with <see cref="DefObj"/> for <paramref name="schema"/>. Nested objects and arrays use <paramref name="typeCatalog"/>.
        /// </summary>
        public static void Materialize(
            DefType schema,
            DefObject target,
            object? jsonRoot,
            IReadOnlyList<DefType>? typeCatalog)
        {
            ArgumentNullException.ThrowIfNull(schema);
            ArgumentNullException.ThrowIfNull(target);
            var map = NormalizeMaterializeRootToMap(jsonRoot);
            if (map.Count == 0)
                return;

            foreach (var kv in map)
            {
                var key = kv.Key;
                var field = schema.Fields?.FirstOrDefault(f => string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase));
                if (field == null)
                    continue;

                var declared = (field.Type ?? "any").Trim();
                target.SetField(field.Name.Trim(), ConvertJsonNodeToFieldType(declared, kv.Value, typeCatalog));
            }
        }

        private static Dictionary<string, object?> NormalizeMaterializeRootToMap(object? jsonRoot)
        {
            if (jsonRoot == null)
                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (jsonRoot is string s)
            {
                s = s.Trim();
                if (s.Length == 0 || string.Equals(s, "{}", StringComparison.Ordinal))
                    return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("materialize: JSON root must be an object.");
                return MaterializeJsonObjectToMap(doc.RootElement);
            }

            if (jsonRoot is JsonElement je)
            {
                if (je.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("materialize: JSON root must be an object.");
                return MaterializeJsonObjectToMap(je);
            }

            if (jsonRoot is IDictionary<string, object?> d0)
                return new Dictionary<string, object?>(d0, StringComparer.OrdinalIgnoreCase);

            if (jsonRoot is IDictionary<string, object> d1)
            {
                var m = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in d1)
                    m[kv.Key] = kv.Value;
                return m;
            }

            if (jsonRoot is IDictionary raw)
            {
                var m = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry e in raw)
                {
                    if (e.Key is string sk)
                        m[sk] = e.Value;
                }
                return m;
            }

            throw new InvalidOperationException($"materialize: data must be a JSON object, string, or dictionary, got {jsonRoot.GetType().Name}.");
        }

        private static Dictionary<string, object?> MaterializeJsonObjectToMap(JsonElement element)
        {
            var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in element.EnumerateObject())
                map[p.Name] = MaterializeJsonElementToValue(p.Value);
            return map;
        }

        private static object? MaterializeJsonElementToValue(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.Object => MaterializeJsonObjectToMap(el),
            JsonValueKind.Array => MaterializeJsonArrayToList(el),
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => TryMaterializeJsonNumber(el),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => el.GetRawText()
        };

        private static List<object?> MaterializeJsonArrayToList(JsonElement el)
        {
            var list = new List<object?>();
            foreach (var item in el.EnumerateArray())
                list.Add(MaterializeJsonElementToValue(item));
            return list;
        }

        private static object TryMaterializeJsonNumber(JsonElement element)
        {
            if (element.TryGetInt64(out var l))
                return l;
            if (element.TryGetDecimal(out var d))
                return d;
            return element.GetDouble();
        }

        private static bool TryGetArrayElementType(string declared, out string elementType)
        {
            elementType = "";
            var t = declared.Trim();
            if (!t.EndsWith("[]", StringComparison.Ordinal))
                return false;
            elementType = t.Substring(0, t.Length - 2).Trim();
            return elementType.Length > 0;
        }

        private static bool IsMaterializeDeclaredPrimitive(string declared)
        {
            var x = (declared ?? "").Trim().ToLowerInvariant();
            return x is "int" or "long" or "string" or "str" or "bool" or "boolean"
                or "float" or "double" or "number" or "any" or "null"
                or "float<decimal>";
        }

        private static DefType? ResolveDefTypeForMaterialize(string? typeName, IReadOnlyList<DefType>? catalog)
        {
            if (string.IsNullOrWhiteSpace(typeName) || catalog == null || catalog.Count == 0)
                return null;
            var tn = typeName.Trim();
            foreach (var dt in catalog)
            {
                if (string.Equals(dt.FullName, tn, StringComparison.OrdinalIgnoreCase))
                    return dt;
                if (string.Equals(dt.Name, tn, StringComparison.OrdinalIgnoreCase))
                    return dt;
            }

            DefType? fallback = null;
            foreach (var dt in catalog)
            {
                var n = dt.FullName;
                var last = n.LastIndexOf(':');
                var shortName = last >= 0 ? n[(last + 1)..] : n;
                if (string.Equals(shortName, tn, StringComparison.OrdinalIgnoreCase))
                    fallback = dt;
            }

            return fallback;
        }

        private static object? ConvertJsonNodeToFieldType(string declared, object? jsonValue, IReadOnlyList<DefType>? catalog)
        {
            if (TryGetArrayElementType(declared, out var elemTn))
            {
                var defList = new DefList { Name = elemTn };
                foreach (var item in EnumerateJsonSequence(jsonValue))
                {
                    if (IsMaterializeDeclaredPrimitive(elemTn))
                        defList.Items.Add(CoerceJsonScalarToDeclaredType(item, elemTn));
                    else
                    {
                        var nestedType = ResolveDefTypeForMaterialize(elemTn, catalog);
                        if (nestedType == null)
                        {
                            defList.Items.Add(item);
                            continue;
                        }

                        var child = DefObj(nestedType);
                        var childMap = TryNormalizeNestedObjectMap(item);
                        if (childMap != null)
                            Materialize(nestedType, child, childMap, catalog);
                        defList.Items.Add(child);
                    }
                }

                return defList;
            }

            if (IsMaterializeDeclaredPrimitive(declared))
                return CoerceJsonScalarToDeclaredType(jsonValue, declared);

            var refType = ResolveDefTypeForMaterialize(declared, catalog);
            if (refType == null)
                return UnwrapJsonNode(jsonValue);

            var objMap = TryNormalizeNestedObjectMap(jsonValue);
            if (objMap == null)
                return UnwrapJsonNode(jsonValue);

            var obj = DefObj(refType);
            Materialize(refType, obj, objMap, catalog);
            return obj;
        }

        private static IEnumerable<object?> EnumerateJsonSequence(object? jsonValue)
        {
            if (jsonValue == null)
                yield break;

            if (jsonValue is JsonElement je && je.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in je.EnumerateArray())
                    yield return MaterializeJsonElementToValue(x);
                yield break;
            }

            if (jsonValue is IList<object?> l0)
            {
                foreach (var x in l0)
                    yield return x;
                yield break;
            }

            if (jsonValue is IList<object> l1)
            {
                foreach (var x in l1)
                    yield return x;
                yield break;
            }

            if (jsonValue is IEnumerable seq && jsonValue is not string)
            {
                foreach (var x in seq)
                    yield return x;
            }
        }

        private static Dictionary<string, object?>? TryNormalizeNestedObjectMap(object? jsonValue)
        {
            if (jsonValue == null)
                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (jsonValue is JsonElement je && je.ValueKind == JsonValueKind.Object)
                return MaterializeJsonObjectToMap(je);

            if (jsonValue is IDictionary<string, object?> d0)
                return new Dictionary<string, object?>(d0, StringComparer.OrdinalIgnoreCase);

            if (jsonValue is IDictionary<string, object> d1)
            {
                var m = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in d1)
                    m[kv.Key] = kv.Value;
                return m;
            }

            if (jsonValue is IDictionary raw)
            {
                var m = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry e in raw)
                {
                    if (e.Key is string sk)
                        m[sk] = e.Value;
                }
                return m;
            }

            return null;
        }

        private static object? UnwrapJsonNode(object? jsonValue)
        {
            if (jsonValue is JsonElement je)
                return MaterializeJsonElementToValue(je);
            return jsonValue;
        }

        private static object? CoerceJsonScalarToDeclaredType(object? value, string declared)
        {
            var t = declared.Trim().ToLowerInvariant();
            value = UnwrapJsonNode(value);

            if (value == null)
                return null;

            try
            {
                switch (t)
                {
                    case "int":
                    case "long":
                        return value switch
                        {
                            long l => l,
                            int i => (long)i,
                            double d => (long)d,
                            decimal m => (long)m,
                            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ll) => ll,
                            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
                        };
                    case "float":
                    case "double":
                    case "number":
                        return value switch
                        {
                            double d => d,
                            float f => (double)f,
                            long l => (double)l,
                            decimal m => (double)m,
                            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dd) => dd,
                            _ => Convert.ToDouble(value, CultureInfo.InvariantCulture)
                        };
                    case "float<decimal>":
                        return value switch
                        {
                            decimal m => m,
                            double d => (decimal)d,
                            long l => l,
                            string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var mm) => mm,
                            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
                        };
                    case "bool":
                    case "boolean":
                        return value switch
                        {
                            bool b => b,
                            long l => l != 0,
                            string s when bool.TryParse(s, out var bb) => bb,
                            _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
                        };
                    case "string":
                    case "str":
                        return value.ToString();
                    case "any":
                    case "null":
                    default:
                        return value;
                }
            }
            catch
            {
                return value;
            }
        }

        private static Devices.Store.IDatabaseDevice? TryGetDatabaseDevice(Devices.Store.DatabaseDevice database)
        {
            if (database is Devices.Store.IDatabaseDevice selfDevice)
                return selfDevice;

            foreach (var g in database.Generalizations)
            {
                if (g is Devices.Store.IDatabaseDevice device)
                    return device;
            }

            return null;
        }
    }
}
