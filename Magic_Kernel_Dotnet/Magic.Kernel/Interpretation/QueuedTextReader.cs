using System.Collections.Concurrent;
using System.IO;

namespace Magic.Kernel.Interpretation;

/// <summary>Очередь строк для <see cref="TextReader.ReadLine"/> (stdin интерпретатора).</summary>
public sealed class QueuedTextReader : TextReader
{
    private readonly BlockingCollection<string> _lines = new(new ConcurrentQueue<string>());

    public void EnqueueLine(string? line) => _lines.Add(line ?? string.Empty);

    public override string? ReadLine() => _lines.Take();
}
