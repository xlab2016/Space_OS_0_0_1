using System.Text;

namespace Magic.Kernel.Processor
{
    public class Procedure
    {
        public string Name { get; set; } = string.Empty;
        public ExecutionBlock Body { get; set; } = new ExecutionBlock();

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Procedure({Name})");
            sb.Append(Body.Count == 0 ? "  (empty)" : Body.ToString().Replace("\n", "\n  "));
            return sb.ToString().TrimEnd();
        }
    }
}
