using Magic.Kernel.Compilation;
using Magic.Kernel.Core;
using Magic.Kernel.Core.OS;
using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Magic.Kernel.Devices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;

namespace Magic.Kernel.Interpretation
{
    public class Interpreter
    {
        private long instructionPointer = 0;
        private KernelConfiguration? _configuration;
        private ExecutableUnit? _unit;
        private ExecutionBlock? _currentBlock;

        private readonly Stack<CallFrame> _callStack = new Stack<CallFrame>();
        private Dictionary<string, int> _currentLabelOffsets = new Dictionary<string, int>(StringComparer.Ordinal);

        private sealed class CallFrame
        {
            public required ExecutionBlock Block { get; init; }
            public required long ReturnIp { get; init; }
            public string? Name { get; init; }
        }

        public List<object> Stack = new List<object>();
        public List<object> Registers = new List<object>();
        public Dictionary<long, object> Memory = new Dictionary<long, object>();
        public Dictionary<long, object> GlobalMemory = new Dictionary<long, object>();

        public KernelConfiguration? Configuration
        {
            get => _configuration;
            set => _configuration = value;
        }

        public async Task<List<InterpretationResult>> InterpreteAsync(List<ExecutableUnit> list)
        {
            var result = new List<InterpretationResult>();
            foreach (var item in list)
            {
                var unitResult = await InterpreteAsync(item);
                result.Add(unitResult);
            }
            return result;
        }

        public async Task<InterpretationResult> InterpreteAsync(ExecutableUnit executableUnit)
        {
            instructionPointer = 0;
            _unit = executableUnit ?? throw new ArgumentNullException(nameof(executableUnit));
            _currentBlock = executableUnit.EntryPoint ?? new ExecutionBlock();
            _currentLabelOffsets = BuildLabelOffsets(_currentBlock);
            _callStack.Clear();
            GlobalMemory.Clear();

            // SpaceName в ExecutableUnit: system|module|program для префикса ключей диска
            executableUnit.SpaceName = BuildSpaceName(executableUnit.System, executableUnit.Module, executableUnit.Name);

            var previousUnit = ExecutionContext.CurrentUnit;
            ExecutionContext.CurrentUnit = executableUnit;
            try
            {
                while (true)
                {
                    if (_currentBlock == null)
                        break;

                    // Implicit return when reaching end of a procedure/function body.
                    if (instructionPointer >= _currentBlock.Count)
                    {
                        if (_callStack.Count == 0)
                            break;

                        var frame = _callStack.Pop();
                        _currentBlock = frame.Block;
                        instructionPointer = frame.ReturnIp;
                        continue;
                    }

                    var command = _currentBlock[(int)instructionPointer];
                    instructionPointer++; // advance first; Call/Ret may override instructionPointer
                    await ExecuteAsync(command);
                }

                return new InterpretationResult
                {
                    Success = true,
                };
            }
            finally
            {
                ExecutionContext.CurrentUnit = previousUnit;
            }
        }

        private async Task ExecuteAsync(Command command)
        {
            switch (command.Opcode)
            {
                case Opcodes.Nop:
                    break;

                case Opcodes.AddVertex:
                    await ExecuteAddVertexAsync(command);
                    break;

                case Opcodes.AddRelation:
                    await ExecuteAddRelationAsync(command);
                    break;

                case Opcodes.AddShape:
                    await ExecuteAddShapeAsync(command);
                    break;

                case Opcodes.Call:
                    await ExecuteCallAsync(command);
                    break;

                case Opcodes.Pop:
                    await ExecutePopAsync(command);
                    break;

                case Opcodes.Push:
                    await ExecutePushAsync(command);
                    break;

                case Opcodes.Def:
                    await ExecuteDefAsync(command);
                    break;
                case Opcodes.DefGen:
                    await ExecuteDefGenAsync(command);
                    break;
                case Opcodes.CallObj:
                    await ExecuteCallObjAsync(command);
                    break;
                case Opcodes.AwaitObj:
                    await ExecuteAwaitObjAsync(command);
                    break;
                case Opcodes.StreamWaitObj:
                    await ExecuteStreamWaitObjAsync(command);
                    break;
                case Opcodes.Await:
                    await ExecuteAwaitAsync(command);
                    break;
                case Opcodes.Label:
                    await ExecuteLabelAsync(command);
                    break;
                case Opcodes.Cmp:
                    await ExecuteCmpAsync(command);
                    break;
                case Opcodes.Je:
                    await ExecuteJeAsync(command);
                    break;
                case Opcodes.Jmp:
                    await ExecuteJmpAsync(command);
                    break;
                case Opcodes.GetObj:
                    await ExecuteGetObjAsync(command);
                    break;
                case Opcodes.SetObj:
                    await ExecuteSetObjAsync(command);
                    break;
                case Opcodes.StreamWait:
                    await ExecuteStreamWaitAsync(command);
                    break;

                case Opcodes.SysCall:
                    await ExecuteSysCallAsync(command);
                    break;

                case Opcodes.Ret:
                    await ExecuteRetAsync();
                    break;

                default:
                    throw new NotImplementedException($"The OpCode '{command.Opcode}' is not implemented.");
            }
        }

