using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Magic.Kernel.Interpretation
{
    /// <summary>Vault reader that loads key-value map from a JSON file (e.g. Code/vault.json).</summary>
    public sealed class JsonFileVaultReader : IVaultReader
    {
        private readonly Dictionary<string, string?> _data;

        public JsonFileVaultReader(string filePath)
        {
            _data = LoadSync(filePath);
        }

        public static async Task<JsonFileVaultReader> CreateAsync(string filePath)
        {
            var data = await LoadAsync(filePath).ConfigureAwait(false);
            return new JsonFileVaultReader(data);
        }

        private JsonFileVaultReader(Dictionary<string, string?> data)
        {
            _data = data;
        }

        public string? Read(string key)
        {
            return _data.TryGetValue(key, out var v) ? v : null;
        }

        private static Dictionary<string, string?> LoadSync(string filePath)
        {
            if (!File.Exists(filePath))
                return new Dictionary<string, string?>();
            var json = File.ReadAllText(filePath);
            return ParseJsonToFlat(json);
        }

        private static async Task<Dictionary<string, string?>> LoadAsync(string filePath)
        {
            if (!File.Exists(filePath))
                return new Dictionary<string, string?>();
            var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            return ParseJsonToFlat(json);
        }

        private static Dictionary<string, string?> ParseJsonToFlat(string json)
        {
            var result = new Dictionary<string, string?>();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return result;
            foreach (var prop in root.EnumerateObject())
            {
                var v = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.GetRawText();
                result[prop.Name] = v;
            }
            return result;
        }
    }
}
