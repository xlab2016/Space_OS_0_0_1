using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Processor
{
    public class Command
    {
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
