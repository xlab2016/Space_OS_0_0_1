using System.Diagnostics;
using Magic.Kernel;
using Magic.Kernel.Build;
using Magic.Kernel.Core;
using Magic.Kernel.Core.OS;
using Magic.Kernel.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SpaceDb.Services;

static string? GetFirstNonOptionArg(string[] args)
{
    foreach (var a in args)
    {
        if (string.IsNullOrWhiteSpace(a)) continue;
        if (a.StartsWith("-", StringComparison.Ordinal)) continue;
        return a;
    }
    return null;
}

static string? GetOutputFormatFromArgs(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        var a = args[i];
        if (string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-o", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length)
            {
                var val = args[i + 1].Trim().ToLowerInvariant();
                if (val is "agiasm" or "agic") return val;
            }
            return null;
        }
        if (a.StartsWith("--output=", StringComparison.OrdinalIgnoreCase))
        {
            var val = a.Substring(9).Trim().ToLowerInvariant();
            if (val is "agiasm" or "agic") return val;
            return null;
        }
    }
    return null;
}

static long? GetLong(Microsoft.Extensions.Configuration.IConfiguration cfg, string key)
{
    var raw = cfg[key];
    if (string.IsNullOrWhiteSpace(raw)) return null;
    return long.TryParse(raw, out var v) ? v : null;
}

OSSystem.StartKernel();

// Prefer executionUnits from SpaceEnvironment configuration. If present, we ignore CLI file arguments.
var executionUnits = SpaceEnvironment.ExecutionUnits;

string? filePath = null;
var runFromConfig = executionUnits.Count > 0;

