using Magic.Kernel.Data;

namespace Magic.Kernel.Devices.Store
{
    /// <summary>Abstraction for runtime database devices (e.g. Postgres, other backends).</summary>
    public interface IDatabaseDevice
    {
        /// <summary>Finds table in runtime database by name.</summary>
        Table? FindTable(DatabaseDevice runtimeDatabase, string tableName);

        /// <summary>Updates or inserts table definition in runtime database.</summary>
        void UpsertTable(DatabaseDevice runtimeDatabase, string tableName, Table table);
    }
}

