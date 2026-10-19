using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Pi.Evals;

internal sealed class AcmeFixtureServer : IAsyncDisposable
{
    public const string OpenAiProviderId = "acme";
    public const string OpenAiModelId = "acme-chat";
    public const string OpenAiProbePrompt = "Reply with ACME_OK.";
    public const string OpenAiProbeResponse = "ACME_OK";
    public const string StreamProviderId = "acme-stream";
    public const string StreamModelId = "acme-stream-chat";
    public const string StreamProbePrompt = "Reply with ACME_STREAM_OK.";
    public const string StreamProbeResponse = "ACME_STREAM_OK";
    public const string StreamApiKey = "resolved-stream-key";
    public const string OpenAiApiKey = "resolved-acme-key";

    public const string StreamApiDocumentation = """
        {
          "name": "Acme Streaming API",
          "request": {
            "method": "POST",
            "path": "/generate",
            "headers": {
              "content-type": "application/json",
              "x-acme-key": "resolved credential"
            },
            "body": {
              "model": "acme-stream-chat",
              "messages": [{ "role": "user", "content": "Hello" }],
              "stream": true
            }
          },
          "response": {
            "contentType": "application/x-ndjson",
            "events": [
              { "type": "text_delta", "text": "Hello" },
              { "type": "usage", "input_tokens": 3, "output_tokens": 2 },
              { "type": "done", "reason": "stop" }
            ]
          }
        }
        """;

    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly byte[] HeaderEnd = [13, 10, 13, 10];

    private readonly string _mode;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _listenTask;
    private int _validRequestReceived;
    private Exception? _serverError;

    private AcmeFixtureServer(string mode)
    {
        _mode = mode;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Origin = $"http://127.0.0.1:{endpoint.Port}";
        _listenTask = ListenAsync();
    }

    public string Origin { get; }

    public string OpenAiBaseUrl => $"{Origin}/v1";

    public bool ValidRequestReceived => Volatile.Read(ref _validRequestReceived) == 1;

    public string ProviderId => _mode == "openai" ? OpenAiProviderId : StreamProviderId;

    public string ModelId => _mode == "openai" ? OpenAiModelId : StreamModelId;

    public string ProbePrompt => _mode == "openai" ? OpenAiProbePrompt : StreamProbePrompt;

    public string ProbeResponse => _mode == "openai" ? OpenAiProbeResponse : StreamProbeResponse;

    public static AcmeFixtureServer Start(string mode)
    {
        if (mode is not ("openai" or "stream"))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Expected openai or stream mode.");
        }