        private async Task ExecuteAddVertexAsync(Command command)
        {
            if (command.Operand1 is not Vertex vertex)
            {
                throw new InvalidOperationException($"AddVertex command expects Vertex as Operand1, but got {command.Operand1?.GetType().Name ?? "null"}");
            }

            if (_configuration?.DefaultDisk == null)
            {
                throw new InvalidOperationException("DefaultSpaceDisk is not configured. Cannot execute AddVertex command.");
            }

            var result = await _configuration.DefaultDisk.AddVertex(vertex, _unit!.SpaceName);

            if (result != SpaceOperationResult.Success)
            {
                throw new InvalidOperationException($"Failed to add vertex: {result}");
            }
        }

        private async Task ExecuteAddRelationAsync(Command command)
        {
            if (command.Operand1 is not Relation relation)
            {
                throw new InvalidOperationException($"AddRelation command expects Relation as Operand1, but got {command.Operand1?.GetType().Name ?? "null"}");
            }

            if (_configuration?.DefaultDisk == null)
            {
                throw new InvalidOperationException("DefaultSpaceDisk is not configured. Cannot execute AddRelation command.");
            }

            var result = await _configuration.DefaultDisk.AddRelation(relation, _unit!.SpaceName);

            if (result != SpaceOperationResult.Success)
            {
                throw new InvalidOperationException($"Failed to add relation: {result}");
            }
        }

        private async Task ExecuteAddShapeAsync(Command command)
        {
            if (command.Operand1 is not Shape shape)
            {
                throw new InvalidOperationException($"AddShape command expects Shape as Operand1, but got {command.Operand1?.GetType().Name ?? "null"}");
            }

            if (_configuration?.DefaultDisk == null)
            {
                throw new InvalidOperationException("DefaultSpaceDisk is not configured. Cannot execute AddShape command.");
            }

            var result = await _configuration.DefaultDisk.AddShape(shape, _unit!.SpaceName);

            if (result != SpaceOperationResult.Success)
            {
                throw new InvalidOperationException($"Failed to add shape: {result}");
            }
        }

