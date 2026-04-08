using Magic.Kernel.Compilation;
using Magic.Kernel.Core.OS;
using Magic.Kernel.Devices;
using Magic.Kernel.Interpretation;
using Magic.Kernel.Runtime;

namespace Magic.Kernel
{
    public class MagicKernel
    {
        public KernelConfiguration Configuration { get; } = new KernelConfiguration();

        public KernelMemory Memory { get; } = new KernelMemory();
        public Compiler Compiler { get; } = new Compiler();

        public List<IDevice> Devices { get; } = new List<IDevice>();

        /// <summary>Корневые интерпретаторы в Runtime для attach из Terminal.</summary>
        public InterpreterAttachRegistry AttachRegistry { get; } = new InterpreterAttachRegistry();

        /// <summary>Erlang-like runtime: TaskQueue → Scheduler → ThreadPool. Started in StartKernel.</summary>
        public KernelRuntime Runtime { get; }

        public MagicKernel()
        {
            Runtime = new KernelRuntime(this);
        }

        internal Interpreter CreateInterpreter()
        {
            return new Interpreter
            {
                Configuration = Configuration
            };
        }

        public async Task StartKernel()
        {
            foreach (var device in Devices)
            {
                if (device is ISpaceDisk spaceDisk)
                {
                    Configuration.DefaultDisk = spaceDisk;
                    spaceDisk.Configuration = new SpaceDiskConfiguration();

                    foreach (var item in Devices)
                    {
                        if (item is IProjectorDevice projector && await spaceDisk.IsCapableAsync(projector))
                            spaceDisk.Configuration.Projector = projector;
                    }
                    break;
                }
            }

            foreach (var device in Devices)
            {
                if (device is IInferenceDevice inferenceDevice)
                {
                    Configuration.DefaultInferenceDevice = inferenceDevice;
                    break;
                }
            }

            foreach (var device in Devices)
            {
                if (device is IProjectorDevice)
                {
                    Configuration.DefaultProjectorDevice = device as IProjectorDevice;
                    break;
                }
            }

            Configuration.Runtime = Runtime;
            Runtime.Start();
        }

        public async Task<List<CompilationResult>> CompileDirectoryAsync(string directoryPath)
        {
            return await Compiler.CompileDirectoryAsync(directoryPath);
        }

        public async Task<CompilationResult> CompileAsync(string sourceCode)
        {
            return await Compiler.CompileAsync(sourceCode);
        }

        public async Task<CompilationResult> CompileAsync(string sourceCode, string? sourcePath)
        {
            return await Compiler.CompileAsync(sourceCode, sourcePath);
        }

        public async Task<InterpretationResult> InterpreteCompiledFileAsync(string compiledPath)
        {
            var executableUnit = await ExecutableUnit.LoadAsync(compiledPath);
            return await InterpreteAsync(executableUnit);
        }

        public async Task<InterpretationResult> InterpreteCompiledRootFileAsync(string compiledPath)
        {
            var executableUnit = await ExecutableUnit.LoadAsync(compiledPath);
            return await InterpreteRootAsync(executableUnit);
        }

        public async Task<List<InterpretationResult>> InterpreteCompiledDirectoryAsync(string directoryPath)
        {
            var results = new List<InterpretationResult>();
            var compiledFiles = Directory.GetFiles(directoryPath, "*.agic", SearchOption.AllDirectories);
            foreach (var file in compiledFiles)
            {
                var result = await InterpreteCompiledFileAsync(file);
                results.Add(result);
            }
            return results;
        }

        public async Task<InterpretationResult> InterpreteSourceCodeAsync(string sourceCode)
        {
            var compilationResult = await CompileAsync(sourceCode);
            if (!compilationResult.Success)
            {
                return new InterpretationResult
                {
                    Success = false
                };
            }
            return await InterpreteAsync(compilationResult.Result);
        }

        public async Task<InterpretationResult> InterpreteAsync(ExecutableUnit executableUnit)
        {
            if (executableUnit == null)
                throw new ArgumentNullException(nameof(executableUnit));

            var interpreter = CreateInterpreter();
            return await interpreter.InterpreteAsync(executableUnit);
        }

        public async Task<InterpretationResult> InterpreteRootAsync(ExecutableUnit executableUnit)
        {
            if (executableUnit == null)
                throw new ArgumentNullException(nameof(executableUnit));

            if (Configuration.Runtime != null)
                return await Runtime.SpawnAndWaitAsync(executableUnit).ConfigureAwait(false);

            return await InterpreteAsync(executableUnit).ConfigureAwait(false);
        }
    }
}