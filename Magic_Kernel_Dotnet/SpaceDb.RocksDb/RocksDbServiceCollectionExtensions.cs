using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SpaceDb.Services;

public static class RocksDbServiceCollectionExtensions
{
    public static IServiceCollection AddRocksDb(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(sp =>
        {
            var cfgPath = configuration["RocksDb:Path"];
            var envPath = Environment.GetEnvironmentVariable("ROCKSDB_PATH");

            var dbPath =
                !string.IsNullOrWhiteSpace(envPath) ? envPath :
                !string.IsNullOrWhiteSpace(cfgPath) ? cfgPath :
                Path.Combine(Directory.GetCurrentDirectory(), "rocksdb");

            return new RocksDbOptions { Path = dbPath };
        });

        // RocksDB Service
        services.AddSingleton<IRocksDbService>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<RocksDbService>>();
            var options = provider.GetRequiredService<RocksDbOptions>();
            return new RocksDbService(options.Path!, logger);
        });

        services.AddSingleton<RocksDbSpaceDisk>();

        return services;
    }
}