        private async Task ExecuteCallAsync(Command command)
        {
            if (command.Operand1 is not CallInfo callInfo)
            {
                throw new InvalidOperationException($"Call command expects CallInfo as Operand1, but got {command.Operand1?.GetType().Name ?? "null"}");
            }

            // Аргументы на стеке: сверху arity, под ним arg1, arg2, ...
            if (callInfo.Parameters == null || callInfo.Parameters.Count == 0)
            {
                var (n, args) = PopArityAndArgs(extraRequired: 0);
                callInfo.Parameters ??= new Dictionary<string, object>();
                for (var i = 0; i < n; i++)
                    callInfo.Parameters[$"{i}"] = args[i]!;
            }

            // 1) User-defined procedure/function in current executable unit
            if (_unit != null)
            {
                if (_unit.Procedures != null && _unit.Procedures.TryGetValue(callInfo.FunctionName, out var proc))
                {
                    if (_currentBlock == null) throw new InvalidOperationException("Interpreter current block is not initialized.");
                    _callStack.Push(new CallFrame { Block = _currentBlock, ReturnIp = instructionPointer, Name = proc.Name });
                    _currentBlock = proc.Body ?? new ExecutionBlock();
                    _currentLabelOffsets = BuildLabelOffsets(_currentBlock);
                    instructionPointer = 0;
                    return;
                }

                if (_unit.Functions != null && _unit.Functions.TryGetValue(callInfo.FunctionName, out var func))
                {
                    if (_currentBlock == null) throw new InvalidOperationException("Interpreter current block is not initialized.");
                    _callStack.Push(new CallFrame { Block = _currentBlock, ReturnIp = instructionPointer, Name = func.Name });
                    _currentBlock = func.Body ?? new ExecutionBlock();
                    _currentLabelOffsets = BuildLabelOffsets(_currentBlock);
                    instructionPointer = 0;
                    return;
                }
            }

            // 1.5) Локальная подпрограмма по label в текущем блоке (для streamwait-loop делегатов и прочих локальных "функций").
            if (_currentBlock != null && !string.IsNullOrEmpty(callInfo.FunctionName) &&
                _currentLabelOffsets.TryGetValue(callInfo.FunctionName, out _))
            {
                _callStack.Push(new CallFrame { Block = _currentBlock, ReturnIp = instructionPointer, Name = callInfo.FunctionName });
                instructionPointer = ResolveLabelOffset(callInfo.FunctionName);
                return;
            }

            // 2) Fallback: system functions (backward compatible behavior)
            var vaultReader = _configuration?.VaultReader ?? new EnvironmentVaultReader();
            var systemFunctions = new SystemFunctions(_configuration, Stack, Memory, vaultReader);
            var handled = await systemFunctions.ExecuteAsync(callInfo);

            if (!handled)
                throw new NotImplementedException($"Function '{callInfo.FunctionName}' is not implemented (not found in Procedures/Functions and not a system function).");
        }
        private async Task ExecuteSysCallAsync(Command command)
        {
            if (command.Operand1 is not CallInfo callInfo)
            {
                throw new InvalidOperationException($"SysCall command expects CallInfo as Operand1, but got {command.Operand1?.GetType().Name ?? "null"}");
            }

            var vaultReader = _configuration?.VaultReader ?? new EnvironmentVaultReader();
            var systemFunctions = new SystemFunctions(_configuration, Stack, Memory, vaultReader);
            var handled = await systemFunctions.ExecuteAsync(callInfo);

            if (!handled)
                throw new NotImplementedException($"System function '{callInfo.FunctionName}' is not implemented.");
        }

        private Task ExecuteRetAsync()
        {
            if (_callStack.Count == 0)
            {
                // Return from entrypoint: terminate execution
                if (_currentBlock != null)
                    instructionPointer = _currentBlock.Count;
                return Task.CompletedTask;
            }

            var frame = _callStack.Pop();
            _currentBlock = frame.Block;
            _currentLabelOffsets = BuildLabelOffsets(_currentBlock);
            instructionPointer = frame.ReturnIp;
            return Task.CompletedTask;
        }

        private Task ExecutePopAsync(Command command)
        {
            if (Stack.Count == 0)
            {
                // Нечего снимать со стека — просто выходим.
                return Task.CompletedTask;
            }

            // Вариант без операнда: просто снять верхнее значение со стека и отбросить.
            if (command.Operand1 is not MemoryAddress memoryAddress)
            {
                Stack.RemoveAt(Stack.Count - 1);
                return Task.CompletedTask;
            }

            if (memoryAddress.Index == null)
            {
                // Исторически пустой pop мог компилироваться как pop с MemoryAddress без индекса.
                // В таком случае ведём себя как унарный pop: просто снимаем верхнее значение.
                Stack.RemoveAt(Stack.Count - 1);
                return Task.CompletedTask;
            }

            // Извлекаем значение из стека
            var value = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);

            // Сохраняем в память: верхний executionUnit — по умолчанию GlobalMemory
            var targetMemory = ResolveMemory(memoryAddress, forWrite: true);
            targetMemory[memoryAddress.Index!.Value] = value;
            return Task.CompletedTask;
        }

        /// <summary>Верхний (корневой) executionUnit — push/pop по умолчанию в GlobalMemory; при вызове процедур — в Memory.</summary>
        private Dictionary<long, object> ResolveMemory(MemoryAddress memoryAddress, bool forWrite)
        {
            if (memoryAddress.IsGlobal) return GlobalMemory;
            if (_callStack.Count == 0) return GlobalMemory; // topmost unit: default to global
            return Memory;
        }

