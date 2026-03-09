using RocksDbSharp;
using System.Text.Json;

namespace SpaceDb.Services;

public interface IRocksDbService : IDisposable
{
    Task<bool> PutAsync(string key, byte[] value);
    Task<byte[]?> GetAsync(string key);
    Task<bool> DeleteAsync(string key);
    Task<bool> ExistsAsync(string key);
    Task<IEnumerable<KeyValuePair<string, byte[]>>> GetRangeAsync(string startKey, string endKey);
    Task<IEnumerable<KeyValuePair<string, byte[]>>> GetAllAsync();
    Task<long> GetCountAsync();
    Task ClearAsync();
    Task<bool> CompactAsync();

    // JSON methods
    Task<bool> PutJsonAsync<T>(string key, T value, JsonSerializerOptions? options = null);
    Task<T?> GetJsonAsync<T>(string key, JsonSerializerOptions? options = null);
    Task<bool> PutJsonStringAsync(string key, string json);
    Task<string?> GetJsonStringAsync(string key);
    Task<IEnumerable<KeyValuePair<string, T>>> GetAllJsonAsync<T>(JsonSerializerOptions? options = null);
    Task<IEnumerable<KeyValuePair<string, T>>> GetRangeJsonAsync<T>(string startKey, string endKey, JsonSerializerOptions? options = null);
}

