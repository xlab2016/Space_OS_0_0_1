using Magic.Kernel.Compilation;
using Magic.Kernel.Core;
using Magic.Kernel.Interpretation;
using Magic.Kernel.Processor;

namespace Magic.Kernel.Runtime
{
    /// <summary>
    /// One runnable unit of work for the runtime (Erlang-style process/actor).
    /// Scheduler dequeues these and runs them on the worker pool.
    /// </summary>
    public sealed class RuntimeTask
    {
        /// <summary>Unit to execute (entry point or procedure/function).</summary>
        public required ExecutableUnit Unit { get; init; }

        /// <summary>Optional: procedure or function name to start at; if null, use Unit.EntryPoint.</summary>
        public string? EntryName { get; init; }

        /// <summary>Optional call parameters when starting at a procedure/function.</summary>
        public CallInfo? CallInfo { get; init; }

        /// <summary>Optional correlation id (e.g. spawn return ref).</summary>
        public object? Tag { get; init; }

        /// <summary>
        /// Optional start block for label-based task entry.
        /// Needed when a label lives in the current execution block rather than in Unit.EntryPoint.
        /// </summary>
        public ExecutionBlock? StartBlock { get; init; }

        /// <summary>Inherited local memory snapshot from parent task.</summary>
        public Dictionary<long, object>? InheritedLocalMemory { get; init; }

        /// <summary>Inherited global memory snapshot from parent task.</summary>
        public Dictionary<long, object>? InheritedGlobalMemory { get; init; }

        /// <summary>Inherited type metadata snapshot from parent task.</summary>
        public List<DefType>? InheritedTypes { get; init; }

        /// <summary>Optional completion source for callers that need to await task completion.</summary>
        public TaskCompletionSource<InterpretationResult>? Completion { get; init; }

        /// <summary>AGI debugger session from parent interpreter (acall/spawn); when set, child hits breakpoints.</summary>
        public InterpreterDebugSession? DebugSession { get; init; }
    }
}
