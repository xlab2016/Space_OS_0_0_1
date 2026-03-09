using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Interpretation
{
    public class InterpretationResult
    {
        public bool Success { get; set; }
        public InterpretationResultData? Data { get; set; }
    }
}
