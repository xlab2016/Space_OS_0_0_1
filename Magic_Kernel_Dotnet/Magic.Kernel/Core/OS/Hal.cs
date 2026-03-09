using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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
using Magic.Kernel.Interpretation;

namespace Magic.Kernel.Core.OS
{
    /// <summary>Hardware/OS abstraction layer: resolves type names to device instances (Def) and generalization types (DefGen).</summary>
    public static class Hal
    {
        /// <summary>Def: creates instance by type name. E.g. "stream" → StreamDevice, "vault" → Vault (with executableUnit).</summary>
        /// <exception cref="DefTypeUnknownException">When type name is not supported.</exception>
        public static object Def(object? typeObj, ExecutableUnit? executableUnit)
        {
            if (string.Equals(typeObj as string, "stream", StringComparison.OrdinalIgnoreCase))
                return new StreamDevice();
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
                return new Devices.Store.Database();
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

            // Column def: args = [parentTable, columnName, columnType, ...modifiers, "column"].
            if (args.Count >= 4 &&
                args[args.Count - 1] is string defKind &&
                string.Equals(defKind, "column", StringComparison.OrdinalIgnoreCase) &&
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

            // Database add table: args = [db, tableRef, "table"].
            if (args.Count == 3 &&
                args[args.Count - 1] is string tableDefKind &&
                string.Equals(tableDefKind, "table", StringComparison.OrdinalIgnoreCase) &&
                args[0] is Data.Database db &&
                args[1] is Table tableRef)
            {
                db.AddTable(tableRef);
                return db;
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
            if (streamDevice != null && genSet.Contains("network") && genSet.Contains("file") && genSet.Contains("telegram"))
            {
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
        public static async Task<object?> CallObjAsync(object? obj, string? methodName, object?[] args)
        {
            if (obj is not IDefType type)
                throw new UnknownTypeException(obj);
            return await type.CallObjAsync(methodName ?? "", args ?? Array.Empty<object?>()).ConfigureAwait(false);
        }

        /// <summary>AwaitObj: awaits def-object (e.g. DefStream returns this). Obj must implement IDefType.</summary>
        /// <exception cref="UnknownTypeException">When obj does not implement IDefType.</exception>
        public static async Task<object?> ExecuteAwaitObjAsync(object? obj)
        {
            if (obj is not IDefType type)
                throw new UnknownTypeException(obj);
            return await type.AwaitObjAsync().ConfigureAwait(false);
        }

        /// <summary>Await: generic await behavior for def-type object.</summary>
        public static async Task<object?> ExecuteAwaitAsync(object? obj)
        {
            if (obj is not IDefType type)
                throw new UnknownTypeException(obj);
            return await type.Await().ConfigureAwait(false);
        }

        /// <summary>StreamWait output: switch on function name — "print" writes to console and adds to RuntimeOutputs.</summary>
        public static void StreamWaitAsync(ExecutableUnit? unit, string? functionName, object? value)
        {
            var prefix = Magic.Kernel.Interpretation.ExecutionContext.GetPrefix(unit);

            switch (functionName?.ToLowerInvariant())
            {
                case "print":
                    var display = value is string s ? s : (value is System.Collections.IDictionary or System.Collections.IEnumerable && value is not string
                        ? JsonSerializer.Serialize(value)
                        : value?.ToString() ?? "");
                    Console.WriteLine(prefix + "Streamwait output: " + display);
                    if (unit != null)
                        unit.RuntimeOutputs.Add(value);
                    break;
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

            if (obj is Devices.Store.Database runtimeDb)
            {
                var databaseDevice = TryGetDatabaseDevice(runtimeDb);
                return databaseDevice?.FindTable(runtimeDb, memberName);
            }

            if (obj is Data.Database schemaDb)
                return schemaDb.Tables.FirstOrDefault(t => string.Equals(t.Name, memberName, StringComparison.OrdinalIgnoreCase));

            if (obj is StreamChunk chunk && string.Equals(memberName, "data", StringComparison.OrdinalIgnoreCase))
                return ConvertStreamChunkData(chunk);

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

        /// <summary>SetObj: writes property/indexed member on object and returns the updated object.</summary>
        public static object? SetObj(object? obj, string? memberName, object? value)
        {
            if (obj == null || string.IsNullOrWhiteSpace(memberName))
                return obj;

            if (obj is Devices.Store.Database runtimeDb && value is Data.Table runtimeTable)
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

        private static Devices.Store.IDatabaseDevice? TryGetDatabaseDevice(Devices.Store.Database database)
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
