using Magic.Kernel.Compilation;
using Magic.Kernel.Core;
using Magic.Kernel.Core.OS;
using Magic.Kernel.Processor;
using Magic.Kernel.Types;
using Magic.Kernel.Space;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Streams;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using System.IO;
using System.Threading;

namespace Magic.Kernel.Interpretation
{
    public partial class Interpreter
    {
        private long instructionPointer = 0;
        private KernelConfiguration? _configuration;
        private ExecutableUnit? _unit;
        private ExecutionBlock? _currentBlock;

        private readonly Stack<CallFrame> _callStack = new Stack<CallFrame>();
        private Dictionary<string, int> _currentLabelOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        private bool _skipToDefExpr;
        private readonly Stack<(ExecutionBlock Block, long ReturnIp)> _lambdaCallStack = new Stack<(ExecutionBlock, long)>();
        private readonly Stack<object?[]> _lambdaArgsStack = new Stack<object?[]>();
        private int _queryExecutionDepth;
        public List<DefType> InheritedTypes { get; set; } = new List<DefType>();
        public Devices.Streams.ClawSocketContext? CurrentSocketContext { get; set; }

        private sealed class CallFrame
        {
            public required ExecutionBlock Block { get; init; }
            public required long ReturnIp { get; init; }
            public string? Name { get; init; }

            /// <summary>После <c>ret</c> из вызванного тела — положить на стек (для <c>callobj</c> → user <c>_ctor_</c> на <see cref="DefObject"/>).</summary>
            public object? PushOnRet { get; init; }

            /// <summary>После <c>ret</c> из метода типа, вызванного через <c>callobj</c> с коротким именем — положить <c>null</c> (как у <see cref="Hal.CallObjAsync"/> для void).</summary>
            public bool PushNullReturnOnRet { get; init; }
        }

        public List<object> Stack = new List<object>();
        public List<object> Registers = new List<object>();

        /// <summary>Слои памяти текущего интерпретатора.</summary>
        public MemoryContext MemoryContext { get; } = new MemoryContext();

        /// <summary>
        /// Глобальная память интерпретатора (обертка над <see cref="MemoryContext"/> для обратной совместимости).
        /// Старые тесты и код обращались к <c>GlobalMemory</c> напрямую.
        /// </summary>
        public Dictionary<long, object> GlobalMemory => MemoryContext.Global;

        public KernelConfiguration? Configuration
        {
            get => _configuration;
            set => _configuration = value;
        }

        public InterpreterDebugSession? DebugSession { get; set; }

        /// <summary>Если true, выполнять entrypoint в этом интерпретаторе, а не через <see cref="KernelRuntime.SpawnAsync"/>.</summary>
        public bool BypassRuntimeSpawn { get; set; }

        /// <summary>Если задано, <c>read/readln</c> читают отсюда; иначе <see cref="System.Console.In"/>.</summary>
        public TextReader? StandardInput { get; set; }

        private int _debugSkipSourceLine;
        private string? _debugSkipSourcePath;
        private int _debugSkipAsmLine;
        private bool _breakOnDifferentSourceLine;
        private int _stepOverAnchorLine;
        private string? _stepOverAnchorSourcePath;
        private int _instructionStepsRemaining;

        /// <summary>Последняя пауза была после инструкции (UI уже на PC следующей). StepInstruction тогда подавляет одно срабатывание breakpoint до её выполнения.</summary>
        private bool _debugPausedAfterInstruction;

        private int _debugOneShotSkipAsmBreakpoint;
        private int _debugOneShotSkipSourceBreakpoint;

        private bool _stepIntoSameLineSkip;
        private int _stepIntoAnchorLine;
        private string? _stepIntoAnchorSourcePath;

        private void ResetDebugSessionState()
        {
            _debugSkipSourceLine = 0;
            _debugSkipSourcePath = null;
            _debugSkipAsmLine = 0;
            _breakOnDifferentSourceLine = false;
            _stepOverAnchorLine = 0;
            _stepOverAnchorSourcePath = null;
            _instructionStepsRemaining = 0;
            _debugPausedAfterInstruction = false;
            _debugOneShotSkipAsmBreakpoint = 0;
            _debugOneShotSkipSourceBreakpoint = 0;
            _stepIntoSameLineSkip = false;
            _stepIntoAnchorLine = 0;
            _stepIntoAnchorSourcePath = null;
        }

        private static bool IsStepIntoUserCallOpcode(Opcodes op)
            => op is Opcodes.Call or Opcodes.ACall or Opcodes.CallObj;

        private static bool DebugPathEquals(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b))
                return true;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        private string? ResolveDebugSourcePath(Command command)
        {
            if (!string.IsNullOrWhiteSpace(command.SourcePath))
                return command.SourcePath;
            if (_currentBlock != null && instructionPointer > 0)
            {
                var cur = (int)instructionPointer - 1;
                for (var i = cur; i < _currentBlock.Count; i++)
                {
                    var sp = _currentBlock[i].SourcePath;
                    if (!string.IsNullOrWhiteSpace(sp))
                        return sp;
                }

                for (var i = cur - 1; i >= 0; i--)
                {
                    var sp = _currentBlock[i].SourcePath;
                    if (!string.IsNullOrWhiteSpace(sp))
                        return sp;
                }
            }

            return _unit?.AttachSourcePath;
        }

        private static string? FirstSourcePathInBlockFrom(ExecutionBlock? block, int startIndex)
        {
            if (block == null || startIndex < 0)
                return null;
            for (var i = startIndex; i < block.Count; i++)
            {
                var sp = block[i].SourcePath;
                if (!string.IsNullOrWhiteSpace(sp))
                    return sp;
            }

            for (var i = startIndex - 1; i >= 0; i--)
            {
                var sp = block[i].SourcePath;
                if (!string.IsNullOrWhiteSpace(sp))
                    return sp;
            }

            return null;
        }

        private CancellationToken DebugContinueCancellationToken
            => DebugSession?.ContinueCancellationToken ?? CancellationToken.None;

