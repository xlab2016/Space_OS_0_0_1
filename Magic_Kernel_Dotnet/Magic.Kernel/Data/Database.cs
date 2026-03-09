using Magic.Kernel.Core;
using Magic.Kernel.Devices.Store;
using Magic.Kernel.Interpretation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Data
{
    public class Database : DefType
    {
        public IDatabaseDevice? Device { get; set; }

        public List<Table> Tables { get; set; } = new List<Table>();

        public void AddTable(Table table)
        {
            if (table == null)
                throw new ArgumentNullException(nameof(table));

            table.Database = this;
            Tables.Add(table);
        }

        public override async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";
            if (string.Equals(name, "open", StringComparison.OrdinalIgnoreCase))
            {
                // пока пустой
                return await Task.FromResult<object?>(null).ConfigureAwait(false);
            }
            throw new CallUnknownMethodException(name, this);
        }
    }
}