        private Task ExecutePushAsync(Command command)
        {
            if (command.Operand1 is MemoryAddress memoryAddress)
            {
                if (memoryAddress.Index == null)
                    throw new InvalidOperationException("Memory address index is not specified.");
                var sourceMemory = ResolveMemory(memoryAddress, forWrite: false);
                if (!sourceMemory.TryGetValue(memoryAddress.Index.Value, out var value))
                    throw new InvalidOperationException($"Memory[{memoryAddress.Index.Value}] is not set. Cannot push.");
                Stack.Add(value);
                return Task.CompletedTask;
            }
            if (command.Operand1 is PushOperand po)
            {
                object? val = po.Kind switch
                {
                    "Type" => po.Value,
                    "IntLiteral" => po.Value,
                    "StringLiteral" => po.Value,
                    _ => null
                };
                Stack.Add(val!);
                return Task.CompletedTask;
            }
            throw new InvalidOperationException($"Push command expects MemoryAddress or PushOperand, but got {command.Operand1?.GetType().Name ?? "null"}");
        }

        private Task ExecuteDefAsync(Command command)
        {
            if (Stack.Count < 1) throw new InvalidOperationException("Def: stack underflow.");

            var arityObj = Stack[Stack.Count - 1];
            if (arityObj is long or int)
            {
                var n = arityObj is long l ? (int)l : (int)arityObj;
                if (n > 0 && Stack.Count >= n + 1)
                {
                    var (_, args) = PopArityAndArgs(extraRequired: 0);
                    Stack.Add(Hal.Def(args, _unit)!);
                    return Task.CompletedTask;
                }
            }

            var typeObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            Stack.Add(Hal.Def(typeObj, _unit));
            return Task.CompletedTask;
        }

        private Task ExecuteDefGenAsync(Command command)
        {
            (_, var genValues) = PopArityAndArgs(extraRequired: 1);
            var defObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            Stack.Add(Hal.DefGen(defObj, genValues));
            return Task.CompletedTask;
        }

        private async Task ExecuteCallObjAsync(Command command)
        {
            (_, var args) = PopArityAndArgs(extraRequired: 1);
            var obj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var methodName = command.Operand1 as string ?? "";  
            var result = await Hal.CallObjAsync(obj, methodName, args).ConfigureAwait(false);
            Stack.Add(result!);
        }

        private Task ExecuteGetObjAsync(Command command)
        {
            if (Stack.Count < 2) throw new InvalidOperationException("GetObj: stack underflow.");
            var memberNameObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var obj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var value = Hal.GetObj(obj, memberNameObj?.ToString());
            Stack.Add(value!);
            return Task.CompletedTask;
        }

        private Task ExecuteSetObjAsync(Command command)
        {
            if (Stack.Count < 3) throw new InvalidOperationException("SetObj: stack underflow.");
            var value = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var memberNameObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var obj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var updated = Hal.SetObj(obj, memberNameObj?.ToString(), value);
            Stack.Add(updated!);
            return Task.CompletedTask;
        }

        private Task ExecuteStreamWaitAsync(Command command)
        {
            if (Stack.Count < 1)
                throw new InvalidOperationException("StreamWait: stack underflow.");

            // New contract: stack = [..., functionName, arg0, ..., argN-1, arity]
            // For backward-compat (old binaries), when there is only a single value on stack,
            // treat it as the payload.
            if (Stack.Count == 1)
            {
                var single = Stack[Stack.Count - 1];
                Stack.RemoveAt(Stack.Count - 1);
                Hal.StreamWaitAsync(ExecutionContext.CurrentUnit, null, single);
                return Task.CompletedTask;
            }

            // Expect at least functionName + arity.
            if (Stack.Count < 2)
                throw new InvalidOperationException("StreamWait: invalid stack layout (expected function and arity).");

            // Pop arity and arguments, leaving functionName on the stack.
            var (n, args) = PopArityAndArgs(extraRequired: 1);
            if (Stack.Count < 1)
                throw new InvalidOperationException("StreamWait: missing function name on stack.");

            var fnObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var fnName = fnObj?.ToString() ?? string.Empty;

            Hal.StreamWaitAsync(ExecutionContext.CurrentUnit, fnName, args);
            return Task.CompletedTask;
        }

