using Microsoft.Extensions.Logging;
using RocksDbSharp;
using System.Text;
using System.Text.Json;

namespace SpaceDb.Services;

public class RocksDbService : IRocksDbService
{
    private readonly RocksDb _db;
    private readonly ILogger _logger;
    private readonly string _dbPath;
    private bool _disposed = false;

    public RocksDbService(string dbPath, ILogger logger)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Создаем директорию если не существует
        Directory.CreateDirectory(_dbPath);

        var options = new DbOptions()
            .SetCreateIfMissing(true)
            .SetCreateMissingColumnFamilies(true)
            .SetMaxWriteBufferNumber(3)
            .SetWriteBufferSize(64 * 1024 * 1024) // 64MB
            .SetMaxBytesForLevelBase(256 * 1024 * 1024) // 256MB
            .SetTargetFileSizeBase(64 * 1024 * 1024); // 64MB

        _db = RocksDb.Open(options, _dbPath);
        _logger.LogInformation("RocksDB инициализирован в пути: {DbPath}", _dbPath);
    }

    public async Task<bool> PutAsync(string key, byte[] value)
    {
        try
        {
            await Task.Run(() => _db.Put(key, Convert.ToBase64String(value)));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при записи ключа {Key} в RocksDB", key);
            return false;
        }
    }

    public async Task<byte[]?> GetAsync(string key)
    {
        try
        {
            var result = await Task.Run(() => _db.Get(key));
            return result != null ? Convert.FromBase64String(result) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при чтении ключа {Key} из RocksDB", key);
            return null;
        }
    }

    public async Task<bool> DeleteAsync(string key)
    {
        try
        {
            await Task.Run(() => _db.Remove(key));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при удалении ключа {Key} из RocksDB", key);
            return false;
        }
    }

    public async Task<bool> ExistsAsync(string key)
    {
        try
        {
            var result = await Task.Run(() => _db.Get(key));
            return result != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при проверке существования ключа {Key} в RocksDB", key);
            return false;
        }
    }

    public async Task<IEnumerable<KeyValuePair<string, byte[]>>> GetRangeAsync(string startKey, string endKey)
    {
        try
        {
            return await Task.Run(() =>
            {
                var result = new List<KeyValuePair<string, byte[]>>();
                using var iterator = _db.NewIterator();

                iterator.Seek(startKey);
                while (iterator.Valid())
                {
                    var key = iterator.StringKey();
                    if (string.Compare(key, endKey, StringComparison.Ordinal) > 0)
                        break;

                    var value = iterator.StringValue();
                    result.Add(new KeyValuePair<string, byte[]>(key, Convert.FromBase64String(value)));
                    iterator.Next();
                }

                return result;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при получении диапазона ключей {StartKey}-{EndKey} из RocksDB", startKey, endKey);
            return Enumerable.Empty<KeyValuePair<string, byte[]>>();
        }
    }

    public async Task<IEnumerable<KeyValuePair<string, byte[]>>> GetAllAsync()
    {
        try
        {
            return await Task.Run(() =>
            {
                var result = new List<KeyValuePair<string, byte[]>>();
                using var iterator = _db.NewIterator();

                iterator.SeekToFirst();
                while (iterator.Valid())
                {
                    var value = iterator.StringValue();
                    result.Add(new KeyValuePair<string, byte[]>(iterator.StringKey(), Convert.FromBase64String(value)));
                    iterator.Next();
                }

                return result;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при получении всех ключей из RocksDB");
            return Enumerable.Empty<KeyValuePair<string, byte[]>>();
        }
    }

    public async Task<long> GetCountAsync()
    {
        try
        {
            return await Task.Run(() =>
            {
                long count = 0;
                using var iterator = _db.NewIterator();

                iterator.SeekToFirst();
                while (iterator.Valid())
                {
                    count++;
                    iterator.Next();
                }

                return count;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при подсчете записей в RocksDB");
            return 0;
        }
    }

    public async Task ClearAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                using var iterator = _db.NewIterator();
                iterator.SeekToFirst();
                while (iterator.Valid())
                {
                    _db.Remove(iterator.StringKey());
                    iterator.Next();
                }
            });
            _logger.LogInformation("RocksDB очищен");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при очистке RocksDB");
        }
    }

    public async Task<bool> CompactAsync()
    {
        try
        {
            await Task.Run(() => _db.CompactRange("", ""));
            _logger.LogInformation("RocksDB скомпактирован");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при компактировании RocksDB");
            return false;
        }
    }

    public async Task<bool> PutJsonAsync<T>(string key, T value, JsonSerializerOptions? options = null)
    {
        try
        {
            var json = JsonSerializer.Serialize(value, options);
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            return await PutAsync(key, jsonBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при записи JSON ключа {Key} в RocksDB", key);
            return false;
        }
    }

    public async Task<T?> GetJsonAsync<T>(string key, JsonSerializerOptions? options = null)
    {
        try
        {
            var jsonBytes = await GetAsync(key);
            if (jsonBytes == null)
                return default;

            var json = Encoding.UTF8.GetString(jsonBytes);
            return JsonSerializer.Deserialize<T>(json, options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при чтении JSON ключа {Key} из RocksDB", key);
            return default;
        }
    }

    public async Task<bool> PutJsonStringAsync(string key, string json)
    {
        try
        {
            if (string.IsNullOrEmpty(json))
            {
                _logger.LogWarning("Пустая JSON строка для ключа {Key}", key);
                return false;
            }

            var jsonBytes = Encoding.UTF8.GetBytes(json);
            return await PutAsync(key, jsonBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при записи JSON строки ключа {Key} в RocksDB", key);
            return false;
        }
    }

    public async Task<string?> GetJsonStringAsync(string key)
    {
        try
        {
            var jsonBytes = await GetAsync(key);
            if (jsonBytes == null)
                return null;

            return Encoding.UTF8.GetString(jsonBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при чтении JSON строки ключа {Key} из RocksDB", key);
            return null;
        }
    }

    public async Task<IEnumerable<KeyValuePair<string, T>>> GetAllJsonAsync<T>(JsonSerializerOptions? options = null)
    {
        try
        {
            var allPairs = await GetAllAsync();
            var result = new List<KeyValuePair<string, T>>();

            foreach (var kv in allPairs)
            {
                try
                {
                    var json = Encoding.UTF8.GetString(kv.Value);
                    var value = JsonSerializer.Deserialize<T>(json, options);
                    if (value != null)
                    {
                        result.Add(new KeyValuePair<string, T>(kv.Key, value));
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Не удалось десериализовать JSON для ключа {Key}", kv.Key);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при получении всех JSON записей из RocksDB");
            return Enumerable.Empty<KeyValuePair<string, T>>();
        }
    }

    public async Task<IEnumerable<KeyValuePair<string, T>>> GetRangeJsonAsync<T>(string startKey, string endKey, JsonSerializerOptions? options = null)
    {
        try
        {
            var rangePairs = await GetRangeAsync(startKey, endKey);
            var result = new List<KeyValuePair<string, T>>();

            foreach (var kv in rangePairs)
            {
                try
                {
                    var json = Encoding.UTF8.GetString(kv.Value);
                    var value = JsonSerializer.Deserialize<T>(json, options);
                    if (value != null)
                    {
                        result.Add(new KeyValuePair<string, T>(kv.Key, value));
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Не удалось десериализовать JSON для ключа {Key}", kv.Key);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при получении диапазона JSON записей {StartKey}-{EndKey} из RocksDB", startKey, endKey);
            return Enumerable.Empty<KeyValuePair<string, T>>();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _db?.Dispose();
            _disposed = true;
            _logger.LogInformation("RocksDB соединение закрыто");
        }
    }
}

