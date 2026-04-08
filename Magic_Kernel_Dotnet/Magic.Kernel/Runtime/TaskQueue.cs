using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Magic.Kernel.Runtime
{
    /// <summary>
    /// Unbounded channel-based task queue for the Erlang-like runtime.
    /// </summary>
    public sealed class TaskQueue : ITaskQueue
    {
        private readonly Channel<RuntimeTask> _channel = Channel.CreateUnbounded<RuntimeTask>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        private int _completed;

        public ValueTask EnqueueAsync(RuntimeTask task, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _completed) != 0)
                return new ValueTask(Task.FromException(new InvalidOperationException("TaskQueue is completed.")));
            return _channel.Writer.WriteAsync(task, cancellationToken);
        }

        public async ValueTask<(bool success, RuntimeTask? task)> DequeueAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _completed) != 0 && !_channel.Reader.TryRead(out _))
                return (false, null);

            try
            {
                var ok = await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                if (!ok)
                    return (false, null);
                if (_channel.Reader.TryRead(out var task))
                    return (true, task);
            }
            catch (OperationCanceledException)
            {
                return (false, null);
            }

            return (false, null);
        }

        public void CompleteAdding()
        {
            Interlocked.Exchange(ref _completed, 1);
            _channel.Writer.Complete();
        }
    }
}
