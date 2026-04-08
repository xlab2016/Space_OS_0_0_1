using Magic.Kernel.Compilation;
using Magic.Kernel.Core;
using Magic.Kernel.Interpretation;
using Magic.Kernel.Processor;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Magic.Kernel.Runtime
{
    /// <summary>
    /// Erlang-like runtime: TaskQueue → Scheduler → ThreadPool.
    /// Tasks are enqueued via EnqueueAsync (e.g. from spawn/call); scheduler picks them and runs on worker pool.
    /// </summary>
    public sealed class KernelRuntime
    {
        private readonly MagicKernel _kernel;
        private readonly ITaskQueue _taskQueue;
        private readonly RuntimeScheduler _scheduler;

        public KernelRuntime(MagicKernel kernel)
        {
            _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
            _taskQueue = new TaskQueue();
            var workerPool = new ThreadPoolWorkerPool(RunTaskAsync);
            _scheduler = new RuntimeScheduler(_taskQueue, workerPool);
        }

        /// <summary>Queue used by scheduler; exposed for tests or custom enqueue.</summary>
        public ITaskQueue TaskQueue => _taskQueue;

        /// <summary>Start the scheduler loop (call once when kernel starts).</summary>
        public void Start()
        {
            _scheduler.Start();
        }

        /// <summary>Stop scheduler and complete queue.</summary>
        public Task StopAsync()
        {
            return _scheduler.StopAsync();
        }

        /// <summary>Add a task to the queue (spawn semantics). Returns when enqueued.</summary>
        public ValueTask EnqueueAsync(RuntimeTask task, CancellationToken cancellationToken = default)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            var unitName = task.Unit?.Name ?? "<unknown>";
            var entry = string.IsNullOrEmpty(task.EntryName) ? "entrypoint" : task.EntryName;
            Console.WriteLine($"[{DateTime.UtcNow:o}] [runtime] enqueue task: unit='{unitName}', entry='{entry}', tag='{task.Tag ?? ""}'");
            return _taskQueue.EnqueueAsync(task, cancellationToken);
        }

        /// <summary>Enqueue run of unit from entry point or procedure/function (spawn).</summary>
        public ValueTask SpawnAsync(
            ExecutableUnit unit,
            string? entryName = null,
            CallInfo? callInfo = null,
            object? tag = null,
            List<DefType>? inheritedTypes = null,
            ExecutionBlock? startBlock = null,
            InterpreterDebugSession? debugSession = null,
            CancellationToken cancellationToken = default)
        {
            return SpawnAsync(unit, entryName, callInfo, tag, inheritedLocalMemory: null, inheritedGlobalMemory: null, inheritedTypes, startBlock, debugSession, cancellationToken);
        }

        /// <summary>
        /// Enqueue run of unit with captured memory from parent task.
        /// capturedMemory/capturedGlobalMemory are read-only snapshots visible as captured_* in the new task.
        /// </summary>
        public ValueTask SpawnAsync(
            ExecutableUnit unit,
            string? entryName,
            CallInfo? callInfo,
            object? tag,
            Dictionary<long, object>? inheritedLocalMemory,
            Dictionary<long, object>? inheritedGlobalMemory,
            List<DefType>? inheritedTypes = null,
            ExecutionBlock? startBlock = null,
            InterpreterDebugSession? debugSession = null,
            CancellationToken cancellationToken = default)
        {
            var task = CreateRuntimeTask(unit, entryName, callInfo, tag, inheritedLocalMemory, inheritedGlobalMemory, inheritedTypes, startBlock, completion: null, debugSession);
            return EnqueueAsync(task, cancellationToken);
        }

        /// <summary>Enqueue root run and await its completion.</summary>
        public async Task<InterpretationResult> SpawnAndWaitAsync(
            ExecutableUnit unit,
            string? entryName = null,
            CallInfo? callInfo = null,
            object? tag = null,
            Dictionary<long, object>? inheritedLocalMemory = null,
            Dictionary<long, object>? inheritedGlobalMemory = null,
            List<DefType>? inheritedTypes = null,
            ExecutionBlock? startBlock = null,
            InterpreterDebugSession? debugSession = null,
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<InterpretationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = CreateRuntimeTask(unit, entryName, callInfo, tag, inheritedLocalMemory, inheritedGlobalMemory, inheritedTypes, startBlock, completion, debugSession);
            await EnqueueAsync(task, cancellationToken).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }

        private static RuntimeTask CreateRuntimeTask(
            ExecutableUnit unit,
            string? entryName,
            CallInfo? callInfo,
            object? tag,
            Dictionary<long, object>? inheritedLocalMemory,
            Dictionary<long, object>? inheritedGlobalMemory,
            List<DefType>? inheritedTypes,
            ExecutionBlock? startBlock,
            TaskCompletionSource<InterpretationResult>? completion,
            InterpreterDebugSession? debugSession)
        {
            return new RuntimeTask
            {
                Unit = unit,
                EntryName = entryName,
                CallInfo = callInfo,
                Tag = tag,
                StartBlock = startBlock,
                InheritedLocalMemory = inheritedLocalMemory,
                InheritedGlobalMemory = inheritedGlobalMemory,
                InheritedTypes = inheritedTypes,
                Completion = completion,
                DebugSession = debugSession
            };
        }

        private async Task RunTaskAsync(RuntimeTask task, CancellationToken cancellationToken)
        {
            var interpreter = _kernel.CreateInterpreter();
            interpreter.DebugSession = task.DebugSession;
            if (task.Unit != null)
                _kernel.AttachRegistry.Register(interpreter, task.Unit);

            try
            {
                void WriteLineEnsuringNewLine(string message)
                {
                    try
                    {
                        // If previous output used Console.Write (no newline), cursor can be mid-line.
                        // Force a line break before runtime logs so they don't glue to user output.
                        if (Console.CursorLeft != 0)
                            Console.WriteLine();
                    }
                    catch
                    {
                        // Cursor properties might be unavailable when output is redirected.
                    }

                    Console.WriteLine(message);
                }

                // Наследуемые слои памяти от родительской задачи.
                interpreter.MemoryContext.Global = task.InheritedGlobalMemory != null
                    ? new Dictionary<long, object>(task.InheritedGlobalMemory)
                    : new Dictionary<long, object>();

                interpreter.MemoryContext.Inherited = task.InheritedLocalMemory != null
                    ? new Dictionary<long, object>(task.InheritedLocalMemory)
                    : new Dictionary<long, object>();
                interpreter.InheritedTypes = task.InheritedTypes != null
                    ? new List<DefType>(task.InheritedTypes)
                    : new List<DefType>();

                interpreter.MemoryContext.ClearLocalScopes();
                interpreter.MemoryContext.ClearAmbientLocal();

                var unitName = task.Unit?.Name ?? "<unknown>";
                var entry = string.IsNullOrEmpty(task.EntryName) ? "entrypoint" : task.EntryName;
                WriteLineEnsuringNewLine($"[{DateTime.UtcNow:o}] [runtime] start task: unit='{unitName}', entry='{entry}', tag='{task.Tag ?? ""}'");

                var result = await interpreter.InterpreteFromEntryAsync(task.Unit, task.EntryName, task.CallInfo, task.StartBlock).ConfigureAwait(false);

                WriteLineEnsuringNewLine($"[{DateTime.UtcNow:o}] [runtime] complete task: unit='{unitName}', entry='{entry}', tag='{task.Tag ?? ""}'");
                task.Completion?.TrySetResult(result);
            }
            catch (Exception ex)
            {
                var prefix = Core.ExecutionCallContext.GetPrefix(task.Unit);
                try
                {
                    if (Console.CursorLeft != 0)
                        Console.WriteLine();
                }
                catch
                {
                    // ignore
                }

                Console.WriteLine($"[{DateTime.UtcNow:o}] {prefix}runtime task failed: {ex}");
                task.Completion?.TrySetResult(new InterpretationResult { Success = false });
            }
            finally
            {
                if (task.Unit != null)
                    _kernel.AttachRegistry.Unregister(task.Unit);
            }
        }
    }
}
