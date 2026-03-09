using Magic.Kernel.Core.OS;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Magic.Kernel.Core
{
    /// <summary>Global Space root path: from SPACE_PATH env var, default c:/Space.</summary>
    public static class SpaceEnvironment
    {
        private const string DefaultPath = OSSystem.SpaceDefaultPath;
        private static readonly string PathValue = ResolvePath();

        private static Dictionary<string, JsonElement>? _configuration;
        private static readonly object _configLock = new object();

        /// <summary>Root directory for Space (vault.json, samples, etc.).</summary>
        public static string Path => PathValue;

        /// <summary>Configuration loaded from Space/dev.json (debug) or Space/release.json (release). Valid after StartKernel().</summary>
        public static IReadOnlyDictionary<string, JsonElement> Configuration => (IReadOnlyDictionary<string, JsonElement>?)_configuration ?? new Dictionary<string, JsonElement>();

        /// <summary>Relative path to debug program from config ("debug" in dev.json), or null when not set.</summary>
        public static string? DebugProgramPath
        {
            get
            {
                StartKernel();
                if (_configuration != null &&
                    _configuration.TryGetValue("debug", out var el) &&
                    el.ValueKind == JsonValueKind.String)
                {
                    return el.GetString();
                }
                return null;
            }
        }

        /// <summary>Absolute path to debug program (Space root + "debug" from config), or null when not configured.</summary>
        public static string? DebugProgramFullPath
        {
            get
            {
                var rel = DebugProgramPath;
                if (string.IsNullOrWhiteSpace(rel))
                    return null;
                return GetFilePath(rel!);
            }
        }

        /// <summary>Execution units configuration from dev/release.json: executionUnits: [ { path, instanceCount } ].</summary>
        public static IReadOnlyList<ExecutionUnitConfig> ExecutionUnits
        {
            get
            {
                StartKernel();
                var result = new List<ExecutionUnitConfig>();
                if (_configuration == null ||
                    !_configuration.TryGetValue("executionUnits", out var el) ||
                    el.ValueKind != JsonValueKind.Array)
                    return result;

                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    if (!item.TryGetProperty("path", out var pathEl) ||
                        pathEl.ValueKind != JsonValueKind.String)
                        continue;

                    var path = pathEl.GetString();
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    var instanceCount = 1;
                    if (item.TryGetProperty("instanceCount", out var countEl) &&
                        countEl.ValueKind == JsonValueKind.Number &&
                        countEl.TryGetInt32(out var parsed) &&
                        parsed > 0)
                    {
                        instanceCount = parsed;
                    }

                    result.Add(new ExecutionUnitConfig(path!, instanceCount));
                }

                return result;
            }
        }

        /// <summary>Инициализация Space: считывает конфиг (dev.json в отладке, release.json в релизе).</summary>
        public static void StartKernel()
        {
            if (_configuration != null)
                return;
            lock (_configLock)
            {
                if (_configuration != null)
                    return;
                var fileName = Debugger.IsAttached ? "dev.json" : "release.json";
                var path = GetFilePath(fileName);
                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    using var doc = JsonDocument.Parse(stream);
                    var dict = new Dictionary<string, JsonElement>();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        dict[prop.Name] = prop.Value.Clone();
                    _configuration = dict;
                }
                else
                    _configuration = new Dictionary<string, JsonElement>();
            }
        }

        private static string ResolvePath()
        {
            var env = Environment.GetEnvironmentVariable("SPACE_PATH");
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim().Replace('\\', '/').TrimEnd('/');
            return DefaultPath;
        }

        /// <summary>Full path for a file under Space root (e.g. vault.json).</summary>
        public static string GetFilePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return PathValue;
            var normalized = relativePath.Trim().Replace('\\', '/').TrimStart('/');
            return string.IsNullOrEmpty(normalized)
                ? PathValue
                : System.IO.Path.Combine(PathValue, normalized).Replace('\\', '/');
        }

        public sealed class ExecutionUnitConfig
        {
            public string Path { get; }
            public int InstanceCount { get; }

            public ExecutionUnitConfig(string path, int instanceCount)
            {
                Path = path;
                InstanceCount = instanceCount;
            }
        }
    }
}
