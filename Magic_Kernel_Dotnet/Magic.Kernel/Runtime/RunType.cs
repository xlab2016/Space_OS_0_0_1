using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Runtime
{
    public enum RunType: int
    {
        Undefined = 0,
        NewThread = 1,
        NewProcess = 2,
        Remote = 3,
        DockerHosted = 4,
        KubernetesHosted = 5,
    }
}
