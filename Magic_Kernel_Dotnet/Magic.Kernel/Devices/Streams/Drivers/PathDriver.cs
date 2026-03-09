using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Magic.Kernel.Devices;

namespace Magic.Kernel.Devices.Streams.Drivers
{
    /// <summary>Stream device over a directory: recursively walks folders by index, reads each file via FileDriver. No file list in memory — next file is resolved from current position.</summary>
    public class PathDriver : IStreamDevice
    {
        private readonly string directoryPath;
        private readonly int chunkSize;
        private bool opened;
        private List<int>? currentPosition;
        private FileDriver? currentFileDriver;

        public PathDriver(string directoryPath, int chunkSize = 65536)
        {
            this.directoryPath = directoryPath ?? throw new ArgumentNullException(nameof(directoryPath));
            this.chunkSize = chunkSize > 0 ? chunkSize : throw new ArgumentException("Chunk size must be greater than 0", nameof(chunkSize));
        }

        private static string GetDirAtPath(string root, IReadOnlyList<int> pathIndices)
        {
            var dir = root;
            for (int i = 0; i < pathIndices.Count; i++)
            {
                var subdirs = Directory.EnumerateDirectories(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
                if (pathIndices[i] < 0 || pathIndices[i] >= subdirs.Count)
                    return dir;
                dir = subdirs[pathIndices[i]];
            }
            return dir;
        }

        /// <summary>Resolves file at position (path indices + file index in dir). Returns (fullPath, pathIndices, pathIndexNames, fileIndexInDir) or null if invalid.</summary>
        private (string FullPath, List<int> PathIndices, List<string> PathIndexNames, int FileIndexInDir)? GetFileAtPosition(string root, IReadOnlyList<int> indices)
        {
            if (indices == null || indices.Count == 0) return null;
            var pathIndices = new List<int>();
            var pathIndexNames = new List<string>();
            var dir = root;
            for (int i = 0; i < indices.Count - 1; i++)
            {
                var subdirs = Directory.EnumerateDirectories(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
                if (indices[i] < 0 || indices[i] >= subdirs.Count) return null;
                dir = subdirs[indices[i]];
                pathIndices.Add(indices[i]);
                pathIndexNames.Add(Path.GetFileName(dir) ?? "");
            }
            var files = Directory.EnumerateFiles(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
            int fileIdx = indices[^1];
            if (fileIdx < 0 || fileIdx >= files.Count) return null;
            return (files[fileIdx], pathIndices, pathIndexNames, fileIdx);
        }

        /// <summary>Next position in enumeration order: root files first, then each subdir tree.</summary>
        private List<int>? GetNextPosition(string root, IReadOnlyList<int> indices)
        {
            if (indices == null || indices.Count == 0) return null;
            if (indices.Count == 1)
            {
                var rootFileCount = Directory.EnumerateFiles(root).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Count();
                var rootSubdirCount = Directory.EnumerateDirectories(root).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Count();
                int fileIdx = indices[0];
                if (fileIdx + 1 < rootFileCount) return new List<int> { fileIdx + 1 };
                if (rootSubdirCount > 0) return new List<int> { 0, 0 };
                return null;
            }
            var dir = GetDirAtPath(root, indices.Take(indices.Count - 1).ToList());
            var fileCount = Directory.EnumerateFiles(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Count();
            var subdirCount = Directory.EnumerateDirectories(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Count();
            int lastFileIdx = indices[^1];
            if (lastFileIdx + 1 < fileCount)
            {
                var next = indices.Take(indices.Count - 1).ToList();
                next.Add(lastFileIdx + 1);
                return next;
            }
            if (subdirCount > 0)
            {
                var next = indices.ToList();
                next.Add(0);
                return next;
            }
            var parentPath = indices.Take(indices.Count - 2).ToList();
            var parentDir = indices.Count == 2 ? root : GetDirAtPath(root, parentPath);
            var parentSubdirs = Directory.EnumerateDirectories(parentDir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
            int nextSubdirIdx = indices[^2] + 1;
            if (nextSubdirIdx < parentSubdirs.Count)
            {
                var next = indices.Take(indices.Count - 2).ToList();
                next.Add(nextSubdirIdx);
                next.Add(0);
                return next;
            }
            if (indices.Count == 2)
                return null;
            return GetNextPosition(root, indices.Take(indices.Count - 2).ToList());
        }

        /// <summary>First valid position: [0] if root has files, else [0,0] if root has subdirs.</summary>
        private static List<int>? GetFirstPosition(string root)
        {
            var rootFiles = Directory.EnumerateFiles(root).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Count();
            var rootSubdirs = Directory.EnumerateDirectories(root).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Count();
            if (rootFiles > 0) return new List<int> { 0 };
            if (rootSubdirs > 0) return new List<int> { 0, 0 };
            return null;
        }

        public async Task<DeviceOperationResult> OpenAsync()
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                    return DeviceOperationResult.NotFound($"Directory not found: {directoryPath}");
                currentPosition = GetFirstPosition(directoryPath);
                currentFileDriver = null;
                opened = true;
                await Task.CompletedTask.ConfigureAwait(false);
                return DeviceOperationResult.Success;
            }
            catch (Exception ex)
            {
                return DeviceOperationResult.IOError(ex.Message, ex.HResult);
            }
        }

        private async Task<bool> EnsureCurrentFileOpenAsync()
        {
            if (!opened || currentPosition == null)
                return false;
            var entry = GetFileAtPosition(directoryPath, currentPosition);
            if (entry == null)
                return false;
            if (currentFileDriver != null)
                return true;
            var (fullPath, pathIndices, pathIndexNames, fileIndexInDir) = entry.Value;
            currentFileDriver = new FileDriver(fullPath, chunkSize, FileStreamAccess.Read, pathIndices, pathIndexNames, fileIndexInDir);
            var openResult = await currentFileDriver.OpenAsync().ConfigureAwait(false);
            if (!openResult.IsSuccess)
            {
                currentFileDriver = null;
                return false;
            }
            return true;
        }

        public async Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync()
        {
            try
            {
                if (!opened)
                    return (DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Not open"), null);

                while (currentPosition != null)
                {
                    if (!await EnsureCurrentFileOpenAsync().ConfigureAwait(false))
                    {
                        currentPosition = GetNextPosition(directoryPath, currentPosition);
                        continue;
                    }

                    var (result, chunk) = await currentFileDriver!.ReadChunkAsync().ConfigureAwait(false);
                    if (!result.IsSuccess)
                        return (result, null);
                    if (chunk != null && chunk.Data != null && chunk.Data.Length > 0)
                        return (DeviceOperationResult.Success, chunk);

                    await currentFileDriver.CloseAsync().ConfigureAwait(false);
                    currentFileDriver = null;
                    currentPosition = GetNextPosition(directoryPath, currentPosition);
                }

                return (DeviceOperationResult.Success, new StreamChunk { ChunkSize = chunkSize, Data = Array.Empty<byte>() });
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
                if (!opened)
                    return Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Not open"));
                if (position?.Indices == null || position.Indices.Count == 0)
                {
                    currentPosition = GetFirstPosition(directoryPath);
                    currentFileDriver = null;
                    return Task.FromResult(DeviceOperationResult.Success);
                }
                var entry = GetFileAtPosition(directoryPath, position.Indices);
                if (entry == null)
                    return Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "No file at given path indices"));
                currentPosition = new List<int>(position.Indices);
                currentFileDriver = null;
                return Task.FromResult(DeviceOperationResult.Success);
            }
            catch (Exception ex)
            {
                return Task.FromResult(DeviceOperationResult.IOError(ex.Message, ex.HResult));
            }
        }

        public Task<(DeviceOperationResult Result, long Length)> LengthAsync()
        {
            if (!opened)
                return Task.FromResult((DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Not open"), 0L));
            long total = 0;
            var pos = GetFirstPosition(directoryPath);
            while (pos != null)
            {
                var e = GetFileAtPosition(directoryPath, pos);
                if (e != null)
                {
                    try { total += new FileInfo(e.Value.FullPath).Length; } catch { /* skip */ }
                }
                pos = GetNextPosition(directoryPath, pos);
            }
            return Task.FromResult((DeviceOperationResult.Success, total));
        }

        public async Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync()
        {
            if (!opened)
                return (DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Not open"), Array.Empty<byte>());
            var buffers = new List<byte[]>();
            var pos = GetFirstPosition(directoryPath);
            while (pos != null)
            {
                var e = GetFileAtPosition(directoryPath, pos);
                if (e != null)
                {
                    var driver = new FileDriver(e.Value.FullPath, chunkSize, FileStreamAccess.Read, e.Value.PathIndices, e.Value.PathIndexNames, e.Value.FileIndexInDir);
                    var openResult = await driver.OpenAsync().ConfigureAwait(false);
                    if (openResult.IsSuccess)
                    {
                        var (res, bytes) = await driver.ReadAsync().ConfigureAwait(false);
                        await driver.CloseAsync().ConfigureAwait(false);
                        if (res.IsSuccess && bytes != null && bytes.Length > 0)
                            buffers.Add(bytes);
                    }
                }
                pos = GetNextPosition(directoryPath, pos);
            }
            if (buffers.Count == 0)
                return (DeviceOperationResult.Success, Array.Empty<byte>());
            var total = buffers.Sum(b => b.Length);
            var result = new byte[total];
            int offset = 0;
            foreach (var b in buffers)
            {
                Buffer.BlockCopy(b, 0, result, offset, b.Length);
                offset += b.Length;
            }
            return (DeviceOperationResult.Success, result);
        }

        public Task<DeviceOperationResult> WriteAsync(byte[] bytes) => Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "PathDriver is read-only"));
        public Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk) => Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "PathDriver is read-only"));
        public Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl) => Task.FromResult(DeviceOperationResult.Success);

        public async Task<DeviceOperationResult> CloseAsync()
        {
            try
            {
                if (currentFileDriver != null)
                {
                    await currentFileDriver.CloseAsync().ConfigureAwait(false);
                    currentFileDriver = null;
                }
                currentPosition = null;
                opened = false;
                return DeviceOperationResult.Success;
            }
            catch (Exception ex)
            {
                return DeviceOperationResult.IOError(ex.Message, ex.HResult);
            }
        }
    }
}
