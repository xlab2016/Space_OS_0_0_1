using System.Threading;
using System.Threading.Tasks;

namespace Magic.Kernel.Runtime
{
    /// <summary>
    /// Thread-safe queue of runtime tasks. Scheduler reads from it; call/spawn enqueues.
    /// </summary>
    public interface ITaskQueue
    {
        /// <summary>Add a task to the queue (non-blocking).</summary>
        ValueTask EnqueueAsync(RuntimeTask task, CancellationToken cancellationToken = default);

        /// <summary>Try take one task; blocks until available or cancelled. Returns false when queue is completed.</summary>
        ValueTask<(bool success, RuntimeTask? task)> DequeueAsync(CancellationToken cancellationToken = default);

        /// <summary>Signal that no more tasks will be added; DequeueAsync will eventually return (false, null).</summary>
        void CompleteAdding();
    }
}