        private void ApplyDebugResumeAction(DebugResumeAction action, int pausedSourceLine, int pausedAsmLine, string? pausedSourcePath)
        {
            switch (action)
            {
                case DebugResumeAction.Stop:
                    _debugPausedAfterInstruction = false;
                    _debugOneShotSkipAsmBreakpoint = 0;
                    _debugOneShotSkipSourceBreakpoint = 0;
                    _stepIntoSameLineSkip = false;
                    _stepIntoAnchorLine = 0;
                    _stepIntoAnchorSourcePath = null;
                    throw new OperationCanceledException("Debug stopped.");
                case DebugResumeAction.Run:
                    _breakOnDifferentSourceLine = false;
                    _stepOverAnchorLine = 0;
                    _stepOverAnchorSourcePath = null;
                    _instructionStepsRemaining = 0;
                    _debugSkipSourceLine = pausedSourceLine;
                    _debugSkipSourcePath = pausedSourcePath;
                    _debugSkipAsmLine = pausedAsmLine;
                    _debugPausedAfterInstruction = false;
                    _debugOneShotSkipAsmBreakpoint = 0;
                    _debugOneShotSkipSourceBreakpoint = 0;
                    _stepIntoSameLineSkip = false;
                    _stepIntoAnchorLine = 0;
                    _stepIntoAnchorSourcePath = null;
                    break;
                case DebugResumeAction.StepOverLine:
                    _breakOnDifferentSourceLine = true;
                    _stepOverAnchorLine = pausedSourceLine;
                    _stepOverAnchorSourcePath = pausedSourcePath;
                    _instructionStepsRemaining = 0;
                    _debugSkipSourceLine = pausedSourceLine;
                    _debugSkipSourcePath = pausedSourcePath;
                    _debugSkipAsmLine = pausedAsmLine;
                    _debugPausedAfterInstruction = false;
                    _debugOneShotSkipAsmBreakpoint = 0;
                    _debugOneShotSkipSourceBreakpoint = 0;
                    _stepIntoSameLineSkip = false;
                    _stepIntoAnchorLine = 0;
                    _stepIntoAnchorSourcePath = null;
                    break;
                case DebugResumeAction.StepInstruction:
                    _breakOnDifferentSourceLine = false;
                    _stepOverAnchorLine = 0;
                    _stepOverAnchorSourcePath = null;
                    _stepIntoSameLineSkip = false;
                    _stepIntoAnchorLine = 0;
                    _stepIntoAnchorSourcePath = null;
                    _debugSkipSourceLine = 0;
                    _debugSkipSourcePath = null;
                    _debugSkipAsmLine = 0;
                    _instructionStepsRemaining = 1;
                    if (_debugPausedAfterInstruction)
                    {
                        if (pausedAsmLine > 0)
                            _debugOneShotSkipAsmBreakpoint = pausedAsmLine;
                        if (pausedSourceLine > 0)
                            _debugOneShotSkipSourceBreakpoint = pausedSourceLine;
                        _debugPausedAfterInstruction = false;
                    }
                    else
                    {
                        _debugOneShotSkipAsmBreakpoint = 0;
                        _debugOneShotSkipSourceBreakpoint = 0;
                    }

                    break;
                case DebugResumeAction.StepInto:
                    _breakOnDifferentSourceLine = false;
                    _stepOverAnchorLine = 0;
                    _stepOverAnchorSourcePath = null;
                    _stepIntoSameLineSkip = true;
                    _stepIntoAnchorLine = pausedSourceLine;
                    _stepIntoAnchorSourcePath = pausedSourcePath;
                    _debugSkipSourceLine = 0;
                    _debugSkipSourcePath = null;
                    _debugSkipAsmLine = 0;
                    _instructionStepsRemaining = 0;
                    _debugPausedAfterInstruction = false;
                    _debugOneShotSkipAsmBreakpoint = 0;
                    _debugOneShotSkipSourceBreakpoint = 0;
                    break;
            }
        }

        private async Task MaybeDebugPauseBeforeExecuteAsync(Command command)
        {
            var session = DebugSession;
            if (session == null)
                return;

            session.ContinueCancellationToken.ThrowIfCancellationRequested();

            var path = ResolveDebugSourcePath(command);
            var line = command.SourceLine;
            var asmLine = command.AsmListingLine;

            if (_stepIntoSameLineSkip)
            {
                var onAnchor = line > 0 && line == _stepIntoAnchorLine &&
                               DebugPathEquals(path, _stepIntoAnchorSourcePath);
                if (!onAnchor)
                {
                    _stepIntoSameLineSkip = false;
                }
                else if (IsStepIntoUserCallOpcode(command.Opcode))
                {
                    _stepIntoSameLineSkip = false;
                    _instructionStepsRemaining = 1;
                    return;
                }
                else
                {
                    return;
                }
            }

            if (line > 0 && _debugSkipSourceLine != 0 &&
                (line != _debugSkipSourceLine || !DebugPathEquals(path, _debugSkipSourcePath)))
            {
                _debugSkipSourceLine = 0;
                _debugSkipSourcePath = null;
            }

            if (asmLine > 0 && _debugSkipAsmLine != 0 && asmLine != _debugSkipAsmLine)
                _debugSkipAsmLine = 0;

            if (_debugOneShotSkipAsmBreakpoint > 0 && asmLine > 0 && asmLine != _debugOneShotSkipAsmBreakpoint)
                _debugOneShotSkipAsmBreakpoint = 0;
            if (_debugOneShotSkipSourceBreakpoint > 0 && line > 0 && line != _debugOneShotSkipSourceBreakpoint)
                _debugOneShotSkipSourceBreakpoint = 0;

            var suppressSrcBpOnce = _debugOneShotSkipSourceBreakpoint > 0 && line == _debugOneShotSkipSourceBreakpoint;
            var suppressAsmBpOnce = _debugOneShotSkipAsmBreakpoint > 0 && asmLine == _debugOneShotSkipAsmBreakpoint;
            if (suppressSrcBpOnce)
                _debugOneShotSkipSourceBreakpoint = 0;
            if (suppressAsmBpOnce)
                _debugOneShotSkipAsmBreakpoint = 0;

            var stepOverHit = _breakOnDifferentSourceLine && line > 0 &&
                              (line != _stepOverAnchorLine || !DebugPathEquals(path, _stepOverAnchorSourcePath));
            var skipMatchesBreakpoint = _debugSkipSourceLine > 0 && line == _debugSkipSourceLine &&
                                        DebugPathEquals(path, _debugSkipSourcePath);
            var breakpointHit = line > 0 && !string.IsNullOrWhiteSpace(path) &&
                                  session.IsBreakpointAgiLine(path, line) &&
                                  !skipMatchesBreakpoint &&
                                  !suppressSrcBpOnce;
            var sameStepOverSourceLine = _breakOnDifferentSourceLine && line > 0 && line == _stepOverAnchorLine &&
                                         DebugPathEquals(path, _stepOverAnchorSourcePath);
            var asmBreakpointHit = asmLine > 0 && session.IsBreakpointAsmLine(asmLine) &&
                                   (_debugSkipAsmLine == 0 || asmLine != _debugSkipAsmLine) &&
                                   !sameStepOverSourceLine &&
                                   !suppressAsmBpOnce;

            if (!stepOverHit && !breakpointHit && !asmBreakpointHit)
                return;

            _debugPausedAfterInstruction = false;
            await session.WaitPausedAsync(this, line, asmLine, path, session.ContinueCancellationToken).ConfigureAwait(false);
            ApplyDebugResumeAction(session.ConsumeResumeAction(), line, asmLine, path);
        }

        private async Task MaybeDebugPauseAfterInstructionAsync(Command executedCommand)
        {
            var session = DebugSession;
            if (session == null || _instructionStepsRemaining <= 0)
                return;

            _instructionStepsRemaining--;
            if (_instructionStepsRemaining > 0)
                return;

            // IP уже указывает на следующую команду; иначе UI показывал бы только что выполненную (def и т.д.).
            GetDebugLinesForNextInstruction(executedCommand, out var showLine, out var showAsm, out var showPath);
            _debugPausedAfterInstruction = true;
            await session.WaitPausedAsync(this, showLine, showAsm, showPath, session.ContinueCancellationToken).ConfigureAwait(false);
            ApplyDebugResumeAction(session.ConsumeResumeAction(), showLine, showAsm, showPath);
        }

