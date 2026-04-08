using Magic.Kernel.Processor;
using Magic.Kernel.Core;
using Magic.Kernel.Types;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Magic.Kernel.Compilation
{
    public class ExecutableUnit
    {
        public string Version { get; set; } = "0.0.1";
        public string? Name { get; set; }
        public string? Module { get; set; }
        public string? System { get; set; }
        /// <summary>Space name for disk key prefix: system|module|program. Set at interpretation start from System+Module+Name.</summary>
        public string? SpaceName { get; set; }
        /// <summary>Output format when saving: "agic" (default, binary/JSON) or "agiasm" (text assembly).</summary>
        public string? OutputFormat { get; set; }

        /// <summary>Optional instance index when unit is hosted with multiple instances (vault instanceIndex routing, etc.). Runtime-only, not serialized.</summary>
        [JsonIgnore]
        public int? InstanceIndex { get; set; }

        /// <summary>Абсолютный путь к исходному .agi при запуске из файла; для attach-отладки UI. Runtime-only, not serialized.</summary>
        [JsonIgnore]
        public string? AttachSourcePath { get; set; }

        public ExecutionBlock EntryPoint { get; set; } = new ExecutionBlock();

        public Dictionary<string, Processor.Procedure> Procedures { get; set; } = new Dictionary<string, Processor.Procedure>();

        public Dictionary<string, Processor.Function> Functions { get; set; } = new Dictionary<string, Processor.Function>();

        /// <summary>Имена функций, которые в текстовом AGIASM должны выводиться как 'method' (для членов типов).</summary>
        public HashSet<string> MethodNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public List<Export> Exports { get; set; } = new List<Export>();

        /// <summary>
        /// Top-level type definitions collected from 'def' operations.
        /// Includes both plain <see cref="DefType"/> and <see cref="DefClass"/>.
        /// Child definitions (fields/columns/etc.) are excluded.
        /// </summary>
        public List<DefType> Types { get; set; } = new List<DefType>();

        /// <summary>
        /// Runtime object definitions collected from 'defobj' operations.
        /// </summary>
        public List<DefObject> Objects { get; set; } = new List<DefObject>();

        /// <summary>Runtime-only outputs (for streamwait print). Not serialized.</summary>
        [JsonIgnore]
        public List<object?> RuntimeOutputs { get; set; } = new List<object?>();

        /// <summary>Текст AGI assembly (@AGIASM), как при сохранении в .agiasm.</summary>
        public string ToAgiasmText() => ExecutableUnitTextSerializer.Serialize(this);

        /// <summary>Как <see cref="ToAgiasmText()"/>, плюс справа от инструкций — строки исходного .agi по <see cref="Processor.Command.SourceLine"/>.</summary>
        public string ToAgiasmText(string? agiSourceForLineHints) =>
            string.IsNullOrEmpty(agiSourceForLineHints)
                ? ToAgiasmText()
                : ExecutableUnitTextSerializer.Serialize(this, agiSourceForLineHints);

        public void StampListingLineNumbers()
        {
            ExecutableUnitAsmListing.StampListingLineNumbers(this);
        }

        public async Task SaveAsync(string filePath)
        {
            var useAgiasm = string.Equals(OutputFormat, "agiasm", StringComparison.OrdinalIgnoreCase)
                || filePath.EndsWith(".agiasm", StringComparison.OrdinalIgnoreCase);
            if (useAgiasm)
            {
                var text = ExecutableUnitTextSerializer.Serialize(this);
                await File.WriteAllTextAsync(filePath, text, Encoding.UTF8);
            }
            else
            {
                var useBinary = string.Equals(OutputFormat, "agic", StringComparison.OrdinalIgnoreCase)
                                 || filePath.EndsWith(".agic", StringComparison.OrdinalIgnoreCase);
                var data = useBinary
                    ? ExecutableUnitBinarySerializer.Serialize(this)
                    : await SerializeAsync();
                await File.WriteAllBytesAsync(filePath, data);
            }
        }

        public async Task<byte[]> SerializeAsync()
        {
            // Typed JSON формат по умолчанию (типобезопасно для object-полей вроде CallInfo.Parameters и Command.Operand*)
            return await Task.FromResult(ExecutableUnitJsonSerializer.Serialize(this));
        }

        public static async Task<ExecutableUnit> LoadAsync(string filePath)
        {
            if (filePath.EndsWith(".agiasm", StringComparison.OrdinalIgnoreCase))
            {
                var text = await File.ReadAllTextAsync(filePath);
                return ExecutableUnitTextSerializer.Deserialize(text);
            }
            var data = await File.ReadAllBytesAsync(filePath);
            return await DeserializeAsync(data);
        }

        public async static Task<ExecutableUnit> DeserializeAsync(byte[] data)
        {
            // 0) AGI Assembly text format (@AGIASM)
            if (ExecutableUnitTextSerializer.IsAgiasmFormat(data))
            {
                var text = Encoding.UTF8.GetString(data);
                return ExecutableUnitTextSerializer.Deserialize(text);
            }
            // 1) Новый typed JSON формат (с $format header)
            if (ExecutableUnitJsonSerializer.IsTypedJson(data))
            {
                return await Task.FromResult(ExecutableUnitJsonSerializer.Deserialize(data));
            }

            // 2) Backward-compat: бинарный формат (с magic header)
            if (ExecutableUnitBinarySerializer.IsBinaryFormat(data))
            {
                return await Task.FromResult(ExecutableUnitBinarySerializer.Deserialize(data));
            }

            // 3) Backward-compat: старый JSON (может содержать JsonElement в object-полях)
            var json = Encoding.UTF8.GetString(data);
            var options = new JsonSerializerOptions
            {
                IncludeFields = true
            };

            var unit = JsonSerializer.Deserialize<ExecutableUnit>(json, options) ?? new ExecutableUnit();
            ExecutableUnitJsonPostProcessor.NormalizeObjectGraphs(unit);
            return await Task.FromResult(unit);
        }
    }
}
