using Magic.Kernel.Processor;
using System.Text;

namespace Magic.Kernel.Compilation
{
    public class Procedure : ExecutionBlock
    {
        public override string ToString()
        {
            if (Count == 0) return "Procedure(Compilation)[0]";
            var sb = new StringBuilder();
            sb.AppendLine($"Procedure(Compilation)[{Count}]");
            for (int i = 0; i < Count; i++)
                sb.AppendLine($"  [{i}] {this[i]}");
            return sb.ToString().TrimEnd();
        }
    }
}
