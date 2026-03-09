using Magic.Kernel.Processor;
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

        public ExecutionBlock EntryPoint { get; set; } = new ExecutionBlock();

        public Dictionary<string, Processor.Procedure> Procedures { get; set; } = new Dictionary<string, Processor.Procedure>();

        public Dictionary<string, Processor.Function> Functions { get; set; } = new Dictionary<string, Processor.Function>();

        public List<Export> Exports { get; set; } = new List<Export>();

        /// <summary>Runtime-only outputs (for streamwait print). Not serialized.</summary>
        [JsonIgnore]
        public List<object?> RuntimeOutputs { get; set; } = new List<object?>();

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
                var data = await SerializeAsync();
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
