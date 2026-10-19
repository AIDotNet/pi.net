using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using AiText = Pi.Ai.Utils.Text;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.Evals;

public sealed record EvalModelSelection(string Provider, string Id);

public abstract record EvalInputStep
{
    public sealed record Prompt(string Content) : EvalInputStep;

    public sealed record Reload : EvalInputStep;
}

public sealed record EvalHarnessContext(
    AgentSession Session,
    AgentSessionServices Services,
    string AgentDir,
    string Response,
    string SystemPrompt,
    CancellationToken CancellationToken);

public sealed record EvalTranscriptEvent
{
    public required string Type { get; init; }

    public string? Role { get; init; }

    public string? Content { get; init; }

    public string? Id { get; init; }

    public string? Name { get; init; }

    public JsonElement? Arguments { get; init; }

    public string? ToolCallId { get; init; }

    public JsonElement? Error { get; init; }
}

public sealed record EvalHarnessResult
{
    public required string Provider { get; init; }

    public required string Model { get; init; }

    public JsonElement Output { get; init; }

    public IReadOnlyList<EvalTranscriptEvent> Events { get; init; } = [];

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public EvalMetrics Metrics { get; init; } = new();

    public IReadOnlyList<string> Errors { get; init; } = [];

    public string? SessionJsonl { get; init; }
}

public sealed record EvalHarnessOptions
{
    public string? Name { get; init; }

    public EvalModelSelection? Model { get; init; }

    public NoToolsMode? NoTools { get; init; }

    public IReadOnlyList<string>? Tools { get; init; }

    public IReadOnlyList<ToolDefinition>? CustomTools { get; init; }

    public bool EnableExtensionCompiler { get; init; }

    public IReadOnlyDictionary<string, string>? WorkspaceFiles { get; init; }

    public IReadOnlyDictionary<string, string>? EnvironmentOverrides { get; init; }

    public Func<string, string>? TransformSystemPrompt { get; init; }

    public bool? ExpectedPiDocumentation { get; init; }

    public Func<EvalHarnessContext, Task<JsonElement>>? Output { get; init; }
}

public static class EvalHarness
{
    public const string PiSessionSnapshotArtifact = EvalReport.PiSessionSnapshotArtifact;

    public static readonly IReadOnlyList<string> DocumentationTools =
        ["read", "write", "edit", "grep", "find", "ls"];

    public static EvalModelSelection ResolveModelSelection(
        EvalModelSelection? explicitModel,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var provider = explicitModel?.Provider
            ?? GetEnvironmentValue(environment, "PI_PROVIDER");
        var model = explicitModel?.Id
            ?? GetEnvironmentValue(environment, "PI_MODEL");
        provider = provider?.Trim();
        model = model?.Trim();
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(model))
        {
            throw new InvalidOperationException(
                "Select a harness model explicitly or set both PI_PROVIDER and PI_MODEL as defaults.");
        }

