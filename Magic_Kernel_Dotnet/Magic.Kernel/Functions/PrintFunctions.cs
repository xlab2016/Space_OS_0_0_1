using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Magic.Kernel.Devices;
using Magic.Kernel;
using Magic.Kernel.Core;
using Magic.Kernel.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Magic.Kernel.Functions
{
    public class PrintFunctions
    {
        // Keep console output readable, but avoid truncating normal text payloads.
        private const int MaxPrintedStringLength = 1024;
        private const int PrintedStringPrefixLength = 256;
        private const int PrintedStringSuffixLength = 128;

        private readonly KernelConfiguration? _configuration;
        private readonly List<object> _stack;
        private readonly Func<MemoryAddress, (bool Found, object? Value)> _memoryReader;
        private readonly Compilation.ExecutableUnit? _currentUnit;

        public PrintFunctions(KernelConfiguration? configuration, List<object> stack, Dictionary<long, object>? memory = null)
            : this(
                configuration,
                stack,
                memoryAddress =>
                {
                    if (memory != null &&
                        memoryAddress.Index.HasValue &&
                        memory.TryGetValue(memoryAddress.Index.Value, out var memoryValue))
                    {
                        return (true, memoryValue);
                    }

                    return (false, null);
                })
        {
        }

        public PrintFunctions(
            KernelConfiguration? configuration,
            List<object> stack,
            Func<MemoryAddress, (bool Found, object? Value)> memoryReader,
            Compilation.ExecutableUnit? currentUnit = null)
        {
            _configuration = configuration;
            _stack = stack;
            _memoryReader = memoryReader ?? throw new ArgumentNullException(nameof(memoryReader));
            _currentUnit = currentUnit;
        }

        public Task ExecutePrintAsync(CallInfo callInfo)
        {
            return ExecutePrintInternalAsync(callInfo, includeDebugPrefix: false);
        }

        public Task ExecutePrintdAsync(CallInfo callInfo)
        {
            return ExecutePrintInternalAsync(callInfo, includeDebugPrefix: true);
        }

        private async Task ExecutePrintInternalAsync(CallInfo callInfo, bool includeDebugPrefix)
        {
            var valuesToPrint = new List<object?>();
            var indexedArgs = callInfo.Parameters
                .Select(kvp =>
                {
                    var parsed = int.TryParse(kvp.Key, out var index);
                    return new { Parsed = parsed, Index = index, Value = kvp.Value };
                })
                .Where(x => x.Parsed)
                .OrderBy(x => x.Index)
                .ToList();

            if (indexedArgs.Count > 0)
            {
                foreach (var indexedArg in indexedArgs)
                    valuesToPrint.Add(await GetValueFromParameterAsync(indexedArg.Value));
            }
            else
            {
                foreach (var parameter in callInfo.Parameters.Where(p => !string.Equals(p.Key, "type", StringComparison.OrdinalIgnoreCase)))
                    valuesToPrint.Add(await GetValueFromParameterAsync(parameter.Value));
            }

            if (valuesToPrint.Count == 0)
            {
                throw new InvalidOperationException("Print function requires a value parameter.");
            }

            var prefix = includeDebugPrefix ? Core.ExecutionCallContext.GetPrefix(_currentUnit) : string.Empty;
            var isPrintln = string.Equals(callInfo.FunctionName, "println", StringComparison.OrdinalIgnoreCase);

            // Проверяем тип вывода
            var outputType = "console";
            if (callInfo.Parameters.TryGetValue("type", out var typeParam))
            {
                var resolvedType = await GetValueFromParameterAsync(typeParam);
                if (resolvedType is string typeStr)
                    outputType = typeStr.ToLowerInvariant();
            }

            if (outputType == "infer")
            {
                // Вывод на inference device и получение результата
                if (_configuration?.DefaultInferenceDevice == null)
                {
                    throw new InvalidOperationException("DefaultInferenceDevice is not configured. Cannot print to inference device.");
                }

                string inferredPayload = valuesToPrint.Count == 1
                    ? (valuesToPrint[0]?.ToString() ?? "null")
                    : string.Join(" | ", valuesToPrint.Select(value => FormatValue(value ?? "null")));

                if (!string.IsNullOrEmpty(prefix))
                    inferredPayload = prefix + inferredPayload;

                var inputData = ConvertToEntityData(inferredPayload);
                var outputData = new EntityData
                {
                    Type = new HierarchicalDataType()
                };

                var result = await _configuration.DefaultInferenceDevice.Print(inputData, outputData);
                if (!result.IsSuccess)
                {
                    throw new InvalidOperationException($"Failed to print to inference device: {result.State}" + (result.ErrorMessage != null ? $", {result.ErrorMessage}" : ""));
                }

                // Результат inference помещаем в стек
                _stack.Add(outputData);
            }
            else
            {
                // Вывод в консоль с форматированием по типам
                var formattedOutput = valuesToPrint.Count == 1
                    ? FormatValue(valuesToPrint[0] ?? "null")
                    : string.Join(" | ", valuesToPrint.Select((value, index) => $"arg{index}: {FormatValue(value ?? "null")}"));
                if (isPrintln)
                    Console.WriteLine(prefix + formattedOutput);
                else
                {
                    Console.Write(prefix + formattedOutput);
                    Console.Out.Flush();
                }
            }
        }

        private string FormatValue(object value)
        {
            return value switch
            {
                DefObject defObj => JsonSerializer.Serialize(defObj.ToJsonMapBySchemaFieldNames(includeTypeDiscriminator: true, _currentUnit?.Types)),
                string str => FormatString(str),
                Position pos => FormatPosition(pos),
                Shape shape => FormatShape(shape),
                Vertex vertex => FormatVertex(vertex),
                Relation relation => FormatRelation(relation),
                EntityBase entity => FormatEntityBase(entity),
                List<object> list => FormatList(list),
                Dictionary<string, object> dict => FormatDictionary(dict),
                null => "None",
                _ => value.ToString() ?? "null"
            };
        }

        private object? AgiValueToJsonTree(object? v)
        {
            if (v == null)
                return null;
            if (v is DefObject d)
                return d.ToJsonMapBySchemaFieldNames(includeTypeDiscriminator: true, _currentUnit?.Types);
            if (v is DefList list)
                return list.Items.Select(AgiValueToJsonTree).ToList();
            if (v is string or bool or long or int or short or byte or decimal or double or float)
                return v;
            return v.ToString();
        }

        private static string FormatString(string value)
        {
            if (value.Length <= MaxPrintedStringLength)
                return value;

            var prefixLength = Math.Min(PrintedStringPrefixLength, value.Length);
            var suffixLength = Math.Min(PrintedStringSuffixLength, Math.Max(0, value.Length - prefixLength));
            var prefix = value[..prefixLength];
            var suffix = suffixLength > 0 ? value[^suffixLength..] : string.Empty;

            return $"{prefix}...{suffix}...[truncated]";
        }

        private string FormatPosition(Position pos)
        {
            if (pos.Dimensions == null || pos.Dimensions.Count == 0)
            {
                return "Position([])";
            }
            return $"Position([{string.Join(", ", pos.Dimensions.Select(d => d.ToString("G9")))}])";
        }

        private string FormatShape(Shape shape)
        {
            var parts = new List<string>();
            
            if (!string.IsNullOrEmpty(shape.Name))
            {
                parts.Add($"Name='{shape.Name}'");
            }
            
            if (shape.Origin != null)
            {
                parts.Add($"Origin={FormatPosition(shape.Origin)}");
            }
            
            if (shape.Position != null)
            {
                parts.Add($"Position={FormatPosition(shape.Position)}");
            }
            
            if (shape.Index.HasValue)
            {
                parts.Add($"Index={shape.Index.Value}");
            }
            
            if (shape.Vertices != null && shape.Vertices.Count > 0)
            {
                parts.Add($"Vertices=[{string.Join(", ", shape.Vertices.Select(v => FormatVertex(v)))}]");
            }
            
            if (shape.VertexIndices != null && shape.VertexIndices.Count > 0)
            {
                parts.Add($"VertexIndices=[{string.Join(", ", shape.VertexIndices)}]");
            }

            return $"Shape({string.Join(", ", parts)})";
        }

        private string FormatVertex(Vertex vertex)
        {
            var parts = new List<string>();
            
            if (vertex.Index.HasValue)
            {
                parts.Add($"Index={vertex.Index.Value}");
            }
            
            if (!string.IsNullOrEmpty(vertex.Name))
            {
                parts.Add($"Name='{vertex.Name}'");
            }
            
            if (vertex.Position != null)
            {
                parts.Add($"Position={FormatPosition(vertex.Position)}");
            }
            
            if (vertex.Weight.HasValue)
            {
                parts.Add($"Weight={vertex.Weight.Value}");
            }

            return $"Vertex({string.Join(", ", parts)})";
        }

        private string FormatRelation(Relation relation)
        {
            var parts = new List<string>();
            
            if (relation.Index.HasValue)
            {
                parts.Add($"Index={relation.Index.Value}");
            }
            
            if (!string.IsNullOrEmpty(relation.Name))
            {
                parts.Add($"Name='{relation.Name}'");
            }

            return $"Relation({string.Join(", ", parts)})";
        }

        private string FormatEntityBase(EntityBase entity)
        {
            var parts = new List<string>();
            
            parts.Add($"Type={entity.Type}");
            
            if (entity.Index.HasValue)
            {
                parts.Add($"Index={entity.Index.Value}");
            }
            
            if (!string.IsNullOrEmpty(entity.Name))
            {
                parts.Add($"Name='{entity.Name}'");
            }
            
            if (entity.Position != null)
            {
                parts.Add($"Position={FormatPosition(entity.Position)}");
            }
            
            if (entity.Weight.HasValue)
            {
                parts.Add($"Weight={entity.Weight.Value}");
            }

            return $"{entity.Type}({string.Join(", ", parts)})";
        }

        private string FormatList(List<object> list)
        {
            if (list.Count == 0)
            {
                return "[]";
            }
            return $"[{string.Join(", ", list.Select(FormatValue))}]";
        }

        private string FormatDictionary(Dictionary<string, object> dict)
        {
            if (dict.Count == 0)
            {
                return "{}";
            }
            var items = dict.Select(kvp => $"'{kvp.Key}': {FormatValue(kvp.Value)}");
            return $"{{{string.Join(", ", items)}}}";
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
                var memoryResult = _memoryReader(memoryAddress);
                if (memoryResult.Found)
                {
                    return memoryResult.Value;
                }

                return param;
            }

            return param;
        }

        private EntityData ConvertToEntityData(object value)
        {
            // Простое преобразование в EntityData
            // В реальной реализации может потребоваться сериализация
            var entityData = new EntityData
            {
                Type = new HierarchicalDataType()
            };

            if (value is string str)
            {
                entityData.Data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(str));
            }
            else
            {
                entityData.Data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value.ToString() ?? ""));
            }

            return entityData;
        }
    }
}
