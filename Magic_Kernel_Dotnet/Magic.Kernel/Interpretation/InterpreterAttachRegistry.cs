using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Magic.Kernel.Compilation;

namespace Magic.Kernel.Interpretation;

/// <summary>
/// Сопоставляет запущенные корневые интерпретаторы (KernelRuntime) с именем program / путём .agi для attach-отладки из UI.
/// </summary>
public sealed class InterpreterAttachRegistry
{
    private readonly ConcurrentDictionary<string, Interpreter> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Interpreter> _bySourcePath = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string?, string?, int>? InterpreterUnregistered;

    private static string NameKey(string? unitName, int instanceIndex)
        => $"{unitName ?? ""}\u001f{instanceIndex}";

    private static string PathKey(string absolutePath, int instanceIndex)
        => $"path:{Path.GetFullPath(absolutePath)}\u001f{instanceIndex}";

    public void Register(Interpreter interpreter, ExecutableUnit unit)
    {
        var idx = unit.InstanceIndex ?? 0;
        _byName[NameKey(unit.Name, idx)] = interpreter;
        if (!string.IsNullOrWhiteSpace(unit.AttachSourcePath))
        {
            var norm = Path.GetFullPath(unit.AttachSourcePath);
            unit.AttachSourcePath = norm;
            _bySourcePath[PathKey(norm, idx)] = interpreter;
        }
    }

    public void Unregister(ExecutableUnit unit)
    {
        var idx = unit.InstanceIndex ?? 0;
        var name = unit.Name ?? "";
        _byName.TryRemove(NameKey(name, idx), out _);
        string? path = null;
        if (!string.IsNullOrWhiteSpace(unit.AttachSourcePath))
        {
            path = Path.GetFullPath(unit.AttachSourcePath);
            _bySourcePath.TryRemove(PathKey(path, idx), out _);
        }

        InterpreterUnregistered?.Invoke(name, path, idx);
    }

    /// <summary>Подбор по абсолютному пути файла, как в редакторе (перебор индексов экземпляров).</summary>
    public bool TryGetBySourcePath(string absolutePath, int maxInstanceIndexExclusive, out Interpreter? interpreter, out int instanceIndex)
    {
        var norm = Path.GetFullPath(absolutePath);
        for (var i = 0; i < maxInstanceIndexExclusive; i++)
        {
            if (_bySourcePath.TryGetValue(PathKey(norm, i), out var inter))
            {
                interpreter = inter;
                instanceIndex = i;
                return true;
            }
        }

        interpreter = null;
        instanceIndex = -1;
        return false;
    }

    /// <summary>Все зарегистрированные корневые интерпретаторы для пути .agi (индексы 0 .. maxInstanceIndexExclusive-1).</summary>
    public IEnumerable<(int InstanceIndex, Interpreter Interpreter)> EnumerateBySourcePath(string absolutePath, int maxInstanceIndexExclusive)
    {
        var norm = Path.GetFullPath(absolutePath);
        for (var i = 0; i < maxInstanceIndexExclusive; i++)
        {
            if (_bySourcePath.TryGetValue(PathKey(norm, i), out var interp) && interp != null)
                yield return (i, interp);
        }
    }

    /// <summary>Есть ли хотя бы один интерпретатор для данного исходного пути.</summary>
    public bool HasAnyForSourcePath(string absolutePath, int maxInstanceIndexExclusive)
    {
        foreach (var _ in EnumerateBySourcePath(absolutePath, maxInstanceIndexExclusive))
            return true;
        return false;
    }
}
