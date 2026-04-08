using System;
using System.Threading;
using System.Threading.Tasks;

namespace Magic.Kernel.Runtime
{
    /// <summary>
    /// Uses .NET ThreadPool to run runtime tasks (each task = one unit of work on a thread-pool thread).
    /// </summary>
    public sealed class ThreadPoolWorkerPool : IWorkerPool
    {
        private readonly Func<RuntimeTask, CancellationToken, Task> _runTask;

        public ThreadPoolWorkerPool(Func<RuntimeTask, CancellationToken, Task> runTask)
        {
            _runTask = runTask ?? throw new ArgumentNullException(nameof(runTask));
        }

        public void Run(RuntimeTask task, CancellationToken cancellationToken = default)
        {
            _ = Task.Run(() => _runTask(task, cancellationToken), cancellationToken);
        }
    }
}
