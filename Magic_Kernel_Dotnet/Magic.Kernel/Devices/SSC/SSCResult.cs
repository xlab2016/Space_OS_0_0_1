using System;
using System.Collections.Generic;

namespace Magic.Kernel.Devices.SSC
{
    /// <summary>Result of stream compilation: vertices and relations created, success flag.</summary>
    public class SSCResult
    {
        public bool IsSuccess { get; set; }
        public string? ErrorMessage { get; set; }
        /// <summary>Vertex indices created (paragraphs then sentences by position).</summary>
        public List<long> VertexIndices { get; set; } = new List<long>();
        /// <summary>Relation indices created (paragraph → sentence).</summary>
        public List<long> RelationIndices { get; set; } = new List<long>();
    }
}
