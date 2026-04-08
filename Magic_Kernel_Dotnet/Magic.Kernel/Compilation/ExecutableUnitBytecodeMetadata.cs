using System;
using System.Collections.Generic;
using System.Linq;
using Magic.Kernel;
using Magic.Kernel.Core;
using Magic.Kernel.Processor;

namespace Magic.Kernel.Compilation
{
    /// <summary>
    /// Fills <see cref="ExecutableUnit.Types"/> field metadata by scanning <c>def</c> instruction patterns
    /// (after types are collected via <see cref="Compiler"/> / <see cref="Linker"/> type-name pass).
    /// </summary>
    internal static class ExecutableUnitBytecodeMetadata
    {
        private static readonly HashSet<string> SimpleTwoArgReservedSecond = new(StringComparer.OrdinalIgnoreCase)
        {
            "type", "table", "database", "list"
        };

        public static void AppendTypeFieldsFromDefInstructions(ExecutableUnit unit)
        {
            if (unit?.Types == null || unit.Types.Count == 0)
                return;

            foreach (var t in unit.Types)
            {
                t.Fields.Clear();
                t.Methods.Clear();
            }

            var typeByName = new Dictionary<string, DefType>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in unit.Types)
            {
                if (t == null)
                    continue;
                var nm = (t.Name ?? "").Trim();
                if (nm.Length > 0)
                    typeByName[nm] = t;
                var fq = t.FullName.Trim();
                if (fq.Length > 0 && !string.Equals(fq, nm, StringComparison.OrdinalIgnoreCase))
                    typeByName[fq] = t;
            }

            void ScanBlock(ExecutionBlock? block)
            {
                if (block == null || block.Count == 0)
                    return;

                var slotToType = new Dictionary<(bool Global, long Index), DefType>(new SlotKeyComparer());

                for (var i = 0; i < block.Count; i++)
                {
                    if (block[i].Opcode != Opcodes.Def)
                        continue;

                    if (!TryReadDefArgs(block, i, out var args))
                        continue;

                    if (TryAttachFieldDef(args, slotToType))
                        continue;

                    if (TryAttachMethodDef(args, slotToType))
                        continue;

                    TryMapTypeOrClassSlotAfterDef(block, i, args, slotToType, typeByName);
                }
            }

            ScanBlock(unit.EntryPoint);
            foreach (var p in unit.Procedures.Values)
                ScanBlock(p.Body);
            foreach (var f in unit.Functions.Values)
                ScanBlock(f.Body);
        }

        private static long? EffectiveMemoryIndex(MemoryAddress? ma) =>
            ma == null ? null : ma.Index ?? ma.LogicalIndex;

        private static bool TryAttachFieldDef(
            List<object?> args,
            Dictionary<(bool Global, long Index), DefType> slotToType)
        {
            if (args.Count != 5)
                return false;
            if (args[0] is not MemoryAddress ownerMa || EffectiveMemoryIndex(ownerMa) is not long ownerIdx)
                return false;
            if (args[1] is not string fieldName || string.IsNullOrWhiteSpace(fieldName))
                return false;
            if (args[2] is not string fieldTypeSpec)
                return false;
            if (args[3] is not string kind || !string.Equals(kind, "field", StringComparison.OrdinalIgnoreCase))
                return false;
            if (args[4] is not string visibility)
                return false;

            var key = (ownerMa.IsGlobal, ownerIdx);
            if (!slotToType.TryGetValue(key, out var owner))
                return true;

            fieldName = fieldName.Trim();
            fieldTypeSpec = string.IsNullOrWhiteSpace(fieldTypeSpec) ? "any" : fieldTypeSpec.Trim();
            visibility = string.IsNullOrWhiteSpace(visibility) ? "public" : visibility.Trim().ToLowerInvariant();

            var existing = owner.Fields.FirstOrDefault(f =>
                string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                owner.Fields.Add(new DefTypeField
                {
                    Name = fieldName,
                    Type = fieldTypeSpec,
                    Visibility = visibility
                });
            }
            else
            {
                existing.Type = fieldTypeSpec;
                existing.Visibility = visibility;
            }

            return true;
        }

