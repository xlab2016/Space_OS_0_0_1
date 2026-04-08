using System.Diagnostics;
using Magic.Kernel;
using Magic.Kernel.Build;
using Magic.Kernel.Core;
using Magic.Kernel.Core.OS;
using Magic.Kernel.Runtime;
using Magic.Kernel.Terminal.Services;
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
    var debug = debugEl2.ValueKind == System.Text.Json.JsonValueKind.String ? debugEl2.GetString() : null;

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
var runService = new SimulatorRunService();
bool success;

if (runFromConfig)
{
    success = await runService.RunUnitsFromConfigAsync(execHost, executionUnits);
}
else
{
    success = await runService.RunSingleFileAsync(execHost, filePath!, args);
}

if (!success)
{
    Console.Error.WriteLine("Execution failed.");
    Environment.ExitCode = 1;
    return;
}

Console.WriteLine("OK");
Console.ReadLine();