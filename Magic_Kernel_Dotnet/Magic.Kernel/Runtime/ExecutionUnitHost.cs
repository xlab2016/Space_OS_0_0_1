using Magic.Kernel.Build;
using Magic.Kernel.Compilation;
using Magic.Kernel.Core;
using Magic.Kernel.Interpretation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Magic.Kernel.Runtime
{
    public class ExecutionUnitHost
    {
        private readonly MagicKernel _kernel;

        public Artifact UnitArtifact { get; set; } = new Artifact();

        public ExecutionUnitHost(MagicKernel kernel)
        {
            _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        }

        /// <summary>
        /// Runs a single instance of the execution unit according to the specified command.
        /// For RunType.NewThread it compiles UnitArtifact and executes it on a thread-pool thread.
        /// </summary>
        public async Task<bool> RunAsync(RunCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            if (UnitArtifact == null)
                throw new InvalidOperationException("UnitArtifact is not set.");

            return command.Type switch
            {
                RunType.NewThread => await RunInstanceNewThreadAsync(command.InstanceIndex ?? 0, command).ConfigureAwait(false),
                _ => throw new NotSupportedException($"RunType '{command.Type}' is not supported by ExecutionUnitHost yet.")
            };
        }

        /// <summary>
        /// Runs all configured instances for this unit (executionUnits in dev/release.json) when RunType.NewThread is used.
        /// </summary>
        public async Task<bool> RunAllInstancesAsync(RunCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            if (UnitArtifact == null)
                throw new InvalidOperationException("UnitArtifact is not set.");

            if (command.Type != RunType.NewThread)
                throw new NotSupportedException($"RunType '{command.Type}' is not supported by ExecutionUnitHost yet.");

            // If explicit instance index is provided, just run that single instance.
            if (command.InstanceIndex.HasValue)
                return await RunInstanceNewThreadAsync(command.InstanceIndex.Value, command).ConfigureAwait(false);

            var instanceCount = command.InstanceCount.HasValue && command.InstanceCount.Value > 0
                ? command.InstanceCount.Value
                : ResolveInstanceCountForArtifact(UnitArtifact, command);
            if (instanceCount <= 0)
                instanceCount = 1;

            var tasks = new List<Task<bool>>(instanceCount);
            for (var i = 0; i < instanceCount; i++)
            {
                var idx = i;
                tasks.Add(RunInstanceNewThreadAsync(idx, command));
            }

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.All(r => r);
        }

        private async Task<bool> RunInstanceNewThreadAsync(int instanceIndex, RunCommand? command)
        {
            try
            {
                var unit = await CompileArtifactAsync(command).ConfigureAwait(false);
                unit.InstanceIndex = instanceIndex;
                var result = await _kernel.InterpreteRootAsync(unit).ConfigureAwait(false);
                if (!result.Success)
                {
                    var prefix = Core.ExecutionCallContext.GetPrefix(unit);
                    Console.WriteLine($"[{DateTime.UtcNow:o}] {prefix}interpretation finished with Success = false.");
                }

                return result.Success;
            }
            catch (Exception ex)
            {
                var prefix = Core.ExecutionCallContext.GetPrefix();
                Console.WriteLine($"[{DateTime.UtcNow:o}] {prefix}failed to start interpreter thread: {ex}");
                return false;
            }
        }

        private async Task<ExecutableUnit> CompileArtifactAsync(RunCommand? command = null)
        {
            switch (UnitArtifact.Type)
            {
                case ArtifactType.Agi:
                    {
                        // Body is AGI source code.
                        // Prefer file-based compilation when SourcePath is provided:
                        // only CompileFileAsync resolves/links `use` directives against module files.
                        CompilationResult compilation;
                        if (!string.IsNullOrWhiteSpace(command?.SourcePath) && File.Exists(command.SourcePath))
                        {
                            compilation = await _kernel.Compiler.CompileFileAsync(command.SourcePath).ConfigureAwait(false);
                        }
                        else
                        {
                            compilation = await _kernel.CompileAsync(UnitArtifact.Body ?? string.Empty).ConfigureAwait(false);
                        }
                        if (!compilation.Success || compilation.Result == null)
                            throw new InvalidOperationException(compilation.ErrorMessage ?? "Compilation failed.");
                        var unit = compilation.Result;
                        if (!string.IsNullOrWhiteSpace(command?.SourcePath))
                            unit.AttachSourcePath = Path.GetFullPath(command.SourcePath);

                        // Optionally persist compiled unit to disk when SourcePath is provided (CLI scenario).
                        if (!string.IsNullOrWhiteSpace(command?.SourcePath))
                        {
                            var outputFormat = string.IsNullOrWhiteSpace(command.OutputFormat)
                                ? "agiasm"
                                : command.OutputFormat.Trim().ToLowerInvariant();

                            unit.OutputFormat = outputFormat;
                            var compiledPath = Path.ChangeExtension(
                                command.SourcePath,
                                outputFormat == "agiasm" ? ".agiasm" : ".agic");
                            await unit.SaveAsync(compiledPath).ConfigureAwait(false);
                        }

                        return unit;
                    }

                case ArtifactType.AgiAsm:
                    {
                        // Body is AGIASM text.
                        return ExecutableUnitTextSerializer.Deserialize(UnitArtifact.Body ?? string.Empty);
                    }

                case ArtifactType.Agic:
                    {
                        // Treat Body as path to compiled .agic file.
                        var path = UnitArtifact.Body;
                        if (string.IsNullOrWhiteSpace(path))
                            throw new InvalidOperationException("UnitArtifact.Body must contain path to compiled file for Agic artifacts.");
                        if (!Path.IsPathRooted(path))
                            path = SpaceEnvironment.GetFilePath(path);
                        return await ExecutableUnit.LoadAsync(path!).ConfigureAwait(false);
                    }

                default:
                    throw new NotSupportedException($"ArtifactType '{UnitArtifact.Type}' is not supported.");
            }
        }

        private static int ResolveInstanceCountForArtifact(Artifact unitArtifact, RunCommand command)
        {
            var units = SpaceEnvironment.ExecutionUnits;
            if (units == null || units.Count == 0)
                return 1;

            // Heuristic: match by configured executionUnits using file name (without extension).
            var artifactName = unitArtifact?.Name;
            if (string.IsNullOrWhiteSpace(artifactName))
                return 1;

            foreach (var cfg in units)
            {
                var cfgName = Path.GetFileNameWithoutExtension(cfg.Path);
                if (string.Equals(cfgName, artifactName, StringComparison.OrdinalIgnoreCase))
                    return cfg.InstanceCount;
            }

            return 1;
        }
    }
}
