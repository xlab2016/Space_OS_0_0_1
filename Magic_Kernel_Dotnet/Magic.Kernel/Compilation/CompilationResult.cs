using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Compilation
{
    public class CompilationResult
    {
        public bool Success { get; set; } = true;
        public ExecutableUnit? Result { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
