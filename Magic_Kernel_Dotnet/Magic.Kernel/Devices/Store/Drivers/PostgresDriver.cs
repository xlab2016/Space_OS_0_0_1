using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Magic.Kernel.Interpretation;
using Npgsql;
using NpgsqlTypes;

namespace Magic.Kernel.Devices.Store.Drivers
{
    /// <summary>Low-level PostgreSQL driver operations for runtime database.</summary>
    public class PostgresDriver
    {
        /// <summary>Upper bound on estimated payload returned by <see cref="ReadSqlAsync"/> (per query).</summary>
        private const long MaxReadSqlResultBytes = 200L * 1024 * 1024;

        private static long EstimateSqlValueBytes(object? value)
        {
            if (value == null || value is DBNull)
                return 0;

            switch (value)
            {
                case string s:
                    return Encoding.UTF8.GetByteCount(s);
                case char[] chars:
                    return Encoding.UTF8.GetByteCount(chars);
                case byte[] bytes:
                    return bytes.Length;
                case bool _:
                    return sizeof(bool);
                case byte _:
                case sbyte _:
                    return 1;
                case short _:
                case ushort _:
                    return sizeof(short);
                case char _:
                    return sizeof(char);
                case int _:
                case uint _:
                    return sizeof(int);
                case long _:
                case ulong _:
                    return sizeof(long);
                case float _:
                    return sizeof(float);
                case double _:
                    return sizeof(double);
                case decimal _:
                    return sizeof(decimal);
                case DateTime _:
                    return sizeof(long);
                case DateTimeOffset _:
                    return 16;
                case TimeSpan _:
                    return sizeof(long);
                case Guid _:
                    return 16;
                default:
                {
                    var t = value.GetType();
                    if (t.IsArray && t.GetElementType()?.IsPrimitive == true && value is Array primitiveArr)
                        return Buffer.ByteLength(primitiveArr);
                    var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.ToString() ?? "";
                    return Encoding.UTF8.GetByteCount(text);
                }
            }
        }

        public Data.Database? ResolveSchema(DatabaseDevice database)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            foreach (var g in database.Generalizations)
            {
                if (g is Data.Database schema)
                    return schema;
            }

            return null;
        }

        public Data.Table? FindTable(DatabaseDevice database, string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                return null;

            var schema = ResolveSchema(database);
            if (schema == null)
                return null;

            foreach (var table in schema.Tables)
            {
                if (IsSameTableName(table.Name, tableName))
                    return table;
            }

            return null;
        }

        public void UpsertTable(DatabaseDevice database, string tableName, Data.Table table)
        {
            if (string.IsNullOrWhiteSpace(tableName) || table == null)
                return;

            var schema = ResolveSchema(database);
            if (schema == null)
                return;

            for (var i = 0; i < schema.Tables.Count; i++)
            {
                if (!IsSameTableName(schema.Tables[i].Name, tableName))
                    continue;
                table.Database = schema;
                schema.Tables[i] = table;
                return;
            }

            table.Database = schema;
            schema.AddTable(table);
        }

        public Task<string> OpenAsync(string connectionString, DatabaseDevice database)
            => EnsureDatabaseAndSchemaAsync(connectionString, database);

