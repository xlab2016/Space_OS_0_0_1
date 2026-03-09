using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Magic.Kernel.Core;
using Magic.Kernel.Devices;
using Magic.Kernel.Interpretation;

namespace Magic.Kernel.Devices.Streams
{
    /// <summary>Base for stream devices that are also def-types (Call by method name). Implements CallAsync once; subclasses implement IStreamDevice.</summary>
    public abstract class DefStream : IStreamDevice, IDefType
    {
        public long? Index { get; set; }
        public string Name { get; set; } = "";
        public StructurePosition? Position { get; set; }
        public List<IDefType> Generalizations { get; set; } = new List<IDefType>();

        public abstract Task<DeviceOperationResult> OpenAsync();
        public abstract Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync();
        public abstract Task<DeviceOperationResult> WriteAsync(byte[] bytes);
        public abstract Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl);
        public abstract Task<DeviceOperationResult> CloseAsync();
        public abstract Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync();
        public abstract Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk);
        public abstract Task<DeviceOperationResult> MoveAsync(StructurePosition? position);
        public abstract Task<(DeviceOperationResult Result, long Length)> LengthAsync();

        public virtual async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";
            if (string.IsNullOrEmpty(name)) throw new CallUnknownMethodException(name, this);

            if (string.Equals(name, "Open", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "OpenAsync", StringComparison.OrdinalIgnoreCase))
                return await OpenAsync().ConfigureAwait(false);
            if (string.Equals(name, "Close", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "CloseAsync", StringComparison.OrdinalIgnoreCase))
                return await CloseAsync().ConfigureAwait(false);
            if (string.Equals(name, "Read", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "ReadAsync", StringComparison.OrdinalIgnoreCase))
                return await ReadAsync().ConfigureAwait(false);
            if (string.Equals(name, "Write", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "WriteAsync", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = ToBytes(args?.Length > 0 ? args[0] : null);
                return await WriteAsync(bytes).ConfigureAwait(false);
            }
            if (string.Equals(name, "ReadChunk", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "ReadChunkAsync", StringComparison.OrdinalIgnoreCase))
                return await ReadChunkAsync().ConfigureAwait(false);
            if (string.Equals(name, "WriteChunk", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "WriteChunkAsync", StringComparison.OrdinalIgnoreCase))
            {
                var chunk = args?.Length > 0 && args[0] is IStreamChunk c ? c : null;
                if (chunk == null) return DeviceOperationResult.Fail(DeviceOperationState.Failed, "WriteChunk requires IStreamChunk argument");
                return await WriteChunkAsync(chunk).ConfigureAwait(false);
            }
            if (string.Equals(name, "Move", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "MoveAsync", StringComparison.OrdinalIgnoreCase))
            {
                var pos = ToPosition(args?.Length > 0 ? args[0] : null) ?? Position;
                return await MoveAsync(pos).ConfigureAwait(false);
            }
            if (string.Equals(name, "Length", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "LengthAsync", StringComparison.OrdinalIgnoreCase))
                return await LengthAsync().ConfigureAwait(false);
            if (string.Equals(name, "Control", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "ControlAsync", StringComparison.OrdinalIgnoreCase))
            {
                var ctrl = args?.Length > 0 && args[0] is DeviceControlBase d ? d : null;
                return await ControlAsync(ctrl!).ConfigureAwait(false);
            }
            throw new CallUnknownMethodException(name, this);
        }

        public virtual Task<object?> AwaitObjAsync() => Task.FromResult<object?>(this);

        public virtual Task<object?> Await() => Task.FromResult<object?>(this);

        public virtual async Task<(bool IsEnd, object? Delta, object? Aggregate)> StreamWaitAsync(string streamWaitType)
        {
            switch (streamWaitType)
            {
                case "delta":
                    var read = await ReadChunkAsync().ConfigureAwait(false);
                    if (!read.Result.IsSuccess || read.Chunk == null)
                        return (true, null, null);
                    return (false, read.Chunk, null);
                case "data":
                    var read2 = await ReadAsync().ConfigureAwait(false);
                    if (!read2.Result.IsSuccess || read2.Bytes == null)
                        return (true, null, null);
                    return (true, read2.Bytes, null);
            }
            return (true, null, null);
        }

        protected static byte[] ToBytes(object? o)
        {
            if (o == null) return Array.Empty<byte>();
            if (o is byte[] b) return b;
            if (o is string s) return System.Text.Encoding.UTF8.GetBytes(s);
            return Array.Empty<byte>();
        }

        protected static StructurePosition? ToPosition(object? o)
        {
            if (o == null) return null;
            if (o is StructurePosition p) return p;
            if (o is long l) return new StructurePosition { AbsolutePosition = l };
            if (o is int i) return new StructurePosition { AbsolutePosition = i };
            return null;
        }
    }
}
