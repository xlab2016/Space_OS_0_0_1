using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Processor
{
    public class ExecutionBlock : List<Command>
    {
        public override string ToString()
        {
            if (Count == 0) return "ExecutionBlock[0]";
            var sb = new StringBuilder();
            sb.AppendLine($"ExecutionBlock[{Count}]");
            for (int i = 0; i < Count; i++)
                sb.AppendLine($"  [{i}] {this[i]}");
            return sb.ToString().TrimEnd();
        }
    }
}
