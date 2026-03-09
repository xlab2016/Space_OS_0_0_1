using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Streams.Drivers;
using Magic.Kernel.Interpretation;

namespace Magic.Kernel.Devices.Streams
{
    /// <summary>Stream device over a path: uses FileDriver for files, PathDriver for directories.</summary>
    public class FileStreamDevice : DefStream
    {
        private IStreamDevice? _driver;

        public string Path { get; set; } = "";
        public int ChunkSize { get; set; } = 65536;

        /// <summary>Only "open" is supported. methodName comes from the interpreter (CallObj command.Operand1). Arg0 is used as Path, then OpenAsync() is called.</summary>
        public override async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";
            if (string.Equals(name, "open", StringComparison.OrdinalIgnoreCase))
            {
                Path = (Position?.IndexNames?.Count > 0 ? Position.IndexNames[0] : null) ?? (args?.Length > 0 ? (args[0]?.ToString() ?? "") : "");
                return await OpenAsync().ConfigureAwait(false);
            }
            throw new CallUnknownMethodException(name, this);
        }

        public override async Task<DeviceOperationResult> OpenAsync()
        {
            var p = Path ?? "";
            if (Directory.Exists(p))
                _driver = new PathDriver(p, ChunkSize);
            else if (p.Length > 0 && (p[p.Length - 1] == System.IO.Path.DirectorySeparatorChar || p[p.Length - 1] == System.IO.Path.AltDirectorySeparatorChar))
                _driver = new PathDriver(p.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar), ChunkSize);
            else
                _driver = new FileDriver(p, ChunkSize, FileStreamAccess.ReadWrite);
            return await _driver.OpenAsync().ConfigureAwait(false);
        }

        private IStreamDevice Driver => _driver ?? throw new InvalidOperationException("Device not opened. Call OpenAsync first.");

        public override Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync() => Driver.ReadAsync();

        public override Task<DeviceOperationResult> WriteAsync(byte[] bytes) => Driver.WriteAsync(bytes);

        public override Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl) => Driver.ControlAsync(deviceControl);

        public override Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync() => Driver.ReadChunkAsync();

        public override Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk) => Driver.WriteChunkAsync(chunk);

        public override Task<DeviceOperationResult> MoveAsync(StructurePosition? position) => Driver.MoveAsync(position);

        public override Task<(DeviceOperationResult Result, long Length)> LengthAsync() => Driver.LengthAsync();

        public override Task<DeviceOperationResult> CloseAsync() => Driver.CloseAsync();

    }
}
