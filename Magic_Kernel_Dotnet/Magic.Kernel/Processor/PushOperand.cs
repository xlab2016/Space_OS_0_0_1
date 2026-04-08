namespace Magic.Kernel.Processor
{
    /// <summary>Operand for Push: slot [n], type literal, int literal, string literal, address literal, or lambda arg.</summary>
    public class PushOperand
    {
        public string Kind { get; set; } = "Slot"; // Slot | Type | IntLiteral | StringLiteral | AddressLiteral | LambdaArg
        public object? Value { get; set; }

        public override string ToString()
        {
            if (Value == null) return $"{Kind}";
            var v = Value.ToString();
            if (v != null && v.Length > 50) v = v.Substring(0, 47) + "...";
            return $"{Kind}({v})";
        }
    }
}
