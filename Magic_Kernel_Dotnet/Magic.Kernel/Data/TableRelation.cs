namespace Magic.Kernel.Data
{
    /// <summary>Schema-level relation between two database tables.</summary>
    public class TableRelation
    {
        public string Name { get; set; } = "";
        public string ReferencedTableType { get; set; } = "";
        public bool IsArray { get; set; }
    }
}
