using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Magic.Kernel.Core;
using Magic.Kernel.Data;

namespace Magic.Kernel.Devices.Store
{
    /// <summary>Runtime database def-type that delegates to driver generalizations.</summary>
    public class Database : DefType, IDatabaseDevice
    {
        public override async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var handlers = GetDriverGeneralizations();
            if (handlers.Count == 0)
                throw new InvalidOperationException("Database has no driver generalizations.");

            var list = new List<object?>();
            foreach (var g in handlers)
            {
                if (g is Postgres postgres)
                    postgres.Database = this;
                var result = await g.CallObjAsync(methodName, args).ConfigureAwait(false);
                list.Add(result);
            }

            return list;
        }

        public override async Task<object?> AwaitObjAsync()
        {
            var handlers = GetDriverGeneralizations();
            if (handlers.Count == 0)
                return this;

            var list = new List<object?>();
            foreach (var g in handlers)
            {
                if (g is Postgres postgres)
                    postgres.Database = this;
                var result = await g.AwaitObjAsync().ConfigureAwait(false);
                list.Add(result);
            }

            return list;
        }

        public override Task<object?> Await() => AwaitObjAsync();

        public override Task<(bool IsEnd, object? Delta, object? Aggregate)> StreamWaitAsync(string streamWaitType)
            => Task.FromResult((true, (object?)null, (object?)null));

        private List<IDefType> GetDriverGeneralizations()
            => Generalizations.Where(g => g is not Data.Database).ToList();

        Table? IDatabaseDevice.FindTable(Database runtimeDatabase, string tableName)
        {
            if (runtimeDatabase == null || string.IsNullOrWhiteSpace(tableName))
                return null;

            var device = GetDriverGeneralizations().OfType<IDatabaseDevice>().FirstOrDefault();
            return device?.FindTable(runtimeDatabase, tableName);
        }

        void IDatabaseDevice.UpsertTable(Database runtimeDatabase, string tableName, Table table)
        {
            if (runtimeDatabase == null || string.IsNullOrWhiteSpace(tableName) || table == null)
                return;

            var device = GetDriverGeneralizations().OfType<IDatabaseDevice>().FirstOrDefault();
            device?.UpsertTable(runtimeDatabase, tableName, table);
        }
    }
}
