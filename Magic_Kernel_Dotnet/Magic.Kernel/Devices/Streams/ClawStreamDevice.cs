using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Magic.Kernel.Compilation;
using Magic.Kernel.Core;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Streams.Drivers;
using Magic.Kernel.Interpretation;
using Magic.Kernel.Processor;

namespace Magic.Kernel.Devices.Streams
{
    /// <summary>
    /// Claw device: an HTTP socket server that listens on a configurable port.
    /// Protocol: receives JSON, returns JSON.
    /// POST /authenticate {user,password} => {token} with progressive lockout.
    /// POST /entrypoint (Bearer auth) {method, data:{command,...}} dispatches to registered AGI procedures.
    /// Bind AGI procedures with: claw1.methods.add("methodName", "&amp;procedureName").
    /// Inside the bound procedure: var socket1 := socket; provides access to connection context.
    /// <c>await socket.write(json: value)</c> or <c>await socket.write(value)</c> serializes value to JSON as the HTTP response body (replaces default <c>result</c> wrapper).
    /// </summary>
    public class ClawStreamDevice : DefStream
    {
        private readonly ConcurrentDictionary<string, string> _methodMap = new(StringComparer.OrdinalIgnoreCase);
        private ClawDriver? _driver;
        private ExecutableUnit? _openUnit;

        /// <summary>Methods registry: methodName => AGI procedure name.</summary>
        public ClawMethodsRegistry Methods { get; }

        /// <summary>Current configured/listening port.</summary>
        public int Port => _driver?.Port ?? 0;

        /// <summary>Execution unit name that created this stream (if available).</summary>
        public string UnitName => _openUnit?.Name ?? string.Empty;

        /// <summary>Execution unit instance index that created this stream (if available).</summary>
        public int? UnitInstanceIndex => _openUnit?.InstanceIndex;

        /// <summary>True when HTTP listener is active and background server loop is running.</summary>
        public bool IsListening => _driver?.IsListening ?? false;

        /// <summary>Сервер поднят из этого интерпретатора (<c>claw.open</c>); для остановки по Stop отладчика.</summary>
        internal bool IsHostedByInterpreter(Interpreter interpreter)
        {
            return _driver?.IsHostedByInterpreter(interpreter) ?? false;
        }

        public ClawStreamDevice()
        {
            Methods = new ClawMethodsRegistry(_methodMap);
        }

        public override async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";

            if (string.Equals(name, "open", StringComparison.OrdinalIgnoreCase))
                return await HandleOpenAsync(args).ConfigureAwait(false);

            if (string.Equals(name, "methods", StringComparison.OrdinalIgnoreCase))
                return Methods;

            throw new CallUnknownMethodException(name, this);
        }

        private async Task<DeviceOperationResult> HandleOpenAsync(object?[]? args)
        {
            _driver ??= new ClawDriver(_methodMap);
            _driver.SetServerName(Name);
            if (args != null && args.Length > 0)
                _driver.ParseAndApplyConfig(args[0]);

            var hostInterp = ExecutionCallContext?.Interpreter;
            _openUnit = ExecutionCallContext?.Unit;
            _driver.SetExecutionContext(
                hostInterp?.Configuration,
                _openUnit,
                hostInterp != null ? new WeakReference<Interpreter>(hostInterp) : null);

            return await _driver.OpenAsync().ConfigureAwait(false);
        }

        private IStreamDevice Driver => _driver ?? throw new InvalidOperationException("Device not opened. Call claw.open first.");

        public override Task<DeviceOperationResult> OpenAsync() => Driver.OpenAsync();

        public override async Task<object?> AwaitObjAsync()
        {
            if (_driver != null)
                await _driver.AwaitUntilStoppedAsync().ConfigureAwait(false);
            return this;
        }

        public override Task<object?> Await() => AwaitObjAsync();

        public override async Task<DeviceOperationResult> CloseAsync()
        {
            try
            {
                if (_driver != null)
                    await _driver.CloseAsync().ConfigureAwait(false);
            }
            finally
            {
                UnregisterFromStreamRegistry();
            }

            return DeviceOperationResult.Success;
        }

