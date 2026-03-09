using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Magic.Kernel.Compilation;
using Magic.Kernel.Core;
using Magic.Kernel.Interpretation;

namespace Magic.Kernel.Devices.Store
{
    public class Vault : IDefType
    {
        /// <summary>Unit that created this Vault; used for SpaceName/path resolution.</summary>
        public ExecutableUnit? ExecutableUnit { get; set; }

        public long? Index { get; set; }
        public string Name { get; set; } = "";
        public StructurePosition? Position { get; set; }
        public List<IDefType> Generalizations { get; set; } = new List<IDefType>();

        private List<VaultEntry>? _entries;
        private readonly object _loadLock = new object();

        private List<VaultEntry> GetEntries()
        {
            if (_entries != null)
                return _entries;
            lock (_loadLock)
            {
                if (_entries != null)
                    return _entries;
                var path = SpaceEnvironment.GetFilePath("vault.json");
                _entries = new List<VaultEntry>();
                if (!File.Exists(path))
                    return _entries;
                try
                {
                    var json = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(json);
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        var program = GetString(el, "program");
                        var system = GetString(el, "system");
                        var module = GetString(el, "module");
                        var instanceIndex = GetInt32OrDefault(el, "instanceIndex", 0);
                        var store = GetStore(el);
                        _entries.Add(new VaultEntry(program, system, module, instanceIndex, store));
                    }
                }
                catch
                {
                    _entries = new List<VaultEntry>();
                }
                return _entries;
            }
        }

        private static string GetString(JsonElement el, string name)
        {
            if (el.TryGetProperty(name, out var p))
                return p.GetString() ?? "";
            return "";
        }

        private static int GetInt32OrDefault(JsonElement el, string name, int defaultValue)
        {
            if (!el.TryGetProperty(name, out var p))
                return defaultValue;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var value))
                return value;
            return defaultValue;
        }

        private static Dictionary<string, object> GetStore(JsonElement el)
        {
            var store = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (!el.TryGetProperty("store", out var storeEl) || storeEl.ValueKind != JsonValueKind.Object)
                return store;
            foreach (var prop in storeEl.EnumerateObject())
            {
                store[prop.Name] = ToObject(prop.Value);
            }
            return store;
        }

        private static object ToObject(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String: return e.GetString() ?? "";
                case JsonValueKind.Number:
                    if (e.TryGetInt64(out var l)) return l;
                    if (e.TryGetDouble(out var d)) return d;
                    return e.GetRawText();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Null: return null!;
                case JsonValueKind.Object:
                    var obj = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in e.EnumerateObject())
                        obj[p.Name] = ToObject(p.Value);
                    return obj;
                case JsonValueKind.Array:
                    return e.EnumerateArray().Select(ToObject).ToList();
                default: return e.GetRawText();
            }
        }

        public async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            if (string.Equals(methodName, "read", StringComparison.OrdinalIgnoreCase))
            {
                var (system, module, program, key) = GetProgramSystemModuleKeyFromPositionOrArgs(args);
                if (program != null || system != null || module != null || (args != null && args.Length >= 3))
                {
                    var p = program ?? "";
                    var s = system ?? "";
                    var m = module ?? "";
                    var entries = GetEntries();
                    var positionInstanceIndex = GetPositionInstanceIndex();
                    var candidates = entries.Where(e =>
                        string.Equals(e.Program, p, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(e.System, s, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(e.Module, m, StringComparison.OrdinalIgnoreCase)).ToList();

                    VaultEntry? entry = null;

                    if (positionInstanceIndex.HasValue)
                    {
                        // 1) Exact match by instanceIndex
                        entry = candidates.FirstOrDefault(e => e.InstanceIndex == positionInstanceIndex.Value);
                        // 2) Fallback to instanceIndex == 0 for backward-compat configs
                        entry ??= candidates.FirstOrDefault(e => e.InstanceIndex == 0);
                    }
                    else
                    {
                        // No instance index in position: prefer instanceIndex == 0 when present
                        entry = candidates.FirstOrDefault(e => e.InstanceIndex == 0);
                    }

                    // 3) Final fallback: any matching entry
                    entry ??= candidates.FirstOrDefault();

                    var store = entry?.Store;
                    if (store != null && !string.IsNullOrWhiteSpace(key) && store.TryGetValue(key, out var value))
                        return value;
                    return null;
                }
                var envKey = args?.Length > 0 ? args[0]?.ToString() : null;
                if (!string.IsNullOrWhiteSpace(envKey))
                    return Environment.GetEnvironmentVariable(envKey);
                return null;
            }

            throw new CallUnknownMethodException(methodName ?? "", this);
        }

        private int? GetPositionInstanceIndex()
        {
            var indices = Position?.Indices;
            if (indices == null || indices.Count == 0)
                return null;
            return indices[0];
        }

        private (string? program, string? system, string? module, string? key) GetProgramSystemModuleKeyFromPositionOrArgs(object?[]? args)
        {
            var names = Position?.IndexNames;
            if (args.Length == 0)
                throw new ArgumentOutOfRangeException("At least one argument (key) is required for Vault read.");
            string? key = args[0] as string;
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key argument for Vault read must be a non-empty string.");
            if (names != null && names.Count >= 4)
                return (names[0] ?? "", names[1] ?? "", names[2] ?? "", names[3] ?? "");
            if (names != null && names.Count >= 3)
                return (names[0] ?? "", names[1] ?? "", names[2] ?? "", key);
            if (names != null && names.Count >= 2)
                return (names[0] ?? "", names[1] ?? "", null, key);
            if (names != null && names.Count >= 1)
                return (names[0] ?? "", null, null, key);
            throw new InvalidPositionException("Unit execution position is undefined.");
        }

        public Task<object?> AwaitObjAsync() => Task.FromResult<object?>(this);

        public Task<object?> Await() => Task.FromResult<object?>(this);

        public Task<(bool IsEnd, object? Delta, object? Aggregate)> StreamWaitAsync(string streamWaitType)
            => Task.FromResult((true, (object?)null, (object?)null));

        private sealed class VaultEntry
        {
            public string Program { get; }
            public string System { get; }
            public string Module { get; }
            public int InstanceIndex { get; }
            public Dictionary<string, object> Store { get; }

            public VaultEntry(string program, string system, string module, int instanceIndex, Dictionary<string, object> store)
            {
                Program = program;
                System = system;
                Module = module;
                InstanceIndex = instanceIndex;
                Store = store;
            }
        }
    }
}