        /// <summary>Ensures the database exists; applies table DDL only when a <see cref="Data.Database"/> schema is attached.</summary>
        /// <returns>Normalized connection string (including resolved <c>Database=</c> when it was omitted).</returns>
        public async Task<string> EnsureDatabaseAndSchemaAsync(string connectionString, DatabaseDevice database)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));
            var schema = ResolveSchema(database);
            var targetBuilder = new NpgsqlConnectionStringBuilder(connectionString);
            var dbName = schema != null
                ? ResolveDatabaseName(targetBuilder, schema, database.Name)
                : ResolveDatabaseNameWithoutSchema(targetBuilder, database.Name);
            if (!string.IsNullOrEmpty(dbName))
                targetBuilder.Database = dbName;
            var normalizedConnectionString = targetBuilder.ConnectionString;

            await EnsureDatabaseExistsAsync(targetBuilder, dbName).ConfigureAwait(false);
            if (schema != null)
                await ApplySchemaAsync(normalizedConnectionString, schema).ConfigureAwait(false);
            return normalizedConnectionString;
        }

        /// <summary>Runs arbitrary SQL (typically SELECT) and returns rows as a list of column dictionaries.</summary>
        public async Task<List<Dictionary<string, object?>>> ReadSqlAsync(string connectionString, DatabaseDevice database, string sql)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));
            if (string.IsNullOrWhiteSpace(sql))
                throw new ArgumentException("SQL is empty.", nameof(sql));

            var targetBuilder = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(targetBuilder.Database))
            {
                var schema = ResolveSchema(database);
                var dbName = schema != null
                    ? ResolveDatabaseName(targetBuilder, schema, database.Name)
                    : ResolveDatabaseNameWithoutSchema(targetBuilder, database.Name);
                if (!string.IsNullOrEmpty(dbName))
                    targetBuilder.Database = dbName;
            }

            await using var conn = new NpgsqlConnection(targetBuilder.ConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            var rows = new List<Dictionary<string, object?>>();
            long totalBytes = 0;
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                long rowBytes = 0;
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    rowBytes += Encoding.UTF8.GetByteCount(name);
                    var cell = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    row[name] = cell;
                    rowBytes += EstimateSqlValueBytes(cell);
                }

                if (totalBytes + rowBytes > MaxReadSqlResultBytes)
                {
                    throw new InvalidOperationException(
                        $"ReadSqlAsync: result size would exceed {MaxReadSqlResultBytes} bytes (approximate). Narrow the query or use LIMIT.");
                }

                totalBytes += rowBytes;
                rows.Add(row);
            }

            return rows;
        }

        /// <summary>Returns 1 if at least one row in table matches the predicate (ExprTree translated to PostgreSQL WHERE by driver visitor), 0 otherwise.</summary>
        public async Task<long> AnyAsync(string connectionString, DatabaseDevice database, Data.Table table, ExprTree whereExpr)
        {
            if (database == null || table == null || whereExpr == null)
                throw new ArgumentNullException(database == null ? nameof(database) : table == null ? nameof(table) : nameof(whereExpr));
            var schema = ResolveSchema(database);
            if (schema == null)
                return 0;
            var normalizedTableName = NormalizeTableName(table.Name);
            if (string.IsNullOrWhiteSpace(normalizedTableName))
                return 0;
            var targetBuilder = new NpgsqlConnectionStringBuilder(connectionString);
            targetBuilder.Database = ResolveDatabaseName(targetBuilder, schema, database.Name);
            await using var conn = new NpgsqlConnection(targetBuilder.ConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            var (whereClause, parameters) = PostgresWhereVisitor.BuildWhere(whereExpr);
            var sql = string.IsNullOrEmpty(whereClause)
                ? $"SELECT 1 FROM {QuoteIdent(normalizedTableName)} LIMIT 1;"
                : $"SELECT 1 FROM {QuoteIdent(normalizedTableName)} WHERE {whereClause} LIMIT 1;";
            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            var scalar = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return scalar != null && scalar != DBNull.Value ? 1L : 0L;
        }

        /// <summary>Returns MAX(columnName) for rows matching predicate; 0 when table is empty or no matching rows.</summary>
        public async Task<long> MaxAsync(string connectionString, DatabaseDevice database, Data.Table table, ExprTree? whereExpr, string columnName)
        {
            if (database == null || table == null)
                throw new ArgumentNullException(database == null ? nameof(database) : nameof(table));
            if (string.IsNullOrWhiteSpace(columnName))
                throw new ArgumentException("Column name is empty.", nameof(columnName));

            var schema = ResolveSchema(database);
            if (schema == null)
                return 0;

            var normalizedTableName = NormalizeTableName(table.Name);
            if (string.IsNullOrWhiteSpace(normalizedTableName))
                return 0;

            var targetBuilder = new NpgsqlConnectionStringBuilder(connectionString);
            targetBuilder.Database = ResolveDatabaseName(targetBuilder, schema, database.Name);
            await using var conn = new NpgsqlConnection(targetBuilder.ConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            var (whereClause, parameters) = PostgresWhereVisitor.BuildWhere(whereExpr);
            var quotedColumn = QuoteIdent(columnName);
            var sql = string.IsNullOrEmpty(whereClause)
                ? $"SELECT MAX({quotedColumn}) FROM {QuoteIdent(normalizedTableName)};"
                : $"SELECT MAX({quotedColumn}) FROM {QuoteIdent(normalizedTableName)} WHERE {whereClause};";

            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

            var scalar = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            if (scalar == null || scalar == DBNull.Value)
                return 0L;

            return scalar switch
            {
                long l => l,
                int i => i,
                short s => s,
                _ => Convert.ToInt64(scalar, CultureInfo.InvariantCulture)
            };
        }

        public async Task<object?> ExecuteQueryAsync(string connectionString, DatabaseDevice database, Data.Table table, QueryExpr query)
        {
            if (database == null || table == null || query == null)
                throw new ArgumentNullException(database == null ? nameof(database) : table == null ? nameof(table) : nameof(query));

            var schema = ResolveSchema(database);
            if (schema == null)
                return table;

            var sourceTable = query.SourceTable;
            var calls = QueryExpr.Decompose(query.Root);

            var plan = BuildQueryPlan(sourceTable, calls);
            var normalizedTableName = NormalizeTableName(sourceTable.Name);
            if (string.IsNullOrWhiteSpace(normalizedTableName))
                return sourceTable;

            var targetBuilder = new NpgsqlConnectionStringBuilder(connectionString);
            targetBuilder.Database = ResolveDatabaseName(targetBuilder, schema, database.Name);
            await using var conn = new NpgsqlConnection(targetBuilder.ConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            return plan.Kind switch
            {
                QueryResultKind.Table => await QueryTableAsync(conn, sourceTable, normalizedTableName, plan.FilterExpr).ConfigureAwait(false),
                QueryResultKind.Any => await QueryAnyAsync(conn, normalizedTableName, plan.FilterExpr).ConfigureAwait(false),
                QueryResultKind.Find => await QueryFindAsync(conn, normalizedTableName, plan.FilterExpr).ConfigureAwait(false),
                QueryResultKind.Max => await QueryMaxAsync(conn, normalizedTableName, plan.FilterExpr, plan.ScalarColumnName!).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unsupported query result kind: {plan.Kind}.")
            };
        }

        public async Task FlushPendingRowsAsync(string connectionString, DatabaseDevice database)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));
            var schema = ResolveSchema(database);
            if (schema == null || schema.Tables.Count == 0)
                return;

            var targetBuilder = new NpgsqlConnectionStringBuilder(connectionString);
            targetBuilder.Database = ResolveDatabaseName(targetBuilder, schema, database.Name);

            await using var conn = new NpgsqlConnection(targetBuilder.ConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);

            try
            {
                foreach (var table in schema.Tables)
                {
                    var rows = table.ConsumePendingRows();
                    foreach (var row in rows)
                    {
                        if (IsUpsertRow(row))
                            await UpsertRowAsync(conn, tx, table, row).ConfigureAwait(false);
                        else
                            await InsertRowAsync(conn, tx, table, row).ConfigureAwait(false);
                    }
                }

                await tx.CommitAsync().ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync().ConfigureAwait(false);
                throw;
            }
        }

        private static async Task EnsureDatabaseExistsAsync(NpgsqlConnectionStringBuilder targetBuilder, string databaseName)
        {
            var adminBuilder = new NpgsqlConnectionStringBuilder(targetBuilder.ConnectionString)
            {
                Database = "postgres"
            };

            await using var conn = new NpgsqlConnection(adminBuilder.ConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            await using var existsCmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @dbName LIMIT 1;", conn);
            existsCmd.Parameters.AddWithValue("dbName", databaseName);
            var exists = await existsCmd.ExecuteScalarAsync().ConfigureAwait(false);
            if (exists != null)
                return;

            var createSql = $"CREATE DATABASE {QuoteIdent(databaseName)};";
            await using var createCmd = new NpgsqlCommand(createSql, conn);
            await createCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task ApplySchemaAsync(string connectionString, Data.Database schema)
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            foreach (var table in schema.Tables)
            {
                var createTableSql = BuildCreateTableSql(table);
                await using var cmd = new NpgsqlCommand(createTableSql, conn);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            foreach (var table in schema.Tables)
                await SyncTableColumnsAsync(conn, schema, table).ConfigureAwait(false);

            await SyncIndexesAsync(conn, schema).ConfigureAwait(false);
            await SyncForeignKeysAsync(conn, schema).ConfigureAwait(false);
        }

        private static async Task SyncTableColumnsAsync(NpgsqlConnection conn, Data.Database schema, Data.Table table)
        {
            var normalizedTableName = NormalizeTableName(table.Name);
            if (string.IsNullOrWhiteSpace(normalizedTableName))
                return;

            var existingColumns = await LoadTableColumnsAsync(conn, normalizedTableName).ConfigureAwait(false);
            var hasPrimaryKey = await HasPrimaryKeyAsync(conn, normalizedTableName).ConfigureAwait(false);

            foreach (var column in EnumerateDesiredColumnsForTable(schema, table))
            {
                if (string.IsNullOrWhiteSpace(column.Name))
                    continue;

                var desiredType = ResolveColumnSqlType(column.Type, column.Modifiers);
                var desiredNullable = IsColumnNullable(column);
                var desiredPrimaryKey = IsPrimaryKey(column);

                if (!existingColumns.TryGetValue(column.Name, out var current))
                {
                    var addSql = $"ALTER TABLE {QuoteIdent(normalizedTableName)} ADD COLUMN {BuildColumnSql(column)};";
                    await using var addCmd = new NpgsqlCommand(addSql, conn);
                    await addCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    if (desiredPrimaryKey && !hasPrimaryKey)
                    {
                        await AddPrimaryKeyAsync(conn, normalizedTableName, column.Name).ConfigureAwait(false);
                        hasPrimaryKey = true;
                    }

                    continue;
                }

                if (!IsSameSqlType(current.Type, desiredType))
                {
                    var alterTypeSql = $"ALTER TABLE {QuoteIdent(normalizedTableName)} ALTER COLUMN {QuoteIdent(column.Name)} TYPE {desiredType} USING {QuoteIdent(column.Name)}::{desiredType};";
                    await using var alterTypeCmd = new NpgsqlCommand(alterTypeSql, conn);
                    await alterTypeCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                if (current.IsNullable != desiredNullable)
                {
                    var nullabilitySql = desiredNullable
                        ? $"ALTER TABLE {QuoteIdent(normalizedTableName)} ALTER COLUMN {QuoteIdent(column.Name)} DROP NOT NULL;"
                        : $"ALTER TABLE {QuoteIdent(normalizedTableName)} ALTER COLUMN {QuoteIdent(column.Name)} SET NOT NULL;";
                    await using var nullabilityCmd = new NpgsqlCommand(nullabilitySql, conn);
                    await nullabilityCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                if (desiredPrimaryKey && !hasPrimaryKey)
                {
                    await AddPrimaryKeyAsync(conn, normalizedTableName, column.Name).ConfigureAwait(false);
                    hasPrimaryKey = true;
                }
            }
        }

        private static async Task<Dictionary<string, TableColumnInfo>> LoadTableColumnsAsync(NpgsqlConnection conn, string tableName)
        {
            var result = new Dictionary<string, TableColumnInfo>(StringComparer.OrdinalIgnoreCase);

            const string sql = @"
SELECT
    c.column_name,
    c.is_nullable,
    c.udt_name,
    c.character_maximum_length
FROM information_schema.columns c
WHERE c.table_schema = current_schema()
  AND c.table_name = @tableName;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("tableName", tableName);
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var isNullable = string.Equals(reader.GetString(1), "YES", StringComparison.OrdinalIgnoreCase);
                var udtName = reader.GetString(2);
                var maxLength = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
                result[name] = new TableColumnInfo(name, NormalizeActualType(udtName, maxLength), isNullable);
            }

            return result;
        }

        private static async Task<bool> HasPrimaryKeyAsync(NpgsqlConnection conn, string tableName)
        {
            const string sql = @"
SELECT EXISTS (
    SELECT 1
    FROM pg_constraint c
    JOIN pg_class t ON t.oid = c.conrelid
    JOIN pg_namespace n ON n.oid = t.relnamespace
    WHERE c.contype = 'p'
      AND n.nspname = current_schema()
      AND t.relname = @tableName
);";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("tableName", tableName);
            var scalar = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return scalar is bool b && b;
        }

        private static async Task AddPrimaryKeyAsync(NpgsqlConnection conn, string tableName, string columnName)
        {
            var constraintName = $"pk_{SanitizeIdentifier(tableName)}";
            var sql = $"ALTER TABLE {QuoteIdent(tableName)} ADD CONSTRAINT {QuoteIdent(constraintName)} PRIMARY KEY ({QuoteIdent(columnName)});";
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static bool IsSameSqlType(string actualType, string desiredType)
            => string.Equals(NormalizeTypeToken(actualType), NormalizeTypeToken(desiredType), StringComparison.Ordinal);

        private static string NormalizeActualType(string udtName, int? maxLength)
        {
            var normalized = udtName.ToLowerInvariant() switch
            {
                "int2" => "smallint",
                "int4" => "integer",
                "int8" => "bigint",
                "float4" => "real",
                "float8" => "double precision",
                "bool" => "boolean",
                "timestamp" => "timestamp",
                "jsonb" => "jsonb",
                "varchar" => maxLength.HasValue ? $"varchar({maxLength.Value})" : "varchar",
                _ => udtName.ToLowerInvariant()
            };
            return NormalizeTypeToken(normalized);
        }

        private static string NormalizeTypeToken(string typeName)
        {
            var t = (typeName ?? "").Trim().ToLowerInvariant();
            if (t == "character varying")
                return "varchar";
            return t;
        }

        private static bool IsColumnNullable(Data.Column column)
        {
            foreach (var modifierObj in column.Modifiers)
            {
                var modifier = modifierObj?.ToString()?.Trim() ?? "";
                if (!modifier.StartsWith("nullable:", StringComparison.OrdinalIgnoreCase))
                    continue;
                return modifier.EndsWith("1", StringComparison.Ordinal);
            }
            return true;
        }

        private static bool IsPrimaryKey(Data.Column column)
        {
            foreach (var modifierObj in column.Modifiers)
            {
                var modifier = modifierObj?.ToString()?.Trim() ?? "";
                if (string.Equals(modifier, "primary key", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsIndexed(Data.Column column)
        {
            foreach (var modifierObj in column.Modifiers)
            {
                var modifier = modifierObj?.ToString()?.Trim() ?? "";
                if (string.Equals(modifier, "index", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string SanitizeIdentifier(string value)
        {
            var source = value ?? "";
            var sb = new StringBuilder(source.Length);
            foreach (var ch in source)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_')
                    sb.Append(char.ToLowerInvariant(ch));
                else
                    sb.Append('_');
            }
            return sb.Length == 0 ? "table" : sb.ToString();
        }

        private static string NormalizeTableName(string? value)
        {
            var name = StripTableContextSuffixes(value);
            return ToPlural(name);
        }

        private static string StripTableContextSuffixes(string? value)
        {
            var name = (value ?? "").Trim();
            if (name.EndsWith("[]", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - 2).Trim();
            if (name.EndsWith("?", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - 1).Trim();
            if (name.EndsWith("<>", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - 2).Trim();
            return name;
        }

        /// <summary>Pluralizes table name: Message => Messages, Category => Categories, Box => Boxes.</summary>
        private static string ToPlural(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return name;
            var s = name.Trim();
            if (s.Length == 0)
                return name;
            // consonant + y => ies
            if (s.Length >= 2 && (s[s.Length - 1] == 'y' || s[s.Length - 1] == 'Y'))
            {
                var c = s[s.Length - 2];
                if (!IsVowel(c))
                    return s.Substring(0, s.Length - 1) + "ies";
            }
            // s, x, z, ch, sh => es
            if (s.EndsWith("ch", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith("sh", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith("s", StringComparison.Ordinal) ||
                s.EndsWith("x", StringComparison.Ordinal) ||
                s.EndsWith("z", StringComparison.Ordinal))
                return s + "es";
            return s + "s";
        }

        private static bool IsVowel(char c)
        {
            var lower = char.ToLowerInvariant(c);
            return lower == 'a' || lower == 'e' || lower == 'i' || lower == 'o' || lower == 'u';
        }

        private static bool IsSameTableName(string? left, string? right)
            => string.Equals(NormalizeTableName(left), NormalizeTableName(right), StringComparison.OrdinalIgnoreCase);

        private sealed record TableColumnInfo(string Name, string Type, bool IsNullable);
        private sealed record TableRelationSpec(
            string OwnerTableName,
            string ReferencedTableName,
            string FkColumnName,
            string ReferencedPkName,
            string FkColumnType,
            string ConstraintName);

        private static async Task InsertRowAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Data.Table table, Dictionary<string, object?> row)
        {
            var normalizedTableName = NormalizeTableName(table.Name);
            if (string.IsNullOrWhiteSpace(normalizedTableName))
                return;

            var allowedColumns = table.Database != null
                ? EnumerateDesiredColumnsForTable(table.Database, table).ToList()
                : table.Columns;
            var selectedColumns = new List<Data.Column>();
            var values = new List<object?>();

            foreach (var column in allowedColumns)
            {
                if (string.IsNullOrWhiteSpace(column.Name))
                    continue;
                if (!TryGetCaseInsensitive(row, column.Name, out var value))
                    continue;
                selectedColumns.Add(column);
                values.Add(NormalizeDbValue(value, column.Type));
            }

            string sql;
            if (selectedColumns.Count == 0)
            {
                sql = $"INSERT INTO {QuoteIdent(normalizedTableName)} DEFAULT VALUES;";
                await using var emptyCmd = new NpgsqlCommand(sql, conn, tx);
                await emptyCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                return;
            }

            var cols = new StringBuilder();
            var vals = new StringBuilder();
            for (var i = 0; i < selectedColumns.Count; i++)
            {
                if (i > 0)
                {
                    cols.Append(", ");
                    vals.Append(", ");
                }
                cols.Append(QuoteIdent(selectedColumns[i].Name));
                vals.Append("@p").Append(i);
            }

            sql = $"INSERT INTO {QuoteIdent(normalizedTableName)} ({cols}) VALUES ({vals});";
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            for (var i = 0; i < values.Count; i++)
            {
                var colType = (selectedColumns[i].Type ?? "").Trim().ToLowerInvariant();
                var param = cmd.Parameters.AddWithValue($"p{i}", values[i] ?? DBNull.Value);

                if (colType is "json" or "jsonb")
                {
                    param.NpgsqlDbType = NpgsqlDbType.Jsonb;
                }
            }
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task UpsertRowAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Data.Table table, Dictionary<string, object?> row)
        {
            if (!TryGetUpsertKey(table, row, out var keyColumn, out var keyValue))
            {
                await InsertRowAsync(conn, tx, table, row).ConfigureAwait(false);
                return;
            }

            var normalizedTableName = NormalizeTableName(table.Name);
            if (string.IsNullOrWhiteSpace(normalizedTableName))
                return;

            var selectedColumns = new List<Data.Column>();
            var values = new List<object?>();
            var upsertColumns = table.Database != null
                ? EnumerateDesiredColumnsForTable(table.Database, table)
                : table.Columns;
            foreach (var column in upsertColumns)
            {
                if (string.IsNullOrWhiteSpace(column.Name) ||
                    string.Equals(column.Name, keyColumn, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!TryGetCaseInsensitive(row, column.Name, out var value))
                    continue;
                selectedColumns.Add(column);
                values.Add(NormalizeDbValue(value, column.Type));
            }

            if (selectedColumns.Count == 0)
                return;

            var setClause = new StringBuilder();
            for (var i = 0; i < selectedColumns.Count; i++)
            {
                if (i > 0)
                    setClause.Append(", ");
                setClause.Append(QuoteIdent(selectedColumns[i].Name)).Append(" = @p").Append(i);
            }

            var sql = $"UPDATE {QuoteIdent(normalizedTableName)} SET {setClause} WHERE {QuoteIdent(keyColumn)} = @key;";
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            for (var i = 0; i < values.Count; i++)
            {
                var colType = (selectedColumns[i].Type ?? "").Trim().ToLowerInvariant();
                var param = cmd.Parameters.AddWithValue($"p{i}", values[i] ?? DBNull.Value);
                if (colType is "json" or "jsonb")
                    param.NpgsqlDbType = NpgsqlDbType.Jsonb;
            }

            cmd.Parameters.AddWithValue("key", keyValue);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static string BuildCreateTableSql(Data.Table table)
        {
            var normalizedTableName = NormalizeTableName(table.Name);
            if (string.IsNullOrWhiteSpace(normalizedTableName))
                throw new InvalidOperationException("Table name is empty.");

            var defs = new List<string>();
            foreach (var column in table.Columns)
                defs.Add(BuildColumnSql(column));
            var body = defs.Count == 0 ? "" : string.Join(", ", defs);
            return $"CREATE TABLE IF NOT EXISTS {QuoteIdent(normalizedTableName)} ({body});";
        }

        private static IEnumerable<Data.Column> EnumerateDesiredColumnsForTable(Data.Database schema, Data.Table targetTable)
        {
            var produced = new Dictionary<string, Data.Column>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in targetTable.Columns)
            {
                if (string.IsNullOrWhiteSpace(c.Name))
                    continue;
                produced[c.Name] = c;
            }

            var targetName = NormalizeTableName(targetTable.Name);
            foreach (var rel in BuildRelationSpecs(schema))
            {
                if (!string.Equals(rel.OwnerTableName, targetName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (produced.ContainsKey(rel.FkColumnName))
                    continue;

                produced[rel.FkColumnName] = new Data.Column
                {
                    Name = rel.FkColumnName,
                    Type = rel.FkColumnType,
                    Modifiers = new List<object?> { "nullable:1" }
                };
            }

            return produced.Values;
        }

        private static async Task SyncForeignKeysAsync(NpgsqlConnection conn, Data.Database schema)
        {
            foreach (var rel in BuildRelationSpecs(schema))
            {
                if (string.IsNullOrWhiteSpace(rel.OwnerTableName) ||
                    string.IsNullOrWhiteSpace(rel.ReferencedTableName) ||
                    string.IsNullOrWhiteSpace(rel.FkColumnName) ||
                    string.IsNullOrWhiteSpace(rel.ReferencedPkName))
                {
                    continue;
                }

                var fkSql = $@"
ALTER TABLE {QuoteIdent(rel.OwnerTableName)}
ADD CONSTRAINT {QuoteIdent(rel.ConstraintName)}
FOREIGN KEY ({QuoteIdent(rel.FkColumnName)})
REFERENCES {QuoteIdent(rel.ReferencedTableName)} ({QuoteIdent(rel.ReferencedPkName)});";
                try
                {
                    await using var fkCmd = new NpgsqlCommand(fkSql, conn);
                    await fkCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                catch (PostgresException ex) when (string.Equals(ex.SqlState, "42710", StringComparison.Ordinal))
                {
                    // constraint already exists
                }
            }
        }

        private static async Task SyncIndexesAsync(NpgsqlConnection conn, Data.Database schema)
        {
            foreach (var table in schema.Tables)
            {
                var tableName = NormalizeTableName(table.Name);
                if (string.IsNullOrWhiteSpace(tableName))
                    continue;

                foreach (var column in EnumerateDesiredColumnsForTable(schema, table))
                {
                    if (string.IsNullOrWhiteSpace(column.Name) || !IsIndexed(column))
                        continue;

                    var indexName = $"ix_{SanitizeIdentifier(tableName)}_{SanitizeIdentifier(column.Name)}";
                    var indexSql =
                        $"CREATE INDEX IF NOT EXISTS {QuoteIdent(indexName)} ON {QuoteIdent(tableName)} ({QuoteIdent(column.Name)});";
                    await using var indexCmd = new NpgsqlCommand(indexSql, conn);
                    await indexCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }

        private static List<TableRelationSpec> BuildRelationSpecs(Data.Database schema)
        {
            var specs = new List<TableRelationSpec>();
            foreach (var firstTable in schema.Tables)
            {
                var firstName = NormalizeTableName(firstTable.Name);
                if (string.IsNullOrWhiteSpace(firstName))
                    continue;

                var firstPk = ResolvePrimaryKeyColumn(firstTable);
                if (firstPk == null)
                    continue;

                foreach (var relation in firstTable.Relations)
                {
                    var refTable = ResolveTableByType(schema, relation.ReferencedTableType);
                    if (refTable == null)
                        continue;

                    var secondName = NormalizeTableName(refTable.Name);
                    var secondPk = ResolvePrimaryKeyColumn(refTable);
                    if (string.IsNullOrWhiteSpace(secondName) || secondPk == null)
                        continue;

                    var fkColumnName = relation.Name + "Id";
                    if (relation.IsArray)
                    {
                        var fkType = ResolveColumnSqlType(firstPk.Type, firstPk.Modifiers);
                        specs.Add(new TableRelationSpec(
                            OwnerTableName: secondName,
                            ReferencedTableName: firstName,
                            FkColumnName: fkColumnName,
                            ReferencedPkName: firstPk.Name,
                            FkColumnType: fkType,
                            ConstraintName: $"{secondName}_{fkColumnName}_{firstName}_{firstPk.Name}"));
                    }
                    else
                    {
                        var fkType = ResolveColumnSqlType(secondPk.Type, secondPk.Modifiers);
                        specs.Add(new TableRelationSpec(
                            OwnerTableName: firstName,
                            ReferencedTableName: secondName,
                            FkColumnName: fkColumnName,
                            ReferencedPkName: secondPk.Name,
                            FkColumnType: fkType,
                            ConstraintName: $"{firstName}_{fkColumnName}_{secondName}_{secondPk.Name}"));
                    }
                }
            }

            return specs
                .GroupBy(s => $"{s.OwnerTableName}|{s.FkColumnName}|{s.ReferencedTableName}|{s.ReferencedPkName}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static Data.Table? ResolveTableByType(Data.Database schema, string referencedTableType)
        {
            if (string.IsNullOrWhiteSpace(referencedTableType))
                return null;

            // Priority for table context symbols:
            // 1) explicit table context (Message<>)
            // 2) raw symbol (Message)
            // 3) legacy db-style symbol (Message>)
            var source = StripTableContextSuffixes(referencedTableType);
            var candidates = new[]
            {
                source + "<>",
                source,
                source + ">"
            };

            foreach (var candidate in candidates)
            {
                var normalized = NormalizeTableName(candidate);
                foreach (var t in schema.Tables)
                {
                    if (IsSameTableName(t.Name, candidate) ||
                        string.Equals(NormalizeTableName(t.Name), normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return t;
                    }
                }
            }

            return null;
        }

        private static Data.Column? ResolvePrimaryKeyColumn(Data.Table table)
        {
            var pk = table.Columns.FirstOrDefault(IsPrimaryKey);
            if (pk != null)
                return pk;
            return table.Columns.FirstOrDefault(c => string.Equals(c.Name, "Id", StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildColumnSql(Data.Column column)
        {
            if (string.IsNullOrWhiteSpace(column.Name))
                throw new InvalidOperationException("Column name is empty.");

            var tokens = new List<string>
            {
                QuoteIdent(column.Name),
                ResolveColumnSqlType(column.Type, column.Modifiers)
            };

            var isNullable = true;
            var hasPrimaryKey = false;
            var hasIdentity = false;
            foreach (var modifierObj in column.Modifiers)
            {
                var modifier = modifierObj?.ToString()?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(modifier))
                    continue;
                if (modifier.StartsWith("nullable:", StringComparison.OrdinalIgnoreCase))
                {
                    isNullable = modifier.EndsWith("1", StringComparison.Ordinal);
                    continue;
                }
                if (string.Equals(modifier, "primary key", StringComparison.OrdinalIgnoreCase))
                {
                    hasPrimaryKey = true;
                    continue;
                }
                if (string.Equals(modifier, "identity", StringComparison.OrdinalIgnoreCase))
                {
                    hasIdentity = true;
                    continue;
                }
                if (modifier.StartsWith("length:", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (hasIdentity)
                tokens.Add("GENERATED BY DEFAULT AS IDENTITY");
            if (!isNullable || hasPrimaryKey)
                tokens.Add("NOT NULL");
            if (hasPrimaryKey)
                tokens.Add("PRIMARY KEY");

            return string.Join(" ", tokens);
        }

        private static string ResolveColumnSqlType(string? columnType, IReadOnlyList<object?> modifiers)
        {
            var type = (columnType ?? "").Trim().ToLowerInvariant();
            int? length = null;
            foreach (var modifierObj in modifiers)
            {
                var modifier = modifierObj?.ToString()?.Trim() ?? "";
                if (!modifier.StartsWith("length:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var rawLength = modifier.Substring("length:".Length).Trim();
                if (int.TryParse(rawLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                    length = parsed;
            }

            return type switch
            {
                "bigint" => "bigint",
                "int" or "integer" => "integer",
                "smallint" => "smallint",
                "datetime" => "timestamp",
                "timestamp" => "timestamp",
                "date" => "date",
                "time" => "time",
                "bool" or "boolean" => "boolean",
                "decimal" => "numeric",
                "double" => "double precision",
                "float" => "real",
                "uuid" => "uuid",
                "json" => "jsonb",
                "jsonb" => "jsonb",
                "text" => "text",
                "varchar" or "nvarchar" => length.HasValue ? $"varchar({length.Value})" : "varchar",
                _ => "text"
            };
        }

        /// <summary>Строка — уже валидный JSON object/array (не двойной encode).</summary>
        private static bool IsValidJsonObjectOrArray(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return false;
            var trimmed = s.TrimStart();
            if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
                return false;
            try
            {
                using (JsonDocument.Parse(s))
                    return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static object? NormalizeDbValue(object? value, string? columnType)
        {
            if (value == null)
                return null;

            var normalizedType = (columnType ?? "").Trim().ToLowerInvariant();
            switch (normalizedType)
            {
                case "datetime":
                case "timestamp":
                    if (TryNormalizeToDateTime(value, out var timestampValue))
                        return timestampValue;
                    break;
                case "date":
                    if (TryNormalizeToDateTime(value, out var dateValue))
                        return dateValue.Date;
                    break;
                case "time":
                    if (TryNormalizeToTimeSpan(value, out var timeValue))
                        return timeValue;
                    break;
                case "bool":
                case "boolean":
                    if (TryNormalizeToBoolean(value, out var boolValue))
                        return boolValue;
                    break;
                case "bigint":
                    if (TryNormalizeToInt64(value, out var int64Value))
                        return int64Value;
                    break;
                case "int":
                case "integer":
                    if (TryNormalizeToInt32(value, out var int32Value))
                        return int32Value;
                    break;
                case "smallint":
                    if (TryNormalizeToInt16(value, out var int16Value))
                        return int16Value;
                    break;
                case "decimal":
                    if (TryNormalizeToDecimal(value, out var decimalValue))
                        return decimalValue;
                    break;
                case "double":
                    if (TryNormalizeToDouble(value, out var doubleValue))
                        return doubleValue;
                    break;
                case "float":
                    if (TryNormalizeToSingle(value, out var floatValue))
                        return floatValue;
                    break;
                case "uuid":
                    if (TryNormalizeToGuid(value, out var guidValue))
                        return guidValue;
                    break;
            }

            if (normalizedType is "json" or "jsonb")
            {
                if (value is string s && IsValidJsonObjectOrArray(s))
                    return s; // уже JSON — не двойной encode
                return JsonSerializer.Serialize(value); // строка "Hi" -> "\"Hi\"" (валидный JSON)
            }

            if (value is IDictionary dictionary)
            {
                var materialized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in dictionary)
                    materialized[entry.Key?.ToString() ?? ""] = entry.Value;
                return JsonSerializer.Serialize(materialized);
            }

            return value;
        }

        private static bool TryNormalizeToDateTime(object? value, out DateTime result)
        {
            result = default;
            if (value == null || value == DBNull.Value)
                return false;

            switch (value)
            {
                case DateTime dt:
                    result = dt;
                    return true;
                case DateTimeOffset dto:
                    result = dto.UtcDateTime;
                    return true;
                case long unixSeconds:
                    result = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
                    return true;
                case int unixSecondsInt:
                    result = DateTimeOffset.FromUnixTimeSeconds(unixSecondsInt).UtcDateTime;
                    return true;
                case string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out var parsedInvariant):
                    result = parsedInvariant;
                    return true;
                case string s when DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedCurrent):
                    result = parsedCurrent;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryNormalizeToTimeSpan(object? value, out TimeSpan result)
        {
            result = default;
            if (value == null || value == DBNull.Value)
                return false;
            switch (value)
            {
                case TimeSpan ts:
                    result = ts;
                    return true;
                case DateTime dt:
                    result = dt.TimeOfDay;
                    return true;
                case string s when TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var parsedInvariant):
                    result = parsedInvariant;
                    return true;
                case string s when TimeSpan.TryParse(s, CultureInfo.CurrentCulture, out var parsedCurrent):
                    result = parsedCurrent;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryNormalizeToBoolean(object? value, out bool result)
        {
            result = false;
            if (value == null || value == DBNull.Value)
                return false;
            switch (value)
            {
                case bool b:
                    result = b;
                    return true;
                case string s when bool.TryParse(s, out var parsed):
                    result = parsed;
                    return true;
                case long l:
                    result = l != 0;
                    return true;
                case int i:
                    result = i != 0;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryNormalizeToInt64(object? value, out long result)
        {
            result = 0;
            if (value == null || value == DBNull.Value)
                return false;
            return value switch
            {
                long l => (result = l) == l,
                int i => (result = i) == i,
                short s => (result = s) == s,
                byte b => (result = b) == b,
                string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => (result = parsed) == parsed,
                _ => false
            };
        }

        private static bool TryNormalizeToInt32(object? value, out int result)
        {
            result = 0;
            if (value == null || value == DBNull.Value)
                return false;
            return value switch
            {
                int i => (result = i) == i,
                short s => (result = s) == s,
                byte b => (result = b) == b,
                long l when l <= int.MaxValue && l >= int.MinValue => (result = (int)l) == (int)l,
                string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => (result = parsed) == parsed,
                _ => false
            };
        }

        private static bool TryNormalizeToInt16(object? value, out short result)
        {
            result = 0;
            if (value == null || value == DBNull.Value)
                return false;
            return value switch
            {
                short s => (result = s) == s,
                byte b => (result = (short)b) == (short)b,
                int i when i <= short.MaxValue && i >= short.MinValue => (result = (short)i) == (short)i,
                long l when l <= short.MaxValue && l >= short.MinValue => (result = (short)l) == (short)l,
                string text when short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => (result = parsed) == parsed,
                _ => false
            };
        }

        private static bool TryNormalizeToDecimal(object? value, out decimal result)
        {
            result = 0m;
            if (value == null || value == DBNull.Value)
                return false;
            return value switch
            {
                decimal d => (result = d) == d,
                double d => decimal.TryParse(d.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out result),
                float f => decimal.TryParse(f.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out result),
                long l => (result = l) == l,
                int i => (result = i) == i,
                string text when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => (result = parsed) == parsed,
                _ => false
            };
        }

        private static bool TryNormalizeToDouble(object? value, out double result)
        {
            result = 0d;
            if (value == null || value == DBNull.Value)
                return false;
            return value switch
            {
                double d => (result = d) == d,
                float f => (result = f) == f,
                decimal d => (result = (double)d) == (double)d,
                long l => (result = l) == l,
                int i => (result = i) == i,
                string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => (result = parsed) == parsed,
                _ => false
            };
        }

        private static bool TryNormalizeToSingle(object? value, out float result)
        {
            result = 0f;
            if (value == null || value == DBNull.Value)
                return false;
            switch (value)
            {
                case float f:
                    result = f;
                    return true;
                case double d when d <= float.MaxValue && d >= float.MinValue:
                    result = (float)d;
                    return true;
                case decimal dec:
                    try
                    {
                        var cast = (float)dec;
                        if (float.IsNaN(cast) || float.IsInfinity(cast))
                            return false;
                        result = cast;
                        return true;
                    }
                    catch (OverflowException)
                    {
                        return false;
                    }
                case long l when l <= int.MaxValue && l >= int.MinValue:
                    result = l;
                    return true;
                case int i:
                    result = i;
                    return true;
                case string text when float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                    result = parsed;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryNormalizeToGuid(object? value, out Guid result)
        {
            result = Guid.Empty;
            if (value == null || value == DBNull.Value)
                return false;
            switch (value)
            {
                case Guid g:
                    result = g;
                    return true;
                case string s when Guid.TryParse(s, out var parsed):
                    result = parsed;
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsUpsertRow(Dictionary<string, object?> row)
        {
            return TryGetCaseInsensitive(row, Data.Table.PendingWriteModeKey, out var mode) &&
                   string.Equals(mode?.ToString(), Data.Table.PendingWriteModeUpsert, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetUpsertKey(Data.Table table, IDictionary<string, object?> row, out string columnName, out long keyValue)
        {
            columnName = string.Empty;
            keyValue = 0L;

            var idColumn = table.Columns.Find(c => string.Equals(c.Name, "Id", StringComparison.OrdinalIgnoreCase));
            if (idColumn != null &&
                TryGetCaseInsensitive(row, idColumn.Name, out var idValue) &&
                TryConvertToPositiveInt64(idValue, out keyValue))
            {
                columnName = idColumn.Name;
                return true;
            }

            var primaryKeyColumn = table.Columns.Find(IsPrimaryKey);
            if (primaryKeyColumn != null &&
                TryGetCaseInsensitive(row, primaryKeyColumn.Name, out var pkValue) &&
                TryConvertToPositiveInt64(pkValue, out keyValue))
            {
                columnName = primaryKeyColumn.Name;
                return true;
            }

            return false;
        }

        private static bool TryConvertToPositiveInt64(object? value, out long result)
        {
            result = 0L;
            if (value == null || value == DBNull.Value)
                return false;

            switch (value)
            {
                case long l when l > 0:
                    result = l;
                    return true;
                case int i when i > 0:
                    result = i;
                    return true;
                case short s when s > 0:
                    result = s;
                    return true;
                case byte b when b > 0:
                    result = b;
                    return true;
                case string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0:
                    result = parsed;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryGetCaseInsensitive(IDictionary<string, object?> source, string key, out object? value)
        {
            if (source.TryGetValue(key, out value))
                return true;
            foreach (var kv in source)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = kv.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        private static QueryExecutionPlan BuildQueryPlan(Data.Table sourceTable, IReadOnlyList<QueryCallExpr> calls)
        {
            var filterExpr = sourceTable.FilterExpr;
            var resultKind = QueryResultKind.Table;
            string? scalarColumnName = null;

            for (var i = 0; i < calls.Count; i++)
            {
                var call = calls[i];
                var methodName = call.MethodName?.Trim() ?? string.Empty;
                var isLast = i == calls.Count - 1;

                if (string.Equals(methodName, "where", StringComparison.OrdinalIgnoreCase))
                {
                    if (call.Args.Count == 0 || call.Args[0] is not LambdaValue whereLambda || whereLambda.ExprTree == null)
                        throw new InvalidOperationException("QueryExpr.where requires lambda argument with ExprTree for SQL execution.");
                    filterExpr = CombineFilterExpr(filterExpr, whereLambda.ExprTree);
                    resultKind = QueryResultKind.Table;
                    continue;
                }

                if (string.Equals(methodName, "any", StringComparison.OrdinalIgnoreCase))
                {
                    if (!isLast)
                        throw new InvalidOperationException("QueryExpr.any must be the last operation in SQL execution.");
                    if (call.Args.Count == 0 || call.Args[0] is not LambdaValue anyLambda || anyLambda.ExprTree == null)
                        throw new InvalidOperationException("QueryExpr.any requires lambda argument with ExprTree for SQL execution.");
                    filterExpr = CombineFilterExpr(filterExpr, anyLambda.ExprTree);
                    resultKind = QueryResultKind.Any;
                    continue;
                }

                if (string.Equals(methodName, "find", StringComparison.OrdinalIgnoreCase))
                {
                    if (!isLast)
                        throw new InvalidOperationException("QueryExpr.find must be the last operation in SQL execution.");
                    if (call.Args.Count == 0 || call.Args[0] is not LambdaValue findLambda || findLambda.ExprTree == null)
                        throw new InvalidOperationException("QueryExpr.find requires lambda argument with ExprTree for SQL execution.");
                    filterExpr = CombineFilterExpr(filterExpr, findLambda.ExprTree);
                    resultKind = QueryResultKind.Find;
                    continue;
                }

                if (string.Equals(methodName, "max", StringComparison.OrdinalIgnoreCase))
                {
                    if (!isLast)
                        throw new InvalidOperationException("QueryExpr.max must be the last operation in SQL execution.");
                    if (call.Args.Count == 0 || call.Args[0] is not LambdaValue maxLambda || !TryGetSelectorMemberName(maxLambda.ExprTree, out scalarColumnName))
                        throw new InvalidOperationException("QueryExpr.max requires selector lambda with member access ExprTree for SQL execution.");
                    resultKind = QueryResultKind.Max;
                    continue;
                }

                throw new InvalidOperationException($"Unsupported QueryExpr method '{methodName}' for SQL execution.");
            }

            return new QueryExecutionPlan(resultKind, filterExpr, scalarColumnName);
        }

        private static async Task<Data.Table> QueryTableAsync(NpgsqlConnection conn, Data.Table sourceTable, string normalizedTableName, ExprTree? whereExpr)
        {
            var (whereClause, parameters) = PostgresWhereVisitor.BuildWhere(whereExpr);
            var sql = string.IsNullOrEmpty(whereClause)
                ? $"SELECT * FROM {QuoteIdent(normalizedTableName)};"
                : $"SELECT * FROM {QuoteIdent(normalizedTableName)} WHERE {whereClause};";

            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

            var rows = new List<Dictionary<string, object?>>();
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }

            return sourceTable.CloneForQuery(rows, whereExpr);
        }

        private static async Task<bool> QueryAnyAsync(NpgsqlConnection conn, string normalizedTableName, ExprTree? whereExpr)
        {
            var (whereClause, parameters) = PostgresWhereVisitor.BuildWhere(whereExpr);
            var sql = string.IsNullOrEmpty(whereClause)
                ? $"SELECT 1 FROM {QuoteIdent(normalizedTableName)} LIMIT 1;"
                : $"SELECT 1 FROM {QuoteIdent(normalizedTableName)} WHERE {whereClause} LIMIT 1;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

            var scalar = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return scalar != null && scalar != DBNull.Value;
        }

        private static async Task<Dictionary<string, object?>?> QueryFindAsync(NpgsqlConnection conn, string normalizedTableName, ExprTree? whereExpr)
        {
            var (whereClause, parameters) = PostgresWhereVisitor.BuildWhere(whereExpr);
            var sql = string.IsNullOrEmpty(whereClause)
                ? $"SELECT * FROM {QuoteIdent(normalizedTableName)} LIMIT 1;"
                : $"SELECT * FROM {QuoteIdent(normalizedTableName)} WHERE {whereClause} LIMIT 1;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
                return null;

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            return row;
        }

        private static async Task<long> QueryMaxAsync(NpgsqlConnection conn, string normalizedTableName, ExprTree? whereExpr, string columnName)
        {
            var (whereClause, parameters) = PostgresWhereVisitor.BuildWhere(whereExpr);
            var quotedColumn = QuoteIdent(columnName);
            var sql = string.IsNullOrEmpty(whereClause)
                ? $"SELECT MAX({quotedColumn}) FROM {QuoteIdent(normalizedTableName)};"
                : $"SELECT MAX({quotedColumn}) FROM {QuoteIdent(normalizedTableName)} WHERE {whereClause};";

            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

            var scalar = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            if (scalar == null || scalar == DBNull.Value)
                return 0L;

            return scalar switch
            {
                long l => l,
                int i => i,
                short s => s,
                _ => Convert.ToInt64(scalar, CultureInfo.InvariantCulture)
            };
        }

        private static ExprTree? CombineFilterExpr(ExprTree? existing, ExprTree? next)
        {
            if (existing == null)
                return next;
            if (next == null)
                return existing;
            return new ExprAnd(UnwrapLambda(existing), UnwrapLambda(next));
        }

        private static ExprTree UnwrapLambda(ExprTree expr)
            => expr is ExprLambda lambda ? lambda.Body : expr;

        private static bool TryGetSelectorMemberName(ExprTree? exprTree, out string memberName)
        {
            memberName = string.Empty;
            if (exprTree == null)
                return false;

            var node = UnwrapLambda(exprTree);
            if (node is not ExprMemberAccess access)
                return false;
            if (access.Target is not ExprParameter)
                return false;
            memberName = access.MemberName ?? string.Empty;
            return !string.IsNullOrWhiteSpace(memberName);
        }

        private static string ResolveDatabaseName(NpgsqlConnectionStringBuilder targetBuilder, Data.Database schema, string runtimeName)
        {
            if (!string.IsNullOrWhiteSpace(targetBuilder.Database))
                return targetBuilder.Database;
            if (!string.IsNullOrWhiteSpace(schema.Name))
                return SanitizeDatabaseName(schema.Name);
            if (!string.IsNullOrWhiteSpace(runtimeName))
                return SanitizeDatabaseName(runtimeName);
            return "magic_db";
        }

        /// <summary>When no embedded schema: use connection string database, else runtime device name, else default.</summary>
        private static string ResolveDatabaseNameWithoutSchema(NpgsqlConnectionStringBuilder targetBuilder, string runtimeName)
        {
            if (!string.IsNullOrWhiteSpace(targetBuilder.Database))
                return targetBuilder.Database;
            if (!string.IsNullOrWhiteSpace(runtimeName))
                return SanitizeDatabaseName(runtimeName);
            return "magic_db";
        }

        private static string SanitizeDatabaseName(string value)
        {
            var cleaned = value.Trim().Replace(">", "").Replace("<", "");
            return string.IsNullOrWhiteSpace(cleaned) ? "magic_db" : cleaned;
        }

        private static string QuoteIdent(string identifier)
            => "\"" + (identifier ?? "").Replace("\"", "\"\"") + "\"";

        private sealed class QueryExecutionPlan
        {
            public QueryExecutionPlan(QueryResultKind kind, ExprTree? filterExpr, string? scalarColumnName)
            {
                Kind = kind;
                FilterExpr = filterExpr;
                ScalarColumnName = scalarColumnName;
            }

            public QueryResultKind Kind { get; }
            public ExprTree? FilterExpr { get; }
            public string? ScalarColumnName { get; }
        }

        private enum QueryResultKind
        {
            Table,
            Any,
            Find,
            Max
        }
    }

    /// <summary>PostgreSQL-specific visitor: translates ExprTree to WHERE clause and Npgsql parameters.</summary>
    internal static class PostgresWhereVisitor
    {
        public static (string WhereClause, List<(string Name, object? Value)> Parameters) BuildWhere(ExprTree? whereExpr)
        {
            if (whereExpr == null)
                return (string.Empty, new List<(string Name, object? Value)>());
            var sb = new StringBuilder();
            var parameters = new List<(string Name, object? Value)>();
            var paramIndex = 0;
            var root = whereExpr is ExprLambda lambda ? lambda.Body : whereExpr;
            Visit(root, sb, parameters, ref paramIndex);
            return (sb.ToString(), parameters);
        }

        private static void Visit(ExprTree node, StringBuilder sb, List<(string Name, object? Value)> parameters, ref int paramIndex)
        {
            switch (node)
            {
                case ExprParameter:
                    break;
                case ExprConstant c:
                    var name = "p" + paramIndex++;
                    parameters.Add((name, c.Value));
                    sb.Append("@").Append(name);
                    break;
                case ExprMemberAccess ma:
                    sb.Append("\"").Append(ma.MemberName?.Replace("\"", "\"\"") ?? "").Append("\"");
                    break;
                case ExprEqual eq:
                    Visit(eq.Left, sb, parameters, ref paramIndex);
                    sb.Append(" = ");
                    Visit(eq.Right, sb, parameters, ref paramIndex);
                    break;
                case ExprAnd and:
                    sb.Append("(");
                    Visit(and.Left, sb, parameters, ref paramIndex);
                    sb.Append(") AND (");
                    Visit(and.Right, sb, parameters, ref paramIndex);
                    sb.Append(")");
                    break;
                case ExprLambda l:
                    Visit(l.Body, sb, parameters, ref paramIndex);
                    break;
                default:
                    break;
            }
        }
    }
}
