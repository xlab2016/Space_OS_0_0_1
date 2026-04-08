using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Magic.Kernel.Processor
{
    public class Command
    {
        /// <summary>1-based номер строки AGI; 0 — неизвестно/синтетическая команда.</summary>
        public int SourceLine { get; set; }

        /// <summary>Абсолютный путь к .agi, откуда скомпилирована команда (линкованные модули). Не сериализуется.</summary>
        [JsonIgnore]
        public string? SourcePath { get; set; }

        /// <summary>
        /// Логический namespace unit-а компиляции (как у DefName.GetNamespace(ExecutableUnit));
        /// нужен для <c>def</c> с коротким именем типа после <c>use</c>.
        /// </summary>
        [JsonIgnore]
        public string? TypeDefUnitNamespace { get; set; }

        /// <summary>1-based строка в тексте AGIASM (нумерация при компиляции); 0 — не задано. Не сериализуется в .agic.</summary>
        [JsonIgnore]
        public int AsmListingLine { get; set; }

        public object? Operand1 { get; set; }
        public object? Operand2 { get; set; }
        public Opcodes Opcode { get; set; } = Opcodes.Nop;

        public override string ToString()
        {
            var o1 = FormatOperand(Operand1);
            var o2 = FormatOperand(Operand2);
            if (string.IsNullOrEmpty(o2))
                return $"{Opcode}({o1})";
            return $"{Opcode}({o1}, {o2})";
        }

        static string FormatOperand(object? value)
        {
            if (value == null) return "null";
            var s = value.ToString();
            if (s == null) return "null";
            if (s.Length > 60) return s.Substring(0, 57) + "...";
            return s;
        }
    }
}
