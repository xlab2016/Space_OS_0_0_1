using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Magic.Kernel.Compilation;
using Magic.Kernel.Core;
using Magic.Kernel.Devices;
using Magic.Kernel.Interpretation;
using Magic.Kernel.Processor;
using Microsoft.IdentityModel.Tokens;

namespace Magic.Kernel.Devices.Streams.Drivers
{
    /// <summary>HTTP Claw server: JWT auth, /entrypoint dispatch to AGI procedures. Stream Read/Write are not used.</summary>
    public sealed class ClawDriver : IStreamDevice
    {
        private static readonly JsonSerializerOptions JsonResponseOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private int _port;
        private List<Credential> _credentials = new List<Credential>();
        private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, UserLockout> _lockouts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _methodMap;

        private string _jwtSecret = "dev_claw_jwt_secret_change_me";
        private TimeSpan _jwtTtl = TimeSpan.FromMinutes(30);
        private readonly JwtSecurityTokenHandler _jwtTokenHandler = new();

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _serverTask;

        private KernelConfiguration? _kernelConfig;
        private ExecutableUnit? _unit;
        private WeakReference<Interpreter>? _procedureDebugHost;

        private readonly SemaphoreSlim _clawProcedureDebugGate = new(1, 1);

        private string _serverName = "claw";
        private readonly string _consolePrefix;

        public ClawDriver(ConcurrentDictionary<string, string> methodMap, string consolePrefix = "")
        {
            _methodMap = methodMap ?? throw new ArgumentNullException(nameof(methodMap));
            _consolePrefix = consolePrefix ?? string.Empty;
        }

        public void SetServerName(string name)
        {
            _serverName = string.IsNullOrWhiteSpace(name) ? "claw" : name.Trim();
        }

        public void SetExecutionContext(
            KernelConfiguration? kernelConfig,
            ExecutableUnit? unit,
            WeakReference<Interpreter>? procedureDebugHost)
        {
            _kernelConfig = kernelConfig;
            _unit = unit;
            _procedureDebugHost = procedureDebugHost;
        }

        public int Port => _port;

        public bool IsListening => _listener?.IsListening == true && _serverTask != null && !_serverTask.IsCompleted;

        internal bool IsHostedByInterpreter(Interpreter interpreter)
        {
            if (_procedureDebugHost == null)
                return false;
            return _procedureDebugHost.TryGetTarget(out var h) && ReferenceEquals(h, interpreter);
        }

        private string LogPrefix
        {
            get
            {
                var serverName = string.IsNullOrWhiteSpace(_serverName) ? "claw" : _serverName.Trim();
                return $"{_consolePrefix}[{serverName}] [claw]";
            }
        }

        public void ParseAndApplyConfig(object? config)
        {
            if (config == null) return;

            if (config is Dictionary<string, object> dict)
            {
                if (dict.TryGetValue("port", out var portObj))
                    _port = ParseInt(portObj, 8080);

                if (dict.TryGetValue("authentication", out var authObj) && authObj is Dictionary<string, object> authDict)
                {
                    if (authDict.TryGetValue("credentials", out var credsObj))
                        _credentials = ParseCredentials(credsObj);

                    if (authDict.TryGetValue("jwtSecret", out var jwtSecretObj) && jwtSecretObj != null)
                    {
                        var s = jwtSecretObj.ToString();
                        if (!string.IsNullOrWhiteSpace(s))
                            _jwtSecret = s;
                    }

                    if (authDict.TryGetValue("jwtTtlSeconds", out var jwtTtlSecondsObj) && jwtTtlSecondsObj != null)
                    {
                        var seconds = ParseInt(jwtTtlSecondsObj, (int)_jwtTtl.TotalSeconds);
                        if (seconds > 0)
                            _jwtTtl = TimeSpan.FromSeconds(seconds);
                    }
                }
            }
            else if (config is string jsonStr)
            {
                try
                {
                    using var doc = JsonDocument.Parse(jsonStr);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("port", out var portEl))
                        _port = portEl.ValueKind == JsonValueKind.Number ? portEl.GetInt32() : 8080;
                    if (root.TryGetProperty("authentication", out var authEl))
                    {
                        if (authEl.TryGetProperty("credentials", out var credsEl))
                            _credentials = ParseCredentialsFromJson(credsEl);

                        if (authEl.TryGetProperty("jwtSecret", out var jwtSecretEl))
                        {
                            var s = jwtSecretEl.ValueKind == JsonValueKind.String ? jwtSecretEl.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(s))
                                _jwtSecret = s;
                        }

                        if (authEl.TryGetProperty("jwtTtlSeconds", out var jwtTtlSecondsEl) &&
                            jwtTtlSecondsEl.ValueKind == JsonValueKind.Number &&
                            jwtTtlSecondsEl.TryGetInt32(out var seconds) &&
                            seconds > 0)
                        {
                            _jwtTtl = TimeSpan.FromSeconds(seconds);
                        }
                    }
                }
                catch { }
            }

