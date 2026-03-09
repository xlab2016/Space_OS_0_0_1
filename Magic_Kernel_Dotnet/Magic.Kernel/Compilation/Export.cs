using Magic.Kernel.Processor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Compilation
{
    public class Export
    {
        public string Type { get; set; } = string.Empty;
        public ExecutionBlock Source { get; set; } = new ExecutionBlock();
    }
}