        /// <summary>Строки для подсветки «текущей» позиции: следующая к выполнению команда, если есть.</summary>
        private void GetDebugLinesForNextInstruction(Command executedCommand, out int sourceLine, out int asmLine, out string? sourcePath)
        {
            if (_currentBlock != null && instructionPointer >= 0 && instructionPointer < _currentBlock.Count)
            {
                var next = _currentBlock[(int)instructionPointer];
                sourceLine = next.SourceLine > 0
                    ? next.SourceLine
                    : (executedCommand.SourceLine > 0 ? executedCommand.SourceLine : (_stepOverAnchorLine > 0 ? _stepOverAnchorLine : 1));
                asmLine = next.AsmListingLine > 0 ? next.AsmListingLine : executedCommand.AsmListingLine;
                sourcePath = !string.IsNullOrWhiteSpace(next.SourcePath)
                    ? next.SourcePath
                    : FirstSourcePathInBlockFrom(_currentBlock, (int)instructionPointer)
                      ?? ResolveDebugSourcePath(executedCommand);
            }
            else
            {
                sourceLine = executedCommand.SourceLine > 0 ? executedCommand.SourceLine : (_stepOverAnchorLine > 0 ? _stepOverAnchorLine : 1);
                asmLine = executedCommand.AsmListingLine;
                sourcePath = ResolveDebugSourcePath(executedCommand);
            }

            if (sourceLine <= 0)
                sourceLine = _stepOverAnchorLine > 0 ? _stepOverAnchorLine : 1;
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
            if (executableUnit == null)
                throw new ArgumentNullException(nameof(executableUnit));

            // Если сконфигурирован Erlang-подобный runtime, интерпретатор не выполняет unit сам,
            // а только делает spawn entrypoint в TaskQueue. Это fire-and-forget, как spawn/1.
            if (_configuration?.Runtime != null && !BypassRuntimeSpawn)
            {
                await _configuration.Runtime.SpawnAsync(executableUnit).ConfigureAwait(false);
                return new InterpretationResult
                {
                    Success = true,
                };
            }

            // Fallback: однопоточное выполнение entrypoint (старое поведение без runtime).
            ResetDebugSessionState();
            instructionPointer = 0;
            _unit = executableUnit;
            _currentBlock = executableUnit.EntryPoint ?? new ExecutionBlock();
            _currentLabelOffsets = BuildLabelOffsets(_currentBlock);
            _callStack.Clear();
            MemoryContext.ClearLocalScopes();
            MemoryContext.ClearAmbientLocal();

            // Для корневого запуска нет наследуемой памяти, локальная/глобальная начинаются с нуля.
            MemoryContext.Inherited.Clear();
            MemoryContext.Global.Clear();

            // SpaceName в ExecutableUnit: system|module|program для префикса ключей диска
            executableUnit.SpaceName = BuildSpaceName(executableUnit.System, executableUnit.Module, executableUnit.Name);

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
                        MemoryContext.PopLocalScopeAndClear();
                        var frame = _callStack.Pop();
                        _currentBlock = frame.Block;
                        instructionPointer = frame.ReturnIp;
                        continue;
                    }

