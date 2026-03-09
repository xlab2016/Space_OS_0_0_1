namespace Magic.Kernel.Compilation.Ast
{
    public class DataParameterNode : ParameterNode
    {
        public string Type { get; set; } = string.Empty;
        public List<string> Types { get; set; } = new List<string>();
        public string Value { get; set; } = string.Empty;
        public string OriginalString { get; set; } = string.Empty; // Для сохранения исходной строки в ошибках
        public bool HasColon { get; set; } = true; // Есть ли двоеточие между типом и значением
    }
}
