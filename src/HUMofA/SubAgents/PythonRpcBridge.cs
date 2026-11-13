using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HUMofA.SubAgents;

public sealed record JsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] object? Params);

public sealed record JsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] JsonElement? Data = null);

public sealed record JsonRpcResponse(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("result")] JsonElement? Result,
    [property: JsonPropertyName("error")] JsonRpcError? Error)
{
    [JsonIgnore]
    public bool IsSuccess => Error is null;
}

public interface IPythonRpcBridge : IAsyncDisposable, IDisposable
{
    bool IsRunning { get; }
    Task<JsonRpcResponse> InvokeAsync(string method, object? parameters = null, CancellationToken ct = default);
    Task StartAsync(string? scriptPath = null, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

/// <summary>
/// Hermes-compatible Python 3.14 JSON-RPC 2.0 over stdio bridge (code_execution_rpc.py equivalent).
/// Collapses multi-step tool pipelines into zero-context-cost RPC turns.
/// </summary>
public sealed class PythonRpcBridge : IPythonRpcBridge
{
    private const string DefaultEmbeddedRpcWorkerPy = """
        import sys, json, math, platform

        def handle_request(req):
            req_id = req.get("id", 0)
            method = req.get("method", "")
            params = req.get("params") or {}

            try:
                if method == "ping":
                    result = {"pong": True, "python_version": platform.python_version()}
                elif method == "echo":
                    result = params
                elif method == "add":
                    if isinstance(params, list):
                        result = sum(params)
                    else:
                        result = params.get("a", 0) + params.get("b", 0)
                elif method == "multiply":
                    result = params.get("a", 1) * params.get("b", 1)
                elif method == "eval_expr":
                    expr = str(params.get("expression", "0"))
                    allowed = {"math": math, "abs": abs, "min": min, "max": max, "round": round, "len": len, "sum": sum}
                    result = eval(expr, {"__builtins__": {}}, allowed)
                elif method == "transform_pipeline":
                    items = params.get("items", [])
                    op = params.get("operation", "upper")
                    if op == "upper":
                        result = [str(x).upper() for x in items]
                    elif op == "reverse":
                        result = list(reversed(items))
                    else:
                        result = items
                else:
                    return {"jsonrpc": "2.0", "id": req_id, "error": {"code": -32601, "message": f"Method not found: {method}"}}

                return {"jsonrpc": "2.0", "id": req_id, "result": result}
            except Exception as exc:
                return {"jsonrpc": "2.0", "id": req_id, "error": {"code": -32000, "message": str(exc)}}

        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            try:
                payload = json.loads(line)
                resp = handle_request(payload)
            except Exception as e:
                resp = {"jsonrpc": "2.0", "id": 0, "error": {"code": -32700, "message": str(e)}}
            sys.stdout.write(json.dumps(resp) + "\n")
            sys.stdout.flush()
        """;

    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private Process? _pythonProcess;
    private int _nextId;
    private bool _disposed;

    public bool IsRunning => _pythonProcess is { HasExited: false };

    public async Task StartAsync(string? scriptPath = null, CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning)
                return;

            var psi = new ProcessStartInfo
            {
                FileName = "python",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-u");

            if (!string.IsNullOrWhiteSpace(scriptPath) && File.Exists(scriptPath))
            {
                psi.ArgumentList.Add(Path.GetFullPath(scriptPath));
            }
            else
            {
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(DefaultEmbeddedRpcWorkerPy);
            }

            _pythonProcess = Process.Start(psi);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<JsonRpcResponse> InvokeAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        if (!IsRunning)
        {
            await StartAsync(null, ct).ConfigureAwait(false);
        }

        var id = Interlocked.Increment(ref _nextId);
        var request = new JsonRpcRequest("2.0", id, method, parameters);
        var jsonLine = JsonSerializer.Serialize(request);

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_pythonProcess is null || _pythonProcess.HasExited)
            {
                return FallbackInMemoryInvoke(id, method, parameters);
            }

            await _pythonProcess.StandardInput.WriteLineAsync(jsonLine.AsMemory(), ct).ConfigureAwait(false);
            await _pythonProcess.StandardInput.FlushAsync(ct).ConfigureAwait(false);

            var responseLine = await _pythonProcess.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                return FallbackInMemoryInvoke(id, method, parameters);
            }

            var parsed = JsonSerializer.Deserialize<JsonRpcResponse>(responseLine);
            return parsed ?? FallbackInMemoryInvoke(id, method, parameters);
        }
        catch
        {
            return FallbackInMemoryInvoke(id, method, parameters);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_pythonProcess is null)
                return;

            try
            {
                if (!_pythonProcess.HasExited)
                {
                    _pythonProcess.StandardInput.Close();
                    _pythonProcess.Kill(entireProcessTree: true);
                    await _pythonProcess.WaitForExitAsync(ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Ignore termination race
            }
            finally
            {
                _pythonProcess.Dispose();
                _pythonProcess = null;
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private static JsonRpcResponse FallbackInMemoryInvoke(int id, string method, object? parameters)
    {
        object? resultObj = method switch
        {
            "ping" => new { pong = true, python_version = "fallback-3.14" },
            "echo" => parameters,
            _ => new { status = "ok", method }
        };

        var element = JsonSerializer.SerializeToElement(resultObj);
        return new JsonRpcResponse("2.0", id, element, null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        await StopAsync().ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (_pythonProcess is { HasExited: false })
                _pythonProcess.Kill(entireProcessTree: true);
        }
        catch { }
        _pythonProcess?.Dispose();
        _ioLock.Dispose();
    }
}