            if (_port <= 0) _port = 8080;
        }

        private static int ParseInt(object? obj, int defaultVal)
        {
            if (obj is long l) return (int)l;
            if (obj is int i) return i;
            if (obj is string s && int.TryParse(s, out var v)) return v;
            return defaultVal;
        }

        private static List<Credential> ParseCredentials(object? credsObj)
        {
            var result = new List<Credential>();
            if (credsObj is List<object> list)
            {
                foreach (var item in list)
                {
                    if (item is Dictionary<string, object> d)
                    {
                        var user = d.TryGetValue("username", out var u) ? u?.ToString() ?? "" : "";
                        var password = d.TryGetValue("password", out var p) ? p?.ToString() ?? "" : "";
                        if (!string.IsNullOrEmpty(user))
                            result.Add(new Credential(user, password));
                    }
                }
            }
            return result;
        }

        private static List<Credential> ParseCredentialsFromJson(JsonElement el)
        {
            var result = new List<Credential>();
            if (el.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in el.EnumerateArray())
            {
                var user = item.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
                var password = item.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(user))
                    result.Add(new Credential(user, password));
            }
            return result;
        }

        public Task<DeviceOperationResult> OpenAsync()
        {
            if (_port <= 0) _port = 8080;

            _cts?.Cancel();
            _cts?.Dispose();

            _cts = new CancellationTokenSource();
            _listener = null;
            _serverTask = null;

            return Task.FromResult(DeviceOperationResult.Success);
        }

        /// <summary>Blocks until the HTTP server loop ends (used by <c>await claw</c>).</summary>
        public async Task AwaitUntilStoppedAsync()
        {
            EnsureServerStarted();

            if (_serverTask != null)
            {
                Console.WriteLine($"[{DateTime.UtcNow:o}] {LogPrefix} await entered. Waiting for listener stop...");
                if (_cts != null)
                {
                    var unblocked = UnblockAwaitIfListenerDeadAsync(_cts.Token);
                    var first = await Task.WhenAny(_serverTask, unblocked).ConfigureAwait(false);
                    if (first == unblocked)
                    {
                        try { await unblocked.ConfigureAwait(false); } catch { /* probe cancelled */ }
                    }
                }

                try
                {
                    await _serverTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[{DateTime.UtcNow:o}] {LogPrefix} await: server task ended with {ex.GetType().Name}: {ex.Message}");
                }

                Console.WriteLine($"[{DateTime.UtcNow:o}] {LogPrefix} await released. Listener stopped.");
            }
        }

        private async Task UnblockAwaitIfListenerDeadAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(400, ct).ConfigureAwait(false);
                var notListeningStreak = 0;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var lis = _listener;
                        if (lis == null)
                            return;

                        if (lis.IsListening)
                            notListeningStreak = 0;
                        else
                        {
                            notListeningStreak++;
                            if (notListeningStreak >= 2)
                            {
                                Console.WriteLine(
                                    $"[{DateTime.UtcNow:o}] {LogPrefix} listener inactive (IsListening=false); aborting to unblock await.");
                                AbortListenerToUnblockAwait();
                                return;
                            }
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch
                    {
                        // ignore, probe continues
                    }

                    await Task.Delay(700, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Close / unregister
            }
        }

        private void AbortListenerToUnblockAwait()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
        }

        private void CleanupListenerAfterServerEnded()
        {
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            _serverTask = null;
        }

        private void EnsureServerStarted()
        {
            if (_serverTask != null && !_serverTask.IsCompleted)
                return;

            if (_serverTask != null && _serverTask.IsCompleted)
                CleanupListenerAfterServerEnded();

            _cts ??= new CancellationTokenSource();

            HttpListenerException? lastException = null;
            string? lastPrefix = null;

            var prefixes = new List<string>
            {
                $"http://localhost:{_port}/",
                $"http://127.0.0.1:{_port}/",
                $"http://+:{_port}/"
            };

            foreach (var prefix in prefixes)
            {
                HttpListener? listener = null;
                lastPrefix = prefix;
                try
                {
                    listener = new HttpListener();
                    listener.Prefixes.Add(prefix);
                    listener.Start();
                    if (!listener.IsListening)
                    {
                        Console.WriteLine(
                            $"[{DateTime.UtcNow:o}] {LogPrefix} Start() did not activate listener for '{prefix}' (port={_port}), trying next prefix.");
                        try { listener.Close(); } catch { }
                        continue;
                    }

                    _listener = listener;
                    _serverTask = RunServerAsync(_cts.Token);

                    Console.WriteLine(
                        $"[{DateTime.UtcNow:o}] {LogPrefix} started. Listening on '{prefix}' (port={_port}).");
                    return;
                }
                catch (HttpListenerException ex)
                {
                    lastException = ex;
                    Console.WriteLine(
                        $"[{DateTime.UtcNow:o}] {LogPrefix} failed to bind prefix '{prefix}': {ex.Message}");
                    try { listener?.Close(); } catch { }
                }
            }

            var principal = string.IsNullOrWhiteSpace(Environment.UserDomainName)
                ? Environment.UserName
                : $"{Environment.UserDomainName}\\{Environment.UserName}";
            var urlAclPrefix = lastPrefix ?? $"http://+:{_port}/";

            if (lastException != null &&
                lastException.Message != null &&
                lastException.Message.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new InvalidOperationException(
                    $"Failed to start Claw listener on port {_port}: {lastException.Message}. " +
                    $"Run (as admin) to reserve URLACL: netsh http add urlacl url=\"{urlAclPrefix}\" user=\"{principal}\"");
            }

            throw new InvalidOperationException(
                $"Failed to start Claw listener on port {_port}: {lastException?.Message ?? "unknown error"}");
        }

        public async Task<DeviceOperationResult> CloseAsync()
        {
            try
            {
                _cts?.Cancel();
                try { _listener?.Stop(); } catch { }
                if (_serverTask != null)
                {
                    try { await _serverTask.ConfigureAwait(false); } catch { }
                }

                CleanupListenerAfterServerEnded();
            }
            finally
            {
                try { _cts?.Dispose(); } catch { }
                _cts = null;
            }

            Console.WriteLine($"[{DateTime.UtcNow:o}] {LogPrefix} stopped. (port={_port})");
            return DeviceOperationResult.Success;
        }

        private async Task RunServerAsync(CancellationToken ct)
        {
            if (_listener?.IsListening != true)
            {
                Console.WriteLine(
                    $"[{DateTime.UtcNow:o}] {LogPrefix} server loop not started: listener inactive (await will unblock).");
                try { _listener?.Stop(); } catch { }
                return;
            }

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await _listener!.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (HttpListenerException ex)
                    {
                        Console.WriteLine(
                            $"[{DateTime.UtcNow:o}] {LogPrefix} HttpListener accept stopped: {ex.Message}");
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"[{DateTime.UtcNow:o}] {LogPrefix} accept loop fatal: {ex.GetType().Name}: {ex.Message}");
                        break;
                    }

                    _ = Task.Run(() => HandleRequestAsync(ctx), ct);
                }

                if (!ct.IsCancellationRequested)
                {
                    Console.WriteLine(
                        $"[{DateTime.UtcNow:o}] {LogPrefix} server loop ended without Close() (await claw released).");
                }
            }
            finally
            {
                if (!ct.IsCancellationRequested)
                {
                    try { _listener?.Stop(); } catch { }
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            var sw = Stopwatch.StartNew();
            var req = ctx.Request;
            var resp = ctx.Response;

            var remote = req.RemoteEndPoint?.ToString() ?? "(unknown)";
            var absoluteUrl = req.Url?.AbsoluteUri ?? "(null-url)";
            var method = req.HttpMethod ?? "(null-method)";
            var path = req.Url?.AbsolutePath?.TrimEnd('/') ?? "";

            var statusCode = 0;

            Console.WriteLine(
                $"[{DateTime.UtcNow:o}] {LogPrefix} request. remote={remote}, method={method}, url={absoluteUrl}");

            try
            {
                resp.ContentType = "application/json";

                if (req.HttpMethod != "POST")
                {
                    statusCode = await WriteJsonResponse(resp, 405,
                            new Dictionary<string, object> { ["error"] = "Method Not Allowed" })
                        .ConfigureAwait(false);
                    return;
                }

                if (string.Equals(path, "/authenticate", StringComparison.OrdinalIgnoreCase))
                {
                    statusCode = await HandleAuthenticateAsync(req, resp).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(path, "/entrypoint", StringComparison.OrdinalIgnoreCase))
                {
                    statusCode = await HandleEntrypointAsync(req, resp).ConfigureAwait(false);
                    return;
                }

                statusCode = await WriteJsonResponse(resp, 404,
                        new Dictionary<string, object> { ["error"] = "Not Found" })
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.UtcNow:o}] {LogPrefix} request failed. url={absoluteUrl}, error={ex}");
                try
                {
                    statusCode = await WriteJsonResponse(ctx.Response, 500,
                            new Dictionary<string, object> { ["error"] = ex.Message })
                        .ConfigureAwait(false);
                }
                catch { }
            }
            finally
            {
                Console.WriteLine(
                    $"[{DateTime.UtcNow:o}] {LogPrefix} request done. url={absoluteUrl}, status={statusCode}, elapsed_ms={sw.ElapsedMilliseconds}");
            }
        }

        private async Task<int> HandleAuthenticateAsync(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var remote = req.RemoteEndPoint?.ToString() ?? "(unknown)";
            var body = await ReadBodyAsync(req).ConfigureAwait(false);
            string? user = null;
            string? password = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("username", out var u)) user = u.GetString();
                if (root.TryGetProperty("password", out var p)) password = p.GetString();
            }
            catch
            {
                return await WriteJsonResponse(resp, 400,
                        new Dictionary<string, object> { ["error"] = "Invalid JSON" })
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(user))
            {
                return await WriteJsonResponse(resp, 400,
                        new Dictionary<string, object> { ["error"] = "user required" })
                    .ConfigureAwait(false);
            }

            if (_lockouts.TryGetValue(user, out var lockout) && lockout.IsLocked())
            {
                var remaining = lockout.RemainingSeconds();
                Console.WriteLine(
                    $"[{DateTime.UtcNow:o}] {LogPrefix} /authenticate rejected (locked). remote={remote}, user={user}, retry_after_seconds={remaining}");
                return await WriteJsonResponse(resp, 429, new Dictionary<string, object>
                {
                    ["error"] = "Too many failed attempts. Account locked.",
                    ["retry_after_seconds"] = (object)remaining
                }).ConfigureAwait(false);
            }

            var found = _credentials.Find(c => string.Equals(c.User, user, StringComparison.OrdinalIgnoreCase) && c.Password == password);
            if (found == null)
            {
                var lo = _lockouts.GetOrAdd(user, _ => new UserLockout());
                lo.RecordFailure();
                Console.WriteLine(
                    $"[{DateTime.UtcNow:o}] {LogPrefix} /authenticate rejected (bad credentials). remote={remote}, user={user}");
                return await WriteJsonResponse(resp, 401,
                        new Dictionary<string, object> { ["error"] = "Invalid credentials" })
                    .ConfigureAwait(false);
            }

            if (_lockouts.TryGetValue(user, out var existingLockout))
                existingLockout.Reset();

            var jwt = GenerateJwtToken(user);
            _tokens[jwt] = user;

            Console.WriteLine(
                $"[{DateTime.UtcNow:o}] {LogPrefix} /authenticate ok. remote={remote}, user={user}");
            return await WriteJsonResponse(resp, 200,
                    new Dictionary<string, object> { ["token"] = (object)$"Bearer {jwt}" })
                .ConfigureAwait(false);
        }

        private async Task<int> HandleEntrypointAsync(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var authHeader = req.Headers["Authorization"] ?? "";

            var jwtToken = ExtractBearerToken(authHeader);
            if (string.IsNullOrEmpty(jwtToken))
            {
                Console.WriteLine(
                    $"[{DateTime.UtcNow:o}] {LogPrefix} /entrypoint rejected (unauthorized). remote={req.RemoteEndPoint}, url={req.Url?.AbsoluteUri}");
                return await WriteJsonResponse(resp, 401,
                        new Dictionary<string, object> { ["error"] = "Unauthorized" })
                    .ConfigureAwait(false);
            }

            var user = TryGetUserFromJwt(jwtToken, out var fromJwt) ? fromJwt : null;
            if (user == null && _tokens.TryGetValue(jwtToken, out var fromDict))
                user = fromDict;

            if (string.IsNullOrEmpty(user))
            {
                Console.WriteLine(
                    $"[{DateTime.UtcNow:o}] {LogPrefix} /entrypoint rejected (unauthorized). remote={req.RemoteEndPoint}, url={req.Url?.AbsoluteUri}");
                return await WriteJsonResponse(resp, 401,
                        new Dictionary<string, object> { ["error"] = "Unauthorized" })
                    .ConfigureAwait(false);
            }

            var body = await ReadBodyAsync(req).ConfigureAwait(false);

            string? methodName = null;
            object? data = null;

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("method", out var m)) methodName = m.GetString();
                if (root.TryGetProperty("data", out var d)) data = JsonElementToObject(d);
            }
            catch
            {
                return await WriteJsonResponse(resp, 400,
                        new Dictionary<string, object> { ["error"] = "Invalid JSON" })
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(methodName))
            {
                return await WriteJsonResponse(resp, 400,
                        new Dictionary<string, object> { ["error"] = "method required" })
                    .ConfigureAwait(false);
            }

            if (!_methodMap.TryGetValue(methodName, out var procedureName))
            {
                return await WriteJsonResponse(resp, 404,
                        new Dictionary<string, object> { ["error"] = $"Method '{methodName}' not found" })
                    .ConfigureAwait(false);
            }

            Console.WriteLine(
                $"[{DateTime.UtcNow:o}] {LogPrefix} /entrypoint. remote={req.RemoteEndPoint}, user={user}, method={methodName}, url={req.Url?.AbsoluteUri}");

            var remoteIp = req.RemoteEndPoint?.Address?.ToString() ?? "";
            var remotePort = req.RemoteEndPoint?.Port ?? 0;

            var socketCtx = new ClawSocketContext(
                string.IsNullOrWhiteSpace(_serverName) ? "claw" : _serverName.Trim(),
                user,
                jwtToken,
                remoteIp,
                remotePort,
                resp);

            var callData = new Dictionary<string, object>(StringComparer.Ordinal);
            if (data is Dictionary<string, object> dataDict)
            {
                foreach (var kv in dataDict)
                    callData[kv.Key] = kv.Value;
            }

            callData["authentication"] = new Dictionary<string, object>
            {
                ["isAuthenticated"] = (object)true,
                ["user"] = (object)user,
                ["token"] = (object)jwtToken
            };
            callData["socket"] = socketCtx;

            object? result = null;
            try
            {
                result = await InvokeProcedureAsync(procedureName, callData, socketCtx).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return await WriteJsonResponse(resp, 500,
                        new Dictionary<string, object> { ["error"] = (object)ex.Message })
                    .ConfigureAwait(false);
            }

            if (socketCtx.ResponseCommitted)
                return socketCtx.ResponseStatusCode;

            var responseObj = result as Dictionary<string, object> ?? new Dictionary<string, object> { ["result"] = result ?? (object)"ok" };

            Console.WriteLine(
                $"[{DateTime.UtcNow:o}] {LogPrefix} /entrypoint result ready. user={user}, method={methodName}");
            return await WriteJsonResponse(resp, 200, responseObj).ConfigureAwait(false);
        }

        private async Task<object?> InvokeProcedureAsync(string procedureName, object? data, ClawSocketContext socketCtx)
        {
            if (_unit == null)
                throw new InvalidOperationException("Claw device not opened or no execution unit available.");

            InterpreterDebugSession? dbg = null;
            if (_procedureDebugHost != null && _procedureDebugHost.TryGetTarget(out var hostInterp))
            {
                var s = hostInterp.DebugSession;
                if (s != null)
                {
                    try
                    {
                        if (!s.ContinueCancellationToken.IsCancellationRequested)
                            dbg = s;
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }

            if (dbg != null)
                await _clawProcedureDebugGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var interpreter = new Interpreter();
                if (_kernelConfig != null)
                    interpreter.Configuration = _kernelConfig;
                interpreter.DebugSession = dbg;
                interpreter.CurrentSocketContext = socketCtx;

                var callInfo = new CallInfo { FunctionName = procedureName };
                callInfo.Parameters["0"] = data;

                var result = await interpreter.InterpreteFromEntryAsync(_unit, procedureName, callInfo).ConfigureAwait(false);
                if (!result.Success)
                    throw new InvalidOperationException($"Procedure '{procedureName}' execution failed.");

                return interpreter.Stack.Count > 0 ? interpreter.Stack[interpreter.Stack.Count - 1] : null;
            }
            finally
            {
                if (dbg != null)
                    _clawProcedureDebugGate.Release();
            }
        }

        private static async Task<string> ReadBodyAsync(HttpListenerRequest req)
        {
            using var ms = new MemoryStream();
            await req.InputStream.CopyToAsync(ms).ConfigureAwait(false);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static async Task<int> WriteJsonResponse(HttpListenerResponse resp, int statusCode, object body)
        {
            resp.StatusCode = statusCode;
            var json = JsonSerializer.Serialize(body, JsonResponseOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            resp.OutputStream.Close();
            return statusCode;
        }

        private string GenerateJwtToken(string user)
        {
            var now = DateTime.UtcNow;
            var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
            var signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, user),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            };

            var token = new JwtSecurityToken(
                claims: claims,
                notBefore: now,
                expires: now.Add(_jwtTtl),
                signingCredentials: signingCredentials);

            return _jwtTokenHandler.WriteToken(token);
        }

        private static string ExtractBearerToken(string authHeader)
        {
            var s = (authHeader ?? "").Trim();
            if (s.Length == 0) return "";

            while (true)
            {
                if (s.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
                {
                    var after = s.Substring("Bearer".Length).TrimStart();
                    if (after.Length == 0 || after.Equals(s, StringComparison.Ordinal)) break;
                    s = after;
                    continue;
                }

                break;
            }

            return s;
        }

        private bool TryGetUserFromJwt(string jwtToken, out string user)
        {
            user = "";
            try
            {
                var tokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret)),
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(10)
                };

                var principal = _jwtTokenHandler.ValidateToken(jwtToken, tokenValidationParameters, out _);
                var sub = principal.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value;
                if (string.IsNullOrWhiteSpace(sub)) return false;

                user = sub;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object? JsonElementToObject(JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.Object => JsonElementToDict(el),
                JsonValueKind.Array => JsonElementToList(el),
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : (object)el.GetDouble(),
                JsonValueKind.True => (object)true,
                JsonValueKind.False => (object)false,
                _ => (object?)null
            };
        }

        private static Dictionary<string, object> JsonElementToDict(JsonElement el)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var prop in el.EnumerateObject())
            {
                var v = JsonElementToObject(prop.Value);
                if (v != null) d[prop.Name] = v;
            }
            return d;
        }

        private static List<object> JsonElementToList(JsonElement el)
        {
            var list = new List<object>();
            foreach (var item in el.EnumerateArray())
            {
                var v = JsonElementToObject(item);
                if (v != null) list.Add(v);
            }
            return list;
        }

        public Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync()
            => Task.FromResult((DeviceOperationResult.Fail(DeviceOperationState.Failed, "Not supported"), Array.Empty<byte>()));

        public Task<DeviceOperationResult> WriteAsync(byte[] bytes)
            => Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.Failed, "Not supported"));

        public Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl)
            => Task.FromResult(DeviceOperationResult.Success);

        public Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync()
            => Task.FromResult<(DeviceOperationResult, IStreamChunk?)>((DeviceOperationResult.Fail(DeviceOperationState.Failed, "Not supported"), null));

        public Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk)
            => Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.Failed, "Not supported"));

        public Task<DeviceOperationResult> MoveAsync(StructurePosition? position)
            => Task.FromResult(DeviceOperationResult.Success);

        public Task<(DeviceOperationResult Result, long Length)> LengthAsync()
            => Task.FromResult((DeviceOperationResult.Success, 0L));

        private sealed record Credential(string User, string Password);
    }

    /// <summary>Progressive lockout durations: 10s, 30s, 1m, 5m, 15m, 30m, 1h, 1d, permanent.</summary>
    internal sealed class UserLockout
    {
        private static readonly TimeSpan[] Durations =
        {
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1),
            TimeSpan.FromDays(1),
            TimeSpan.MaxValue
        };

        private int _failureCount;
        private DateTime _lockedUntil = DateTime.MinValue;

        public void RecordFailure()
        {
            var idx = Math.Min(_failureCount, Durations.Length - 1);
            var duration = Durations[idx];
            _lockedUntil = duration == TimeSpan.MaxValue ? DateTime.MaxValue : DateTime.UtcNow.Add(duration);
            _failureCount++;
        }

        public bool IsLocked() => DateTime.UtcNow < _lockedUntil;

        public long RemainingSeconds()
        {
            if (_lockedUntil == DateTime.MaxValue) return long.MaxValue;
            var remaining = _lockedUntil - DateTime.UtcNow;
            return remaining.Ticks > 0 ? (long)remaining.TotalSeconds + 1 : 0;
        }

        public void Reset()
        {
            _failureCount = 0;
            _lockedUntil = DateTime.MinValue;
        }
    }
}