        return new EvalModelSelection(provider, model);
    }

    public static IReadOnlyDictionary<string, string?> CurrentEnvironment()
        => Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(
                entry => (string)entry.Key,
                entry => entry.Value?.ToString(),
                StringComparer.Ordinal);

    public static IDisposable ApplyIsolatedEnvironment(
        string home,
        string agentDir,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (overrides is not null && overrides.Keys.Any(
                name => string.IsNullOrWhiteSpace(name) || name.StartsWith("PI_EVAL_", StringComparison.Ordinal)))
        {
            throw new ArgumentException("Eval environment overrides cannot set PI_EVAL_* variables.", nameof(overrides));
        }

        var saved = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in CurrentEnvironment().Keys.Where(name => name.StartsWith("PI_EVAL_", StringComparison.Ordinal)))
        {
            saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        var replacements = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["PI_CODING_AGENT_DIR"] = agentDir,
        };
        if (overrides is not null)
        {
            foreach (var (name, value) in overrides)
            {
                replacements[name] = value;
            }
        }

        foreach (var (name, value) in replacements)
        {
            if (!saved.ContainsKey(name))
            {
                saved[name] = Environment.GetEnvironmentVariable(name);
            }

            Environment.SetEnvironmentVariable(name, value);
        }

        return new EnvironmentRestorer(saved);
    }

    public static DocumentationVariant ResolveDocumentationVariant(string? value = null)
    {
        value ??= Environment.GetEnvironmentVariable("PI_EVAL_VARIANT");
        return value switch
        {
            "without_docs" => DocumentationVariant.WithoutDocs,
            "with_docs" => DocumentationVariant.WithDocs,
            _ => throw new InvalidOperationException(
                "PI_EVAL_VARIANT must be \"without_docs\" or \"with_docs\"."),
        };
    }

    public static string ExcludePiDocumentation(string systemPrompt)
    {
        const string startMarker = "\n<docs>\n";
        const string endMarker = "\n</docs>";
        var start = systemPrompt.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("Default Pi system prompt has no Pi documentation section.");
        }

        var end = systemPrompt.IndexOf(endMarker, start, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException("Default Pi system prompt has no complete Pi documentation section.");
        }

        var cwd = systemPrompt.LastIndexOf("\n<cwd>\n", StringComparison.Ordinal);
        if (cwd < end)
        {
            throw new InvalidOperationException("Default Pi system prompt has no working-directory section.");
        }

        return string.Concat(systemPrompt.AsSpan(0, start), systemPrompt.AsSpan(end + endMarker.Length));
    }

    public static string VerifySystemPrompt(
        string systemPrompt,
        string? name,
        bool? expectedPiDocumentation)
    {
        if (expectedPiDocumentation is null)
        {
            return systemPrompt;
        }

        if (!systemPrompt.Contains("\n<rules>\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Pi system prompt lost its rules in the {name} eval variant.");
        }

        var hasDocumentation = systemPrompt.Contains("\n<docs>\nPi documentation (read only", StringComparison.Ordinal);
        if (hasDocumentation != expectedPiDocumentation.Value)
        {
            throw new InvalidOperationException($"Pi system prompt does not match the {name} eval variant.");
        }

        return systemPrompt;
    }

    public static EvalHarnessOptions CreatePiDocumentationEvalHarness(EvalHarnessOptions? options = null)
    {
        options ??= new EvalHarnessOptions();
        var container = Environment.GetEnvironmentVariable("PI_EVAL_CONTAINER") == "1";
        if (!container || ResolveSandboxIdentity() is null)
        {
            throw new InvalidOperationException("Documentation evals must run in the isolated container sandbox.");
        }

        var variant = ResolveDocumentationVariant();
        return options with
        {
            Name = variant == DocumentationVariant.WithoutDocs ? "without_docs" : "with_docs",
            Tools = options.Tools ?? DocumentationTools,
            TransformSystemPrompt = variant == DocumentationVariant.WithoutDocs
                ? ExcludePiDocumentation
                : options.TransformSystemPrompt,
            ExpectedPiDocumentation = variant == DocumentationVariant.WithDocs,
        };
    }

    public static async Task<EvalHarnessResult> RunAsync(
        IReadOnlyList<EvalInputStep> input,
        EvalHarnessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new EvalHarnessOptions();
        if (input.Count == 0 || !input.Any(step => step is EvalInputStep.Prompt))
        {
            throw new ArgumentException("Pi eval input must include at least one prompt step.", nameof(input));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var selection = ResolveModelSelection(options.Model);
        var hostAgentDir = Config.GetAgentDir();
        var isContainer = Environment.GetEnvironmentVariable("PI_EVAL_CONTAINER") == "1";
        var sandboxIdentity = ResolveSandboxIdentity();
        var root = Directory.CreateTempSubdirectory("pi-eval-").FullName;
        var workspace = Path.Combine(root, "workspace");
        var isolatedHome = Path.Combine(root, "home");
        var agentDir = Path.Combine(isolatedHome, ".pi", "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        var secretValues = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var events = new List<EvalTranscriptEvent>();
        var metrics = new EvalMetrics();
        JsonElement output = JsonSerializer.SerializeToElement<object?>(null, EvalJson.Options);
        string? sessionJsonl = null;
        string? response = null;
        string? systemPrompt = null;
        ModelRuntime? modelRuntime = null;
        ModelSpec? selectedModel = null;
        SessionManager? sessionManager = null;
        AgentSession? session = null;
        IDisposable? environmentRestorer = null;
        Exception? runError = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            environmentRestorer = ApplyIsolatedEnvironment(
                isolatedHome,
                agentDir,
                options.EnvironmentOverrides);
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(agentDir);
            Directory.CreateDirectory(sessionDirectory);
            SeedWorkspace(workspace, options.WorkspaceFiles);

            var credentials = new InMemoryCredentialStore();
            var storedCredential = ReadStoredCredential(selection.Provider, hostAgentDir);
            if (storedCredential is not null)
            {
                await credentials.ModifyAsync(
                    selection.Provider,
                    _ => Task.FromResult<Credential?>(storedCredential),
                    cancellationToken).ConfigureAwait(false);
                if (storedCredential is Credential.ApiKey { Key: { Length: > 0 } key })
                {
                    secretValues.Add(key);
                }
                else if (storedCredential is Credential.OAuth oauth)
                {
                    secretValues.Add(oauth.Access);
                    secretValues.Add(oauth.Refresh);
                }
            }

            modelRuntime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
            {
                Credentials = credentials,
                RefreshOnCreate = true,
                AllowModelNetwork = false,
                Signal = cancellationToken,
            }).ConfigureAwait(false);
            selectedModel = modelRuntime.GetModel(selection.Provider, selection.Id)
                ?? throw new InvalidOperationException($"Eval model not found: {selection.Provider}/{selection.Id}");
            var auth = await modelRuntime.GetAuthAsync(selectedModel).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Eval model has no configured authentication: {selection.Provider}/{selection.Id}");
            if (auth.Auth.ApiKey is { Length: > 0 } apiKey)
            {
                secretValues.Add(apiKey);
                if (storedCredential is null)
                {
                    await modelRuntime.SetRuntimeApiKeyAsync(selection.Provider, apiKey, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var authPath = Path.Combine(hostAgentDir, "auth.json");
            if (sandboxIdentity is not null)
            {
                if (!isContainer
                    || !IsWithin(Path.GetTempPath(), hostAgentDir))
                {
                    throw new InvalidOperationException(
                        "Credential-file removal is permitted only for the container-owned eval auth directory.");
                }

                if (File.Exists(authPath))
                {
                    File.Delete(authPath);
                }

                if (auth.Source is { } source && IsEnvironmentName(source))
                {
                    var value = Environment.GetEnvironmentVariable(source);
                    if (!string.IsNullOrEmpty(value))
                    {
                        secretValues.Add(value);
                        Environment.SetEnvironmentVariable(source, null);
                    }
                }
            }

            var extensionFactories = new List<InlineExtension>();
            if (options.TransformSystemPrompt is { } transform)
            {
                extensionFactories.Add(CreateSystemPromptExtension(transform, prompt => systemPrompt = prompt));
            }

            var services = await AgentSessionServicesBuilder.CreateAgentSessionServicesAsync(
                new CreateAgentSessionServicesOptions
                {
                    Cwd = workspace,
                    AgentDir = agentDir,
                    ModelRuntime = modelRuntime,
                    ResourceLoaderOptions = new ResourceLoaderExtraOptions
                    {
                        ExtensionFactories = extensionFactories,
                    },
                },
                cancellationToken).ConfigureAwait(false);

            sessionManager = SessionManager.Create(workspace, sessionDirectory);
            metadata["runId"] = sessionManager.GetSessionId();
            var created = await AgentSessionServicesBuilder.CreateAgentSessionFromServicesAsync(
                new CreateAgentSessionFromServicesOptions
                {
                    Services = services,
                    SessionManager = sessionManager,
                    Model = selectedModel,
                    ThinkingLevel = ThinkingLevel.Off,
                    Tools = options.Tools,
                    NoTools = options.NoTools,
                    CustomTools = options.EnableExtensionCompiler
                        ? [.. (options.CustomTools ?? []), ExtensionCompiler.CreateTool(agentDir)]
                        : options.CustomTools,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            session = created.Session;

            var expectedInlinePaths = extensionFactories
                .OfType<InlineExtension.Described>()
                .Select(extension => $"<inline:{extension.Name}>")
                .ToHashSet(StringComparer.Ordinal);
            var unexpectedExtensions = session.ExtensionRunner.GetExtensionPaths()
                .Where(path => !expectedInlinePaths.Contains(path))
                .ToArray();
            if (unexpectedExtensions.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Isolated eval loaded unexpected extensions: {string.Join(", ", unexpectedExtensions)}");
            }

            if (sandboxIdentity is not null)
            {
                EnterToolSandbox(
                    root,
                    sandboxIdentity.Value.Uid,
                    sandboxIdentity.Value.Gid,
                    isContainer);
            }

            foreach (var step in input)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (step is EvalInputStep.Reload)
                {
                    await session.ReloadAsync().ConfigureAwait(false);
                    continue;
                }

                response = await PromptAgentAsync(
                    session,
                    ((EvalInputStep.Prompt)step).Content,
                    cancellationToken).ConfigureAwait(false);
            }

            systemPrompt ??= session.SystemPrompt;
            VerifySystemPrompt(systemPrompt, options.Name, options.ExpectedPiDocumentation);
            metadata["systemPromptSha256"] = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(systemPrompt)));

            output = options.Output is null
                ? JsonSerializer.SerializeToElement(response, EvalJson.Options)
                : await options.Output(new EvalHarnessContext(
                    session,
                    services,
                    agentDir,
                    response ?? string.Empty,
                    systemPrompt,
                    cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            runError = error;
        }
        finally
        {
            stopwatch.Stop();
            if (session is not null)
            {
                try
                {
                    events = ToTranscriptEvents(session.Messages);
                    if (selectedModel is not null)
                    {
                        metrics = Metrics(session.GetSessionStats(), selectedModel);
                    }

                    systemPrompt ??= session.SystemPrompt;
                    if (!metadata.ContainsKey("systemPromptSha256"))
                    {
                        metadata["systemPromptSha256"] = Convert.ToHexStringLower(
                            SHA256.HashData(Encoding.UTF8.GetBytes(systemPrompt)));
                    }
                }
                catch (Exception error)
                {
                    errors.Add($"Session diagnostics failed: {Redact(error.ToString(), secretValues)}");
                }

                try
                {
                    session.Dispose();
                }
                catch (Exception error)
                {
                    errors.Add($"Session cleanup failed: {Redact(error.ToString(), secretValues)}");
                }
            }

            if (sessionManager is not null)
            {
                var sessionPath = sessionManager.GetSessionFile();
                if (sessionPath is null || !File.Exists(sessionPath))
                {
                    errors.Add("Pi eval produced no session file.");
                }
                else
                {
                    try
                    {
                        sessionJsonl = await File.ReadAllTextAsync(sessionPath, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        errors.Add($"Session snapshot failed: {Redact(error.ToString(), secretValues)}");
                    }
                }
            }

            if (runError is not null)
            {
                errors.Insert(0, Redact(runError.ToString(), secretValues));
            }

            if (root is not null && Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (Exception error)
                {
                    errors.Add($"Temporary directory cleanup failed: {Redact(error.ToString(), secretValues)}");
                }
            }

            environmentRestorer?.Dispose();
        }

        metrics = metrics with { TotalMs = stopwatch.Elapsed.TotalMilliseconds };
        return new EvalHarnessResult
        {
            Provider = selection.Provider,
            Model = selection.Id,
            Output = output,
            Events = events,
            Metadata = metadata,
            Metrics = metrics,
            Errors = errors,
            SessionJsonl = sessionJsonl,
        };
    }

    private static string? GetEnvironmentValue(
        IReadOnlyDictionary<string, string?>? environment,
        string name)
        => environment is null
            ? Environment.GetEnvironmentVariable(name)
            : environment.GetValueOrDefault(name);

    private static Credential? ReadStoredCredential(string provider, string agentDir)
    {
        var path = Path.Combine(agentDir, "auth.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Eval authentication file is invalid: {path}");
        }

        var auth = JsonSerializer.Deserialize<Dictionary<string, Credential>>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return auth?.GetValueOrDefault(provider);
    }

    private static bool IsEnvironmentName(string value)
        => value.Length > 0
            && value[0] is >= 'A' and <= 'Z'
            && value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    private static InlineExtension CreateSystemPromptExtension(
        Func<string, string> transform,
        Action<string> capture)
        => new InlineExtension.Described(
            "eval-system-prompt-transform",
            pi =>
            {
                pi.On<ExtensionEvent.BeforeAgentStart, BeforeAgentStartEventResult>(
                    ExtensionEventNames.BeforeAgentStart,
                    (@event, _) =>
                    {
                        var transformed = transform(@event.SystemPrompt);
                        capture(transformed);
                        return Task.FromResult<BeforeAgentStartEventResult?>(new()
                        {
                            SystemPrompt = transformed,
                        });
                    });
                return Task.CompletedTask;
            },
            Hidden: true);

    private static void SeedWorkspace(string workspace, IReadOnlyDictionary<string, string>? files)
    {
        if (files is null)
        {
            return;
        }

        foreach (var (name, content) in files)
        {
            if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name))
            {
                throw new ArgumentException($"Invalid workspace fixture path: {name}", nameof(files));
            }

            var path = Path.GetFullPath(Path.Combine(workspace, name));
            if (!IsWithin(workspace, path))
            {
                throw new ArgumentException($"Workspace fixture escapes the workspace: {name}", nameof(files));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static async Task<string> PromptAgentAsync(
        AgentSession session,
        string input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previousMessageCount = session.Messages.Count;
        Task? abortTask = null;
        using var registration = cancellationToken.Register(() => abortTask ??= session.AbortAsync());
        try
        {
            await session.PromptAsync(input).ConfigureAwait(false);
        }
        finally
        {
            if (abortTask is not null)
            {
                await abortTask.ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var assistant = session.Messages
            .Skip(previousMessageCount)
            .OfType<AssistantMessage>()
            .LastOrDefault()
            ?? throw new InvalidOperationException("Agent run completed without an assistant message.");
        if (assistant.StopReason is not (StopReason.Stop or StopReason.ToolUse))
        {
            throw new InvalidOperationException(
                assistant.ErrorMessage
                ?? $"Agent run ended with unexpected stop reason: {assistant.StopReason}.");
        }

        var output = session.GetLastAssistantText();
        if (string.IsNullOrEmpty(output) && assistant.StopReason == StopReason.Stop)
        {
            throw new InvalidOperationException("Agent run produced no assistant text.");
        }

        return output ?? string.Empty;
    }

    private static EvalMetrics Metrics(SessionStats stats, ModelSpec model)
    {
        var cost = model.Cost;
        var hasPricing = cost is not null
            && (cost.Input > 0 || cost.Output > 0 || cost.CacheRead > 0 || cost.CacheWrite > 0);
        return new EvalMetrics
        {
            InputTokens = stats.Tokens.Input,
            OutputTokens = stats.Tokens.Output,
            CacheReadTokens = stats.Tokens.CacheRead,
            CacheWriteTokens = stats.Tokens.CacheWrite,
            TotalTokens = stats.Tokens.Total,
            ToolCalls = stats.ToolCalls,
            EstimatedCostUsd = hasPricing ? stats.Cost : null,
        };
    }

    private static List<EvalTranscriptEvent> ToTranscriptEvents(IReadOnlyList<ChatMessage> messages)
    {
        var events = new List<EvalTranscriptEvent>();
        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage user:
                    events.Add(new EvalTranscriptEvent
                    {
                        Type = "message",
                        Role = "user",
                        Content = AiText.ContentText(user.Content),
                    });
                    break;

                case AssistantMessage assistant:
                    var text = AiText.ContentText(assistant.Content);
                    if (text.Length > 0)
                    {
                        events.Add(new EvalTranscriptEvent
                        {
                            Type = "message",
                            Role = "assistant",
                            Content = text,
                        });
                    }

                    foreach (var call in assistant.Content.OfType<ToolCallContent>())
                    {
                        events.Add(new EvalTranscriptEvent
                        {
                            Type = "tool_call",
                            Id = call.Id,
                            Name = call.Name,
                            Arguments = JsonSerializer.SerializeToElement(call.Arguments, EvalJson.Options),
                        });
                    }

                    break;

                case ToolResultMessage toolResult:
                    var content = AiText.ContentText(toolResult.Content);
                    events.Add(new EvalTranscriptEvent
                    {
                        Type = "tool_result",
                        ToolCallId = toolResult.ToolCallId,
                        Name = toolResult.ToolName,
                        Content = content,
                        Error = toolResult.IsError
                            ? JsonSerializer.SerializeToElement(new { message = content.Length == 0 ? "Tool failed" : content }, EvalJson.Options)
                            : null,
                    });
                    break;
            }
        }

        return events;
    }

    private static EvalSandboxIdentity? ResolveSandboxIdentity()
    {
        var uidText = Environment.GetEnvironmentVariable("PI_EVAL_SANDBOX_UID");
        var gidText = Environment.GetEnvironmentVariable("PI_EVAL_SANDBOX_GID");
        if (uidText is null && gidText is null)
        {
            return null;
        }

        if (!uint.TryParse(uidText, out var uid) || uid == 0
            || !uint.TryParse(gidText, out var gid) || gid == 0)
        {
            throw new InvalidOperationException(
                "Set both PI_EVAL_SANDBOX_UID and PI_EVAL_SANDBOX_GID to positive integers, or neither.");
        }

        return new EvalSandboxIdentity(uid, gid);
    }

    internal static void EnterToolSandbox(string root, uint uid, uint gid, bool readOnlyImage = false)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("The eval filesystem sandbox requires Linux user APIs.");
        }

        if (NativeMethods.getuid() != 0 || NativeMethods.geteuid() != 0)
        {
            throw new InvalidOperationException("The eval runner must start as root before entering the tool sandbox.");
        }

        var evaluatorAssembly = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(evaluatorAssembly) || !File.Exists(evaluatorAssembly))
        {
            throw new InvalidOperationException("Cannot protect the eval runner assembly before sandboxing.");
        }

        if (File.GetUnixFileMode(evaluatorAssembly) != UnixFileMode.UserRead)
        {
            if (readOnlyImage)
            {
                throw new InvalidOperationException(
                    "The read-only eval image must protect the eval runner assembly before startup.");
            }

            File.SetUnixFileMode(evaluatorAssembly, UnixFileMode.UserRead);
        }

        ChownTree(root, new EvalSandboxIdentity(uid, gid));
        NativeMethods.umask(0x3f);
        if (NativeMethods.setgroups(0, []) != 0
            || NativeMethods.setgid(gid) != 0
            || NativeMethods.setuid(uid) != 0)
        {
            throw new InvalidOperationException("Failed to enter the unprivileged eval tool sandbox.");
        }

        if (NativeMethods.getuid() != uid || NativeMethods.geteuid() != uid)
        {
            throw new InvalidOperationException("Failed to verify the unprivileged eval tool sandbox.");
        }

        try
        {
            using var stream = File.OpenRead(evaluatorAssembly);
            throw new InvalidOperationException("The unprivileged eval process can read the eval runner assembly.");
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ChownTree(string path, EvalSandboxIdentity identity)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"Cannot sandbox a symbolic link: {path}");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                ChownTree(entry, identity);
            }
        }

        if (NativeMethods.chown(path, identity.Uid, identity.Gid) != 0)
        {
            throw new IOException($"Cannot transfer eval sandbox ownership: {path}");
        }
    }

    private static string Redact(string text, IEnumerable<string> secrets)
    {
        foreach (var secret in secrets.Where(secret => secret.Length > 0))
        {
            text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }

        return text;
    }

    private readonly record struct EvalSandboxIdentity(uint Uid, uint Gid);

    private sealed class EnvironmentRestorer(Dictionary<string, string?> saved) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            _disposed = true;
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        internal static extern uint getuid();

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        internal static extern uint geteuid();

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        internal static extern int setgroups(uint size, uint[] groups);

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        internal static extern int setgid(uint gid);

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        internal static extern int setuid(uint uid);

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        internal static extern int chown(string path, uint owner, uint group);

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        internal static extern uint umask(uint mask);
    }
}
