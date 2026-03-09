using System.Threading.Tasks;
using Magic.Kernel.Data;
using Magic.Kernel.Devices.Store.Drivers;
using Magic.Kernel.Interpretation;

namespace Magic.Kernel.Devices.Store
{
    /// <summary>PostgreSQL def-type that delegates work to PostgresDriver.</summary>
    public class Postgres : Core.DefType, IDatabaseDevice
    {
        private readonly PostgresDriver _driver = new PostgresDriver();
        private string _connectionString = "";
        private bool _isOpened;

        public Database? Database { get; set; }

        public async Task OpenAsync(string connectionString, Database database)
        {
            await _driver.OpenAsync(connectionString, database).ConfigureAwait(false);
        }

        public async Task EnsureDatabaseAndSchemaAsync(string connectionString, Database database)
        {
            await _driver.EnsureDatabaseAndSchemaAsync(connectionString, database).ConfigureAwait(false);
        }

        public async Task FlushPendingRowsAsync(string connectionString, Database database)
        {
            await _driver.FlushPendingRowsAsync(connectionString, database).ConfigureAwait(false);
        }

        public PostgresDriver Driver => _driver;

        Table? IDatabaseDevice.FindTable(Database runtimeDatabase, string tableName)
        {
            if (runtimeDatabase == null || string.IsNullOrWhiteSpace(tableName))
                return null;

            Database = runtimeDatabase;
            var sanitizedName = SanitizeTableName(tableName);
            return _driver.FindTable(runtimeDatabase, sanitizedName);
        }

        void IDatabaseDevice.UpsertTable(Database runtimeDatabase, string tableName, Table table)
        {
            if (runtimeDatabase == null || string.IsNullOrWhiteSpace(tableName) || table == null)
                return;

            Database = runtimeDatabase;
            var sanitizedName = SanitizeTableName(tableName);
            _driver.UpsertTable(runtimeDatabase, sanitizedName, table);
        }

        public override async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";
            var database = Database ?? throw new InvalidOperationException("Postgres driver is not attached to runtime database.");

            if (string.Equals(name, "open", StringComparison.OrdinalIgnoreCase))
            {
                if (args == null || args.Length == 0 || args[0] is not string connectionString || string.IsNullOrWhiteSpace(connectionString))
                    throw new ArgumentException("Database.open requires connection string as first argument.");
                await OpenAsync(connectionString, database).ConfigureAwait(false);
                _connectionString = connectionString;
                _isOpened = true;
                return this;
            }

            if (string.Equals(name, "ensure", StringComparison.OrdinalIgnoreCase))
            {
                EnsureOpened();
                await EnsureDatabaseAndSchemaAsync(_connectionString, database).ConfigureAwait(false);
                return this;
            }

            if (string.Equals(name, "flush", StringComparison.OrdinalIgnoreCase))
            {
                EnsureOpened();
                await FlushPendingRowsAsync(_connectionString, database).ConfigureAwait(false);
                return this;
            }

            throw new CallUnknownMethodException(name, this);
        }

        public override async Task<object?> AwaitObjAsync()
        {
            if (_isOpened && Database != null)
                await FlushPendingRowsAsync(_connectionString, Database).ConfigureAwait(false);
            return this;
        }

        public override Task<object?> Await() => AwaitObjAsync();

        public override Task<(bool IsEnd, object? Delta, object? Aggregate)> StreamWaitAsync(string streamWaitType)
            => Task.FromResult((true, (object?)null, (object?)null));

        private void EnsureOpened()
        {
            if (!_isOpened || string.IsNullOrWhiteSpace(_connectionString))
                throw new InvalidOperationException("Database is not opened. Call db.open(connectionString) first.");
        }

        private static string SanitizeTableName(string tableName)
            => (tableName ?? string.Empty).Replace("<", string.Empty).Replace(">", string.Empty);
    }
}
