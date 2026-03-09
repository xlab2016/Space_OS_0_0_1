using System;
using System.Collections.Generic;

namespace Magic.Kernel.Compilation
{
    /// <summary>Читает исходный код по строкам без прямого доступа парсера к исходнику.</summary>
    public sealed class LineReader
    {
        private readonly List<(string Raw, string Trimmed)> _lines = new List<(string, string)>();

        public LineReader(string sourceCode)
        {
            if (string.IsNullOrEmpty(sourceCode)) return;
            var span = sourceCode.AsSpan();
            int start = 0;
            for (int i = 0; i <= span.Length; i++)
            {
                if (i == span.Length || span[i] == '\n' || span[i] == '\r')
                {
                    var raw = start < i ? sourceCode.Substring(start, i - start) : "";
                    var trimmed = raw.Trim();
                    _lines.Add((raw, trimmed));

                    if (i < span.Length && span[i] == '\r' && i + 1 < span.Length && span[i + 1] == '\n')
                        i++;
                    start = i + 1;
                }
            }
        }

        public int Count => _lines.Count;

        public (string Raw, string Trimmed) GetLine(int index)
        {
            if (index < 0 || index >= _lines.Count)
                return ("", "");
            return _lines[index];
        }

        public IEnumerable<(string Raw, string Trimmed)> GetLines()
        {
            for (int i = 0; i < _lines.Count; i++)
                yield return _lines[i];
        }
    }
}
