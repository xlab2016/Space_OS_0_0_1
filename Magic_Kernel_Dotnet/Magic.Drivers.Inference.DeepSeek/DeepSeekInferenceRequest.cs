using System.Collections.Generic;

namespace Magic.Drivers.Inference.DeepSeek
{
    /// <summary>Typed request payload for <see cref="DeepSeekHttpClient.SendStreamingAsync"/>.</summary>
    public class DeepSeekInferenceRequest
    {
        /// <summary>Data context passed to the model (serialised as JSON inside the &lt;data&gt; XML section).</summary>
        public object? Data { get; set; }

        /// <summary>System-role instruction that defines the model's persona or behaviour.</summary>
        public string? System { get; set; }

        /// <summary>User instruction — the actual request or question sent to the model.</summary>
        public string? Instruction { get; set; }

        /// <summary>Conversation history entries. Each entry should have role/content keys (OpenAI compatible).</summary>
        public List<object?> History { get; set; } = new List<object?>();

        /// <summary>Tool definitions available to the model (reserved).</summary>
        public object? Tools { get; set; }

        /// <summary>Skill definitions available to the model (reserved).</summary>
        public object? Skills { get; set; }
    }
}

