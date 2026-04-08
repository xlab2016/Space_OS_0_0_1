using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Magic.Kernel.Core;
using Magic.Kernel.Core.OS;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.SSC;
using Magic.Kernel.Devices.Streams;
using Magic.Kernel.Functions;
using Magic.Kernel.Runtime;
using Magic.Kernel.Types;
using Magic.Kernel;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text;

namespace Magic.Kernel.Interpretation
{
    public interface IVaultReader
    {
        string? Read(string key);
    }

    public sealed class EnvironmentVaultReader : IVaultReader
    {
        public string? Read(string key)
        {
            return Environment.GetEnvironmentVariable(key);
        }
    }

    /// <summary>Vault reader from an in-memory dictionary (e.g. for library usage).</summary>
    public sealed class DictionaryVaultReader : IVaultReader
    {
        private readonly IReadOnlyDictionary<string, string?> _data;

        public DictionaryVaultReader(IReadOnlyDictionary<string, string?> data)
        {
            _data = data ?? new Dictionary<string, string?>();
        }

        public string? Read(string key)
        {
            return _data.TryGetValue(key, out var v) ? v : null;
        }
    }

    public class SystemFunctions
    {
        private readonly KernelConfiguration? _configuration;
        private readonly List<object> _stack;
        private readonly Func<MemoryAddress, (bool Found, object? Value)> _memoryReader;
        private readonly Action<MemoryAddress, object?> _memoryWriter;
        private readonly PrintFunctions _printFunctions;
        private readonly IVaultReader _vaultReader;
        private readonly Compilation.ExecutableUnit? _currentUnit;
        private readonly Func<Magic.Kernel.Devices.Streams.ClawSocketContext?> _socketAccessor;
        private readonly TextReader? _standardInput;
        private readonly Func<InterpreterDebugSession?>? _debugSessionAccessor;

        public SystemFunctions(KernelConfiguration? configuration, List<object> stack, Dictionary<long, object> memory)
            : this(configuration, stack, memory, new EnvironmentVaultReader(), null, null, null, null)
        {
        }

        public SystemFunctions(KernelConfiguration? configuration, List<object> stack, Dictionary<long, object> memory, IVaultReader vaultReader, Compilation.ExecutableUnit? currentUnit = null, Func<Magic.Kernel.Devices.Streams.ClawSocketContext?>? socketAccessor = null, TextReader? standardInput = null, Func<InterpreterDebugSession?>? debugSessionAccessor = null)
            : this(
                configuration,
                stack,
                memoryAddress =>
                {
                    if (memoryAddress.Index.HasValue && memory.TryGetValue(memoryAddress.Index.Value, out var memoryValue))
                    {
                        return (true, memoryValue);
                    }

                    return (false, null);
                },
                (memoryAddress, value) =>
                {
                    if (!memoryAddress.Index.HasValue)
                        throw new InvalidOperationException("Memory address index is not specified.");

                    memory[memoryAddress.Index.Value] = value!;
                },
                vaultReader,
                currentUnit,
                socketAccessor,
                standardInput,
                debugSessionAccessor)
        {
        }

        internal SystemFunctions(
            KernelConfiguration? configuration,
            List<object> stack,
            Func<MemoryAddress, (bool Found, object? Value)> memoryReader,
            Action<MemoryAddress, object?> memoryWriter,
            IVaultReader vaultReader,
            Compilation.ExecutableUnit? currentUnit,
            Func<Magic.Kernel.Devices.Streams.ClawSocketContext?>? socketAccessor,
            TextReader? standardInput = null,
            Func<InterpreterDebugSession?>? debugSessionAccessor = null)
        {
            _configuration = configuration;
            _stack = stack;
            _memoryReader = memoryReader ?? throw new ArgumentNullException(nameof(memoryReader));
            _memoryWriter = memoryWriter ?? throw new ArgumentNullException(nameof(memoryWriter));
            _printFunctions = new PrintFunctions(configuration, stack, memoryReader, currentUnit);
            _vaultReader = vaultReader ?? throw new ArgumentNullException(nameof(vaultReader));
            _currentUnit = currentUnit;
            _socketAccessor = socketAccessor ?? (() => null);
            _standardInput = standardInput;
            _debugSessionAccessor = debugSessionAccessor;
        }

        private bool TryReadMemory(MemoryAddress memoryAddress, out object? value)
        {
            var result = _memoryReader(memoryAddress);
            value = result.Value;
            return result.Found;
        }

