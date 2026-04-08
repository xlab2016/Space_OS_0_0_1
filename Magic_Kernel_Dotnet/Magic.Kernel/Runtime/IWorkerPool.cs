using System.Threading;
using System.Threading.Tasks;

namespace Magic.Kernel.Runtime
{
    /// <summary>
    /// Executes runtime tasks on a pool of workers (Erlang-style scheduler → worker pool).
    /// </summary>
    public interface IWorkerPool
    {
        /// <summary>Run the task on a pool worker; returns when the task has been submitted (fire-and-forget execution).</summary>
        void Run(RuntimeTask task, CancellationToken cancellationToken = default);
    }
}
