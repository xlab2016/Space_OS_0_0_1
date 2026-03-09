using Magic.Kernel.Core.OS;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Runtime
{
    public class RunCommand: OSCommand
    {
        public int? InstanceIndex { get; set; }

        /// <summary>
        /// Number of instances to run when using RunAllInstancesAsync. When set, overrides config-based resolution.
        /// </summary>
        public int? InstanceCount { get; set; }

        public RunType Type { get; set; } = RunType.NewThread;

        /// <summary>
        /// Desired output format for compiled units when running from AGI source.
        /// "agiasm" (default) or "agic".
        /// </summary>
        public string? OutputFormat { get; set; }

        /// <summary>
        /// Optional source path for the artifact (e.g. .agi file) to derive compiled file path.
        /// </summary>
        public string? SourcePath { get; set; }
    }
}
