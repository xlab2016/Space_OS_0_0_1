using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace Magic.Kernel.Devices.Store.Drivers
{
    /// <summary>Low-level PostgreSQL driver operations for runtime database.</summary>
    public class PostgresDriver
    {
        public Data.Database? ResolveSchema(Database database)
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

        public Data.Table? FindTable(Database database, string tableName)
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

        public void UpsertTable(Database database, string tableName, Data.Table table)
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

        public async Task OpenAsync(string connectionString, Database database)
        {
            await EnsureDatabaseAndSchemaAsync(connectionString, database).ConfigureAwait(false);
        }

        public async Task EnsureDatabaseAndSchemaAsync(string connectionString, Database database)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));
            var schema = ResolveSchema(database);
            if (schema == null)
                return;

            var targetBuilder = new NpgsqlConnectionStringBuilder(connectionString);
            var dbName = ResolveDatabaseName(targetBuilder, schema, database.Name);
            if (!string.IsNullOrEmpty(dbName))
                targetBuilder.Database = dbName;
            var normalizedConnectionString = targetBuilder.ConnectionString;

            await EnsureDatabaseExistsAsync(targetBuilder, dbName).ConfigureAwait(false);
            await ApplySchemaAsync(normalizedConnectionString, schema).ConfigureAwait(false);
        }

        public async Task FlushPendingRowsAsync(string connectionString, Database database)
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
                        await InsertRowAsync(conn, tx, table, row).ConfigureAwait(false);
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

                await SyncTableColumnsAsync(conn, table).ConfigureAwait(false);
            }
        }

        private static async Task SyncTableColumnsAsync(NpgsqlConnection conn, Data.Table table)
        {
            var normalizedTableName = NormalizeTableName(table.Name);
            if (string.IsNullOrWhiteSpace(normalizedTableName))
                return;

            var existingColumns = await LoadTableColumnsAsync(conn, normalizedTableName).ConfigureAwait(false);
            var hasPrimaryKey = await HasPrimaryKeyAsync(conn, normalizedTableName).ConfigureAwait(false);

            foreach (var column in table.Columns)
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
            var name = (value ?? "").Trim();
            if (name.EndsWith("<>", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - 2).Trim();
            return ToPlural(name);
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

        private static async Task InsertRowAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Data.Table table, Dictionary<string, object?> row)
        {
            var normalizedTableName = NormalizeTableName(table.Name);
            if (string.IsNullOrWhiteSpace(normalizedTableName))
                return;

            var allowedColumns = table.Columns;
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

        private static string SanitizeDatabaseName(string value)
        {
            var cleaned = value.Trim().Replace(">", "").Replace("<", "");
            return string.IsNullOrWhiteSpace(cleaned) ? "magic_db" : cleaned;
        }

        private static string QuoteIdent(string identifier)
            => "\"" + (identifier ?? "").Replace("\"", "\"\"") + "\"";
    }
}
