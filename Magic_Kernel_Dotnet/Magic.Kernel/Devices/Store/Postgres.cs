using System.Collections;
using System.Collections.Generic;
using System.Linq;
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

        public DatabaseDevice? Database { get; set; }

        public async Task OpenAsync(string connectionString, DatabaseDevice database)
        {
            _connectionString = await _driver.OpenAsync(connectionString, database).ConfigureAwait(false);
        }

        public async Task EnsureDatabaseAndSchemaAsync(string connectionString, DatabaseDevice database)
        {
            _connectionString = await _driver.EnsureDatabaseAndSchemaAsync(connectionString, database).ConfigureAwait(false);
        }

        public async Task FlushPendingRowsAsync(string connectionString, DatabaseDevice database)
        {
            await _driver.FlushPendingRowsAsync(connectionString, database).ConfigureAwait(false);
        }

        public PostgresDriver Driver => _driver;

        Table? IDatabaseDevice.FindTable(DatabaseDevice runtimeDatabase, string tableName)
        {
            if (runtimeDatabase == null || string.IsNullOrWhiteSpace(tableName))
                return null;

            Database = runtimeDatabase;
            var sanitizedName = SanitizeTableName(tableName);
            return _driver.FindTable(runtimeDatabase, sanitizedName);
        }

        void IDatabaseDevice.UpsertTable(DatabaseDevice runtimeDatabase, string tableName, Table table)
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
                _isOpened = true;
                return this;
            }

            if (string.Equals(name, "close", StringComparison.OrdinalIgnoreCase))
            {
                if (_isOpened && Database != null && !string.IsNullOrWhiteSpace(_connectionString))
                    await FlushPendingRowsAsync(_connectionString, Database).ConfigureAwait(false);
                _isOpened = false;
                _connectionString = "";
                return this;
            }

            if (string.Equals(name, "read", StringComparison.OrdinalIgnoreCase))
            {
                EnsureOpened();
                var sql = TryExtractReadSql(args);
                if (string.IsNullOrWhiteSpace(sql))
                    throw new ArgumentException("Database.read requires { instruction: \"...\" } or a SQL string argument.");
                var rows = await _driver.ReadSqlAsync(_connectionString, database, sql).ConfigureAwait(false);
                return rows;
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

        /// <summary>Returns 1 if table has at least one row matching predicate (ExprTree as SQL WHERE), 0 otherwise. Used by Table.any(lambda).</summary>
        public async Task<long> AnyAsync(Table table, ExprTree whereExpr)
        {
            EnsureOpened();
            var database = Database ?? throw new InvalidOperationException("Postgres is not attached to runtime database.");
            return await _driver.AnyAsync(_connectionString, database, table, whereExpr).ConfigureAwait(false);
        }

        /// <summary>Returns MAX(columnName) over rows matching predicate (ExprTree as SQL WHERE). 0 when no rows.</summary>
        public async Task<long> MaxAsync(Table table, ExprTree? whereExpr, string columnName)
        {
            EnsureOpened();
            var database = Database ?? throw new InvalidOperationException("Postgres is not attached to runtime database.");
            return await _driver.MaxAsync(_connectionString, database, table, whereExpr, columnName).ConfigureAwait(false);
        }

        public async Task<object?> ExecuteQueryAsync(Table table, QueryExpr query)
        {
            EnsureOpened();
            var database = Database ?? throw new InvalidOperationException("Postgres is not attached to runtime database.");
            await FlushPendingRowsAsync(_connectionString, database).ConfigureAwait(false);
            return await _driver.ExecuteQueryAsync(_connectionString, database, table, query).ConfigureAwait(false);
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

        private static string? TryExtractReadSql(object?[]? args)
        {
            if (args == null || args.Length == 0)
                return null;
            var a0 = args[0];
            if (a0 is string s)
                return s;
            if (a0 is IDictionary<string, object?> d0)
            {
                foreach (var kv in d0)
                {
                    if (string.Equals(kv.Key, "instruction", StringComparison.OrdinalIgnoreCase))
                        return kv.Value?.ToString();
                }
            }
            if (a0 is IDictionary<string, object> d1)
            {
                foreach (var kv in d1)
                {
                    if (string.Equals(kv.Key, "instruction", StringComparison.OrdinalIgnoreCase))
                        return kv.Value?.ToString();
                }
            }
            if (a0 is IDictionary raw)
            {
                foreach (DictionaryEntry e in raw)
                {
                    if (e.Key is string k && string.Equals(k, "instruction", StringComparison.OrdinalIgnoreCase))
                        return e.Value?.ToString();
                }
            }

            return null;
        }
    }
}
