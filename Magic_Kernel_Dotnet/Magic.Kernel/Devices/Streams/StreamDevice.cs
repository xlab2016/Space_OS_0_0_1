using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Magic.Kernel.Core;
using Magic.Kernel.Devices;

namespace Magic.Kernel.Devices.Streams
{
    /// <summary>Generic stream device wrapper; override or use for in-memory / test streams.</summary>
    public class StreamDevice : DefStream
    {
        private readonly Func<byte[]?>? readAll;
        private readonly Action<byte[]?>? writeAll;
        private long length;
        private long position;

        public StreamDevice(Func<byte[]?>? readAll = null, Action<byte[]?>? writeAll = null, long length = 0)
        {
            this.readAll = readAll;
            this.writeAll = writeAll;
            this.length = length;
        }

        public override async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            if (Generalizations == null || Generalizations.Count == 0)
                return await base.CallObjAsync(methodName, args).ConfigureAwait(false);
            var list = new List<object?>();
            foreach (var g in Generalizations)
            {
                if (g != null)
                {
                    var r = await g.CallObjAsync(methodName, args).ConfigureAwait(false);
                    list.Add(r);
                }
            }
            return list;
        }

        public override Task<DeviceOperationResult> OpenAsync() => Task.FromResult(DeviceOperationResult.Success);

        public override async Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync()
        {
            if (Generalizations != null && Generalizations.Count > 0)
            {
                foreach (var g in Generalizations)
                {
                    if (g is IStreamDevice dev)
                    {
                        return await dev.ReadAsync().ConfigureAwait(false);
                    }
                }
            }
            var bytes = readAll?.Invoke() ?? Array.Empty<byte>();
            return (DeviceOperationResult.Success, bytes);
        }

        public override async Task<DeviceOperationResult> WriteAsync(byte[] bytes)
        {
            if (Generalizations != null && Generalizations.Count > 0)
            {
                foreach (var g in Generalizations)
                {
                    if (g is IStreamDevice dev)
                    {
                        var r = await dev.WriteAsync(bytes).ConfigureAwait(false);
                        if (!r.IsSuccess) return r;
                    }
                }
                return DeviceOperationResult.Success;
            }
            writeAll?.Invoke(bytes);
            return DeviceOperationResult.Success;
        }

        public override Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl) => Task.FromResult(DeviceOperationResult.Success);

        public override Task<DeviceOperationResult> CloseAsync() => Task.FromResult(DeviceOperationResult.Success);

        public override async Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync()
        {
            if (Generalizations != null && Generalizations.Count > 0)
            {
                foreach (var g in Generalizations)
                {
                    if (g is IStreamDevice dev)
                    {
                        return await dev.ReadChunkAsync().ConfigureAwait(false);
                    }
                }
            }
            var data = readAll?.Invoke() ?? Array.Empty<byte>();
            var chunk = new StreamChunk { ChunkSize = data?.Length ?? 0, Data = data ?? Array.Empty<byte>() };
            return (DeviceOperationResult.Success, chunk);
        }

        public override async Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk)
        {
            if (Generalizations != null && Generalizations.Count > 0)
            {
                foreach (var g in Generalizations)
                {
                    if (g is IStreamDevice dev)
                    {
                        var r = await dev.WriteChunkAsync(chunk!).ConfigureAwait(false);
                        if (!r.IsSuccess) return r;
                    }
                }
                return DeviceOperationResult.Success;
            }
            writeAll?.Invoke(chunk?.Data);
            return DeviceOperationResult.Success;
        }

        public override async Task<DeviceOperationResult> MoveAsync(StructurePosition? position)
        {
            if (Generalizations != null && Generalizations.Count > 0)
            {
                foreach (var g in Generalizations)
                {
                    if (g is IStreamDevice dev)
                    {
                        var r = await dev.MoveAsync(position).ConfigureAwait(false);
                        if (!r.IsSuccess) return r;
                    }
                }
                return DeviceOperationResult.Success;
            }
            long pos = position == null ? 0 : (position.AbsolutePosition != 0 ? position.AbsolutePosition : position.RelativeIndex);
            this.position = Math.Max(0, pos);
            return DeviceOperationResult.Success;
        }

        public override async Task<(DeviceOperationResult Result, long Length)> LengthAsync()
        {
            if (Generalizations != null && Generalizations.Count > 0)
            {
                foreach (var g in Generalizations)
                {
                    if (g is IStreamDevice dev)
                    {
                        return await dev.LengthAsync().ConfigureAwait(false);
                    }
                }
            }
            return (DeviceOperationResult.Success, length);
        }
    }
}
