using System.Collections.Generic;

namespace Magic.Drivers.Inference.OpenAI
{
    /// <summary>Typed request payload for <see cref="OpenAIHttpClient.SendStreamingAsync"/>.
    /// Replaces the untyped <c>object? payload</c> dictionary pattern.</summary>
    public class OpenAIInferenceRequest
    {
        /// <summary>Data context passed to the model (serialised as JSON inside the &lt;data&gt; XML section).</summary>
        public object? Data { get; set; }

        /// <summary>System-role instruction that defines the model's persona or behaviour.</summary>
        public string? System { get; set; }

        /// <summary>User instruction — the actual request or question sent to the model.</summary>
        public string? Instruction { get; set; }

        /// <summary>Conversation history entries. Each entry should have <c>role</c> and <c>content</c> keys.</summary>
        public List<object?> History { get; set; } = new List<object?>();

        /// <summary>Tool definitions available to the model. Currently unused (reserved for future use).</summary>
        public object? Tools { get; set; }

        /// <summary>Skill definitions available to the model. Currently unused (reserved for future use).</summary>
        public object? Skills { get; set; }
    }
}
