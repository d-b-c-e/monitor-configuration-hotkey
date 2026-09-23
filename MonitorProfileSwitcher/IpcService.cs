using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorProfileSwitcher;

/// <summary>
/// A local, current-user-only named pipe. Each message is a little-endian 32-bit byte length
/// followed by one UTF-8 JSON object, matching the framing used by XOutputRedux.
/// </summary>
internal sealed class IpcService : IDisposable
{
    public const string PipeName = "MonitorProfileSwitcher_IPC";
    public const int ProtocolVersion = 1;

    private const int MaxRequestBytes = 64 * 1024;
    private const int MaxResponseBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Func<IpcRequest, Task<IpcResponse>> _dispatch;
    private readonly CancellationTokenSource _shutdown = new();
    private NamedPipeServerStream? _activePipe;
    private Task? _serverTask;
    private bool _disposed;

    public IpcService(Func<IpcRequest, Task<IpcResponse>> dispatch) => _dispatch = dispatch;

    public void Start()
    {
        _serverTask = Task.Run(ServerLoopAsync);
        AppLog.Info($"IPC server starting on pipe {PipeName} (protocol {ProtocolVersion}, current user only).");
    }

    private async Task ServerLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _activePipe = pipe;
                await pipe.WaitForConnectionAsync(_shutdown.Token);
                await HandleClientAsync(pipe);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLog.Error("IPC server connection failed.", ex);
                await Task.Delay(500);
            }
            finally
            {
                _activePipe = null;
            }
        }

        AppLog.Info("IPC server stopped.");
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var request = await ReadFrameAsync<IpcRequest>(pipe, MaxRequestBytes, readTimeout.Token);
            IpcResponse response;
            if (request is null)
            {
                response = IpcResponse.Error("invalid_request", "The request is not a JSON object.");
            }
            else if (request.ProtocolVersion != ProtocolVersion)
            {
                response = IpcResponse.Error("unsupported_version", $"Expected protocol version {ProtocolVersion}.");
            }
            else
            {
                AppLog.Info($"IPC request: command='{request.Command}' profile='{request.ProfileName ?? ""}'.");
                response = await _dispatch(request);
                AppLog.Info($"IPC response: command='{request.Command}' success={response.Success} errorCode='{response.ErrorCode ?? ""}' operationId='{response.OperationId ?? ""}'.");
            }

            await WriteFrameAsync(pipe, response, _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // The tray application is closing.
        }
        catch (Exception ex)
        {
            AppLog.Error("IPC request failed.", ex);
            try
            {
                if (pipe.IsConnected)
                    await WriteFrameAsync(pipe, IpcResponse.Error("ipc_error", ex.Message), _shutdown.Token);
            }
            catch (Exception responseError)
            {
                AppLog.Warn($"Could not send IPC error response: {responseError.Message}");
            }
        }
    }

    public static async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync(connectTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return IpcResponse.Error("not_running", "Monitor Profile Switcher is not running or its IPC pipe is unavailable.");
        }
        catch (IOException)
        {
            return IpcResponse.Error("not_running", "Monitor Profile Switcher is not running or its IPC pipe is unavailable.");
        }

        using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        await WriteFrameAsync(client, request, responseTimeout.Token);
        return await ReadFrameAsync<IpcResponse>(client, MaxResponseBytes, responseTimeout.Token)
            ?? IpcResponse.Error("invalid_response", "The IPC response was empty.");
    }

    private static async Task<T?> ReadFrameAsync<T>(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maximumBytes)
            throw new InvalidDataException($"Invalid IPC frame length {length}.");

        var data = new byte[length];
        await stream.ReadExactlyAsync(data, cancellationToken);
        return JsonSerializer.Deserialize<T>(data, JsonOptions);
    }

    private static async Task WriteFrameAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _activePipe?.Dispose();
    }
}

internal sealed class IpcRequest
{
    public int ProtocolVersion { get; set; } = IpcService.ProtocolVersion;
    public string? Command { get; set; }
    public string? ProfileName { get; set; }
}

internal sealed class IpcResponse
{
    public int ProtocolVersion { get; set; } = IpcService.ProtocolVersion;
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
    public string? OperationId { get; set; }
    public string? ProfileName { get; set; }
    public IpcProfile[]? Profiles { get; set; }
    public IpcDisplayStatus? Status { get; set; }

    public static IpcResponse Error(string code, string message) => new()
    {
        Success = false,
        ErrorCode = code,
        Message = message,
    };
}

internal sealed class IpcProfile
{
    public required string Name { get; init; }
    public required string Topology { get; init; }
    public required string Summary { get; init; }
    public string? Hotkey { get; init; }
}

internal sealed class IpcDisplayStatus
{
    public required string Topology { get; init; }
    public required int MonitorCount { get; init; }
    public required uint DesktopWidth { get; init; }
    public required uint DesktopHeight { get; init; }
    public string? ActiveProfileName { get; init; }
    public uint? SurroundRows { get; init; }
    public uint? SurroundColumns { get; init; }
    public IpcMonitor[] Monitors { get; init; } = [];
}

internal sealed class IpcMonitor
{
    public required string Name { get; init; }
    public required uint Width { get; init; }
    public required uint Height { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required bool Primary { get; init; }
}