        return new AcmeFixtureServer(mode);
    }

    public void Reset()
    {
        Volatile.Write(ref _validRequestReceived, 0);
        _serverError = null;
    }

    public void ThrowIfFailed()
    {
        if (_serverError is { } error)
        {
            throw new InvalidOperationException("The Acme fixture server failed.", error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        try
        {
            await _listenTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                await HandleClientAsync(client, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _serverError = error;
            throw;
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        try
        {
            var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
            var (status, contentType, body) = CreateResponse(request);
            await WriteResponseAsync(stream, status, contentType, body, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException error)
        {
            var body = Encoding.UTF8.GetBytes(error.Message);
            await WriteResponseAsync(stream, 400, "text/plain", body, cancellationToken).ConfigureAwait(false);
        }
    }

    private (int Status, string ContentType, byte[] Body) CreateResponse(FixtureRequest request)
    {
        if (request.Method != "POST")
        {
            return TextResponse(405, "Expected POST");
        }

        if (!request.Headers.TryGetValue("content-type", out var contentType)
            || !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return TextResponse(415, "Expected application/json");
        }

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return TextResponse(422, "Expected a JSON object");
        }

        var prompt = FindUserPrompt(root);
        if (_mode == "stream")
        {
            if (request.Path != "/generate")
            {
                return TextResponse(404, "Unknown endpoint");
            }

            if (!request.Headers.TryGetValue("x-acme-key", out var key) || key != StreamApiKey)
            {
                return TextResponse(401, "Invalid Acme Stream credential");
            }

            if (!HasString(root, "model", StreamModelId) || prompt is null || !HasBoolean(root, "stream", true))
            {
                return TextResponse(422, "Invalid Acme Stream request");
            }

            if (prompt == StreamProbePrompt)
            {
                Volatile.Write(ref _validRequestReceived, 1);
            }

            return (
                200,
                "application/x-ndjson",
                Encoding.UTF8.GetBytes(
                    "{\"type\":\"text_delta\",\"text\":\"ACME_\"}\n"
                    + "{\"type\":\"text_delta\",\"text\":\"STREAM_OK\"}\n"
                    + "{\"type\":\"usage\",\"input_tokens\":4,\"output_tokens\":3}\n"
                    + "{\"type\":\"done\",\"reason\":\"stop\"}\n"));
        }

        if (request.Path != "/v1/chat/completions")
        {
            return TextResponse(404, "Unknown endpoint");
        }

        if (!request.Headers.TryGetValue("authorization", out var authorization) || authorization != "******")
        {
            return TextResponse(401, "Invalid Acme credential");
        }

        if (!HasString(root, "model", OpenAiModelId) || prompt is null || !HasBoolean(root, "stream", true))
        {
            return TextResponse(422, "Invalid OpenAI-compatible request");
        }

        if (prompt == OpenAiProbePrompt)
        {
            Volatile.Write(ref _validRequestReceived, 1);
        }

        var firstChunk = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl-acme",
            ["object"] = "chat.completion.chunk",
            ["created"] = 0,
            ["model"] = OpenAiModelId,
            ["choices"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = OpenAiProbeResponse,
                    },
                    ["finish_reason"] = null,
                },
            },
        });
        var finalChunk = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl-acme",
            ["object"] = "chat.completion.chunk",
            ["created"] = 0,
            ["model"] = OpenAiModelId,
            ["choices"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["delta"] = new Dictionary<string, object?>(),
                    ["finish_reason"] = "stop",
                },
            },
            ["usage"] = new Dictionary<string, object?>
            {
                ["prompt_tokens"] = 3,
                ["completion_tokens"] = 2,
            },
        });
        var response = $"data: {firstChunk}\n\ndata: {finalChunk}\n\ndata: [DONE]\n\n";
        return (200, "text/event-stream", Encoding.UTF8.GetBytes(response));
    }

    private static async Task<FixtureRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var received = new MemoryStream();
        var buffer = new byte[8192];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("Request ended before its headers were complete.");
            }

            received.Write(buffer, 0, read);
            if (received.Length > MaxHeaderBytes)
            {
                throw new InvalidDataException("Request headers are too large.");
            }

            headerEnd = FindHeaderEnd(received.GetBuffer().AsSpan(0, checked((int)received.Length)));
        }

        var bytes = received.GetBuffer();
        var headerText = Encoding.Latin1.GetString(bytes, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3)
        {
            throw new InvalidDataException("Invalid HTTP request line.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidDataException("Invalid HTTP request header.");
            }

            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        if (headers.ContainsKey("transfer-encoding")
            || !headers.TryGetValue("content-length", out var lengthText)
            || !int.TryParse(lengthText, out var contentLength)
            || contentLength < 0
            || contentLength > MaxBodyBytes)
        {
            throw new InvalidDataException("A valid bounded Content-Length is required.");
        }

        var bodyStart = headerEnd + HeaderEnd.Length;
        var requiredLength = bodyStart + contentLength;
        while (received.Length < requiredLength)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("Request body ended before Content-Length bytes arrived.");
            }

            received.Write(buffer, 0, read);
            if (received.Length > requiredLength)
            {
                throw new InvalidDataException("Unexpected bytes after request body.");
            }
        }

        var body = Encoding.UTF8.GetString(bytes, bodyStart, contentLength);
        return new FixtureRequest(requestLine[0], requestLine[1], headers, body);
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index <= bytes.Length - HeaderEnd.Length; index++)
        {
            if (bytes.Slice(index, HeaderEnd.Length).SequenceEqual(HeaderEnd))
            {
                return index;
            }
        }

        return -1;
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        int status,
        string contentType,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            405 => "Method Not Allowed",
            415 => "Unsupported Media Type",
            422 => "Unprocessable Entity",
            _ => "Error",
        };
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n"
            + $"Content-Type: {contentType}\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + "Connection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (int Status, string ContentType, byte[] Body) TextResponse(int status, string message)
        => (status, "text/plain", Encoding.UTF8.GetBytes(message));

    private static string? FindUserPrompt(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object
                || !HasString(message, "role", "user")
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            return content.GetString();
        }

        return null;
    }

    private static bool HasString(JsonElement value, string name, string expected)
        => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() == expected;

    private static bool HasBoolean(JsonElement value, string name, bool expected)
        => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            && property.GetBoolean() == expected;

    private sealed record FixtureRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        string Body);
}
