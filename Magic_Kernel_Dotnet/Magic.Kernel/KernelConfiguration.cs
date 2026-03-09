using Magic.Kernel.Compilation;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.SSC;
using Magic.Kernel.Interpretation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel
{
    public class KernelConfiguration
    {
        /// <summary>Deprecated: do not use when units run in parallel. Use ExecutionContext.CurrentUnit (AsyncLocal) instead.</summary>
        public ExecutableUnit? CurrentExecutableUnit { get; set; }

        public ISpaceDisk? DefaultDisk { get; set; }
        public ISSCompiler? DefaultSSCCompiler { get; set; }
        public IInferenceDevice? DefaultInferenceDevice { get; set; }
        public IProjectorDevice? DefaultProjectorDevice { get; set; }

        /// <summary>Optional vault reader for vault_read(); when null, Interpreter uses EnvironmentVaultReader.</summary>
        public IVaultReader? VaultReader { get; set; }
    }
}
