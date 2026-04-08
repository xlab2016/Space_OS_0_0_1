using System;
using System.Threading;
using System.Threading.Tasks;

namespace Magic.Kernel.Runtime
{
    /// <summary>
    /// Erlang-like scheduler: continuously dequeues from ITaskQueue and dispatches to IWorkerPool.
    /// Run one instance per runtime (e.g. one background task).
    /// </summary>
    public sealed class RuntimeScheduler
    {
        private readonly ITaskQueue _queue;
        private readonly IWorkerPool _workerPool;
        private Task? _runTask;
        private CancellationTokenSource? _cts;

        public RuntimeScheduler(ITaskQueue queue, IWorkerPool workerPool)
        {
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _workerPool = workerPool ?? throw new ArgumentNullException(nameof(workerPool));
        }

        /// <summary>Start the scheduler loop (one long-running task).</summary>
        public void Start()
        {
            if (_runTask != null)
                return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _runTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    var (success, task) = await _queue.DequeueAsync(token).ConfigureAwait(false);
                    if (!success || task == null)
                        break;
                    _workerPool.Run(task, token);
                }
            }, token);
        }

        /// <summary>Signal shutdown and wait for the scheduler loop to exit.</summary>
        public async Task StopAsync()
        {
            _cts?.Cancel();
            _queue.CompleteAdding();
            if (_runTask != null)
            {
                try
                {
                    await _runTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                _runTask = null;
            }
            _cts?.Dispose();
            _cts = null;
        }
    }
}