        private static bool TryAttachMethodDef(
            List<object?> args,
            Dictionary<(bool Global, long Index), DefType> slotToType)
        {
            if (args.Count != 6 && args.Count != 7)
                return false;
            if (args[0] is not MemoryAddress ownerMa || EffectiveMemoryIndex(ownerMa) is not long ownerIdx)
                return false;
            if (args[1] is not string methodShort || string.IsNullOrWhiteSpace(methodShort))
                return false;
            if (args[2] is not string methodFull || string.IsNullOrWhiteSpace(methodFull))
                return false;
            if (args[3] is not string returnType)
                return false;
            if (args[4] is not string kind || !string.Equals(kind, "method", StringComparison.OrdinalIgnoreCase))
                return false;
            if (args[5] is not string visibility)
                return false;

            var firstParamFq = args.Count == 7 && args[6] is string fp && !string.IsNullOrWhiteSpace(fp)
                ? fp.Trim()
                : null;

            var key = (ownerMa.IsGlobal, ownerIdx);
            if (!slotToType.TryGetValue(key, out var owner))
                return true;

            methodShort = methodShort.Trim();
            methodFull = methodFull.Trim();
            returnType = string.IsNullOrWhiteSpace(returnType) ? "void" : returnType.Trim();
            visibility = string.IsNullOrWhiteSpace(visibility) ? "public" : visibility.Trim().ToLowerInvariant();

            var existing = owner.Methods.FirstOrDefault(m =>
                string.Equals(m.FullName, methodFull, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                owner.Methods.Add(new DefTypeMethod
                {
                    Name = methodShort,
                    FullName = methodFull,
                    ReturnType = returnType,
                    Visibility = visibility,
                    FirstParameterTypeFq = firstParamFq
                });
            }
            else
            {
                existing.ReturnType = returnType;
                existing.Visibility = visibility;
                if (firstParamFq != null)
                    existing.FirstParameterTypeFq = firstParamFq;
            }

            return true;
        }

        private static void TryMapTypeOrClassSlotAfterDef(
            ExecutionBlock block,
            int defIndex,
            List<object?> args,
            Dictionary<(bool Global, long Index), DefType> slotToType,
            Dictionary<string, DefType> typeByName)
        {
            if (defIndex + 1 >= block.Count || block[defIndex + 1].Opcode != Opcodes.Pop)
                return;

            if (!TryGetPopSlot(block[defIndex + 1], out var slotKey))
                return;

            string? typeName = null;

            if (args.Count == 2 &&
                args[0] is string tn1 &&
                args[1] is string k2 &&
                string.Equals(k2, "type", StringComparison.OrdinalIgnoreCase))
            {
                typeName = tn1.Trim();
            }
            else if (args.Count >= 2 &&
                     args.All(a => a is string) &&
                     args[0] is string classOrTypeName &&
                     args[1] is string second &&
                     !SimpleTwoArgReservedSecond.Contains(second))
            {
                typeName = classOrTypeName.Trim();
            }

            if (string.IsNullOrEmpty(typeName) || !typeByName.TryGetValue(typeName, out var defType))
                return;

            slotToType[slotKey] = defType;
        }

        private static bool TryGetPopSlot(Command popCmd, out (bool Global, long Index) slotKey)
        {
            slotKey = default;
            if (popCmd.Opcode != Opcodes.Pop)
                return false;
            if (popCmd.Operand1 is not MemoryAddress ma || EffectiveMemoryIndex(ma) is not long idx)
                return false;
            slotKey = (ma.IsGlobal, idx);
            return true;
        }

        internal static object? TryReadPushValue(Command pushCommand)
        {
            if (pushCommand.Opcode != Opcodes.Push)
                return null;

            if (pushCommand.Operand1 is PushOperand po)
                return po.Value;

            if (pushCommand.Operand1 is MemoryAddress ma)
                return ma;

            return pushCommand.Operand1;
        }

        internal static bool TryReadDefArgs(ExecutionBlock block, int defIndex, out List<object?> args)
        {
            args = new List<object?>();
            if (defIndex <= 0)
                return false;

            if (block[defIndex - 1].Opcode != Opcodes.Push)
                return false;

            var arityObj = TryReadPushValue(block[defIndex - 1]);
            var arity = arityObj is long l ? (int)l : arityObj is int i32 ? i32 : -1;
            if (arity <= 0)
            {
                var single = TryReadPushValue(block[defIndex - 1]);
                args.Add(single);
                return true;
            }

            var firstArgIndex = defIndex - 1 - arity;
            if (firstArgIndex < 0)
                return false;

            for (var i = firstArgIndex; i < defIndex - 1; i++)
            {
                if (block[i].Opcode != Opcodes.Push)
                    return false;
                args.Add(TryReadPushValue(block[i]));
            }

            return true;
        }

        private sealed class SlotKeyComparer : IEqualityComparer<(bool Global, long Index)>
        {
            public bool Equals((bool Global, long Index) x, (bool Global, long Index) y) =>
                x.Global == y.Global && x.Index == y.Index;

            public int GetHashCode((bool Global, long Index) obj) =>
                HashCode.Combine(obj.Global, obj.Index);
        }
    }
}