                    var command = _currentBlock[(int)instructionPointer];
                    instructionPointer++; // advance first; Call/Ret may override instructionPointer
                    await MaybeDebugPauseBeforeExecuteAsync(command).ConfigureAwait(false);
                    if (_skipToDefExpr)
                    {
                        if (command.Opcode == Opcodes.DefExpr)
                        {
                            await ExecuteDefExprAsync(command).ConfigureAwait(false);
                            _skipToDefExpr = false;
                        }
                        continue;
                    }
                    await ExecuteAsync(command);
                    await MaybeDebugPauseAfterInstructionAsync(command).ConfigureAwait(false);
                }

                return new InterpretationResult
                {
                    Success = true,
                };
            }
            catch (OperationCanceledException)
            {
                return new InterpretationResult { Success = false };
            }
            finally
            {
                await CloseOwnedDefStreamsAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Run unit starting at entry point or at the given procedure/function/label (for runtime spawn).
        /// When entryName and callInfo are set, parameters are pushed onto stack (arg0..argN, then arity) and execution starts at that body.
        /// </summary>
        public async Task<InterpretationResult> InterpreteFromEntryAsync(ExecutableUnit executableUnit, string? entryName = null, Processor.CallInfo? callInfo = null, ExecutionBlock? startBlock = null)
        {
            _unit = executableUnit ?? throw new ArgumentNullException(nameof(executableUnit));
            _callStack.Clear();
            MemoryContext.ClearLocalScopes();
            MemoryContext.ClearAmbientLocal();
            // Global / Inherited уже проставлены рантаймом как наследованные слои.
            executableUnit.SpaceName = BuildSpaceName(executableUnit.System, executableUnit.Module, executableUnit.Name);

            if (!string.IsNullOrEmpty(entryName) && _unit.Procedures != null && _unit.Procedures.TryGetValue(entryName, out var proc))
            {
                _currentBlock = proc.Body ?? new ExecutionBlock();
                instructionPointer = 0;
                PushCallArgs(callInfo);
                MemoryContext.PushCallFrame();
            }
            else if (!string.IsNullOrEmpty(entryName) && _unit.Functions != null && _unit.Functions.TryGetValue(entryName, out var func))
            {
                _currentBlock = func.Body ?? new ExecutionBlock();
                instructionPointer = 0;
                PushCallArgs(callInfo);
                MemoryContext.PushCallFrame();
            }
            else if (!string.IsNullOrEmpty(entryName))
            {
                _currentBlock = startBlock ?? executableUnit.EntryPoint ?? new ExecutionBlock();
                _currentLabelOffsets = BuildLabelOffsets(_currentBlock);

                if (_currentLabelOffsets.TryGetValue(entryName, out var labelOffset))
                {
                    instructionPointer = labelOffset;
                    PushCallArgs(callInfo);
                }
                else
                {
                    _currentBlock = executableUnit.EntryPoint ?? new ExecutionBlock();
                    _currentLabelOffsets = BuildLabelOffsets(_currentBlock);

                    if (_currentLabelOffsets.TryGetValue(entryName, out labelOffset))
                    {
                        instructionPointer = labelOffset;
                        PushCallArgs(callInfo);
                    }
                    else
                    {
                        instructionPointer = 0;
                    }
                }
            }
            else
            {
                _currentBlock = executableUnit.EntryPoint ?? new ExecutionBlock();
                instructionPointer = 0;
            }

            _currentLabelOffsets = BuildLabelOffsets(_currentBlock!);

            ResetDebugSessionState();
            try
            {
                while (true)
                {
                    if (_currentBlock == null)
                        break;
                    if (instructionPointer >= _currentBlock.Count)
                    {
                        if (_callStack.Count == 0)
                            break;
                        MemoryContext.PopLocalScopeAndClear();
                        var frame = _callStack.Pop();
                        _currentBlock = frame.Block;
                        _currentLabelOffsets = BuildLabelOffsets(_currentBlock);
                        instructionPointer = frame.ReturnIp;
                        continue;
                    }
                    var command = _currentBlock[(int)instructionPointer];
                    instructionPointer++;
                    await MaybeDebugPauseBeforeExecuteAsync(command).ConfigureAwait(false);
                    if (_skipToDefExpr)
                    {
                        if (command.Opcode == Opcodes.DefExpr)
                        {
                            await ExecuteDefExprAsync(command).ConfigureAwait(false);
                            _skipToDefExpr = false;
                        }
                        continue;
                    }
                    await ExecuteAsync(command);
                    await MaybeDebugPauseAfterInstructionAsync(command).ConfigureAwait(false);
                }
                return new InterpretationResult { Success = true };
            }
            catch (OperationCanceledException)
            {
                return new InterpretationResult { Success = false };
            }
            finally
            {
                await CloseOwnedDefStreamsAsync().ConfigureAwait(false);
            }
        }

        private void PushCallArgs(Processor.CallInfo? callInfo)
        {
            if (callInfo?.Parameters == null || callInfo.Parameters.Count == 0)
            {
                Stack.Add(0);
                return;
            }
            var indexed = callInfo.Parameters.Keys
                .Where(k => k.Length <= 2 && k.All(char.IsDigit))
                .OrderBy(k => int.Parse(k))
                .Select(k => callInfo.Parameters[k])
                .ToArray();
            foreach (var v in indexed)
                Stack.Add(v!);
            Stack.Add(indexed.Length);
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

                case Opcodes.ACall:
                    await ExecuteACallAsync(command);
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
                case Opcodes.DefObj:
                    await ExecuteDefObjAsync(command);
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

                case Opcodes.Expr:
                    await ExecuteExprAsync(command);
                    break;
                case Opcodes.DefExpr:
                    await ExecuteDefExprAsync(command);
                    break;
                case Opcodes.Lambda:
                    break; // no-op; lambda ref already on stack from Expr
                case Opcodes.Equals:
                    await ExecuteEqualsAsync(command);
                    break;
                case Opcodes.Lt:
                    await ExecuteLtAsync(command);
                    break;
                case Opcodes.Add:
                    await ExecuteAddAsync(command);
                    break;
                case Opcodes.Sub:
                    await ExecuteSubAsync(command);
                    break;
                case Opcodes.Mul:
                    await ExecuteMulAsync(command);
                    break;
                case Opcodes.Div:
                    await ExecuteDivAsync(command);
                    break;
                case Opcodes.Pow:
                    await ExecutePowAsync(command);
                    break;
                case Opcodes.Not:
                    await ExecuteNotAsync(command);
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
            // Важно: не мутируем CallInfo, лежащий внутри команды (Operand1),
            // чтобы байткод оставался иммутабельным во время исполнения.
            var callInfoTemplate = GetCallInfo(command, "Call");
            var callInfo = CloneCallInfo(callInfoTemplate);
            PopulateCallParametersFromStack(callInfo);
            await ExecuteResolvedCallAsync(callInfo).ConfigureAwait(false);
        }

        private async Task ExecuteACallAsync(Command command)
        {
            // Аналогично Call: работаем только с копией CallInfo.
            var callInfoTemplate = GetCallInfo(command, "ACall");
            var callInfo = CloneCallInfo(callInfoTemplate);
            PopulateCallParametersFromStack(callInfo);

            if (await TrySpawnCallAsync(callInfo).ConfigureAwait(false))
                return;

            await ExecuteResolvedCallAsync(callInfo).ConfigureAwait(false);
        }

        private CallInfo GetCallInfo(Command command, string opcodeName)
        {
            if (command.Operand1 is not CallInfo callInfo)
                throw new InvalidOperationException($"{opcodeName} command expects CallInfo as Operand1, but got {command.Operand1?.GetType().Name ?? "null"}");

            return callInfo;
        }

        private static CallInfo CloneCallInfo(CallInfo source)
        {
            var clone = new CallInfo
            {
                FunctionName = source.FunctionName,
                Parameters = source.Parameters != null
                    ? new Dictionary<string, object>(source.Parameters, StringComparer.Ordinal)
                    : new Dictionary<string, object>(StringComparer.Ordinal),
                StreamWaitDeltaBindSlots = source.StreamWaitDeltaBindSlots != null
                    ? (long[])source.StreamWaitDeltaBindSlots.Clone()
                    : null,
                StreamWaitCaptureToSlots = source.StreamWaitCaptureToSlots != null
                    ? (long[])source.StreamWaitCaptureToSlots.Clone()
                    : null
            };
            return clone;
        }

        private void PopulateCallParametersFromStack(CallInfo callInfo)
        {
            // Аргументы на стеке: сверху arity, под ним arg1, arg2, ...
            if ((callInfo.Parameters == null || callInfo.Parameters.Count == 0) && Stack.Count > 0)
            {
                var isUserDefined =
                    _unit?.Procedures != null && _unit.Procedures.ContainsKey(callInfo.FunctionName) ||
                    _unit?.Functions != null && _unit.Functions.ContainsKey(callInfo.FunctionName);

                if (isUserDefined)
                {
                    // Don't consume the stack here: user-defined procedure/function bodies
                    // usually start with parameter-binding instructions that expect the values
                    // (arity + args) to still be present on the stack.
                    var arityObj = Stack[^1];
                    var n = arityObj switch
                    {
                        long l => l,
                        int i => i,
                        double d => (long)d,
                        _ => 0L
                    };

                    callInfo.Parameters ??= new Dictionary<string, object>();
                    var baseIndex = Stack.Count - 1 - (int)n;
                    for (var i = 0; i < n; i++)
                        callInfo.Parameters[$"{i}"] = Stack[baseIndex + i]!;
                }
                else
                {
                    var (n, args) = PopArityAndArgs(extraRequired: 0);
                    callInfo.Parameters ??= new Dictionary<string, object>();
                    for (var i = 0; i < n; i++)
                        callInfo.Parameters[$"{i}"] = args[i]!;
                }
            }
        }

        private async Task<bool> TrySpawnCallAsync(CallInfo callInfo)
        {
            if (_configuration?.Runtime == null || _unit == null || string.IsNullOrEmpty(callInfo.FunctionName))
                return false;

            string? entryNameForSpawn = null;

            if (_unit.Procedures != null && _unit.Procedures.TryGetValue(callInfo.FunctionName, out var proc))
            {
                entryNameForSpawn = proc.Name;
            }
            else if (_unit.Functions != null && _unit.Functions.TryGetValue(callInfo.FunctionName, out var func))
            {
                entryNameForSpawn = func.Name;
            }

            // Локальная метка в текущем блоке (streamwait_loop_N_delta и т.п.) — только inline через
            // ExecuteResolvedCallAsync. Spawn ставит новый Interpreter в очередь и не ждёт: родитель
            // сразу делает jmp на следующую итерацию streamwaitobj и ломает ожидание потока.

            if (string.IsNullOrEmpty(entryNameForSpawn))
                return false;

            Dictionary<long, object>? inheritedLocal = MemoryContext.CaptureLocalsForSpawn();

            Dictionary<long, object>? inheritedGlobal = null;
            if (MemoryContext.Global.Count > 0)
                inheritedGlobal = new Dictionary<long, object>(MemoryContext.Global);

            await _configuration.Runtime.SpawnAsync(
                _unit,
                entryNameForSpawn,
                callInfo,
                tag: null,
                inheritedLocalMemory: inheritedLocal,
                inheritedGlobalMemory: inheritedGlobal,
                inheritedTypes: _unit.Types?.ToList() ?? new List<DefType>(),
                startBlock: null,
                debugSession: DebugSession
            ).ConfigureAwait(false);

            return true;
        }

        private async Task ExecuteResolvedCallAsync(CallInfo callInfo)
        {
            // 1) User-defined procedure/function in current executable unit.
            if (_unit != null)
            {
                if (_unit.Procedures != null && _unit.Procedures.TryGetValue(callInfo.FunctionName, out var proc))
                {
                    if (_currentBlock == null) throw new InvalidOperationException("Interpreter current block is not initialized.");
                    MemoryContext.PushLocalScope();
                    _callStack.Push(new CallFrame { Block = _currentBlock, ReturnIp = instructionPointer, Name = proc.Name });
                    _currentBlock = proc.Body ?? new ExecutionBlock();
                    _currentLabelOffsets = BuildLabelOffsets(_currentBlock);
                    instructionPointer = 0;
                    return;
                }

                if (_unit.Functions != null && _unit.Functions.TryGetValue(callInfo.FunctionName, out var func))
                {
                    if (_currentBlock == null) throw new InvalidOperationException("Interpreter current block is not initialized.");
                    MemoryContext.PushLocalScope();
                    _callStack.Push(new CallFrame { Block = _currentBlock, ReturnIp = instructionPointer, Name = func.Name });
                    _currentBlock = func.Body ?? new ExecutionBlock();
                    _currentLabelOffsets = BuildLabelOffsets(_currentBlock);
                    instructionPointer = 0;
                    return;
                }
            }

            // 2) Локальная подпрограмма по label в текущем блоке (для streamwait-loop делегатов и прочих локальных "функций").
            if (_currentBlock != null && !string.IsNullOrEmpty(callInfo.FunctionName) &&
                _currentLabelOffsets.TryGetValue(callInfo.FunctionName, out _))
            {
                MemoryContext.PushLocalScope();
                if (callInfo.StreamWaitDeltaBindSlots is { Length: 2 } bind &&
                    callInfo.Parameters.TryGetValue("0", out var aggArg) &&
                    callInfo.Parameters.TryGetValue("1", out var deltaArg))
                {
                    MemoryContext.Local[bind[0]] = aggArg!;
                    MemoryContext.Local[bind[1]] = deltaArg!;
                    if (callInfo.StreamWaitCaptureToSlots is { Length: > 0 } capSlots)
                    {
                        for (var i = 0; i < capSlots.Length; i++)
                        {
                            if (!callInfo.Parameters.TryGetValue($"{2 + i}", out var capVal))
                                throw new InvalidOperationException(
                                    $"streamwait_loop delta call: expected capture argument index {2 + i} on stack (arity includes outer slots).");
                            MemoryContext.Local[capSlots[i]] = capVal!;
                        }
                    }
                }

                _callStack.Push(new CallFrame { Block = _currentBlock, ReturnIp = instructionPointer, Name = callInfo.FunctionName });
                instructionPointer = ResolveLabelOffset(callInfo.FunctionName);
                return;
            }

            // 3) Fallback: system functions (backward compatible behavior)
            var vaultReader = _configuration?.VaultReader ?? new EnvironmentVaultReader();
            var systemFunctions = CreateSystemFunctions(vaultReader);
            var handled = await systemFunctions.ExecuteAsync(callInfo).ConfigureAwait(false);

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
            var systemFunctions = CreateSystemFunctions(vaultReader);
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

            MemoryContext.PopLocalScopeAndClear();
            var frame = _callStack.Pop();
            _currentBlock = frame.Block;
            _currentLabelOffsets = BuildLabelOffsets(_currentBlock);
            instructionPointer = frame.ReturnIp;
            if (frame.PushOnRet != null)
                Stack.Add(frame.PushOnRet);
            else if (frame.PushNullReturnOnRet)
                Stack.Add(null!);
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
            MemoryContext.Write(memoryAddress, value, IsExecutingEntryPoint(), _configuration?.Runtime != null);
            return Task.CompletedTask;
        }

        private SystemFunctions CreateSystemFunctions(IVaultReader vaultReader)
        {
            return new SystemFunctions(
                _configuration,
                Stack,
                memoryAddress =>
                {
                    var found = MemoryContext.TryRead(memoryAddress, IsExecutingEntryPoint(), out var value);
                    return (found, value);
                },
                (memoryAddress, value) =>
                {
                    MemoryContext.Write(memoryAddress, value, IsExecutingEntryPoint(), _configuration?.Runtime != null);
                },
                vaultReader,
                _unit,
                () => CurrentSocketContext,
                StandardInput,
                () => DebugSession);
        }

        private bool IsExecutingEntryPoint()
        {
            return _unit?.EntryPoint != null
                && _currentBlock != null
                && ReferenceEquals(_currentBlock, _unit.EntryPoint);
        }

        private Task ExecutePushAsync(Command command)
        {
            if (command.Operand1 is MemoryAddress memoryAddress)
            {
                var value = MemoryContext.Read(memoryAddress, IsExecutingEntryPoint());
                Stack.Add(value);
                return Task.CompletedTask;
            }
            if (command.Operand1 is PushOperand po)
            {
                object? val = po.Kind switch
                {
                    "Type" => po.Value,
                    "Class" => FindType(po.Value?.ToString()),
                    "IntLiteral" => po.Value,
                    "StringLiteral" => po.Value,
                    "AddressLiteral" => new AddressLiteral(po.Value?.ToString() ?? string.Empty),
                    "LambdaArg" => _lambdaArgsStack.Count > 0 && po.Value is int argIndex && argIndex >= 0 && argIndex < _lambdaArgsStack.Peek().Length
                        ? _lambdaArgsStack.Peek()[argIndex]
                        : null,
                    _ => null
                };
                Stack.Add(val!);
                return Task.CompletedTask;
            }
            throw new InvalidOperationException($"Push command expects MemoryAddress or PushOperand, but got {command.Operand1?.GetType().Name ?? "null"}");
        }

        /// <summary>Resolves a type name against <see cref="ExecutableUnit.Types"/> and <see cref="InheritedTypes"/>.</summary>
        private DefType FindType(string? typeName)
        {
            var requested = (typeName ?? string.Empty).Trim();
            if (requested.Length == 0)
                throw new InvalidOperationException("push class: class name is empty.");

            static bool Matches(DefType t, string req) =>
                string.Equals(t.Name, req, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.FullName, req, StringComparison.OrdinalIgnoreCase);

            if (_unit?.Types != null)
            {
                var direct = _unit.Types.FirstOrDefault(t => Matches(t, requested));
                if (direct != null)
                    return direct;
            }

            if (InheritedTypes.Count > 0)
            {
                var inherited = InheritedTypes.FirstOrDefault(t => Matches(t, requested));
                if (inherited != null)
                    return inherited;
            }

            throw new InvalidOperationException($"Type '{requested}' is not defined in current ExecutableUnit.Types.");
        }

        private Task ExecuteDefObjAsync(Command command)
        {
            if (Stack.Count < 1)
                throw new InvalidOperationException("DefObj: stack underflow.");

            // new T[] : push class T; push "list"; defobj
            var top = Stack[Stack.Count - 1];
            if (top is string topStr && string.Equals(topStr, "list", StringComparison.OrdinalIgnoreCase))
            {
                Stack.RemoveAt(Stack.Count - 1);
                if (Stack.Count < 1)
                    throw new InvalidOperationException("DefObj(list): missing element type under 'list'.");

                var elemKind = Stack[Stack.Count - 1];
                Stack.RemoveAt(Stack.Count - 1);
                var list = elemKind switch
                {
                    DefType dt => new DefList { Name = dt.Name },
                    string s => new DefList { Name = FindType(s).Name },
                    _ => throw new InvalidOperationException($"DefObj(list): expected DefType or type name under 'list', got {elemKind?.GetType().Name ?? "null"}.")
                };
                Stack.Add(list);
                return Task.CompletedTask;
            }

            var typeObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var defType = typeObj as DefType
                ?? (typeObj is string ts ? FindType(ts) : null);
            if (defType == null)
                throw new InvalidOperationException($"DefObj: expected DefType or type name on stack, got {typeObj?.GetType().Name ?? "null"}.");

            var obj = Hal.DefObj(defType);
            if (_unit != null)
                _unit.Objects.Add(obj);
            Stack.Add(obj);
            return Task.CompletedTask;
        }

        private Task ExecuteDefAsync(Command command)
        {
            if (Stack.Count < 1) throw new InvalidOperationException("Def: stack underflow.");

            var arityObj = Stack[Stack.Count - 1];
            // Arity после десериализации JSON / иных путей может быть double/float — иначе n=0, снимается только «column» и стек ломается.
            if (TryCoerceStackArity(arityObj, out var n) && n > 0 && Stack.Count >= n + 1)
            {
                var (_, args) = PopArityAndArgs(extraRequired: 0);
                // Hal.Def (multi-arg strings): DefClass.Inheritances = bases; Generalizations reserved for DefGen/device meanings.
                Stack.Add(Hal.Def(args, _unit)!);
                return Task.CompletedTask;
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

            // defobj → DefObject; конструктор типа — отдельная функция unit с именем …_ctor_N.
            // Стек callobj: [receiver, arg0.., arity=argCount]; внутри функции ожидается [this, args…, arity=1+argCount] как у call.
            if (obj is DefObject defObj &&
                methodName.IndexOf("_ctor_", StringComparison.OrdinalIgnoreCase) >= 0 &&
                _unit?.Functions != null &&
                _unit.Functions.TryGetValue(methodName, out var ctorFunc))
            {
                Stack.Add(defObj);
                foreach (var a in args)
                    Stack.Add(a);
                Stack.Add(1 + args.Length);

                if (_currentBlock == null)
                    throw new InvalidOperationException("CallObj: interpreter has no current block.");

                MemoryContext.PushLocalScope();
                _callStack.Push(new CallFrame
                {
                    Block = _currentBlock,
                    ReturnIp = instructionPointer,
                    Name = methodName,
                    PushOnRet = defObj
                });
                _currentBlock = ctorFunc.Body ?? new ExecutionBlock();
                _currentLabelOffsets = BuildLabelOffsets(_currentBlock);
                instructionPointer = 0;
                return;
            }

            // Экземпляр пользовательского типа: callobj с коротким именем → перегрузки по FirstParameterTypeFq и иерархии типов.
            if (obj is DefObject instanceObj && _unit?.Functions != null)
            {
                foreach (var userProcName in OrderDefObjectUserMethodProcedureKeys(instanceObj, methodName, args, _unit))
                {
                    if (!_unit.Functions.TryGetValue(userProcName, out var userMethodFunc))
                        continue;

                    Stack.Add(instanceObj);
                    foreach (var a in args)
                        Stack.Add(a);
                    Stack.Add(1 + args.Length);

                    if (_currentBlock == null)
                        throw new InvalidOperationException("CallObj: interpreter has no current block.");

                    MemoryContext.PushLocalScope();
                    _callStack.Push(new CallFrame
                    {
                        Block = _currentBlock,
                        ReturnIp = instructionPointer,
                        Name = userProcName,
                        PushNullReturnOnRet = true
                    });
                    _currentBlock = userMethodFunc.Body ?? new ExecutionBlock();
                    _currentLabelOffsets = BuildLabelOffsets(_currentBlock);
                    instructionPointer = 0;
                    return;
                }
            }

            var result = await Hal.CallObjAsync(obj, methodName, args, BuildCallContext()).ConfigureAwait(false);
            Stack.Add(result!);
        }

        /// <summary>Порядок имён процедур для <c>callobj</c>: квалифицированное имя как есть; иначе перегрузки с учётом типа первого аргумента.</summary>
        private static IEnumerable<string> OrderDefObjectUserMethodProcedureKeys(
            DefObject instance,
            string methodName,
            object?[] args,
            ExecutableUnit unit)
        {
            var mn = (methodName ?? "").Trim();
            if (string.IsNullOrEmpty(mn))
                yield break;

            if (mn.IndexOf(':') >= 0)
            {
                yield return mn;
                yield break;
            }

            var list = instance.Type.Methods;
            if (list == null || list.Count == 0)
                yield break;

            var matches = list
                .Where(m => string.Equals((m.Name ?? "").Trim(), mn, StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(m.FullName))
                .ToList();
            if (matches.Count == 0)
                yield break;
            if (matches.Count == 1)
            {
                yield return matches[0].FullName!.Trim();
                yield break;
            }

            foreach (var full in SelectBestOverloadProcedureNames(matches, args, unit))
                yield return full;
        }

        private static IEnumerable<string> SelectBestOverloadProcedureNames(
            List<DefTypeMethod> matches,
            object?[] args,
            ExecutableUnit unit)
        {
            var arg0 = args.Length > 0 ? args[0] : null;
            var argFq = arg0 is DefObject d ? (d.Type?.FullName ?? d.TypeName) : null;

            if (!string.IsNullOrEmpty(argFq))
            {
                foreach (var m in matches)
                {
                    var p = m.FirstParameterTypeFq?.Trim();
                    if (!string.IsNullOrEmpty(p) && string.Equals(argFq, p, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return m.FullName!.Trim();
                        yield break;
                    }
                }

                var assignable = matches
                    .Where(m => !string.IsNullOrWhiteSpace(m.FirstParameterTypeFq) &&
                                IsSubtypeOfFq(argFq, m.FirstParameterTypeFq!.Trim(), unit))
                    .ToList();
                if (assignable.Count > 0)
                {
                    DefTypeMethod? best = null;
                    foreach (var m in assignable)
                    {
                        if (best == null)
                        {
                            best = m;
                            continue;
                        }

                        var bp = best.FirstParameterTypeFq?.Trim() ?? "";
                        var mp = m.FirstParameterTypeFq?.Trim() ?? "";
                        if (ParamTypeStrictlyMoreSpecificThan(mp, bp, unit))
                            best = m;
                    }

                    if (best != null)
                    {
                        yield return best.FullName!.Trim();
                        yield break;
                    }
                }
            }

            foreach (var m in matches)
                yield return m.FullName!.Trim();
        }

        private static bool ParamTypeStrictlyMoreSpecificThan(string paramA, string paramB, ExecutableUnit unit) =>
            !string.IsNullOrEmpty(paramA) && !string.IsNullOrEmpty(paramB) &&
            IsSubtypeOfFq(paramA, paramB, unit) && !IsSubtypeOfFq(paramB, paramA, unit);

        private static DefType? TryResolveTypeByFullName(ExecutableUnit? unit, string fq)
        {
            if (unit?.Types == null || string.IsNullOrWhiteSpace(fq))
                return null;
            foreach (var t in unit.Types)
                if (string.Equals(t.FullName, fq, StringComparison.OrdinalIgnoreCase))
                    return t;
            return null;
        }

        internal static bool IsSubtypeOfFq(string subFq, string superFq, ExecutableUnit? unit)
        {
            if (string.Equals(subFq, superFq, StringComparison.OrdinalIgnoreCase))
                return true;
            var sub = TryResolveTypeByFullName(unit, subFq);
            if (sub is not DefClass cl)
                return false;
            foreach (var b in cl.Inheritances)
            {
                if (string.IsNullOrWhiteSpace(b))
                    continue;
                var bt = b.Trim();
                if (string.Equals(bt, superFq, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (IsSubtypeOfFq(bt, superFq, unit))
                    return true;
            }

            return false;
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

        private Task ExecuteExprAsync(Command command)
        {
            if (_currentBlock == null) throw new InvalidOperationException("Expr: no current block.");
            // instructionPointer already points to first instruction after Expr (main loop incremented it before calling us)
            var body = new ExecutionBlock();
            long ip = instructionPointer;
            while (ip < _currentBlock.Count)
            {
                var cmd = _currentBlock[(int)ip];
                if (cmd.Opcode == Opcodes.DefExpr)
                    break;
                body.Add(cmd);
                ip++;
            }
            // Leave instructionPointer at first body instruction; main loop will skip until DefExpr then run ExecuteDefExprAsync
            Stack.Add(new LambdaValue(body));
            _skipToDefExpr = true;
            return Task.CompletedTask;
        }

        /// <summary>Build ExprTree from lambda body; resolves push [slot]/pop [slot] via MemoryContext.</summary>
        private ExprTree? TryBuildExprTreeFromBody(IReadOnlyList<Command> body)
        {
            if (body == null || body.Count == 0)
                return null;
            var exprStack = new Stack<ExprTree>();
            var exprMemory = new Dictionary<long, ExprTree>();
            var isEntry = IsExecutingEntryPoint();
            foreach (var cmd in body)
            {
                switch (cmd.Opcode)
                {
                    case Opcodes.Push:
                        if (cmd.Operand1 is MemoryAddress ma && ma.Index.HasValue)
                        {
                            if (exprMemory.TryGetValue(ma.Index.Value, out var stored))
                                exprStack.Push(stored);
                            else
                            {
                                var value = MemoryContext.Read(ma, isEntry);
                                exprStack.Push(new ExprConstant(value));
                            }
                            break;
                        }
                        if (cmd.Operand1 is PushOperand po)
                        {
                            if (po.Kind == "LambdaArg" && (po.Value is int iArg || (po.Value is long lArg && lArg >= 0 && lArg <= int.MaxValue)))
                            {
                                exprStack.Push(new ExprParameter(po.Value is int i ? i : (int)(long)po.Value));
                                break;
                            }
                            if (po.Kind == "StringLiteral" && po.Value is string s)
                            {
                                exprStack.Push(new ExprConstant(s));
                                break;
                            }
                            if (po.Kind == "IntLiteral" && po.Value != null)
                            {
                                exprStack.Push(new ExprConstant(po.Value));
                                break;
                            }
                        }
                        return null;
                    case Opcodes.Pop:
                        if (exprStack.Count == 0)
                            return null;
                        var top = exprStack.Pop();
                        if (cmd.Operand1 is MemoryAddress popMa && popMa.Index.HasValue)
                            exprMemory[popMa.Index.Value] = top;
                        break;
                    case Opcodes.GetObj:
                        if (exprStack.Count < 2)
                            return null;
                        var memberNameNode = exprStack.Pop();
                        var target = exprStack.Pop();
                        if (memberNameNode is not ExprConstant nameConst || nameConst.Value is not string memberName)
                            return null;
                        exprStack.Push(new ExprMemberAccess(target, memberName));
                        break;
                    case Opcodes.Equals:
                        if (exprStack.Count < 2)
                            return null;
                        var right = exprStack.Pop();
                        var left = exprStack.Pop();
                        exprStack.Push(new ExprEqual(left, right));
                        break;
                    case Opcodes.Lambda:
                        if (exprStack.Count == 0)
                            return null;
                        var lambdaBody = exprStack.Pop();
                        var args = new List<ExprTree>();
                        while (exprStack.Count > 0)
                        {
                            // вставляем в начало, чтобы сохранить порядок push-ей как порядок аргументов
                            args.Insert(0, exprStack.Pop());
                        }
                        exprStack.Push(new ExprLambda(args, lambdaBody));
                        break;
                    default:
                        return null;
                }
            }
            return exprStack.Count == 1 ? exprStack.Pop() : null;
        }

        private Task ExecuteDefExprAsync(Command command)
        {
            // If top of stack is LambdaValue without ExprTree: build tree from body (with memory resolution) and replace with LambdaValue(body, tree)
            if (Stack.Count > 0 && Stack[Stack.Count - 1] is LambdaValue lv && lv.ExprTree == null)
            {
                Stack.RemoveAt(Stack.Count - 1);
                var tree = TryBuildExprTreeFromBody(lv.Body);
                Stack.Add(new LambdaValue(lv.Body, tree));
                return Task.CompletedTask;
            }
            if (_lambdaCallStack.Count > 0)
            {
                var (block, returnIp) = _lambdaCallStack.Pop();
                _lambdaArgsStack.Pop();
                _currentBlock = block;
                _currentLabelOffsets = BuildLabelOffsets(block);
                instructionPointer = returnIp;
            }
            return Task.CompletedTask;
        }

        private Task ExecuteEqualsAsync(Command command)
        {
            if (Stack.Count < 2) throw new InvalidOperationException("Equals: stack underflow.");
            var b = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var a = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var eq = a == null ? b == null : (b != null && a.Equals(b));
            Stack.Add(eq);
            return Task.CompletedTask;
        }

        private Task ExecuteNotAsync(Command command)
        {
            if (Stack.Count < 1) throw new InvalidOperationException("Not: stack underflow.");
            var value = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            Stack.Add(IsTruthy(value) ? 0L : 1L);
            return Task.CompletedTask;
        }

        private Task ExecuteLtAsync(Command command)
        {
            if (Stack.Count < 2) throw new InvalidOperationException("Lt: stack underflow.");
            var b = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var a = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var lt = CompareForLt(a, b);
            Stack.Add(lt ? 1L : 0L);
            return Task.CompletedTask;
        }

        private Task ExecuteAddAsync(Command command)
        {
            if (Stack.Count < 2) throw new InvalidOperationException("Add: stack underflow.");
            var b = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var a = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);

            // Prefer numeric addition when both sides look like numbers.
            if (TryConvertToDecimal(a, out var leftNum) && TryConvertToDecimal(b, out var rightNum))
            {
                var sum = leftNum + rightNum;
                // Keep integer-ish results as long (most AGI numeric code uses long).
                if (sum == Math.Truncate(sum))
                    Stack.Add((long)sum);
                else
                    Stack.Add(sum);
                return Task.CompletedTask;
            }

            // Fallback: string concatenation.
            Stack.Add((a?.ToString() ?? "") + (b?.ToString() ?? ""));
            return Task.CompletedTask;
        }

        private Task ExecuteSubAsync(Command command)
        {
            if (Stack.Count < 2) throw new InvalidOperationException("Sub: stack underflow.");
            var b = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var a = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            if (!TryConvertToDecimal(a, out var da) || !TryConvertToDecimal(b, out var db))
                throw new InvalidOperationException("Sub: operands must be numeric.");
            var diff = da - db;
            if (diff == Math.Truncate(diff))
                Stack.Add((long)diff);
            else
                Stack.Add(diff);
            return Task.CompletedTask;
        }

        private Task ExecuteMulAsync(Command command)
        {
            if (Stack.Count < 2) throw new InvalidOperationException("Mul: stack underflow.");
            var b = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var a = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            if (!TryConvertToDecimal(a, out var da) || !TryConvertToDecimal(b, out var db))
                throw new InvalidOperationException("Mul: operands must be numeric.");
            var prod = da * db;
            if (prod == Math.Truncate(prod))
                Stack.Add((long)prod);
            else
                Stack.Add(prod);
            return Task.CompletedTask;
        }

        private Task ExecuteDivAsync(Command command)
        {
            if (Stack.Count < 2) throw new InvalidOperationException("Div: stack underflow.");
            var b = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var a = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            if (a is FloatDecimal leftFloatDecimal && b is FloatDecimal rightFloatDecimal)
            {
                Stack.Add(leftFloatDecimal / rightFloatDecimal);
                return Task.CompletedTask;
            }
            if (!TryConvertToDecimal(a, out var da) || !TryConvertToDecimal(b, out var db))
                throw new InvalidOperationException("Div: operands must be numeric.");
            if (db == 0m)
                throw new DivideByZeroException("Div: division by zero.");
            var quot = da / db;
            if (quot == Math.Truncate(quot))
                Stack.Add((long)quot);
            else
                Stack.Add(quot);
            return Task.CompletedTask;
        }

        private Task ExecutePowAsync(Command command)
        {
            if (Stack.Count < 2) throw new InvalidOperationException("Pow: stack underflow.");
            var b = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var a = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            if (a is FloatDecimal || b is FloatDecimal)
                throw new InvalidOperationException("Pow: FloatDecimal operands not supported; use decimal/long.");
            if (!TryConvertToDecimal(a, out var da) || !TryConvertToDecimal(b, out var db))
                throw new InvalidOperationException("Pow: operands must be numeric.");
            var adb = (double)da;
            var bdb = (double)db;
            var r = Math.Pow(adb, bdb);
            if (double.IsNaN(r) || double.IsInfinity(r))
                throw new InvalidOperationException("Pow: result is NaN or infinity.");
            var dr = (decimal)r;
            if (dr == Math.Truncate(dr))
                Stack.Add((long)dr);
            else
                Stack.Add(dr);
            return Task.CompletedTask;
        }

        private static bool CompareForLt(object? a, object? b)
        {
            if (a is FloatDecimal leftFloatDecimal && b is FloatDecimal rightFloatDecimal)
                return leftFloatDecimal < rightFloatDecimal;

            if (TryConvertToDecimal(a, out var leftNum) && TryConvertToDecimal(b, out var rightNum))
                return leftNum < rightNum;
            var sa = a?.ToString() ?? "";
            var sb = b?.ToString() ?? "";
            return string.Compare(sa, sb, StringComparison.Ordinal) < 0;
        }

        /// <summary>Invoke a lambda with given arguments. Used by devices (e.g. Table.any(predicate)).</summary>
        public async Task<object?> InvokeLambdaAsync(LambdaValue lambda, object?[] args)
        {
            if (lambda == null) throw new ArgumentNullException(nameof(lambda));
            var stackBase = Stack.Count;
            _lambdaArgsStack.Push(args ?? Array.Empty<object?>());
            object? result = null;
            try
            {
                foreach (var cmd in lambda.Body)
                    await ExecuteAsync(cmd);
                if (Stack.Count > stackBase)
                {
                    result = Stack[Stack.Count - 1];
                    Stack.RemoveRange(stackBase, Stack.Count - stackBase);
                }
            }
            finally
            {
                if (Stack.Count > stackBase)
                    Stack.RemoveRange(stackBase, Stack.Count - stackBase);
                _lambdaArgsStack.Pop();
            }
            return result;
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
                Hal.StreamWaitAsync(_unit, null, single);
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

            Hal.StreamWaitAsync(_unit, fnName, args);
            return Task.CompletedTask;
        }

        private Core.ExecutionCallContext BuildCallContext()
        {
            return new Core.ExecutionCallContext
            {
                Unit = _unit,
                Interpreter = this,
                CurrentDatabase = null,
                IsExecutingQueryExpr = _queryExecutionDepth > 0,
                CurrentSocket = CurrentSocketContext
            };
        }

        private async Task ExecuteStreamWaitObjAsync(Command command)
        {
            if (Stack.Count < 2) throw new InvalidOperationException("StreamWaitObj: stack underflow.");
            var streamWaitTypeObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            var obj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);

            var streamWaitType = streamWaitTypeObj?.ToString() ?? "delta";
            var result = await Hal.StreamWaitObjAsync(obj, streamWaitType).WaitAsync(DebugContinueCancellationToken).ConfigureAwait(false);
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
            var result = await Hal.ExecuteAwaitObjAsync(obj).WaitAsync(DebugContinueCancellationToken).ConfigureAwait(false);
            Stack.Add(result!);
        }

        private async Task ExecuteAwaitAsync(Command command)
        {
            if (Stack.Count < 1) throw new InvalidOperationException("Await: stack underflow.");
            var obj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);

            if (obj is Task task)
            {
                await task.WaitAsync(DebugContinueCancellationToken).ConfigureAwait(false);
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
                var awaited = await Hal.ExecuteAwaitAsync(obj).WaitAsync(DebugContinueCancellationToken).ConfigureAwait(false);
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

        /// <summary>Pops arity (numeric) from stack, then pops n values. Returns (n, args) with args[0] = first arg below arity. Throws if stack has fewer than 1 + n + extraRequired elements.</summary>
        private (int n, object?[] args) PopArityAndArgs(int extraRequired = 0)
        {
            if (Stack.Count < 1) throw new InvalidOperationException("Stack underflow (arity).");
            var arityObj = Stack[Stack.Count - 1];
            Stack.RemoveAt(Stack.Count - 1);
            if (!TryCoerceStackArity(arityObj, out var n) || n < 0)
                throw new InvalidOperationException($"Invalid arity on stack (expected integral count): {arityObj?.GetType().Name ?? "null"}.");
            if (Stack.Count < n + extraRequired) throw new InvalidOperationException("Not enough arguments on stack.");
            var args = new object?[n];
            for (var i = 0; i < n; i++)
            {
                args[n - 1 - i] = Stack[Stack.Count - 1];
                Stack.RemoveAt(Stack.Count - 1);
            }
            return (n, args);
        }

        /// <summary>Arity для <see cref="Opcodes.Def"/> / <see cref="Opcodes.CallObj"/> и т.д.: int/long или целое в double/float.</summary>
        private static bool TryCoerceStackArity(object? arityObj, out int n)
        {
            n = 0;
            if (arityObj == null) return false;
            switch (arityObj)
            {
                case int i:
                    n = i;
                    return true;
                case long l:
                    if (l > int.MaxValue || l < int.MinValue) return false;
                    n = (int)l;
                    return true;
                case uint ui:
                    if (ui > int.MaxValue) return false;
                    n = (int)ui;
                    return true;
                case short s:
                    n = s;
                    return true;
                case ushort us:
                    n = us;
                    return true;
                case byte b:
                    n = b;
                    return true;
                case sbyte sb:
                    n = sb;
                    return true;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) return false;
                    var rd = Math.Round(d);
                    if (Math.Abs(d - rd) > 1e-9) return false;
                    if (rd > int.MaxValue || rd < int.MinValue) return false;
                    n = (int)rd;
                    return true;
                case float f:
                    if (float.IsNaN(f) || float.IsInfinity(f)) return false;
                    var rf = (float)Math.Round(f);
                    if (Math.Abs(f - rf) > 1e-5f) return false;
                    if (rf > int.MaxValue || rf < int.MinValue) return false;
                    n = (int)rf;
                    return true;
                default:
                    return false;
            }
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
            return MemoryContext.TryRead(memoryAddress, IsExecutingEntryPoint(), out value);
        }

        private static bool AreCmpValuesEqual(object? left, object? right)
        {
            if (left is FloatDecimal leftFloatDecimal && right is FloatDecimal rightFloatDecimal)
                return leftFloatDecimal == rightFloatDecimal;

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
                FloatDecimal fd => !fd.IsZero,
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
                case FloatDecimal fd when fd.IsFinite &&
                                          decimal.TryParse(fd.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
                    result = parsed;
                    return true;
                case bool b:
                    result = b ? 1m : 0m;
                    return true;
                case string s:
                    if (string.IsNullOrWhiteSpace(s))
                    {
                        result = 0m;
                        return true;
                    }
                    if (decimal.TryParse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedString))
                    {
                        result = parsedString;
                        return true;
                    }
                    return false;
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

        private sealed class DefStreamReferenceEquality : IEqualityComparer<DefStream>
        {
            internal static readonly DefStreamReferenceEquality Instance = new();

            public bool Equals(DefStream? x, DefStream? y) => ReferenceEquals(x, y);

            public int GetHashCode(DefStream obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private static void CollectDefStreamsForShutdown(object? o, HashSet<DefStream> sink)
        {
            switch (o)
            {
                case null:
                    return;
                case DefStream ds:
                    sink.Add(ds);
                    foreach (var g in ds.Generalizations)
                    {
                        if (g is DefStream inner)
                            CollectDefStreamsForShutdown(inner, sink);
                    }

                    return;
                case DefList list:
                    foreach (var it in list.Items)
                        CollectDefStreamsForShutdown(it, sink);
                    return;
            }
        }

        /// <summary>Принудительно закрыть потоки (claw и т.д.) извне — например Terminal Restart, пока корень ждёт await claw.</summary>
        public async Task ForceCloseOwnedStreamsAsync()
        {
            await CloseOwnedDefStreamsAsync().ConfigureAwait(false);
        }

        /// <summary>Закрыть потоки, созданные этим интерпретатором (Stop отладки / выход из unit).</summary>
        private async Task CloseOwnedDefStreamsAsync()
        {
            var seen = new HashSet<DefStream>(DefStreamReferenceEquality.Instance);
            void add(object? o) => CollectDefStreamsForShutdown(o, seen);
            foreach (var kv in MemoryContext.Global)
                add(kv.Value);
            foreach (var kv in MemoryContext.Inherited)
                add(kv.Value);
            foreach (var v in MemoryContext.EnumerateAllFrameLocalValues())
                add(v);
            foreach (var o in Stack)
                add(o);

            foreach (var s in DefStream.GetActiveStreams())
            {
                if (s is ClawStreamDevice claw && claw.IsHostedByInterpreter(this))
                    seen.Add(s);
            }

            foreach (var d in seen.ToArray())
            {
                try
                {
                    //await d.CloseAsync().ConfigureAwait(false);
                }
                catch
                {
                    // best-effort
                }
            }
        }
    }
}