        public async Task<bool> ExecuteAsync(CallInfo callInfo)
        {
            switch (callInfo.FunctionName.ToLower())
            {
                case "origin":
                    await ExecuteOriginAsync(callInfo);
                    return true;

                case "print":
                case "println":
                    await ExecutePrintAsync(callInfo);
                    return true;

                case "printd":
                    await ExecutePrintdAsync(callInfo);
                    return true;

                case "debug":
                case "debugger":
                    await ExecuteDebugAsync(callInfo);
                    return true;

                case "intersect":
                    await ExecuteIntersectAsync(callInfo);
                    return true;

                case "compile":
                    await ExecuteCompileAsync(callInfo);
                    return true;

                case "get":
                    await ExecuteGetAsync(callInfo);
                    return true;

                case "opjson":
                    await ExecuteOpJsonAsync(callInfo);
                    return true;

                case "convert":
                    await ExecuteConvertAsync(callInfo);
                    return true;

                case "spawn":
                    await ExecuteSpawnAsync(callInfo);
                    return true;

                case "unit":
                    await ExecuteUnitAsync(callInfo);
                    return true;

                case "format":
                    await ExecuteFormatAsync(callInfo);
                    return true;

                case "length":
                    ExecuteLength(callInfo);
                    return true;

                case "materialize":
                    ExecuteMaterialize(callInfo);
                    return true;

                case "is":
                    ExecuteIs(callInfo);
                    return true;

                case "read":
                    await ExecuteReadAsync();
                    return true;
                case "readln":
                    await ExecuteReadLnAsync();
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Console input helper for AGI: `read()` -> pushes parsed number (long/decimal) or string.
        /// </summary>
        private Task ExecuteReadAsync()
        {
            var line = (_standardInput ?? Console.In).ReadLine();
            line ??= string.Empty;
            if (long.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                _stack.Add(l);
                return Task.CompletedTask;
            }
            if (decimal.TryParse(line.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            {
                _stack.Add(d);
                return Task.CompletedTask;
            }
            _stack.Add(line);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Console input helper for AGI: `readln()` -> pushes raw line as string.
        /// Unlike `read()`, it does not coerce numeric values.
        /// </summary>
        private Task ExecuteReadLnAsync()
        {
            var line = (_standardInput ?? Console.In).ReadLine() ?? string.Empty;
            _stack.Add(line);
            return Task.CompletedTask;
        }

        /// <summary>Erlang-like spawn: enqueue task (current unit + procedure/function) to runtime TaskQueue. Pushes 0 (ok) on stack.</summary>
        private async Task ExecuteSpawnAsync(CallInfo callInfo)
        {
            var unit = _currentUnit;
            if (unit == null)
                throw new InvalidOperationException("spawn requires current execution unit.");
            var runtime = _configuration?.Runtime;
            if (runtime == null)
                throw new InvalidOperationException("spawn requires KernelConfiguration.Runtime (start kernel with StartKernel).");

            string? entryName = null;
            if (callInfo.Parameters.TryGetValue("0", out var nameObj))
                entryName = nameObj?.ToString();
            else if (callInfo.Parameters.TryGetValue("name", out var n))
                entryName = n?.ToString();

            CallInfo? spawnCallInfo = null;
            if (callInfo.Parameters != null)
            {
                var args = callInfo.Parameters
                    .Where(p => p.Key != "name" && p.Key != "0")
                    .OrderBy(p => int.TryParse(p.Key, out var i) ? i : 999)
                    .Select(p => p.Value)
                    .ToList();
                if (args.Count > 0)
                {
                    spawnCallInfo = new CallInfo { FunctionName = entryName ?? "" };
                    for (var i = 0; i < args.Count; i++)
                        spawnCallInfo.Parameters[$"{i}"] = args[i]!;
                }
            }

            await runtime.SpawnAsync(unit, entryName, spawnCallInfo, debugSession: _debugSessionAccessor?.Invoke()).ConfigureAwait(false);
            _stack.Add(0); // ok
        }

        private async Task ExecuteOpJsonAsync(CallInfo callInfo)
        {
            if (!callInfo.Parameters.TryGetValue("source", out var sourceObj))
                throw new InvalidOperationException("opjson requires 'source' parameter.");
            if (sourceObj is not MemoryAddress sourceAddr || !sourceAddr.Index.HasValue)
                throw new InvalidOperationException("opjson source must be memory address.");

            var sourceIndex = sourceAddr.Index.Value;
            var operation = callInfo.Parameters.TryGetValue("operation", out var opObj)
                ? (await GetValueFromParameterAsync(opObj).ConfigureAwait(false))?.ToString()
                : null;
            var path = callInfo.Parameters.TryGetValue("path", out var pathObj)
                ? (await GetValueFromParameterAsync(pathObj).ConfigureAwait(false))?.ToString()
                : null;

            // data: сначала именованный параметр "data", иначе позиционный "0"
            object? data = null;
            if (callInfo.Parameters.TryGetValue("data", out var dataObj))
            {
                data = await GetValueFromParameterAsync(dataObj).ConfigureAwait(false);
            }
            else if (callInfo.Parameters.TryGetValue("0", out var dataPositional))
            {
                data = await GetValueFromParameterAsync(dataPositional).ConfigureAwait(false);
            }

            // dataJson: сначала "dataJson", иначе позиционный "1"
            string? dataJson = null;
            if (callInfo.Parameters.TryGetValue("dataJson", out var dataJsonObj))
            {
                dataJson = (await GetValueFromParameterAsync(dataJsonObj).ConfigureAwait(false))?.ToString();
            }
            else if (callInfo.Parameters.TryGetValue("1", out var dataJsonPositional))
            {
                dataJson = (await GetValueFromParameterAsync(dataJsonPositional).ConfigureAwait(false))?.ToString();
            }

            object root = NormalizeJsonRoot(TryReadMemory(sourceAddr, out var current) ? current : null);
            var value = dataJson != null ? ParseDataJsonLiteral(dataJson) : data;

            switch ((operation ?? "").ToLowerInvariant())
            {
                case "set":
                    root = SetPath(root, path ?? "", value);
                    break;
                case "append":
                    root = AppendPath(root, path ?? "", value);
                    break;
                case "ensureobject":
                    root = EnsurePathContainer(root, path ?? "", expectArray: false);
                    break;
                case "ensurearray":
                    root = EnsurePathContainer(root, path ?? "", expectArray: true);
                    break;
                default:
                    throw new InvalidOperationException($"opjson operation '{operation}' is not supported.");
            }

            _memoryWriter(sourceAddr, root);
            _stack.Add(root);
        }

        /// <summary>AGI <c>length(x)</c>: для <see cref="DefList"/> — число элементов; для строки — длина; для <see cref="ICollection"/> — <c>Count</c>.</summary>
        private void ExecuteLength(CallInfo callInfo)
        {
            if (!callInfo.Parameters.TryGetValue("0", out var p))
                throw new InvalidOperationException("length requires one argument.");

            object? val = p;
            if (p is MemoryAddress ma && TryReadMemory(ma, out var memVal))
                val = memVal;

            long len;
            if (val is DefList dl)
                len = dl.Items.Count;
            else if (val is string s)
                len = s.Length;
            else if (val is Array arr)
                len = arr.Length;
            else if (val is System.Collections.ICollection coll)
                len = coll.Count;
            else
                throw new InvalidOperationException(
                    $"length: unsupported value type '{val?.GetType().Name ?? "null"}'.");
            _stack.Add(len);
        }

        private Task ExecuteGetAsync(CallInfo callInfo)
        {
            object? key = null;
            if (callInfo.Parameters.TryGetValue("0", out var keyObj))
                key = keyObj;
            else if (callInfo.Parameters.Count > 0)
                key = callInfo.Parameters.Values.FirstOrDefault();

            var symbolic = key?.ToString() ?? "";
            if (string.Equals(symbolic, ":time", StringComparison.OrdinalIgnoreCase))
            {
                _stack.Add(DateTime.UtcNow);
                return Task.CompletedTask;
            }

            if (string.Equals(symbolic, ":socket", StringComparison.OrdinalIgnoreCase))
            {
                var socketCtx = _socketAccessor();
                _stack.Add((object?)socketCtx ?? (object)"");
                return Task.CompletedTask;
            }

            _stack.Add(symbolic);
            return Task.CompletedTask;
        }

        private static object NormalizeJsonRoot(object? current)
        {
            if (current is Dictionary<string, object> or List<object>)
                return current;
            if (current is string s)
            {
                if (string.Equals(s.Trim(), "[]", StringComparison.Ordinal))
                    return new List<object>();
                if (string.Equals(s.Trim(), "{}", StringComparison.Ordinal))
                    return new Dictionary<string, object>(StringComparer.Ordinal);
            }
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        private static object? ParseDataJsonLiteral(string raw)
        {
            using var doc = JsonDocument.Parse(raw);
            return ConvertJsonElement(doc.RootElement);
        }

        private static object? ConvertJsonElement(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    var map = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var p in el.EnumerateObject())
                        map[p.Name] = ConvertJsonElement(p.Value)!;
                    return map;
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (var item in el.EnumerateArray())
                        list.Add(ConvertJsonElement(item)!);
                    return list;
                case JsonValueKind.String:
                    return el.GetString() ?? "";
                case JsonValueKind.Number:
                    if (el.TryGetInt64(out var l)) return l;
                    if (el.TryGetDouble(out var d)) return d;
                    return el.ToString();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                    return null;
                default:
                    return el.ToString();
            }
        }

        private sealed class PathSegment
        {
            public string? Property { get; set; }
            public int? Index { get; set; }
        }

        private static List<PathSegment> ParsePath(string path)
        {
            var result = new List<PathSegment>();
            var text = path?.Trim() ?? "";
            if (string.IsNullOrEmpty(text))
                return result;

            var i = 0;
            while (i < text.Length)
            {
                if (text[i] == '.')
                {
                    i++;
                    continue;
                }
                if (text[i] == '[')
                {
                    i++;
                    var start = i;
                    while (i < text.Length && text[i] != ']') i++;
                    var token = text.Substring(start, i - start);
                    if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                        result.Add(new PathSegment { Index = idx });
                    if (i < text.Length && text[i] == ']') i++;
                    continue;
                }

                var propStart = i;
                while (i < text.Length && text[i] != '.' && text[i] != '[') i++;
                var prop = text.Substring(propStart, i - propStart);
                result.Add(new PathSegment { Property = prop });
            }

            return result;
        }

        private static object EnsurePathContainer(object root, string path, bool expectArray)
        {
            var segments = ParsePath(path);
            if (segments.Count == 0)
                return expectArray ? (root is List<object> ? root : new List<object>()) : (root is Dictionary<string, object> ? root : new Dictionary<string, object>(StringComparer.Ordinal));

            EnsureContainer(root, segments, expectArray);
            return root;
        }

        private static object SetPath(object root, string path, object? value)
        {
            var segments = ParsePath(path);
            if (segments.Count == 0)
                return value ?? new Dictionary<string, object>(StringComparer.Ordinal);

            var parent = EnsureContainer(root, segments.Take(segments.Count - 1).ToList(), expectArray: false);
            var last = segments[segments.Count - 1];
            var normalizedValue = NormalizeJsonValue(value);

            if (last.Property != null)
            {
                var map = parent as Dictionary<string, object> ?? new Dictionary<string, object>(StringComparer.Ordinal);
                map[last.Property] = normalizedValue!;
                if (segments.Count == 1) root = map;
            }
            else if (last.Index.HasValue)
            {
                var arr = parent as List<object> ?? new List<object>();
                EnsureArraySize(arr, last.Index.Value + 1);
                arr[last.Index.Value] = normalizedValue!;
                if (segments.Count == 1) root = arr;
            }

            return root;
        }

        private static object AppendPath(object root, string path, object? value)
        {
            var segments = ParsePath(path);
            if (segments.Count == 0)
            {
                var rootArr = root as List<object> ?? new List<object>();
                rootArr.Add(NormalizeJsonValue(value)!);
                return rootArr;
            }

            var target = EnsureContainer(root, segments, expectArray: true);
            var arr = target as List<object> ?? new List<object>();
            arr.Add(NormalizeJsonValue(value)!);
            return root;
        }

        private static object EnsureContainer(object root, List<PathSegment> segments, bool expectArray)
        {
            object current = root;
            if (segments.Count == 0)
                return current;

            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                var last = i == segments.Count - 1;
                var nextExpectArray = last ? expectArray : segments[i + 1].Index.HasValue;

                if (seg.Property != null)
                {
                    var map = current as Dictionary<string, object>;
                    if (map == null)
                        throw new InvalidOperationException("opjson path expects object container.");

                    if (!map.TryGetValue(seg.Property, out var child) || child == null)
                    {
                        child = nextExpectArray ? new List<object>() : new Dictionary<string, object>(StringComparer.Ordinal);
                        map[seg.Property] = child;
                    }
                    current = child;
                    continue;
                }

                if (!seg.Index.HasValue)
                    continue;

                var arr = current as List<object>;
                if (arr == null)
                    throw new InvalidOperationException("opjson path expects array container.");

                EnsureArraySize(arr, seg.Index.Value + 1);
                if (arr[seg.Index.Value] == null)
                    arr[seg.Index.Value] = nextExpectArray ? new List<object>() : new Dictionary<string, object>(StringComparer.Ordinal);
                current = arr[seg.Index.Value]!;
            }

            return current;
        }

        private static void EnsureArraySize(List<object> arr, int size)
        {
            while (arr.Count < size)
                arr.Add(new Dictionary<string, object>(StringComparer.Ordinal));
        }

        private static object? NormalizeJsonValue(object? value)
        {
            if (value is null)
                return null;

            // Специальный случай: бинарные данные в JSON храним как base64-строку
            if (value is byte[] || value is IReadOnlyList<byte> || (value is IEnumerable<byte> && value is not string))
            {
                return ConvertValue(value, "base64");
            }

            return value;
        }

        private async Task ExecuteConvertAsync(CallInfo callInfo)
        {
            if (callInfo == null) throw new ArgumentNullException(nameof(callInfo));

            object? valueParam = null;
            if (callInfo.Parameters.TryGetValue("value", out var v))
                valueParam = v;
            else if (callInfo.Parameters.TryGetValue("0", out var v0))
                valueParam = v0;
            else if (callInfo.Parameters.Count > 0)
                valueParam = callInfo.Parameters.Values.FirstOrDefault();

            object? typeParam = null;
            if (callInfo.Parameters.TryGetValue("type", out var t))
                typeParam = t;
            else if (callInfo.Parameters.TryGetValue("1", out var t1))
                typeParam = t1;

            if (typeParam == null)
                throw new InvalidOperationException("convert requires 'type' parameter.");

            var typeString = (await GetValueFromParameterAsync(typeParam).ConfigureAwait(false))?.ToString();
            var value = valueParam != null ? await GetValueFromParameterAsync(valueParam).ConfigureAwait(false) : null;

            var converted = ConvertValue(value, typeString);
            _stack.Add(converted!);
        }

        /// <summary>Converts value by unit. E.g. unit(20, "mb") → 20971520, unit(20971520, "1/mb") → 20.</summary>
        private async Task ExecuteUnitAsync(CallInfo callInfo)
        {
            object? valueParam = null;
            if (callInfo.Parameters.TryGetValue("0", out var v0))
                valueParam = v0;
            else if (callInfo.Parameters.TryGetValue("value", out var v))
                valueParam = v;

            object? unitParam = null;
            if (callInfo.Parameters.TryGetValue("1", out var u1))
                unitParam = u1;
            else if (callInfo.Parameters.TryGetValue("unit", out var u))
                unitParam = u;

            object? typeParam = null;
            if (callInfo.Parameters.TryGetValue("2", out var t2))
                typeParam = t2;
            else if (callInfo.Parameters.TryGetValue("type", out var t))
                typeParam = t;

            if (valueParam == null)
                throw new InvalidOperationException("unit requires numeric value (first argument or 'value').");

            var valueObj = await GetValueFromParameterAsync(valueParam).ConfigureAwait(false);
            var unitString = unitParam != null
                ? (await GetValueFromParameterAsync(unitParam).ConfigureAwait(false))?.ToString()?.Trim().ToLowerInvariant()
                : "b";
            var typeString = typeParam != null
                ? (await GetValueFromParameterAsync(typeParam).ConfigureAwait(false))?.ToString()
                : null;

            var num = ToNumber(valueObj);
            var converted = ApplyUnit(num, unitString);
            if (!string.IsNullOrWhiteSpace(typeString))
                converted = ConvertValue(converted, typeString);
            _stack.Add(converted);
        }

        private static long ToNumber(object? value)
        {
            if (value == null) return 0;
            if (value is long l) return l;
            if (value is int i) return i;
            if (value is double d) return (long)d;
            if (value is float f) return (long)f;
            if (value is decimal dec) return (long)dec;
            if (long.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            if (double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var dparsed))
                return (long)dparsed;
            return 0;
        }

        private static object ApplyUnit(long value, string? unit)
        {
            if (string.IsNullOrEmpty(unit)) return value;
            var normalizedUnit = unit.Trim().ToLowerInvariant();
            var inverse = false;
            if (normalizedUnit.StartsWith("1/", StringComparison.Ordinal))
            {
                inverse = true;
                normalizedUnit = normalizedUnit.Substring(2);
            }

            var factor = normalizedUnit switch
            {
                "b" => 1L,
                "kb" => 1024L,
                "mb" => 1024L * 1024L,
                "gb" => 1024L * 1024L * 1024L,
                "tb" => 1024L * 1024L * 1024L * 1024L,
                _ => throw new InvalidOperationException($"unit: unknown unit '{unit}'. Use b, kb, mb, gb, tb or inverse form 1/mb.")
            };

            if (!inverse)
                return value * factor;

            if (factor == 1L)
                return value;

            return value % factor == 0
                ? value / factor
                : (decimal)value / factor;
        }

        /// <summary>format("{0} {1}", v0, v1, ...) — substitutes {0}, {1}, ... with argument values. Pushes result string.</summary>
        private async Task ExecuteFormatAsync(CallInfo callInfo)
        {
            if (!callInfo.Parameters.TryGetValue("0", out var fmtObj))
                throw new InvalidOperationException("format requires format string as first argument (0).");
            var formatString = (await GetValueFromParameterAsync(fmtObj).ConfigureAwait(false))?.ToString() ?? string.Empty;
            var args = new List<object?>();
            var idx = 1;
            while (callInfo.Parameters.TryGetValue(idx.ToString(), out var argObj))
            {
                args.Add(await GetValueFromParameterAsync(argObj).ConfigureAwait(false));
                idx++;
            }
            try
            {
                for (var i = 0; i < args.Count; i++)
                {
                    if (args[i] is DefObject d)
                        args[i] = d.ToConstructorStyleString(typeCatalog: _currentUnit?.Types);
                }

                var result = string.Format(CultureInfo.InvariantCulture, formatString, args.ToArray());
                _stack.Add(result);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"format: invalid format string or argument count. {ex.Message}", ex);
            }
        }

        /// <summary>
        /// <c>materialize(targetType, sourceKind, targetObject, data)</c> — fills <paramref name="callInfo"/> args from stack:
        /// push class, push source kind string (<c>json</c>), push DefObject, push JSON tree/slot value, push 4, call materialize.
        /// </summary>
        private void ExecuteMaterialize(CallInfo callInfo)
        {
            if (callInfo.Parameters == null ||
                !callInfo.Parameters.TryGetValue("0", out var a0) ||
                !callInfo.Parameters.TryGetValue("1", out var a1) ||
                !callInfo.Parameters.TryGetValue("2", out var a2) ||
                !callInfo.Parameters.TryGetValue("3", out var a3))
                throw new InvalidOperationException("materialize expects 4 arguments: targetType, sourceKind, targetObject, jsonData.");

            if (a0 is not DefType schema)
                throw new InvalidOperationException("materialize: first argument must be target type (push class).");

            var source = a1?.ToString() ?? "";
            if (!string.Equals(source, "json", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"materialize: source kind '{source}' is not supported (only \"json\").");

            if (a2 is not DefObject target)
                throw new InvalidOperationException("materialize: third argument must be DefObject instance.");

            IReadOnlyList<DefType>? catalog = _currentUnit?.Types;
            Hal.Materialize(schema, target, a3, catalog);
            // Как у остальных call: результат на стеке (тот же DefObject); statement-lowering сбрасывает через pop.
            _stack.Add(target);
        }

        /// <summary>
        /// <c>is(instance, type)</c> — для <see cref="DefObject"/>: подтип по <see cref="DefClass.Inheritances"/>.
        /// Стек: <c>push</c> instance, <c>push class</c>, <c>push 2</c>, <c>call is</c> → два значения:
        /// снизу экземпляр для binding (тот же <see cref="DefObject"/> при успехе, иначе <c>null</c>),
        /// сверху <c>1</c> / <c>0</c> (как у <c>cmp …, 1</c> + <c>je</c> на ветку).
        /// </summary>
        private void ExecuteIs(CallInfo callInfo)
        {
            if (callInfo.Parameters == null ||
                !callInfo.Parameters.TryGetValue("0", out var inst) ||
                !callInfo.Parameters.TryGetValue("1", out var typeArg))
                throw new InvalidOperationException("is requires two arguments: instance and type (push class).");

            void PushNoMatch()
            {
                _stack.Add(null!);
                _stack.Add(0L);
            }

            if (!TryResolveIsSuperFq(typeArg, out var superFq) || string.IsNullOrWhiteSpace(superFq))
            {
                PushNoMatch();
                return;
            }

            superFq = superFq.Trim();
            if (inst is not DefObject d)
            {
                PushNoMatch();
                return;
            }

            var subFq = (d.Type?.FullName ?? d.TypeName)?.Trim();
            if (string.IsNullOrEmpty(subFq))
            {
                PushNoMatch();
                return;
            }

            var ok = Interpreter.IsSubtypeOfFq(subFq, superFq, _currentUnit);
            if (!ok)
            {
                PushNoMatch();
                return;
            }

            _stack.Add(d);
            _stack.Add(1L);
        }

        private bool TryResolveIsSuperFq(object? typeArg, out string fq)
        {
            fq = "";
            switch (typeArg)
            {
                case DefType dt:
                    fq = !string.IsNullOrWhiteSpace(dt.FullName) ? dt.FullName.Trim() : (dt.Name ?? "").Trim();
                    return fq.Length > 0;
                case string s when !string.IsNullOrWhiteSpace(s):
                {
                    var req = s.Trim();
                    if (_currentUnit?.Types != null)
                    {
                        foreach (var t in _currentUnit.Types)
                        {
                            if (string.Equals(t.Name, req, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(t.FullName, req, StringComparison.OrdinalIgnoreCase))
                            {
                                fq = !string.IsNullOrWhiteSpace(t.FullName) ? t.FullName.Trim() : t.Name.Trim();
                                return fq.Length > 0;
                            }
                        }
                    }

                    fq = req;
                    return true;
                }
                default:
                    return false;
            }
        }

        private static object? ConvertValue(object? value, string? type)
        {
            if (string.IsNullOrWhiteSpace(type))
                return value;

            var kind = NormalizeTypeName(type);
            switch (kind)
            {
                case "base64":
                case "to_base64":
                    if (value == null)
                        return string.Empty;

                    if (value is byte[] bytes)
                        return Convert.ToBase64String(bytes);

                    if (value is IReadOnlyList<byte> roList)
                    {
                        var tmp = new byte[roList.Count];
                        for (var i = 0; i < roList.Count; i++)
                            tmp[i] = roList[i];
                        return Convert.ToBase64String(tmp);
                    }

                    if (value is IEnumerable<byte> seqBytes && value is not string)
                    {
                        var tmp = seqBytes.ToArray();
                        return Convert.ToBase64String(tmp);
                    }

                    if (value is Stream stream)
                    {
                        if (stream.CanSeek)
                            stream.Position = 0;
                        using var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        return Convert.ToBase64String(ms.ToArray());
                    }

                    var s = value.ToString() ?? string.Empty;
                    var strBytes = Encoding.UTF8.GetBytes(s);
                    return Convert.ToBase64String(strBytes);

                case "string":
                case "str":
                    return value?.ToString() ?? string.Empty;

                case "decimal":
                    return ConvertToDecimal(value);

                case "float<decimal>":
                case "floatdecimal":
                case "types/floatdecimal":
                    return ConvertToFloatDecimal(value);

                case "identity":
                case "none":
                    return value;

                default:
                    return value;
            }
        }

        private static string NormalizeTypeName(string type) =>
            string.Concat(type.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();

        private static decimal ConvertToDecimal(object? value)
        {
            if (value == null)
                return 0m;

            return value switch
            {
                decimal dm => dm,
                FloatDecimal fd when fd.IsFinite && decimal.TryParse(fd.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
                bool b => b ? 1m : 0m,
                _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
            };
        }

        private static FloatDecimal ConvertToFloatDecimal(object? value)
        {
            if (value == null)
                return FloatDecimal.Zero34;

            return value switch
            {
                FloatDecimal fd => fd,
                byte b => FloatDecimal.FromInt64(b),
                sbyte sb => FloatDecimal.FromInt64(sb),
                short s => FloatDecimal.FromInt64(s),
                ushort us => FloatDecimal.FromInt64(us),
                int i => FloatDecimal.FromInt64(i),
                uint ui => FloatDecimal.FromBigInteger(ui),
                long l => FloatDecimal.FromInt64(l),
                ulong ul => FloatDecimal.FromBigInteger(ul),
                decimal dm => FloatDecimal.FromDecimal(dm),
                float f => FloatDecimal.FromDouble(f),
                double d => FloatDecimal.FromDouble(d),
                bool b => b ? FloatDecimal.One34 : FloatDecimal.Zero34,
                string s when FloatDecimal.TryParse(s, FloatDecimalFormat.Decfloat34, out var parsed) => parsed,
                _ => FloatDecimal.Parse(value.ToString() ?? string.Empty, FloatDecimalFormat.Decfloat34)
            };
        }

        private async Task ExecuteCompileAsync(CallInfo callInfo)
        {
            object? dataObj = null;
            if (_stack.Count > 0)
            {
                dataObj = _stack[_stack.Count - 1];
                _stack.RemoveAt(_stack.Count - 1);
            }
            else if (callInfo.Parameters?.TryGetValue("0", out var p) == true)
            {
                dataObj = p is MemoryAddress memAddr && TryReadMemory(memAddr, out var memVal) ? memVal : p;
            }
            if (dataObj == null)
                dataObj = new Dictionary<string, object> { ["content"] = "" };

            if (dataObj is IStreamDevice streamDevice && _configuration?.DefaultDisk != null && _configuration.DefaultSSCCompiler != null)
            {
                var result = await _configuration.DefaultDisk.CompileAsync(streamDevice, _configuration.DefaultSSCCompiler).ConfigureAwait(false);
                _stack.Add(result);
                return;
            }

            // StreamHandle (path-only) — создаём FileStreamDevice и компилируем
            if (dataObj is StreamHandle streamHandle && _configuration?.DefaultDisk != null && _configuration.DefaultSSCCompiler != null)
            {
                var device = new FileStreamDevice { Path = streamHandle.Path };
                var result = await _configuration.DefaultDisk.CompileAsync(device, _configuration.DefaultSSCCompiler).ConfigureAwait(false);
                _stack.Add(result);
                return;
            }

            throw new CompileByUnknownDeviceException(dataObj);
        }

        private sealed class StreamHandle
        {
            public string Path { get; set; } = "";
        }

        private async Task ExecuteOriginAsync(CallInfo callInfo)
        {
            var shapes = new List<Shape>();

            // Собираем все shapes из параметров
            foreach (var param in callInfo.Parameters.Values)
            {
                Shape? shape = null;

                if (param is Dictionary<string, object> entityRef)
                {
                    // Параметр вида: { "type": EntityType.Shape, "index": 1 }
                    if (entityRef.TryGetValue("type", out var typeObj) &&
                        entityRef.TryGetValue("index", out var indexObj) &&
                        typeObj is EntityType entityType &&
                        entityType == EntityType.Shape &&
                        indexObj is long index)
                    {
                        if (_configuration?.DefaultDisk != null)
                        {
                            shape = await _configuration.DefaultDisk.GetShape(index, null, _currentUnit?.SpaceName);
                        }
                    }
                }
                else if (param is MemoryAddress memoryAddress && memoryAddress.Index.HasValue)
                {
                    // Параметр из памяти
                    if (TryReadMemory(memoryAddress, out var memoryValue) && memoryValue is Shape memShape)
                    {
                        shape = memShape;
                    }
                }
                else if (param is Shape directShape)
                {
                    shape = directShape;
                }

                if (shape != null)
                {
                    shapes.Add(shape);
                }
            }

            if (shapes.Count == 0)
            {
                throw new InvalidOperationException("Origin function requires at least one shape parameter.");
            }

            // Вычисляем центр массы из всех Position shapes
            var positions = new List<Position>();
            foreach (var shape in shapes)
            {
                if (shape.Position != null)
                {
                    positions.Add(shape.Position);
                }
                else if (shape.Origin != null)
                {
                    positions.Add(shape.Origin);
                }

                // Если у shape есть VertexIndices, загружаем вершины и добавляем их Positions
                if (shape.VertexIndices != null && shape.VertexIndices.Count > 0 && _configuration?.DefaultDisk != null)
                {
                    foreach (var vertexIndex in shape.VertexIndices)
                    {
                        var vertex = await _configuration.DefaultDisk.GetVertex(vertexIndex, null, _currentUnit?.SpaceName);
                        if (vertex?.Position != null)
                        {
                            positions.Add(vertex.Position);
                        }
                    }
                }
            }

            if (positions.Count == 0)
            {
                throw new InvalidOperationException("Origin function requires shapes with positions.");
            }

            // Вычисляем центр массы
            var centerOfMass = MathFunctions.CalculateCenterOfMass(positions);
            _stack.Add(centerOfMass);
        }

        private async Task ExecutePrintAsync(CallInfo callInfo)
        {
            await _printFunctions.ExecutePrintAsync(callInfo);
        }

        private async Task ExecutePrintdAsync(CallInfo callInfo)
        {
            await _printFunctions.ExecutePrintdAsync(callInfo);
        }

        private Task ExecuteDebugAsync(CallInfo callInfo)
        {
            // Debug hook: place a breakpoint here when debugging AGI programs.
            System.Diagnostics.Debugger.Break();
            return Task.CompletedTask;
        }

        private async Task ExecuteIntersectAsync(CallInfo callInfo)
        {
            Shape? shapeA = null;
            Shape? shapeB = null;

            // Ищем shapeA и shapeB в параметрах
            if (callInfo.Parameters.TryGetValue("shapeA", out var paramA))
            {
                var valueA = await GetValueFromParameterAsync(paramA);
                shapeA = valueA as Shape;
            }
            else if (callInfo.Parameters.TryGetValue("0", out var param0))
            {
                var value0 = await GetValueFromParameterAsync(param0);
                shapeA = value0 as Shape;
            }

            if (callInfo.Parameters.TryGetValue("shapeB", out var paramB))
            {
                var valueB = await GetValueFromParameterAsync(paramB);
                shapeB = valueB as Shape;
            }
            else if (callInfo.Parameters.TryGetValue("1", out var param1))
            {
                var value1 = await GetValueFromParameterAsync(param1);
                shapeB = value1 as Shape;
            }

            if (shapeA == null || shapeB == null)
            {
                throw new InvalidOperationException("Intersect function requires two shape parameters (shapeA and shapeB).");
            }

            // Вычисляем пересечение
            var intersection = await MathFunctions.CalculateIntersectionAsync(shapeA, shapeB, _configuration?.DefaultDisk, _currentUnit?.SpaceName);
            _stack.Add(intersection);
        }

        private async Task<object?> GetValueFromParameterAsync(object param)
        {
            if (param is Dictionary<string, object> entityRef)
            {
                // Параметр вида: { "type": EntityType.Shape, "index": 1 }
                if (entityRef.TryGetValue("type", out var typeObj) &&
                    entityRef.TryGetValue("index", out var indexObj) &&
                    typeObj is EntityType entityType &&
                    indexObj is long index)
                {
                    if (_configuration?.DefaultDisk != null)
                    {
                        var sn = _currentUnit?.SpaceName;
                        switch (entityType)
                        {
                            case EntityType.Vertex:
                                return await _configuration.DefaultDisk.GetVertex(index, null, sn);
                            case EntityType.Shape:
                                return await _configuration.DefaultDisk.GetShape(index, null, sn);
                            case EntityType.Relation:
                                return await _configuration.DefaultDisk.GetRelation(index, null, sn);
                        }
                    }
                }
            }
            else if (param is MemoryAddress memoryAddress && memoryAddress.Index.HasValue)
            {
                // Параметр из памяти
                if (TryReadMemory(memoryAddress, out var memoryValue))
                {
                    return memoryValue;
                }
            }

            return param;
        }

    }
}

