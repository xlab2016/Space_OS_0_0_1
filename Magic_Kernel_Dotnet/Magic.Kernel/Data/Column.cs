using Magic.Kernel.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Data
{
    public class Column : DefType
    {
        /// <summary>Column type name (e.g. "int", "text").</summary>
        public string Type { get; set; } = "";

        /// <summary>Optional modifiers (e.g. "primary key", "not null").</summary>
        public List<object?> Modifiers { get; set; } = new List<object?>();
    }
}
