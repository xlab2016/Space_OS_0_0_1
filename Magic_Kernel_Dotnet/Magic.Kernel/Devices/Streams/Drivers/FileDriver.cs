using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Magic.Kernel.Devices;
using Magic.Kernel.Core.OS;

namespace Magic.Kernel.Devices.Streams.Drivers
{
    /// <summary>Stream access mode for file open.</summary>
    public enum FileStreamAccess
    {
        Read,
        Write,
        ReadWrite,
    }

    public class FileDriver : IStreamDevice
    {
        private readonly string filePath;
        private readonly int chunkSize;
        private readonly FileStreamAccess access;
        private readonly List<int>? pathIndices;
        private readonly List<string>? pathIndexNames;
        private readonly int fileIndexInDir;
        private FileStream? stream;
        private int currentChunkIndex;
        private long fileLength;
        private string? fileName;

        public FileDriver(
            string filePath,
            int chunkSize = 65536,
            FileStreamAccess access = FileStreamAccess.Read,
            List<int>? pathIndices = null,
            List<string>? pathIndexNames = null,
            int fileIndexInDir = 0)
        {
            this.filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            this.chunkSize = chunkSize > 0 ? chunkSize : throw new ArgumentException("Chunk size must be greater than 0", nameof(chunkSize));
            this.access = access;
            this.pathIndices = pathIndices;
            this.pathIndexNames = pathIndexNames;
            this.fileIndexInDir = fileIndexInDir;
            currentChunkIndex = 0;
            fileLength = 0;
        }

        public async Task<DeviceOperationResult> OpenAsync()
        {
            try
            {
                FileMode mode;
                FileAccess fileAccess;
                switch (access)
                {
                    case FileStreamAccess.Read:
                        if (!File.Exists(filePath))
                            return DeviceOperationResult.NotFound($"File not found: {filePath}");
                        mode = FileMode.Open;
                        fileAccess = FileAccess.Read;
                        break;
                    case FileStreamAccess.Write:
                        mode = FileMode.OpenOrCreate;
                        fileAccess = FileAccess.Write;
                        break;
                    case FileStreamAccess.ReadWrite:
                        mode = FileMode.OpenOrCreate;
                        fileAccess = FileAccess.ReadWrite;
                        break;
                    default:
                        return DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Unknown access mode");
                }

                stream = new FileStream(filePath, mode, fileAccess, FileShare.Read, bufferSize: 4096, useAsync: true);
                fileLength = stream.Length;
                fileName = Path.GetFileName(filePath);
                currentChunkIndex = 0;
                await Task.CompletedTask.ConfigureAwait(false);
                return DeviceOperationResult.Success;
            }
            catch (Exception ex)
            {
                return DeviceOperationResult.IOError(ex.Message, ex.HResult);
            }
        }

        public async Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync()
        {
            try
            {
                if (stream == null || !stream.CanRead)
                    return (DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Stream not open or not readable"), null);

                if (stream.Position >= fileLength)
                    return (DeviceOperationResult.Success, new StreamChunk { ChunkSize = chunkSize, Data = Array.Empty<byte>(), DataFormat = GetDataFormatFromPath(filePath), ApplicationFormat = ApplicationFormatDetection.FromPath(filePath) });

                const int minRequiredMultiplier = 2;
                long requiredBytes = (long)chunkSize * minRequiredMultiplier;
                if (OSSystem.CheckMemoryAvailable(requiredBytes) is { } memError)
                    return (memError, null);

                var buffer = new byte[chunkSize];
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, chunkSize)).ConfigureAwait(false);

                if (bytesRead == 0)
                    return (DeviceOperationResult.Success, new StreamChunk { ChunkSize = chunkSize, Data = Array.Empty<byte>(), DataFormat = GetDataFormatFromPath(filePath), ApplicationFormat = ApplicationFormatDetection.FromPath(filePath) });

                var chunkData = new byte[bytesRead];
                Array.Copy(buffer, chunkData, bytesRead);

                string chunkHash;
                using (var sha256 = SHA256.Create())
                {
                    var hashBytes = sha256.ComputeHash(chunkData);
                    chunkHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                }

                var indices = pathIndices != null
                    ? new List<int>(pathIndices) { fileIndexInDir }
                    : new List<int> { fileIndexInDir };
                var indexNames = pathIndexNames != null
                    ? new List<string>(pathIndexNames) { fileName ?? Path.GetFileName(filePath) }
                    : new List<string> { Path.GetDirectoryName(filePath) ?? "", fileName ?? Path.GetFileName(filePath) };

                var chunk = new StreamChunk
                {
                    ChunkSize = chunkSize,
                    Data = chunkData,
                    DataFormat = GetDataFormatFromPath(filePath),
                    ApplicationFormat = ApplicationFormatDetection.FromPath(filePath),
                    Position = new StructurePosition
                    {
                        RelativeIndex = currentChunkIndex,
                        RelativeIndexName = chunkHash,
                        Indices = indices,
                        IndexNames = indexNames
                    }
                };
                currentChunkIndex++;
                return (DeviceOperationResult.Success, chunk);
            }
            catch (Exception ex)
            {
                return (DeviceOperationResult.IOError(ex.Message, ex.HResult), null);
            }
        }