if (!runFromConfig)
{
    var debugEl2 = SpaceEnvironment.Configuration.TryGetValue("debug", out var debugEl) ? debugEl : default(System.Text.Json.JsonElement);
    var debug = debugEl2.GetString();

    filePath = GetFirstNonOptionArg(args);
    var isDebugMode = Debugger.IsAttached;
    if (string.IsNullOrEmpty(filePath) && isDebugMode && !string.IsNullOrWhiteSpace(debug))
        filePath = SpaceEnvironment.GetFilePath(debug);

    if (filePath is null || args.Any(a => a is "--help" or "-h" or "/?"))
    {
        Console.WriteLine("Usage: Space.OS.Simulator [options] [file.agi|file.agic|file.agiasm]");
        Console.WriteLine();
        Console.WriteLine("  file.agi    - Source file (will be compiled and executed)");
        Console.WriteLine("  file.agic   - Compiled file binary/JSON (executed directly)");
        Console.WriteLine("  file.agiasm - Compiled file text assembly (executed directly)");
        Console.WriteLine();
        Console.WriteLine("Options (when compiling .agi):");
        Console.WriteLine("  --output agiasm|agic, -o agiasm|agic   Output format (default: agiasm)");
        Console.WriteLine();
        Console.WriteLine("Config:");
        Console.WriteLine("  RocksDb:Path            (or env ROCKSDB_PATH)");
        Console.WriteLine("  SpaceDisk:SpaceName (key prefix; else from program: module|program)");
        Console.WriteLine("  SpaceDisk:VertexSequenceIndex / RelationSequenceIndex / ShapeSequenceIndex");
        return;
    }

    if (!File.Exists(filePath))
    {
        Console.Error.WriteLine($"File not found: {Path.GetFullPath(filePath)}");
        Environment.ExitCode = 2;
        return;
    }
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<MagicKernel>();
builder.Services.AddRocksDb(builder.Configuration);

using var host = builder.Build();

using var scope = host.Services.CreateScope();
var kernel = scope.ServiceProvider.GetRequiredService<MagicKernel>();
var defaultDisk = scope.ServiceProvider.GetRequiredService<RocksDbSpaceDisk>();

kernel.Devices.Add(defaultDisk);
await kernel.StartKernel();

// Apply disk configuration AFTER StartKernel() (it overwrites ISpaceDisk.Configuration).
var spaceNameFromConfig = builder.Configuration["SpaceDisk:SpaceName"];
if (!string.IsNullOrWhiteSpace(spaceNameFromConfig))
    defaultDisk.Configuration.SpaceName = spaceNameFromConfig.Trim();
defaultDisk.Configuration.VertexSequenceIndex ??= GetLong(builder.Configuration, "SpaceDisk:VertexSequenceIndex") ?? 0;
defaultDisk.Configuration.RelationSequenceIndex ??= GetLong(builder.Configuration, "SpaceDisk:RelationSequenceIndex") ?? 0;
defaultDisk.Configuration.ShapeSequenceIndex ??= GetLong(builder.Configuration, "SpaceDisk:ShapeSequenceIndex") ?? 0;

var execHost = new ExecutionUnitHost(kernel);
bool success;

if (runFromConfig)
{
    success = await RunUnitsFromConfigAsync(execHost, executionUnits);
}
else
{
    success = await RunSingleFileAsync(execHost, filePath!, args);
}

if (!success)
{
    Console.Error.WriteLine("Execution failed.");
    Environment.ExitCode = 1;
    return;
}

Console.WriteLine("OK");
Console.ReadLine();

static async Task<bool> RunSingleFileAsync(ExecutionUnitHost execHost, string filePath, string[] args)
{
    var artifact = new Artifact
    {
        Name = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty,
        Namespace = string.Empty
    };

    var extension = Path.GetExtension(filePath).ToLowerInvariant();
    var runCommand = new RunCommand { Type = RunType.NewThread };

    if (extension == ".agic" || extension == ".agiasm")
    {
        if (extension == ".agic")
        {
            artifact.Type = ArtifactType.Agic;
            artifact.Body = filePath;
        }
        else
        {
            artifact.Type = ArtifactType.AgiAsm;
            artifact.Body = await File.ReadAllTextAsync(filePath);
        }

        execHost.UnitArtifact = artifact;
        return await execHost.RunAsync(runCommand);
    }

    if (extension == ".agi")
    {
        var source = await File.ReadAllTextAsync(filePath);
        artifact.Type = ArtifactType.Agi;
        artifact.Body = source;

        runCommand.OutputFormat = GetOutputFormatFromArgs(args) ?? "agiasm";
        runCommand.SourcePath = filePath;

        execHost.UnitArtifact = artifact;
        return await execHost.RunAsync(runCommand);
    }

    Console.Error.WriteLine($"Unsupported file extension: {extension}. Expected .agi, .agic or .agiasm");
    Environment.ExitCode = 2;
    return false;
}

static async Task<bool> RunUnitsFromConfigAsync(ExecutionUnitHost execHost, IReadOnlyList<SpaceEnvironment.ExecutionUnitConfig> executionUnits)
{
    var results = new List<bool>();

    foreach (var unit in executionUnits)
    {
        var fullPath = SpaceEnvironment.GetFilePath(unit.Path);

        if (!File.Exists(fullPath))
        {
            Console.Error.WriteLine($"Configured executionUnit file not found: {Path.GetFullPath(fullPath)}");
            results.Add(false);
            continue;
        }

        var artifact = new Artifact
        {
            Name = Path.GetFileNameWithoutExtension(fullPath) ?? string.Empty,
            Namespace = string.Empty
        };

        var extension = Path.GetExtension(fullPath).ToLowerInvariant();

        if (extension == ".agic" || extension == ".agiasm")
        {
            if (extension == ".agic")
            {
                artifact.Type = ArtifactType.Agic;
                artifact.Body = fullPath;
            }
            else
            {
                artifact.Type = ArtifactType.AgiAsm;
                artifact.Body = await File.ReadAllTextAsync(fullPath);
            }

            execHost.UnitArtifact = artifact;

            var cmd = new RunCommand { Type = RunType.NewThread, InstanceCount = unit.InstanceCount };
            var ok = await execHost.RunAllInstancesAsync(cmd);
            results.Add(ok);

            continue;
        }

        if (extension == ".agi")
        {
            var source = await File.ReadAllTextAsync(fullPath);
            artifact.Type = ArtifactType.Agi;
            artifact.Body = source;

            execHost.UnitArtifact = artifact;

            var cmd = new RunCommand { Type = RunType.NewThread, InstanceCount = unit.InstanceCount };
            var ok = await execHost.RunAllInstancesAsync(cmd);
            results.Add(ok);

            continue;
        }

        Console.Error.WriteLine($"Unsupported file extension in executionUnits config: {extension}. Expected .agi, .agic or .agiasm");
        results.Add(false);
    }

    return results.Count > 0 && results.All(r => r);
}