        public override Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync() => Driver.ReadAsync();
        public override Task<DeviceOperationResult> WriteAsync(byte[] bytes) => Driver.WriteAsync(bytes);
        public override Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl) => Driver.ControlAsync(deviceControl);
        public override Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync() => Driver.ReadChunkAsync();
        public override Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk) => Driver.WriteChunkAsync(chunk);
        public override Task<DeviceOperationResult> MoveAsync(StructurePosition? position) => Driver.MoveAsync(position);
        public override Task<(DeviceOperationResult Result, long Length)> LengthAsync() => Driver.LengthAsync();
    }

    /// <summary>Registry for claw method bindings: external method name => AGI procedure name.</summary>
    public sealed class ClawMethodsRegistry : IDefType
    {
        private readonly ConcurrentDictionary<string, string> _map;

        public long? Index { get; set; }
        public string Name { get; set; } = "methods";
        public StructurePosition? Position { get; set; }
        public List<IDefType> Generalizations { get; set; } = new List<IDefType>();

        public ClawMethodsRegistry(ConcurrentDictionary<string, string> map)
        {
            _map = map;
        }

        /// <summary>Копия карты для дерева отладчика (read-only).</summary>
        internal IReadOnlyDictionary<string, string> GetBindingsSnapshot()
            => new Dictionary<string, string>(_map, StringComparer.OrdinalIgnoreCase);

        public Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";

            if (string.Equals(name, "add", StringComparison.OrdinalIgnoreCase))
            {
                if (args == null || args.Length < 2)
                    throw new ArgumentException("methods.add requires (methodName, &procedureName)");

                var bindName = args[0]?.ToString() ?? "";
                string procName;
                if (args[1] is AddressLiteral addressLiteral)
                    procName = addressLiteral.Address;
                else
                {
                    var rawProcRef = args[1]?.ToString() ?? "";
                    procName = rawProcRef.StartsWith("&", StringComparison.Ordinal) ? rawProcRef.Substring(1) : rawProcRef;
                }
                if (!string.IsNullOrEmpty(bindName) && !string.IsNullOrEmpty(procName))
                    _map[bindName] = procName;

                return Task.FromResult<object?>(this);
            }

            throw new CallUnknownMethodException(name, this);
        }

        public Task<object?> AwaitObjAsync() => Task.FromResult<object?>(this);
        public Task<object?> Await() => Task.FromResult<object?>(this);
        public Task<(bool IsEnd, object? Delta, object? Aggregate)> StreamWaitAsync(string streamWaitType)
            => Task.FromResult<(bool, object?, object?)>((true, null, null));
    }

    /// <summary>Context injected as the 'socket' variable inside a claw-called procedure.</summary>
    public sealed class ClawSocketContext : IDefType
    {
        private static readonly JsonSerializerOptions JsonWriteOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly object _responseGate = new();
        private HttpListenerResponse? _httpResponse;

        public long? Index { get; set; }
        /// <summary>Client socket identity (ip:port).</summary>
        public string Name { get; set; }
        public StructurePosition? Position { get; set; }
        public List<IDefType> Generalizations { get; set; } = new List<IDefType>();

        /// <summary>Name of the claw stream/server instance.</summary>
        public string ServerName { get; }

        /// <summary>Name of the authenticated user for this connection.</summary>
        public string User { get; }

        /// <summary>Alias to User for AGI compatibility: socket.login.</summary>
        public string Login => User;

        /// <summary>Bearer token for this session.</summary>
        public string Token { get; }

        /// <summary>Remote client IP address.</summary>
        public string Ip { get; }

        /// <summary>Remote client source port.</summary>
        public int Port { get; }

        /// <summary>True after <c>write</c> sent the HTTP body; entrypoint skips default JSON envelope.</summary>
        public bool ResponseCommitted { get; private set; }

        /// <summary>Status code set by <c>write</c> (200 when successful).</summary>
        public int ResponseStatusCode { get; private set; }

        public ClawSocketContext(string serverName, string user, string token, string ip, int port, HttpListenerResponse? httpResponse = null)
        {
            ServerName = string.IsNullOrWhiteSpace(serverName) ? "claw" : serverName.Trim();
            User = user;
            Token = token;
            Ip = ip ?? "";
            Port = port;
            _httpResponse = httpResponse;
            Name = !string.IsNullOrWhiteSpace(Ip) && Port > 0
                ? $"{Ip}:{Port}"
                : !string.IsNullOrWhiteSpace(Ip)
                    ? Ip
                    : !string.IsNullOrWhiteSpace(User)
                        ? User
                        : "client";
        }

        public Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";
            if (string.Equals(name, "write", StringComparison.OrdinalIgnoreCase))
                return WriteJsonResponseAsync(args);

            throw new CallUnknownMethodException(methodName, this);
        }

        private async Task<object?> WriteJsonResponseAsync(object?[] args)
        {
            object? payload = null;
            if (args != null && args.Length > 0)
                payload = args[0];

            string json;
            try
            {
                json = JsonSerializer.Serialize(payload, JsonWriteOptions);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"socket.write: JSON serialization failed: {ex.Message}", ex);
            }

            var bytes = Encoding.UTF8.GetBytes(json);

            HttpListenerResponse resp;
            lock (_responseGate)
            {
                if (ResponseCommitted)
                    throw new InvalidOperationException("socket.write: response already sent.");
                if (_httpResponse == null)
                    throw new InvalidOperationException("socket.write: no HTTP response (not inside claw entrypoint).");
                resp = _httpResponse;
                resp.StatusCode = 200;
                resp.ContentType = "application/json; charset=utf-8";
                resp.ContentLength64 = bytes.Length;
            }

            await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            try
            {
                resp.OutputStream.Close();
            }
            catch
            {
                // ignore double-close
            }

            lock (_responseGate)
            {
                ResponseCommitted = true;
                ResponseStatusCode = 200;
            }

            return this;
        }

        public Task<object?> AwaitObjAsync() => Task.FromResult<object?>(this);
        public Task<object?> Await() => Task.FromResult<object?>(this);
        public Task<(bool IsEnd, object? Delta, object? Aggregate)> StreamWaitAsync(string streamWaitType)
            => Task.FromResult<(bool, object?, object?)>((true, null, null));
    }
}