        public Task<DeviceOperationResult> MoveAsync(StructurePosition? position)
        {
            try
            {
                if (stream == null)
                    return Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Stream not open"));

                long offset;
                if (position != null && position.AbsolutePosition != 0)
                    offset = position.AbsolutePosition;
                else
                {
                    int chunkIndex = position?.RelativeIndex ?? 0;
                    if (chunkIndex < 0)
                        return Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Position index must be non-negative"));
                    offset = (long)chunkIndex * chunkSize;
                }
                if (offset < 0)
                    return Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Position must be non-negative"));

                if (stream.CanRead)
                {
                    long maxOffset = Math.Max(fileLength, stream.Length);
                    offset = Math.Min(offset, maxOffset);
                }
                stream.Position = offset;
                currentChunkIndex = (int)(offset / chunkSize);
                return Task.FromResult(DeviceOperationResult.Success);
            }
            catch (Exception ex)
            {
                return Task.FromResult(DeviceOperationResult.IOError(ex.Message, ex.HResult));
            }
        }

        public Task<(DeviceOperationResult Result, long Length)> LengthAsync()
        {
            if (stream == null)
                return Task.FromResult((DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Stream not open"), 0L));
            long len = stream.Length;
            fileLength = len;
            return Task.FromResult((DeviceOperationResult.Success, len));
        }

        public async Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync()
        {
            try
            {
                if (!File.Exists(filePath))
                    return (DeviceOperationResult.NotFound($"File not found: {filePath}"), Array.Empty<byte>());
                var bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
                return (DeviceOperationResult.Success, bytes);
            }
            catch (Exception ex)
            {
                return (DeviceOperationResult.IOError(ex.Message, ex.HResult), Array.Empty<byte>());
            }
        }

        public async Task<DeviceOperationResult> WriteAsync(byte[] bytes)
        {
            try
            {
                if (bytes == null || bytes.Length == 0)
                    return DeviceOperationResult.Success;
                if (stream != null && stream.CanWrite)
                {
                    await stream.WriteAsync(bytes.AsMemory(0, bytes.Length)).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                    fileLength = stream.Length;
                    return DeviceOperationResult.Success;
                }
                await File.WriteAllBytesAsync(filePath, bytes).ConfigureAwait(false);
                return DeviceOperationResult.Success;
            }
            catch (Exception ex)
            {
                return DeviceOperationResult.IOError(ex.Message, ex.HResult);
            }
        }

        public async Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk)
        {
            try
            {
                if (chunk?.Data == null || chunk.Data.Length == 0)
                    return DeviceOperationResult.Success;
                if (stream == null || !stream.CanWrite)
                    return DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Stream not open for write");

                await stream.WriteAsync(chunk.Data.AsMemory(0, chunk.Data.Length)).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                currentChunkIndex++;
                fileLength = stream.Length;
                return DeviceOperationResult.Success;
            }
            catch (Exception ex)
            {
                return DeviceOperationResult.IOError(ex.Message, ex.HResult);
            }
        }

        public Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl)
            => Task.FromResult(DeviceOperationResult.Success);

        public async Task<DeviceOperationResult> CloseAsync()
        {
            try
            {
                if (stream != null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    stream = null;
                }
                currentChunkIndex = 0;
                return DeviceOperationResult.Success;
            }
            catch (Exception ex)
            {
                return DeviceOperationResult.IOError(ex.Message, ex.HResult);
            }
        }

        private static DataFormat GetDataFormatFromPath(string path)
        {
            var ext = (Path.GetExtension(path) ?? "").TrimStart('.').ToLowerInvariant();
            var appFmt = ApplicationFormatDetection.FromExtension(ext);
            if (appFmt != ApplicationFormat.Unknown && appFmt != ApplicationFormat.Yaml && appFmt != ApplicationFormat.Markdown)
                return DataFormat.Code;
            return ext switch
            {
                "pdf" => DataFormat.Pdf,
                "doc" or "docx" => DataFormat.Word,
                "xls" or "xlsx" or "xlsm" or "csv" => DataFormat.Excel,
                "mp3" or "wav" or "ogg" or "flac" or "m4a" or "aac" or "wma" => DataFormat.Audio,
                "mp4" or "avi" or "mkv" or "mov" or "wmv" or "webm" or "flv" or "m4v" => DataFormat.VideoAudio,
                "txt" or "json" or "xml" or "log" or "md" or "html" or "htm" or "rtf" => DataFormat.Text,
                _ => DataFormat.Text
            };
        }
    }
}
