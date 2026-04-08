using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Magic.Kernel.Core;
using Magic.Kernel.Types;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Magic.Kernel.Compilation
{
    internal static class ExecutableUnitBinarySerializer
    {
        private static readonly byte[] Magic = new byte[] { (byte)'M', (byte)'K', (byte)'E', (byte)'X' };
        private const byte FormatVersion = 1;

        // Variant tags (keep stable!)
        private enum V : byte
        {
            Null = 0,
            Bool = 1,
            Int64 = 2,
            Double = 3,
            Single = 4,
            String = 5,
            Bytes = 6,

            MemoryAddress = 7,
            CallInfo = 8,
            Vertex = 9,
            Relation = 10,
            Shape = 11,
            Position = 12,
            EntityData = 13,
            HierarchicalDataType = 14,
            Dictionary = 15,
            List = 16,
            EntityType = 17,
            DataType = 18,
            PushOperand = 19,
        }

        public static bool IsBinaryFormat(byte[] data)
        {
            return data.Length >= 5 &&
                   data[0] == Magic[0] &&
                   data[1] == Magic[1] &&
                   data[2] == Magic[2] &&
                   data[3] == Magic[3];
        }

        public static byte[] Serialize(ExecutableUnit unit)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            bw.Write(Magic);
            bw.Write(FormatVersion);

            WriteString(bw, unit.Version ?? "0.0.1");
            WriteStringNullable(bw, unit.Name);
            WriteStringNullable(bw, unit.Module);

            WriteExecutionBlock(bw, unit.EntryPoint);
            WriteStringKeyedDictionary(bw, unit.Procedures, WriteProcedure);
            WriteStringKeyedDictionary(bw, unit.Functions, WriteFunction);
            WriteList(bw, unit.Exports, WriteExport);
            WriteList(bw, unit.Types, WriteDefType);
            WriteList(bw, unit.Objects, WriteDefObject);

            bw.Flush();
            return ms.ToArray();
        }

        public static ExecutableUnit Deserialize(byte[] data)
        {
            using var ms = new MemoryStream(data, writable: false);
            using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

            var magic = br.ReadBytes(4);
            if (magic.Length != 4 || !magic.SequenceEqual(Magic))
                throw new InvalidOperationException("Invalid ExecutableUnit binary header.");

            var version = br.ReadByte();
            if (version != FormatVersion)
                throw new InvalidOperationException($"Unsupported ExecutableUnit binary format version: {version}");

            var unit = new ExecutableUnit
            {
                Version = ReadString(br) ?? "0.0.1",
                Name = ReadStringNullable(br),
                Module = ReadStringNullable(br),
            };

            unit.EntryPoint = ReadExecutionBlock(br);
            unit.Procedures = ReadStringKeyedDictionary(br, ReadProcedure);
            unit.Functions = ReadStringKeyedDictionary(br, ReadFunction);
            unit.Exports = ReadList(br, ReadExport);
            unit.Types = ReadList(br, ReadDefType);
            unit.Objects = ReadList(br, ReadDefObject);

            return unit;
        }

        private static void WriteExport(BinaryWriter bw, Export export)
        {
            WriteString(bw, export.Type ?? string.Empty);
            WriteExecutionBlock(bw, export.Source);
        }

        private static Export ReadExport(BinaryReader br)
        {
            return new Export
            {
                Type = ReadString(br) ?? string.Empty,
                Source = ReadExecutionBlock(br),
            };
        }

        private static void WriteDefType(BinaryWriter bw, DefType type)
        {
            bw.Write(type is DefClass);
            WriteString(bw, type.FullName ?? string.Empty);
            if (type is DefClass dc)
            {
                var bases = dc.Inheritances ?? new List<string>();
                bw.Write(bases.Count);
                foreach (var b in bases)
                    WriteString(bw, b ?? string.Empty);
            }
            else
            {
                var gens = type.Generalizations ?? new List<IDefType>();
                bw.Write(gens.Count);
                foreach (var g in gens)
                    WriteString(bw, g?.Name ?? string.Empty);
            }
            var fields = type.Fields ?? new List<DefTypeField>();
            bw.Write(fields.Count);
            foreach (var field in fields)
            {
                WriteString(bw, field.Name ?? string.Empty);
                WriteString(bw, field.Type ?? "any");
                WriteString(bw, field.Visibility ?? "public");
            }
        }

        private static DefType ReadDefType(BinaryReader br)
        {
            var isClass = br.ReadBoolean();
            var name = ReadString(br) ?? string.Empty;
            var count = br.ReadInt32();
            var baseNames = new List<string>(count);
            for (var i = 0; i < count; i++)
                baseNames.Add(ReadString(br) ?? string.Empty);
            var fieldCount = br.ReadInt32();
            var fields = new List<DefTypeField>(fieldCount);
            for (var i = 0; i < fieldCount; i++)
            {
                fields.Add(new DefTypeField
                {
                    Name = ReadString(br) ?? string.Empty,
                    Type = ReadString(br) ?? "any",
                    Visibility = ReadString(br) ?? "public"
                });
            }

            if (isClass)
            {
                var cls = new DefClass(name, baseNames) { Fields = fields };
                cls.NormalizeLegacyQualifiedName();
                return cls;
            }

            var dt = new DefType
            {
                Name = name,
                Generalizations = baseNames.Select(x => (IDefType)new DefType { Name = x }).ToList(),
                Fields = fields
            };
            dt.NormalizeLegacyQualifiedName();
            return dt;
        }

        private static void WriteDefObject(BinaryWriter bw, DefObject obj)
            => WriteString(bw, obj.Type.FullName ?? string.Empty);

        private static DefObject ReadDefObject(BinaryReader br)
        {
            var s = ReadString(br) ?? string.Empty;
            return new DefObject(DefType.FromDefBytecode(s, null, false, null));
        }

        private static void WriteProcedure(BinaryWriter bw, Processor.Procedure proc)
        {
            WriteString(bw, proc.Name ?? string.Empty);
            WriteExecutionBlock(bw, proc.Body);
        }

        private static Processor.Procedure ReadProcedure(BinaryReader br)
        {
            return new Processor.Procedure
            {
                Name = ReadString(br) ?? string.Empty,
                Body = ReadExecutionBlock(br),
            };
        }

        private static void WriteFunction(BinaryWriter bw, Processor.Function func)
        {
            WriteString(bw, func.Name ?? string.Empty);
            WriteExecutionBlock(bw, func.Body);
        }

        private static Processor.Function ReadFunction(BinaryReader br)
        {
            return new Processor.Function
            {
                Name = ReadString(br) ?? string.Empty,
                Body = ReadExecutionBlock(br),
            };
        }

        private static void WriteExecutionBlock(BinaryWriter bw, ExecutionBlock block)
        {
            bw.Write(block?.Count ?? 0);
            if (block == null) return;
            foreach (var cmd in block)
            {
                WriteCommand(bw, cmd);
            }
        }

        private static ExecutionBlock ReadExecutionBlock(BinaryReader br)
        {
            var count = br.ReadInt32();
            var block = new ExecutionBlock();
            for (var i = 0; i < count; i++)
            {
                block.Add(ReadCommand(br));
            }
            return block;
        }

        private static void WriteCommand(BinaryWriter bw, Command cmd)
        {
            bw.Write((int)cmd.Opcode);
            WriteVariant(bw, cmd.Operand1);
            WriteVariant(bw, cmd.Operand2);
        }

        private static Command ReadCommand(BinaryReader br)
        {
            var opcode = (Opcodes)br.ReadInt32();
            var op1 = ReadVariant(br);
            var op2 = ReadVariant(br);
            return new Command
            {
                Opcode = opcode,
                Operand1 = op1,
                Operand2 = op2,
            };
        }

        private static void WriteVariant(BinaryWriter bw, object? value)
        {
            if (value == null)
            {
                bw.Write((byte)V.Null);
                return;
            }

            // primitives
            if (value is bool b)
            {
                bw.Write((byte)V.Bool);
                bw.Write(b);
                return;
            }

            if (value is int i32)
            {
                bw.Write((byte)V.Int64);
                bw.Write((long)i32);
                return;
            }

            if (value is long i64)
            {
                bw.Write((byte)V.Int64);
                bw.Write(i64);
                return;
            }

            if (value is double d)
            {
                bw.Write((byte)V.Double);
                bw.Write(d);
                return;
            }

            if (value is float f)
            {
                bw.Write((byte)V.Single);
                bw.Write(f);
                return;
            }

            if (value is string s)
            {
                bw.Write((byte)V.String);
                WriteString(bw, s);
                return;
            }

            if (value is byte[] bytes)
            {
                bw.Write((byte)V.Bytes);
                bw.Write(bytes.Length);
                bw.Write(bytes);
                return;
            }

            // enums
            if (value is EntityType et)
            {
                bw.Write((byte)V.EntityType);
                bw.Write((int)et);
                return;
            }

            if (value is DataType dt)
            {
                bw.Write((byte)V.DataType);
                bw.Write((int)dt);
                return;
            }

            // known objects
            if (value is MemoryAddress ma)
            {
                bw.Write((byte)V.MemoryAddress);
                WriteNullableInt64(bw, ma.Index);
                return;
            }

            if (value is CallInfo ci)
            {
                bw.Write((byte)V.CallInfo);
                WriteString(bw, ci.FunctionName ?? string.Empty);
                bw.Write(ci.Parameters?.Count ?? 0);
                if (ci.Parameters != null)
                {
                    foreach (var kv in ci.Parameters)
                    {
                        WriteString(bw, kv.Key);
                        WriteVariant(bw, kv.Value);
                    }
                }
                return;
            }

            if (value is PushOperand po)
            {
                bw.Write((byte)V.PushOperand);
                WriteString(bw, po.Kind ?? string.Empty);
                WriteVariant(bw, po.Value);
                return;
            }

            if (value is Vertex v)
            {
                bw.Write((byte)V.Vertex);
                WriteEntityBase(bw, v);
                return;
            }

            if (value is Relation r)
            {
                bw.Write((byte)V.Relation);
                WriteEntityBase(bw, r);
                WriteNullableInt64(bw, r.FromIndex);
                WriteNullableInt64(bw, r.ToIndex);
                WriteNullableEnum(bw, r.FromType);
                WriteNullableEnum(bw, r.ToType);
                return;
            }

            if (value is Shape sh)
            {
                bw.Write((byte)V.Shape);
                WriteEntityBase(bw, sh);
                WritePositionNullable(bw, sh.Origin);
                WriteListNullable(bw, sh.Vertices, vv => WriteVariant(bw, vv));
                WriteListNullable(bw, sh.VertexIndices, idx => bw.Write(idx));
                return;
            }

            if (value is Position pos)
            {
                bw.Write((byte)V.Position);
                WritePosition(bw, pos);
                return;
            }

            if (value is EntityData ed)
            {
                bw.Write((byte)V.EntityData);
                WriteVariant(bw, ed.Type);
                WriteStringNullable(bw, ed.Data);
                return;
            }

            if (value is HierarchicalDataType hdt)
            {
                bw.Write((byte)V.HierarchicalDataType);
                bw.Write(hdt.Types?.Count ?? 0);
                if (hdt.Types != null)
                {
                    foreach (var t in hdt.Types)
                    {
                        bw.Write((int)t);
                    }
                }
                return;
            }

            // collections (must come after known objects, because Vertex/Shape also implement IEnumerable? they don't)
            if (value is Dictionary<string, object> dict)
            {
                bw.Write((byte)V.Dictionary);
                bw.Write(dict.Count);
                foreach (var kv in dict)
                {
                    WriteString(bw, kv.Key);
                    WriteVariant(bw, kv.Value);
                }
                return;
            }

            if (value is List<object> list)
            {
                bw.Write((byte)V.List);
                bw.Write(list.Count);
                foreach (var item in list)
                {
                    WriteVariant(bw, item);
                }
                return;
            }

            // Best-effort: stringify unknown types to avoid hard crash, but keep explicit.
            bw.Write((byte)V.String);
            WriteString(bw, value.ToString() ?? string.Empty);
        }

        private static object? ReadVariant(BinaryReader br)
        {
            var tag = (V)br.ReadByte();
            switch (tag)
            {
                case V.Null:
                    return null;
                case V.Bool:
                    return br.ReadBoolean();
                case V.Int64:
                    return br.ReadInt64();
                case V.Double:
                    return br.ReadDouble();
                case V.Single:
                    return br.ReadSingle();
                case V.String:
                    return ReadString(br) ?? string.Empty;
                case V.Bytes:
                {
                    var len = br.ReadInt32();
                    return br.ReadBytes(len);
                }
                case V.EntityType:
                    return (EntityType)br.ReadInt32();
                case V.DataType:
                    return (DataType)br.ReadInt32();
                case V.MemoryAddress:
                    return new MemoryAddress { Index = ReadNullableInt64(br) };
                case V.CallInfo:
                {
                    var fn = ReadString(br) ?? string.Empty;
                    var count = br.ReadInt32();
                    var p = new Dictionary<string, object>(StringComparer.Ordinal);
                    for (var i = 0; i < count; i++)
                    {
                        var key = ReadString(br) ?? string.Empty;
                        p[key] = ReadVariant(br)!;
                    }
                    return new CallInfo { FunctionName = fn, Parameters = p };
                }
                case V.PushOperand:
                {
                    var kind = ReadString(br) ?? string.Empty;
                    var value = ReadVariant(br);
                    return new PushOperand { Kind = kind, Value = value };
                }
                case V.Vertex:
                    return ReadVertex(br);
                case V.Relation:
                    return ReadRelation(br);
                case V.Shape:
                    return ReadShape(br);
                case V.Position:
                    return ReadPosition(br);
                case V.EntityData:
                {
                    var typeObj = ReadVariant(br);
                    var data = ReadStringNullable(br);
                    return new EntityData
                    {
                        Type = typeObj as HierarchicalDataType ?? new HierarchicalDataType(),
                        Data = data
                    };
                }
                case V.HierarchicalDataType:
                {
                    var count = br.ReadInt32();
                    var h = new HierarchicalDataType();
                    for (var i = 0; i < count; i++)
                    {
                        h.Types.Add((DataType)br.ReadInt32());
                    }
                    return h;
                }
                case V.Dictionary:
                {
                    var count = br.ReadInt32();
                    var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    for (var i = 0; i < count; i++)
                    {
                        var key = ReadString(br) ?? string.Empty;
                        dict[key] = ReadVariant(br)!;
                    }
                    return dict;
                }
                case V.List:
                {
                    var count = br.ReadInt32();
                    var list = new List<object>(count);
                    for (var i = 0; i < count; i++)
                    {
                        list.Add(ReadVariant(br)!);
                    }
                    return list;
                }
                default:
                    throw new InvalidOperationException($"Unknown variant tag: {(byte)tag}");
            }
        }

        private static Vertex ReadVertex(BinaryReader br)
        {
            var baseEntity = ReadEntityBase(br);
            var v = new Vertex();
            ApplyEntityBase(v, baseEntity);
            return v;
        }

        private static Relation ReadRelation(BinaryReader br)
        {
            var baseEntity = ReadEntityBase(br);
            var r = new Relation();
            ApplyEntityBase(r, baseEntity);
            r.FromIndex = ReadNullableInt64(br);
            r.ToIndex = ReadNullableInt64(br);
            r.FromType = ReadNullableEnum(br);
            r.ToType = ReadNullableEnum(br);
            return r;
        }

        private static Shape ReadShape(BinaryReader br)
        {
            var baseEntity = ReadEntityBase(br);
            var sh = new Shape();
            ApplyEntityBase(sh, baseEntity);
            sh.Origin = ReadPositionNullable(br);

            sh.Vertices = ReadListNullable(br, () => (Vertex)ReadVariant(br)!);
            sh.VertexIndices = ReadListNullable(br, () => br.ReadInt64());
            return sh;
        }

        private sealed class EntityBaseSnapshot
        {
            public EntityType Type { get; set; }
            public long? Index { get; set; }
            public string? Name { get; set; }
            public float? Weight { get; set; }
            public float? Energy { get; set; }
            public Position? Position { get; set; }
            public EntityData? Data { get; set; }
        }

        private static void WriteEntityBase(BinaryWriter bw, EntityBase e)
        {
            bw.Write((int)e.Type);
            WriteNullableInt64(bw, e.Index);
            WriteStringNullable(bw, e.Name);
            WriteNullableSingle(bw, e.Weight);
            WriteNullableSingle(bw, e.Energy);
            WritePositionNullable(bw, e.Position);
            WriteVariant(bw, e.Data);
        }

        private static EntityBaseSnapshot ReadEntityBase(BinaryReader br)
        {
            return new EntityBaseSnapshot
            {
                Type = (EntityType)br.ReadInt32(),
                Index = ReadNullableInt64(br),
                Name = ReadStringNullable(br),
                Weight = ReadNullableSingle(br),
                Energy = ReadNullableSingle(br),
                Position = ReadPositionNullable(br),
                Data = ReadVariant(br) as EntityData,
            };
        }

        private static void ApplyEntityBase(EntityBase target, EntityBaseSnapshot snap)
        {
            target.Type = snap.Type;
            target.Index = snap.Index;
            target.Name = snap.Name;
            target.Weight = snap.Weight;
            target.Energy = snap.Energy;
            target.Position = snap.Position;
            target.Data = snap.Data;
        }

        private static void WritePositionNullable(BinaryWriter bw, Position? pos)
        {
            if (pos == null)
            {
                bw.Write(false);
                return;
            }
            bw.Write(true);
            WritePosition(bw, pos);
        }

        private static Position? ReadPositionNullable(BinaryReader br)
        {
            var has = br.ReadBoolean();
            if (!has) return null;
            return ReadPosition(br);
        }

        private static void WritePosition(BinaryWriter bw, Position pos)
        {
            var dims = pos.Dimensions ?? new List<float>();
            bw.Write(dims.Count);
            foreach (var d in dims)
            {
                bw.Write(d);
            }
        }

        private static Position ReadPosition(BinaryReader br)
        {
            var count = br.ReadInt32();
            var pos = new Position();
            for (var i = 0; i < count; i++)
            {
                pos.Dimensions.Add(br.ReadSingle());
            }
            return pos;
        }

        private static void WriteNullableInt64(BinaryWriter bw, long? v)
        {
            bw.Write(v.HasValue);
            if (v.HasValue) bw.Write(v.Value);
        }

        private static long? ReadNullableInt64(BinaryReader br)
        {
            var has = br.ReadBoolean();
            return has ? br.ReadInt64() : null;
        }

        private static void WriteNullableSingle(BinaryWriter bw, float? v)
        {
            bw.Write(v.HasValue);
            if (v.HasValue) bw.Write(v.Value);
        }

        private static float? ReadNullableSingle(BinaryReader br)
        {
            var has = br.ReadBoolean();
            return has ? br.ReadSingle() : null;
        }

        private static void WriteNullableEnum(BinaryWriter bw, EntityType? et)
        {
            bw.Write(et.HasValue);
            if (et.HasValue) bw.Write((int)et.Value);
        }

        private static EntityType? ReadNullableEnum(BinaryReader br)
        {
            var has = br.ReadBoolean();
            return has ? (EntityType)br.ReadInt32() : null;
        }

        private static void WriteString(BinaryWriter bw, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            bw.Write(bytes.Length);
            bw.Write(bytes);
        }

        private static string? ReadString(BinaryReader br)
        {
            var len = br.ReadInt32();
            if (len < 0) return null;
            if (len == 0) return string.Empty;
            var bytes = br.ReadBytes(len);
            return Encoding.UTF8.GetString(bytes);
        }

        private static void WriteStringNullable(BinaryWriter bw, string? value)
        {
            bw.Write(value != null);
            if (value != null) WriteString(bw, value);
        }

        private static string? ReadStringNullable(BinaryReader br)
        {
            var has = br.ReadBoolean();
            return has ? ReadString(br) : null;
        }

        private static void WriteList<T>(BinaryWriter bw, List<T>? list, Action<BinaryWriter, T> writeItem)
        {
            bw.Write(list?.Count ?? 0);
            if (list == null) return;
            foreach (var item in list) writeItem(bw, item);
        }

        private static List<T> ReadList<T>(BinaryReader br, Func<BinaryReader, T> readItem)
        {
            var count = br.ReadInt32();
            var list = new List<T>(count);
            for (var i = 0; i < count; i++) list.Add(readItem(br));
            return list;
        }

        private static void WriteListNullable<T>(BinaryWriter bw, List<T>? list, Action<T> writeItem)
        {
            bw.Write(list != null);
            if (list == null) return;
            bw.Write(list.Count);
            foreach (var item in list) writeItem(item);
        }

        private static List<T>? ReadListNullable<T>(BinaryReader br, Func<T> readItem)
        {
            var has = br.ReadBoolean();
            if (!has) return null;
            var count = br.ReadInt32();
            var list = new List<T>(count);
            for (var i = 0; i < count; i++) list.Add(readItem());
            return list;
        }

        private static void WriteStringKeyedDictionary<T>(BinaryWriter bw, Dictionary<string, T>? dict, Action<BinaryWriter, T> writeValue)
        {
            bw.Write(dict?.Count ?? 0);
            if (dict == null) return;
            foreach (var kv in dict)
            {
                WriteString(bw, kv.Key);
                writeValue(bw, kv.Value);
            }
        }

        private static Dictionary<string, T> ReadStringKeyedDictionary<T>(BinaryReader br, Func<BinaryReader, T> readValue)
        {
            var count = br.ReadInt32();
            var dict = new Dictionary<string, T>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var key = ReadString(br) ?? string.Empty;
                dict[key] = readValue(br);
            }
            return dict;
        }
    }
}

