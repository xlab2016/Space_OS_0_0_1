using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Magic.Kernel.Core;
using Magic.Kernel.Types;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Magic.Kernel.Compilation
{
    /// <summary>
    /// Typed JSON serializer for ExecutableUnit that preserves runtime types for all object-typed fields:
    /// - Command.Operand1/Operand2
    /// - CallInfo.Parameters values (Dictionary&lt;string, object&gt;)
    /// It uses small type tags: { "$t": "...", "$v": ... }.
    /// </summary>
    internal static class ExecutableUnitJsonSerializer
    {
        private const string Format = "mkex-json";
        private const int FormatVersion = 1;

        public static byte[] Serialize(ExecutableUnit unit)
        {
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });

            writer.WriteStartObject();
            writer.WriteString("$format", Format);
            writer.WriteNumber("$version", FormatVersion);

            writer.WriteString("version", unit.Version ?? "0.0.1");
            WriteNullableString(writer, "name", unit.Name);
            WriteNullableString(writer, "module", unit.Module);

            writer.WritePropertyName("entryPoint");
            WriteExecutionBlock(writer, unit.EntryPoint);

            writer.WritePropertyName("procedures");
            writer.WriteStartObject();
            foreach (var kv in unit.Procedures)
            {
                writer.WritePropertyName(kv.Key);
                WriteProcedure(writer, kv.Value);
            }
            writer.WriteEndObject();

            writer.WritePropertyName("functions");
            writer.WriteStartObject();
            foreach (var kv in unit.Functions)
            {
                writer.WritePropertyName(kv.Key);
                WriteFunction(writer, kv.Value);
            }
            writer.WriteEndObject();

            writer.WritePropertyName("exports");
            writer.WriteStartArray();
            foreach (var e in unit.Exports)
            {
                WriteExport(writer, e);
            }
            writer.WriteEndArray();

            writer.WritePropertyName("types");
            writer.WriteStartArray();
            foreach (var t in unit.Types)
                WriteDefType(writer, t);
            writer.WriteEndArray();

            writer.WritePropertyName("objects");
            writer.WriteStartArray();
            foreach (var o in unit.Objects)
                WriteDefObject(writer, o);
            writer.WriteEndArray();

            writer.WriteEndObject();
            writer.Flush();
            return ms.ToArray();
        }

        public static bool IsTypedJson(byte[] data)
        {
            // cheap sniff: find first non-ws and ensure '{', then parse small header
            var i = 0;
            while (i < data.Length && (data[i] == (byte)' ' || data[i] == (byte)'\n' || data[i] == (byte)'\r' || data[i] == (byte)'\t'))
                i++;
            if (i >= data.Length || data[i] != (byte)'{') return false;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                if (!root.TryGetProperty("$format", out var fmt)) return false;
                return fmt.ValueKind == JsonValueKind.String && string.Equals(fmt.GetString(), Format, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        public static ExecutableUnit Deserialize(byte[] data)
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (!root.TryGetProperty("$format", out var fmt) || fmt.GetString() != Format)
                throw new InvalidOperationException("Not a typed ExecutableUnit JSON payload.");
            if (!root.TryGetProperty("$version", out var ver) || ver.GetInt32() != FormatVersion)
                throw new InvalidOperationException("Unsupported typed ExecutableUnit JSON version.");

            var unit = new ExecutableUnit
            {
                Version = root.TryGetProperty("version", out var v) ? (v.GetString() ?? "0.0.1") : "0.0.1",
                Name = root.TryGetProperty("name", out var n) && n.ValueKind != JsonValueKind.Null ? n.GetString() : null,
                Module = root.TryGetProperty("module", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetString() : null,
            };

            unit.EntryPoint = root.TryGetProperty("entryPoint", out var ep) ? ReadExecutionBlock(ep) : new ExecutionBlock();

            unit.Procedures = new Dictionary<string, Processor.Procedure>(StringComparer.Ordinal);
            if (root.TryGetProperty("procedures", out var procs) && procs.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in procs.EnumerateObject())
                {
                    unit.Procedures[prop.Name] = ReadProcedure(prop.Value);
                }
            }

            unit.Functions = new Dictionary<string, Processor.Function>(StringComparer.Ordinal);
            if (root.TryGetProperty("functions", out var funcs) && funcs.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in funcs.EnumerateObject())
                {
                    unit.Functions[prop.Name] = ReadFunction(prop.Value);
                }
            }

            unit.Exports = new List<Export>();
            if (root.TryGetProperty("exports", out var exports) && exports.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in exports.EnumerateArray())
                {
                    unit.Exports.Add(ReadExport(item));
                }
            }

            unit.Types = new List<DefType>();
            if (root.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in types.EnumerateArray())
                    unit.Types.Add(ReadDefType(item));
            }

            unit.Objects = new List<DefObject>();
            if (root.TryGetProperty("objects", out var objects) && objects.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in objects.EnumerateArray())
                    unit.Objects.Add(ReadDefObject(item));
            }

            return unit;
        }

        private static void WriteDefType(Utf8JsonWriter w, DefType t)
        {
            w.WriteStartObject();
            w.WriteString("kind", t is DefClass ? "class" : "type");
            w.WriteString("namespace", t.Namespace ?? string.Empty);
            w.WriteString("name", t.Name ?? string.Empty);
            w.WritePropertyName("bases");
            w.WriteStartArray();
            if (t is DefClass dc)
            {
                foreach (var b in dc.Inheritances ?? new List<string>())
                    w.WriteStringValue(b ?? string.Empty);
            }
            else
            {
                foreach (var g in t.Generalizations ?? new List<IDefType>())
                    w.WriteStringValue(g?.Name ?? string.Empty);
            }

            w.WriteEndArray();
            w.WritePropertyName("fields");
            w.WriteStartArray();
            foreach (var f in t.Fields ?? new List<DefTypeField>())
            {
                w.WriteStartObject();
                w.WriteString("name", f.Name ?? string.Empty);
                w.WriteString("type", f.Type ?? "any");
                w.WriteString("visibility", f.Visibility ?? "public");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WritePropertyName("methods");
            w.WriteStartArray();
            foreach (var m in t.Methods ?? new List<DefTypeMethod>())
            {
                w.WriteStartObject();
                w.WriteString("name", m.Name ?? string.Empty);
                w.WriteString("fullName", m.FullName ?? string.Empty);
                w.WriteString("returnType", m.ReturnType ?? "void");
                w.WriteString("visibility", m.Visibility ?? "public");
                w.WriteString("firstParameterTypeFq", m.FirstParameterTypeFq ?? string.Empty);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        private static DefType ReadDefType(JsonElement e)
        {
            var kind = e.TryGetProperty("kind", out var k) ? (k.GetString() ?? "type") : "type";
            var ns = e.TryGetProperty("namespace", out var nsEl) ? (nsEl.GetString() ?? string.Empty) : string.Empty;
            var name = e.TryGetProperty("name", out var n) ? (n.GetString() ?? string.Empty) : string.Empty;
            var baseNames = new List<string>();
            if (e.TryGetProperty("bases", out var bases) && bases.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in bases.EnumerateArray())
                {
                    if (b.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(b.GetString()))
                        baseNames.Add(b.GetString()!);
                }
            }

            var fields = new List<DefTypeField>();
            if (e.TryGetProperty("fields", out var fieldItems) && fieldItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var fieldItem in fieldItems.EnumerateArray())
                {
                    if (fieldItem.ValueKind != JsonValueKind.Object)
                        continue;

                    var fieldName = fieldItem.TryGetProperty("name", out var fName)
                        ? (fName.GetString() ?? string.Empty)
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(fieldName))
                        continue;

                    var visibility = fieldItem.TryGetProperty("visibility", out var fVisibility)
                        ? (fVisibility.GetString() ?? "public")
                        : "public";
                    var fieldType = fieldItem.TryGetProperty("type", out var fType)
                        ? (fType.GetString() ?? "any")
                        : "any";
                    fields.Add(new DefTypeField
                    {
                        Name = fieldName,
                        Type = string.IsNullOrWhiteSpace(fieldType) ? "any" : fieldType,
                        Visibility = string.IsNullOrWhiteSpace(visibility) ? "public" : visibility
                    });
                }
            }

            var methods = new List<DefTypeMethod>();
            if (e.TryGetProperty("methods", out var methodItems) && methodItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var methodItem in methodItems.EnumerateArray())
                {
                    if (methodItem.ValueKind != JsonValueKind.Object)
                        continue;

                    var mName = methodItem.TryGetProperty("name", out var mn) ? (mn.GetString() ?? string.Empty) : string.Empty;
                    if (string.IsNullOrWhiteSpace(mName))
                        continue;
                    var mFull = methodItem.TryGetProperty("fullName", out var mf) ? (mf.GetString() ?? string.Empty) : string.Empty;
                    var mRet = methodItem.TryGetProperty("returnType", out var mr) ? (mr.GetString() ?? "void") : "void";
                    var mVis = methodItem.TryGetProperty("visibility", out var mv) ? (mv.GetString() ?? "public") : "public";
                    var mFp = methodItem.TryGetProperty("firstParameterTypeFq", out var mfp) ? mfp.GetString() : null;
                    methods.Add(new DefTypeMethod
                    {
                        Name = mName.Trim(),
                        FullName = string.IsNullOrWhiteSpace(mFull) ? mName.Trim() : mFull.Trim(),
                        ReturnType = string.IsNullOrWhiteSpace(mRet) ? "void" : mRet.Trim(),
                        Visibility = string.IsNullOrWhiteSpace(mVis) ? "public" : mVis.Trim(),
                        FirstParameterTypeFq = string.IsNullOrWhiteSpace(mFp) ? null : mFp.Trim()
                    });
                }
            }

            DefType type = string.Equals(kind, "class", StringComparison.OrdinalIgnoreCase)
                ? new DefClass(name, baseNames)
                : new DefType
                {
                    Name = name,
                    Generalizations = baseNames.Select(x => (IDefType)new DefType { Name = x }).ToList()
                };

            type.Namespace = ns;
            type.Fields = fields;
            type.Methods = methods;

            if (string.IsNullOrEmpty((type.Namespace ?? "").Trim()) &&
                !string.IsNullOrEmpty(type.Name) &&
                type.Name.IndexOf(':') >= 0)
                type.NormalizeLegacyQualifiedName();

            return type;
        }

        private static void WriteDefObject(Utf8JsonWriter w, DefObject o)
        {
            w.WriteStartObject();
            w.WriteString("namespace", o.Type.Namespace ?? string.Empty);
            w.WriteString("name", o.Type.Name ?? string.Empty);
            w.WriteString("typeName", o.Type.FullName ?? string.Empty);
            w.WriteEndObject();
        }

        private static DefObject ReadDefObject(JsonElement e)
        {
            var ns = e.TryGetProperty("namespace", out var nsEl) ? (nsEl.GetString() ?? string.Empty) : string.Empty;
            var nm = e.TryGetProperty("name", out var nmEl) ? (nmEl.GetString() ?? string.Empty) : string.Empty;
            if (!string.IsNullOrEmpty(ns.Trim()) || !string.IsNullOrEmpty(nm.Trim()))
                return new DefObject(new DefType { Namespace = ns, Name = nm });

            var typeName = e.TryGetProperty("typeName", out var n) ? (n.GetString() ?? string.Empty) : string.Empty;
            return new DefObject(DefType.FromDefBytecode(typeName, null, false, null));
        }

        private static void WriteExport(Utf8JsonWriter w, Export e)
        {
            w.WriteStartObject();
            w.WriteString("type", e.Type ?? string.Empty);
            w.WritePropertyName("source");
            WriteExecutionBlock(w, e.Source);
            w.WriteEndObject();
        }

        private static Export ReadExport(JsonElement e)
        {
            return new Export
            {
                Type = e.TryGetProperty("type", out var t) ? (t.GetString() ?? string.Empty) : string.Empty,
                Source = e.TryGetProperty("source", out var s) ? ReadExecutionBlock(s) : new ExecutionBlock(),
            };
        }

        private static void WriteProcedure(Utf8JsonWriter w, Processor.Procedure p)
        {
            w.WriteStartObject();
            w.WriteString("name", p.Name ?? string.Empty);
            w.WritePropertyName("body");
            WriteExecutionBlock(w, p.Body);
            w.WriteEndObject();
        }

        private static Processor.Procedure ReadProcedure(JsonElement e)
        {
            return new Processor.Procedure
            {
                Name = e.TryGetProperty("name", out var n) ? (n.GetString() ?? string.Empty) : string.Empty,
                Body = e.TryGetProperty("body", out var b) ? ReadExecutionBlock(b) : new ExecutionBlock(),
            };
        }

        private static void WriteFunction(Utf8JsonWriter w, Processor.Function f)
        {
            w.WriteStartObject();
            w.WriteString("name", f.Name ?? string.Empty);
            w.WritePropertyName("body");
            WriteExecutionBlock(w, f.Body);
            w.WriteEndObject();
        }

        private static Processor.Function ReadFunction(JsonElement e)
        {
            return new Processor.Function
            {
                Name = e.TryGetProperty("name", out var n) ? (n.GetString() ?? string.Empty) : string.Empty,
                Body = e.TryGetProperty("body", out var b) ? ReadExecutionBlock(b) : new ExecutionBlock(),
            };
        }

        private static void WriteExecutionBlock(Utf8JsonWriter w, ExecutionBlock block)
        {
            w.WriteStartArray();
            foreach (var cmd in block)
            {
                w.WriteStartObject();
                w.WriteString("opcode", cmd.Opcode.ToString());
                w.WritePropertyName("op1");
                WriteVariant(w, cmd.Operand1);
                w.WritePropertyName("op2");
                WriteVariant(w, cmd.Operand2);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        private static ExecutionBlock ReadExecutionBlock(JsonElement e)
        {
            var block = new ExecutionBlock();
            if (e.ValueKind != JsonValueKind.Array) return block;
            foreach (var item in e.EnumerateArray())
            {
                var opcode = Opcodes.Nop;
                if (item.TryGetProperty("opcode", out var op))
                {
                    if (op.ValueKind == JsonValueKind.String)
                    {
                        var s = op.GetString();
                        if (!string.IsNullOrWhiteSpace(s) && Enum.TryParse<Opcodes>(s, ignoreCase: true, out var parsed))
                        {
                            opcode = parsed;
                        }
                    }
                    else if (op.ValueKind == JsonValueKind.Number)
                    {
                        opcode = (Opcodes)op.GetInt32();
                    }
                }
                var op1 = item.TryGetProperty("op1", out var o1) ? ReadVariant(o1) : null;
                var op2 = item.TryGetProperty("op2", out var o2) ? ReadVariant(o2) : null;
                block.Add(new Command { Opcode = opcode, Operand1 = op1, Operand2 = op2 });
            }
            return block;
        }

        private static void WriteVariant(Utf8JsonWriter w, object? value)
        {
            if (value == null)
            {
                w.WriteStartObject();
                w.WriteString("$t", "null");
                w.WriteNull("$v");
                w.WriteEndObject();
                return;
            }

            switch (value)
            {
                case bool b:
                    Tagged(w, "bool", () => w.WriteBooleanValue(b));
                    return;
                case string s:
                    Tagged(w, "string", () => w.WriteStringValue(s));
                    return;
                case int i32:
                    Tagged(w, "i64", () => w.WriteStringValue(((long)i32).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    return;
                case long i64:
                    Tagged(w, "i64", () => w.WriteStringValue(i64.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    return;
                case float f32:
                    Tagged(w, "f32", () => w.WriteStringValue(f32.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                    return;
                case double f64:
                    Tagged(w, "f64", () => w.WriteStringValue(f64.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                    return;
                case byte[] bytes:
                    Tagged(w, "bytes", () => w.WriteStringValue(Convert.ToBase64String(bytes)));
                    return;
                case EntityType et:
                    Tagged(w, "entityType", () => w.WriteStringValue(et.ToString()));
                    return;
                case DataType dt:
                    Tagged(w, "dataType", () => w.WriteStringValue(dt.ToString()));
                    return;
                case MemoryAddress ma:
                    w.WriteStartObject();
                    w.WriteString("$t", "mem");
                    w.WritePropertyName("$v");
                    w.WriteStartObject();
                    WriteNullableInt64(w, "index", ma.Index);
                    w.WriteBoolean("global", ma.IsGlobal);
                    w.WriteEndObject();
                    w.WriteEndObject();
                    return;
                case CallInfo ci:
                    w.WriteStartObject();
                    w.WriteString("$t", "call");
                    w.WritePropertyName("$v");
                    w.WriteStartObject();
                    w.WriteString("fn", ci.FunctionName ?? string.Empty);
                    w.WritePropertyName("p");
                    w.WriteStartObject();
                    foreach (var kv in ci.Parameters)
                    {
                        w.WritePropertyName(kv.Key);
                        WriteVariant(w, kv.Value);
                    }
                    w.WriteEndObject();
                    w.WriteEndObject();
                    w.WriteEndObject();
                    return;
                case Vertex v:
                    WriteEntity(w, "vertex", v);
                    return;
                case Relation r:
                    WriteRelation(w, r);
                    return;
                case Shape s:
                    WriteShape(w, s);
                    return;
                case Position p:
                    w.WriteStartObject();
                    w.WriteString("$t", "pos");
                    w.WritePropertyName("$v");
                    w.WriteStartArray();
                    foreach (var d in p.Dimensions ?? new List<float>())
                    {
                        w.WriteStringValue(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                    return;
                case EntityData ed:
                    w.WriteStartObject();
                    w.WriteString("$t", "edata");
                    w.WritePropertyName("$v");
                    w.WriteStartObject();
                    w.WritePropertyName("type");
                    WriteVariant(w, ed.Type);
                    WriteNullableString(w, "data", ed.Data);
                    w.WriteEndObject();
                    w.WriteEndObject();
                    return;
                case HierarchicalDataType hdt:
                    w.WriteStartObject();
                    w.WriteString("$t", "hdt");
                    w.WritePropertyName("$v");
                    w.WriteStartArray();
                    foreach (var t in hdt.Types)
                    {
                        w.WriteStringValue(t.ToString());
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                    return;
                case Dictionary<string, object> dict:
                    w.WriteStartObject();
                    w.WriteString("$t", "dict");
                    w.WritePropertyName("$v");
                    w.WriteStartObject();
                    foreach (var kv in dict)
                    {
                        w.WritePropertyName(kv.Key);
                        WriteVariant(w, kv.Value);
                    }
                    w.WriteEndObject();
                    w.WriteEndObject();
                    return;
                case List<object> list:
                    w.WriteStartObject();
                    w.WriteString("$t", "list");
                    w.WritePropertyName("$v");
                    w.WriteStartArray();
                    foreach (var item in list)
                    {
                        WriteVariant(w, item);
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                    return;
                case Tuple<long, long> tuple2:
                    w.WriteStartObject();
                    w.WriteString("$t", "tuple_i64_i64");
                    w.WritePropertyName("$v");
                    w.WriteStartArray();
                    w.WriteStringValue(tuple2.Item1.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    w.WriteStringValue(tuple2.Item2.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    w.WriteEndArray();
                    w.WriteEndObject();
                    return;
            }

            // last resort
            Tagged(w, "string", () => w.WriteStringValue(value.ToString() ?? string.Empty));
        }

        private static object? ReadVariant(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            if (!e.TryGetProperty("$t", out var tEl) || tEl.ValueKind != JsonValueKind.String) return null;
            var t = tEl.GetString();
            var v = e.TryGetProperty("$v", out var vEl) ? vEl : default;

            switch (t)
            {
                case "null":
                    return null;
                case "bool":
                    return v.ValueKind == JsonValueKind.True;
                case "string":
                    return v.GetString() ?? string.Empty;
                case "i64":
                    return long.Parse(v.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                case "f32":
                    return float.Parse(v.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                case "f64":
                    return double.Parse(v.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                case "bytes":
                    return Convert.FromBase64String(v.GetString() ?? "");
                case "entityType":
                    if (v.ValueKind == JsonValueKind.String)
                        return (EntityType)Enum.Parse(typeof(EntityType), v.GetString() ?? "Undefined", ignoreCase: true);
                    return (EntityType)v.GetInt32();
                case "dataType":
                    if (v.ValueKind == JsonValueKind.String)
                        return (DataType)Enum.Parse(typeof(DataType), v.GetString() ?? "Text", ignoreCase: true);
                    return (DataType)v.GetInt32();
                case "mem":
                    return new MemoryAddress
                    {
                        Index = ReadNullableInt64(v, "index"),
                        IsGlobal = v.TryGetProperty("global", out var ge) && ge.ValueKind == JsonValueKind.True
                    };
                case "call":
                {
                    var fn = v.TryGetProperty("fn", out var fnEl) ? (fnEl.GetString() ?? string.Empty) : string.Empty;
                    var p = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (v.TryGetProperty("p", out var pEl) && pEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in pEl.EnumerateObject())
                        {
                            p[prop.Name] = ReadVariant(prop.Value)!;
                        }
                    }
                    return new CallInfo { FunctionName = fn, Parameters = p };
                }
                case "vertex":
                    return ReadEntity<Vertex>(v);
                case "relation":
                    return ReadRelation(v);
                case "shape":
                    return ReadShape(v);
                case "pos":
                {
                    var pos = new Position { Dimensions = new List<float>() };
                    if (v.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in v.EnumerateArray())
                        {
                            pos.Dimensions.Add(float.Parse(item.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture));
                        }
                    }
                    return pos;
                }
                case "edata":
                {
                    var typeObj = v.TryGetProperty("type", out var te) ? ReadVariant(te) : null;
                    var data = v.TryGetProperty("data", out var de) && de.ValueKind != JsonValueKind.Null ? de.GetString() : null;
                    return new EntityData
                    {
                        Type = typeObj as HierarchicalDataType ?? new HierarchicalDataType(),
                        Data = data
                    };
                }
                case "hdt":
                {
                    var h = new HierarchicalDataType();
                    if (v.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in v.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                h.Types.Add((DataType)Enum.Parse(typeof(DataType), item.GetString() ?? "Text", ignoreCase: true));
                            }
                            else
                            {
                                h.Types.Add((DataType)item.GetInt32());
                            }
                        }
                    }
                    return h;
                }
                case "dict":
                {
                    var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (v.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in v.EnumerateObject())
                        {
                            dict[prop.Name] = ReadVariant(prop.Value)!;
                        }
                    }
                    return dict;
                }
                case "list":
                {
                    var list = new List<object>();
                    if (v.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in v.EnumerateArray())
                        {
                            list.Add(ReadVariant(item)!);
                        }
                    }
                    return list;
                }
                case "tuple_i64_i64":
                {
                    if (v.ValueKind == JsonValueKind.Array)
                    {
                        var arr = v.EnumerateArray().ToList();
                        if (arr.Count >= 2)
                        {
                            var a = long.Parse(arr[0].GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                            var b = long.Parse(arr[1].GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                            return Tuple.Create(a, b);
                        }
                    }
                    return Tuple.Create(0L, 0L);
                }
                default:
                    return null;
            }
        }

        private static void WriteRelation(Utf8JsonWriter w, Relation r)
        {
            w.WriteStartObject();
            w.WriteString("$t", "relation");
            w.WritePropertyName("$v");
            w.WriteStartObject();
            WriteEntityBaseFields(w, r);
            WriteNullableInt64(w, "fromIndex", r.FromIndex);
            WriteNullableInt64(w, "toIndex", r.ToIndex);
            WriteNullableEnum(w, "fromType", r.FromType);
            WriteNullableEnum(w, "toType", r.ToType);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        private static Relation ReadRelation(JsonElement v)
        {
            var r = ReadEntity<Relation>(v);
            r.FromIndex = ReadNullableInt64(v, "fromIndex");
            r.ToIndex = ReadNullableInt64(v, "toIndex");
            r.FromType = ReadNullableEntityType(v, "fromType");
            r.ToType = ReadNullableEntityType(v, "toType");
            return r;
        }

        private static void WriteShape(Utf8JsonWriter w, Shape s)
        {
            w.WriteStartObject();
            w.WriteString("$t", "shape");
            w.WritePropertyName("$v");
            w.WriteStartObject();
            WriteEntityBaseFields(w, s);
            w.WritePropertyName("origin");
            WriteVariant(w, s.Origin);

            w.WritePropertyName("vertices");
            if (s.Vertices == null) w.WriteNullValue();
            else
            {
                w.WriteStartArray();
                foreach (var v in s.Vertices)
                {
                    WriteVariant(w, v);
                }
                w.WriteEndArray();
            }

            w.WritePropertyName("vertexIndices");
            if (s.VertexIndices == null) w.WriteNullValue();
            else
            {
                w.WriteStartArray();
                foreach (var idx in s.VertexIndices)
                {
                    w.WriteStringValue(idx.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                w.WriteEndArray();
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        private static Shape ReadShape(JsonElement v)
        {
            var s = ReadEntity<Shape>(v);
            s.Origin = v.TryGetProperty("origin", out var o) ? (ReadVariant(o) as Position) : null;

            if (v.TryGetProperty("vertices", out var verts) && verts.ValueKind == JsonValueKind.Array)
            {
                var list = new List<Vertex>();
                foreach (var item in verts.EnumerateArray())
                {
                    list.Add((Vertex)ReadVariant(item)!);
                }
                s.Vertices = list;
            }
            else
            {
                s.Vertices = null;
            }

            if (v.TryGetProperty("vertexIndices", out var idxs) && idxs.ValueKind == JsonValueKind.Array)
            {
                var list = new List<long>();
                foreach (var item in idxs.EnumerateArray())
                {
                    list.Add(long.Parse(item.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture));
                }
                s.VertexIndices = list;
            }
            else
            {
                s.VertexIndices = null;
            }

            return s;
        }

        private static void WriteEntity(Utf8JsonWriter w, string tag, EntityBase e)
        {
            w.WriteStartObject();
            w.WriteString("$t", tag);
            w.WritePropertyName("$v");
            w.WriteStartObject();
            WriteEntityBaseFields(w, e);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        private static T ReadEntity<T>(JsonElement v) where T : EntityBase, new()
        {
            var e = new T();
            ReadEntityBaseFields(v, e);
            return e;
        }

        private static void WriteEntityBaseFields(Utf8JsonWriter w, EntityBase e)
        {
            w.WriteString("type", e.Type.ToString());
            WriteNullableInt64(w, "index", e.Index);
            WriteNullableString(w, "name", e.Name);
            WriteNullableSingle(w, "weight", e.Weight);
            WriteNullableSingle(w, "energy", e.Energy);

            w.WritePropertyName("position");
            WriteVariant(w, e.Position);

            w.WritePropertyName("data");
            WriteVariant(w, e.Data);
        }

        private static void ReadEntityBaseFields(JsonElement v, EntityBase target)
        {
            target.Type = EntityType.Undefined;
            if (v.TryGetProperty("type", out var te))
            {
                if (te.ValueKind == JsonValueKind.String)
                {
                    var s = te.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && Enum.TryParse<EntityType>(s, ignoreCase: true, out var parsed))
                    {
                        target.Type = parsed;
                    }
                }
                else if (te.ValueKind == JsonValueKind.Number)
                {
                    target.Type = (EntityType)te.GetInt32();
                }
            }
            target.Index = ReadNullableInt64(v, "index");
            target.Name = v.TryGetProperty("name", out var ne) && ne.ValueKind != JsonValueKind.Null ? ne.GetString() : null;
            target.Weight = ReadNullableSingle(v, "weight");
            target.Energy = ReadNullableSingle(v, "energy");
            target.Position = v.TryGetProperty("position", out var pe) ? (ReadVariant(pe) as Position) : null;
            target.Data = v.TryGetProperty("data", out var de) ? (ReadVariant(de) as EntityData) : null;
        }

        private static void Tagged(Utf8JsonWriter w, string t, Action writeValue)
        {
            w.WriteStartObject();
            w.WriteString("$t", t);
            w.WritePropertyName("$v");
            writeValue();
            w.WriteEndObject();
        }

        private static void WriteNullableString(Utf8JsonWriter w, string name, string? value)
        {
            if (value == null) w.WriteNull(name);
            else w.WriteString(name, value);
        }

        private static void WriteNullableInt64(Utf8JsonWriter w, string name, long? value)
        {
            if (!value.HasValue) w.WriteNull(name);
            else w.WriteString(name, value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private static long? ReadNullableInt64(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return null;
            return long.Parse(el.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void WriteNullableSingle(Utf8JsonWriter w, string name, float? value)
        {
            if (!value.HasValue) w.WriteNull(name);
            else w.WriteString(name, value.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static float? ReadNullableSingle(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return null;
            return float.Parse(el.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void WriteNullableEnum(Utf8JsonWriter w, string name, EntityType? value)
        {
            if (!value.HasValue) w.WriteNull(name);
            else w.WriteString(name, value.Value.ToString());
        }

        private static EntityType? ReadNullableEntityType(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return null;
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s) && Enum.TryParse<EntityType>(s, ignoreCase: true, out var parsed))
                    return parsed;
                return null;
            }
            return (EntityType)el.GetInt32();
        }
    }
}