        private async Task ExecuteStreamWaitObjAsync(Command command)
        {
            if (Stack.Count < 2) throw new InvalidOperationException("StreamWaitObj: stack underflow.");
            var streamWaitTypeObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var obj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);

            var streamWaitType = streamWaitTypeObj?.ToString() ?? "delta";
            var result = await Hal.StreamWaitObjAsync(obj, streamWaitType).ConfigureAwait(false);
            if (string.Equals(streamWaitType, "data", StringComparison.OrdinalIgnoreCase))
                Stack.Add(result.Delta!);
            else
            {
                Stack.Add(result.Aggregate!);
                Stack.Add(result.Delta!);
                Stack.Add(result.IsEnd ? 1L : 0L);
            }
        }

        private async Task ExecuteAwaitObjAsync(Command command)
        {
            if (Stack.Count < 1) throw new InvalidOperationException("AwaitObj: stack underflow.");
            var obj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var result = await Hal.ExecuteAwaitObjAsync(obj).ConfigureAwait(false);
            Stack.Add(result!);
        }

        private async Task ExecuteAwaitAsync(Command command)
        {
            if (Stack.Count < 1) throw new InvalidOperationException("Await: stack underflow.");
            var obj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);

            if (obj is Task task)
            {
                await task.ConfigureAwait(false);
                var taskType = task.GetType();
                if (taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var resultProperty = taskType.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
                    Stack.Add(resultProperty?.GetValue(task)!);
                }
                else
                {
                    Stack.Add(null!);
                }
                return;
            }

            if (obj is IDefType)
            {
                var awaited = await Hal.ExecuteAwaitAsync(obj).ConfigureAwait(false);
                Stack.Add(awaited!);
                return;
            }

            Stack.Add(obj!);
        }

        private Task ExecuteLabelAsync(Command command)
        {
            return Task.CompletedTask;
        }

        private Task ExecuteCmpAsync(Command command)
        {
            if (!TryResolveCmpOperandValue(command.Operand1, out var left))
            {
                Stack.Add(0L);
                return Task.CompletedTask;
            }

            if (!TryResolveCmpOperandValue(command.Operand2, out var right))
            {
                Stack.Add(0L);
                return Task.CompletedTask;
            }

            Stack.Add(AreCmpValuesEqual(left, right) ? 1L : 0L);
            return Task.CompletedTask;
        }

        private Task ExecuteJeAsync(Command command)
        {
            if (Stack.Count == 0)
                throw new InvalidOperationException("Je: stack underflow (condition is missing).");

            var conditionObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            if (!IsTruthy(conditionObj))
                return Task.CompletedTask;

            var label = command.Operand1?.ToString() ?? "";
            instructionPointer = ResolveLabelOffset(label);
            return Task.CompletedTask;
        }

        private Task ExecuteJmpAsync(Command command)
        {
            var label = command.Operand1?.ToString() ?? "";
            instructionPointer = ResolveLabelOffset(label);
            return Task.CompletedTask;
        }

        /// <summary>Pops arity (int/long) from stack, then pops n values. Returns (n, args) with args[0] = first arg below arity. Throws if stack has fewer than 1 + n + extraRequired elements.</summary>
        private (int n, object?[] args) PopArityAndArgs(int extraRequired = 0)
        {
            if (Stack.Count < 1) throw new InvalidOperationException("Stack underflow (arity).");
            var arityObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var n = arityObj is long l ? (int)l : (arityObj is int k ? k : 0);
            if (Stack.Count < n + extraRequired) throw new InvalidOperationException("Not enough arguments on stack.");
            var args = new object?[n];
            for (var i = 0; i < n; i++)
            {
                args[n - 1 - i] = Stack[Stack.Count - 1];
                Stack.RemoveAt(Stack.Count - 1);
            }
            return (n, args);
        }

        private bool TryResolveCmpOperandValue(object? operand, out object? value)
        {
            value = null;
            if (operand == null)
                return false;

            if (operand is MemoryAddress memoryAddress)
                return TryResolveMemoryValue(memoryAddress, out value);

            if (operand is PushOperand pushOperand)
            {
                value = pushOperand.Value;
                return true;
            }

            value = operand;
            return true;
        }

        private bool TryResolveMemoryValue(MemoryAddress memoryAddress, out object? value)
        {
            value = null;
            if (!memoryAddress.Index.HasValue)
                return false;

            var index = memoryAddress.Index.Value;
            var preferredMemory = ResolveMemory(memoryAddress, forWrite: false);
            if (preferredMemory.TryGetValue(index, out value))
                return true;

            // Fallback: tolerate values that may be placed in the other scope.
            if (!ReferenceEquals(preferredMemory, Memory) && Memory.TryGetValue(index, out value))
                return true;
            if (!ReferenceEquals(preferredMemory, GlobalMemory) && GlobalMemory.TryGetValue(index, out value))
                return true;

            return false;
        }

        private static bool AreCmpValuesEqual(object? left, object? right)
        {
            if (TryConvertToDecimal(left, out var leftNumber) && TryConvertToDecimal(right, out var rightNumber))
                return leftNumber == rightNumber;

            if (left is string leftString && right is string rightString)
                return string.Equals(leftString, rightString, StringComparison.Ordinal);

            return Equals(left, right);
        }

        private static bool IsTruthy(object? value)
        {
            return value switch
            {
                null => false,
                bool b => b,
                byte b => b != 0,
                sbyte sb => sb != 0,
                short s => s != 0,
                ushort us => us != 0,
                int i => i != 0,
                uint ui => ui != 0,
                long l => l != 0,
                ulong ul => ul != 0,
                float f => f != 0f,
                double d => d != 0d,
                decimal dm => dm != 0m,
                string str => !string.IsNullOrEmpty(str) &&
                              (str.Equals("true", StringComparison.OrdinalIgnoreCase) || str == "1"),
                _ => true
            };
        }

        private static bool TryConvertToDecimal(object? value, out decimal result)
        {
            result = 0m;
            switch (value)
            {
                case null:
                    return true;
                case byte b:
                    result = b;
                    return true;
                case sbyte sb:
                    result = sb;
                    return true;
                case short s:
                    result = s;
                    return true;
                case ushort us:
                    result = us;
                    return true;
                case int i:
                    result = i;
                    return true;
                case uint ui:
                    result = ui;
                    return true;
                case long l:
                    result = l;
                    return true;
                case ulong ul:
                    result = ul;
                    return true;
                case float f:
                    result = (decimal)f;
                    return true;
                case double d:
                    result = (decimal)d;
                    return true;
                case decimal dm:
                    result = dm;
                    return true;
                case bool b:
                    result = b ? 1m : 0m;
                    return true;
                case object o:
                    result = o != null ? 1m : 0m;
                    return true;
                default:
                    return false;
            }
        }

        private static Dictionary<string, int> BuildLabelOffsets(ExecutionBlock? block)
        {
            var labels = new Dictionary<string, int>(StringComparer.Ordinal);
            if (block == null)
                return labels;

            for (var i = 0; i < block.Count; i++)
            {
                if (block[i].Opcode != Opcodes.Label)
                    continue;
                var labelName = block[i].Operand1?.ToString() ?? "";
                if (!string.IsNullOrEmpty(labelName))
                    labels[labelName] = i + 1;
            }

            return labels;
        }

        private int ResolveLabelOffset(string labelName)
        {
            if (_currentBlock == null)
                throw new InvalidOperationException("Current block is not set.");
            if (string.IsNullOrEmpty(labelName) || !_currentLabelOffsets.TryGetValue(labelName, out var offset))
                throw new InvalidOperationException($"Label '{labelName}' is not defined.");
            return offset;
        }

        /// <summary>spaceName = system|module|program (non-empty parts joined by "|"); empty string when all empty.</summary>
        private static string BuildSpaceName(string? systemName, string? moduleName, string? programName)
        {
            var s = string.IsNullOrEmpty(systemName) ? null : systemName.Trim();
            var m = string.IsNullOrEmpty(moduleName) ? null : moduleName.Trim();
            var p = string.IsNullOrEmpty(programName) ? null : programName.Trim();
            var parts = new List<string>();
            if (s != null) parts.Add(s);
            if (m != null) parts.Add(m);
            if (p != null) parts.Add(p);
            return parts.Count > 0 ? string.Join("|", parts) : string.Empty;
        }
    }
}
