using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Interpretation
{
    public class InterpretationResultData
    {
        public List<MemoryAddress> Outputs { get; set; } = new List<MemoryAddress>();
    }
}
