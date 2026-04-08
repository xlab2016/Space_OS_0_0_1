using System.Collections.Generic;
using Magic.Kernel.Processor;

namespace Magic.Kernel.Compilation;

/// <summary>
/// Нумерация строк листинга AGIASM на <see cref="Command"/> — тот же порядок, что <see cref="ExecutableUnitTextSerializer"/>.
/// </summary>
public static class ExecutableUnitAsmListing
{
    public static void StampListingLineNumbers(ExecutableUnit unit)
    {
        var line = 1;
        line++; // @AGIASM 1
        line++; // @AGI …
        if (!string.IsNullOrEmpty(unit.Name))
            line++;
        if (!string.IsNullOrEmpty(unit.Module))
            line++;

        foreach (var kv in unit.Procedures)
        {
            line++; // procedure name
            foreach (var cmd in kv.Value.Body ?? new ExecutionBlock())
                cmd.AsmListingLine = line++;
        }

        foreach (var kv in unit.Functions)
        {
            line++; // function name
            foreach (var cmd in kv.Value.Body ?? new ExecutionBlock())
                cmd.AsmListingLine = line++;
        }

        line++; // entrypoint
        foreach (var cmd in unit.EntryPoint ?? new ExecutionBlock())
            cmd.AsmListingLine = line++;
    }
}
