using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Extensions.Mcp;
using Pi.Tui;

namespace Pi.CodingAgent.Cli;

// ============================================================================
// `pi auth login` / `pi auth logout` — TS v1.5.1 (9b9cb30ae)
//   cli/auth-login.ts / auth-logout.ts / auth-terminal.ts
//   core/provider-matching.ts / radius-mcp.ts / utils/web-url.ts
//
// 采用「纯决策叶子 + seam 编排」：解析、provider 匹配、auth 类型选择、URL 归一化、
// Radius MCP 计划都是可独立回归的静态叶子；编排层通过 IAuthLoginRuntime / ILoginTerminal
// 注入，使 481 行上游 auth-login.test.ts 的行为向量可在无网络、无真实终端下复刻。
// ============================================================================

/// <summary>解析后的 `pi auth login` 请求。</summary>
internal sealed record LoginRequest(string ProviderRef, bool ApiKey, bool Browser);

/// <summary><c>parseLoginRequest</c> 的结果。</summary>
internal sealed record LoginRequestParse(bool Valid, LoginRequest? Request, string? Message);

/// <summary><c>selectAuthType</c> 的结果：可用类型或不可用原因文案。</summary>
internal sealed record AuthTypeChoice(bool Available, CredentialKind Type, string? UnavailableMessage);

/// <summary>全局 mcp.json 中指向 Radius 登录的服务器写入计划。</summary>
internal sealed record RadiusMcpPlan(string McpPath, string Name, McpServerConfig Config);

/// <summary>纯决策叶子：对应上游 auth-login.ts / provider-matching.ts / web-url.ts / radius-mcp.ts。</summary>
internal static class AuthLoginLogic
{
    public static string LoginUsage(string appName) => $"{appName} auth login <provider> [--api-key] [--no-browser]";

    public static string LogoutUsage(string appName) => $"{appName} auth logout <provider>";

    /// <summary>对应 TS <c>parseLoginRequest</c>：逐参数解析，冲突/多余即返回 usage 或错误文案。</summary>
    public static LoginRequestParse ParseLoginRequest(IReadOnlyList<string> args, string appName)
    {
        var usage = new LoginRequestParse(false, null, $"Usage: {LoginUsage(appName)}");
        var providerRef = "";
        var apiKey = false;
        var browser = true;
        foreach (var arg in args)
        {
            if (arg == "--api-key")
            {
                apiKey = true;
            }
            else if (arg == "--no-browser")
            {
                browser = false;
            }
            else if (arg.StartsWith("-", StringComparison.Ordinal) || providerRef.Length > 0)
            {
                return usage;
            }
            else
            {
                providerRef = arg;
            }
        }

        if (providerRef.Length == 0)
        {
            return usage;
        }

        if (apiKey && !browser)
        {
            return new LoginRequestParse(
                false, null, "--no-browser only applies to account sign-in; omit it with --api-key.");
        }

        return new LoginRequestParse(true, new LoginRequest(providerRef, apiKey, browser), null);
    }

    /// <summary>对应 TS <c>findMatchingProviders</c>：按 id / name 精确匹配，忽略大小写与首尾空白。</summary>
    public static IReadOnlyList<T> FindMatchingProviders<T>(
        IEnumerable<T> providers, string providerRef, Func<T, string> id, Func<T, string> name)
    {
        var normalized = providerRef.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return [];
        }

        return providers.Where(p =>
            string.Equals(id(p), normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name(p), normalized, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>对应 TS <c>selectAuthType</c>。</summary>
    public static AuthTypeChoice SelectAuthType(
        ProviderAuth? auth, string providerName, bool apiKey, string appName)
    {
        var hasApiKeyLogin = auth?.ApiKey?.HasLogin == true;
        if (!apiKey && auth?.OAuth is not null)
        {
            return new AuthTypeChoice(true, CredentialKind.OAuth, null);
        }

        if (hasApiKeyLogin)
        {
            return new AuthTypeChoice(true, CredentialKind.ApiKey, null);
        }

        if (apiKey && auth?.OAuth is not null)
        {
            return new AuthTypeChoice(
                false, default, $"{providerName} has no API key login. Omit --api-key to sign in.");
        }

        var method = auth?.ApiKey?.Name ?? "ambient credentials";
        return new AuthTypeChoice(
            false, default, $"{providerName} is configured outside {appName} ({method}).");
    }

    /// <summary>对应 TS <c>webUrl</c>：仅 http/https 绝对 URL，归一化为 href。</summary>
    public static string? WebUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }

    /// <summary>对应 TS <c>displayUrl</c>：非 web URL 时替换控制字符，防终端注入。</summary>
    public static string DisplayUrl(string value) => WebUrl(value) ?? Ansi.ReplaceControlCharacters(value);

    /// <summary>
    /// 对应 TS <c>planRadiusMcpServer</c>：给定现有 servers，返回写入计划或 null（已有同 provider 登录）。
    /// <paramref name="mcpNamespace"/> 注入 <c>McpServers.Namespace</c>（= 上游 mcpNamespace）。
    /// </summary>
    public static RadiusMcpPlan? PlanRadiusMcpServer(
        IReadOnlyList<McpServerEntry> servers, string providerId, string mcpPath,
        string radiusMcpUrl, Func<string, string> mcpNamespace)
    {
        static string Normalize(string url) => url.TrimEnd('/');
        var existing = servers.FirstOrDefault(
            s => s.Config.Url is { } u && Normalize(u) == Normalize(radiusMcpUrl));
        if (existing is not null && existing.Config.AuthProvider == providerId)
        {
            return null;
        }

        var name = existing?.Name ?? "radius";
        if (existing is null)
        {
            var namespaces = new HashSet<string>(
                servers.Select(s => mcpNamespace(s.Name)), StringComparer.Ordinal);
            for (var suffix = 1; namespaces.Contains(mcpNamespace(name)); suffix++)
            {
                name = suffix == 1 ? "radius-mcp" : $"radius-mcp-{suffix}";
            }
        }

        JsonObject config;
        if (existing is not null && existing.Config.Url is not null)
        {
            config = (JsonObject)existing.Config.Value.DeepClone();
            config["auth"] = new JsonObject { ["provider"] = providerId };
            config.Remove("oauth");
        }
        else
        {
            config = new JsonObject
            {
                ["url"] = radiusMcpUrl,
                ["auth"] = new JsonObject { ["provider"] = providerId },
            };
        }

        return new RadiusMcpPlan(mcpPath, name, new McpServerConfig(config));
    }
}

/// <summary>编排所需的运行期能力（真实包装 ModelRuntime，测试注入脚本化实现）。</summary>
internal interface IAuthLoginRuntime
{
    IReadOnlyList<IProvider> GetProviders();

    Task<Credential> LoginAsync(
        string providerId, CredentialKind type, ProviderAuthInteraction interaction, LoginOptions? options);

    Task LogoutAsync(string providerId, CancellationToken ct);

    Task<IReadOnlyList<CredentialInfo>> ListCredentialsAsync(CancellationToken ct);

    Task<ModelsRefreshResult> RefreshAsync(string providerId, CancellationToken ct);
}

/// <summary>命令行登录终端（真实包装 Console，测试脚本化记录）。对应 TS <c>LoginTerminal</c> / <c>AuthOutput</c>。</summary>
internal interface ILoginTerminal
{
    bool Interactive { get; }

    void Write(string line);

    void Error(string line);

    void OpenUrl(string url);

    Task<string> Question(string message, CancellationToken ct);

    Task<string> Secret(string message, CancellationToken ct);
}

internal sealed record AuthLoginOptions(
    IAuthLoginRuntime Runtime,
    ILoginTerminal Terminal,
    string AgentDir,
    string Cwd,
    Func<string> GetDeviceId,
    CancellationToken Signal,
    string AppName,
    string AuthPath,
    string RadiusMcpUrl,
    Func<IReadOnlyList<McpServerEntry>> LoadMcpServers,
    Func<RadiusMcpPlan, bool> ApplyMcpPlan,
    Func<string, string> McpNamespace);

internal sealed record AuthLogoutOptions(
    IAuthLoginRuntime Runtime, ILoginTerminal Terminal, CancellationToken Signal, string AppName);

/// <summary>
/// 对应 TS <c>createLoginInteraction</c>：把 provider 的提示/事件适配到选定登录方法的终端交互。
/// 实现端口 <see cref="IAuthInteraction"/>，整体 signal 由外层 <see cref="ProviderAuthInteraction"/> 承载，
/// prompt 级 signal 由 <c>PromptAsync</c> 的 <c>cancellationToken</c> 承载，二者合并为 linked token。
/// </summary>
internal sealed class LoginInteraction : IAuthInteraction
{
    private readonly string _providerId;
    private readonly LoginRequest _request;
    private readonly ILoginTerminal _terminal;
    private readonly CancellationToken _signal;
    private readonly string _appName;
    private readonly string _methodChoice;
    private List<string> _skippedMethods = [];

    public LoginInteraction(
        string providerId, CredentialKind authType, LoginRequest request,
        ILoginTerminal terminal, CancellationToken signal, string appName)
    {
        _providerId = providerId;
        _request = request;
        _terminal = terminal;
        _signal = signal;
        _appName = appName;
        // 账号登录：browser 默认取浏览器方法、--no-browser 取其它方法；API key 设置每次都问。
        _methodChoice = authType == CredentialKind.ApiKey ? "ask" : request.Browser ? "browser" : "other";
    }

    public async Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_signal, cancellationToken);
        var promptSignal = linked.Token;
        if (prompt is AuthPrompt.Select select)
        {
            return await SelectMethodAsync(select, promptSignal).ConfigureAwait(false);
        }

        var (message, placeholder, isSecret) = prompt switch
        {
            AuthPrompt.Secret s => (s.Message, s.Placeholder, true),
            AuthPrompt.Text t => (t.Message, t.Placeholder, false),
            AuthPrompt.ManualCode m => (m.Message, m.Placeholder, false),
            _ => (string.Empty, (string?)null, false),
        };
        var suffix = placeholder is not null ? $" ({placeholder})" : "";
        var text = $"{message}{suffix}: ";
        return isSecret
            ? await _terminal.Secret(text, promptSignal).ConfigureAwait(false)
            : await _terminal.Question(text, promptSignal).ConfigureAwait(false);
    }

    private async Task<string> SelectMethodAsync(AuthPrompt.Select prompt, CancellationToken ct)
    {
        var options = prompt.Options;
        var first = options.Count > 0 ? options[0] : null;
        var others = options.Skip(1).ToList();
        if (_methodChoice == "ask" || first is null)
        {
            return await SelectInTerminalAsync(prompt.Message, options, ct).ConfigureAwait(false);
        }

        if (_methodChoice == "browser")
        {
            _skippedMethods = others.Select(o => o.Label).ToList();
            return first.Id;
        }

        var onlyOther = others.Count > 0 ? others[0] : null;
        var moreOthers = others.Skip(1).ToList();
        if (onlyOther is null)
        {
            // 提供者只有浏览器登录这一个方法，仍用它，只是不开浏览器。
            return first.Id;
        }

        if (moreOthers.Count > 0)
        {
            return await SelectInTerminalAsync(prompt.Message, others, ct).ConfigureAwait(false);
        }

        _terminal.Write($"{prompt.Message} {onlyOther.Label}");
        return onlyOther.Id;
    }

    private async Task<string> SelectInTerminalAsync(
        string message, IReadOnlyList<AuthSelectOption> options, CancellationToken ct)
    {
        _terminal.Write(message);
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            _terminal.Write(
                $"  {i + 1}. {option.Label}{(option.Description is not null ? $" - {option.Description}" : "")}");
        }

        while (true)
        {
            var answer = (await _terminal.Question($"Select 1-{options.Count}: ", ct).ConfigureAwait(false)).Trim();
            var option = answer.Length > 0 && answer.All(c => c is >= '0' and <= '9')
                         && int.TryParse(answer, out var number) && number >= 1 && number <= options.Count
                ? options[number - 1]
                : null;
            if (option is not null)
            {
                return option.Id;
            }

            _terminal.Write($"Enter a number from 1 to {options.Count}.");
        }
    }

    public void Notify(AuthEvent @event)
    {
        switch (@event)
        {
            case AuthEvent.AuthUrl authUrl:
                _terminal.Write($"Open this URL to sign in:\n  {AuthLoginLogic.DisplayUrl(authUrl.Url)}");
                if (authUrl.Instructions is not null)
                {
                    _terminal.Write(authUrl.Instructions);
                }

                var target = AuthLoginLogic.WebUrl(authUrl.Url);
                if (target is not null && _request.Browser)
                {
                    _terminal.OpenUrl(target);
                }

                var rerun = $"\"{_appName} auth login {_providerId} --no-browser\"";
                var only = _skippedMethods.Count > 0 ? _skippedMethods[0] : null;
                if (only is not null && _skippedMethods.Count == 1)
                {
                    _terminal.Write($"To use \"{only}\" instead, run {rerun}");
                }
                else if (only is not null)
                {
                    var labels = string.Join(", ", _skippedMethods.Select(label => $"\"{label}\""));
                    _terminal.Write($"Other sign-in methods: {labels}. Run {rerun} to choose one.");
                }

                break;

            case AuthEvent.DeviceCode device:
                _terminal.Write(
                    $"Open {AuthLoginLogic.DisplayUrl(device.VerificationUri)} and enter code: {device.UserCode}");
                var deviceTarget = AuthLoginLogic.WebUrl(device.VerificationUri);
                // 端口 AuthEvent.DeviceCode 无 openBrowserOnConfirm 字段（默认视为 true），与上游一致开浏览器。
                if (deviceTarget is not null && _request.Browser)
                {
                    _terminal.OpenUrl(deviceTarget);
                }

                _terminal.Write("Waiting for authentication...");
                break;

            case AuthEvent.Info info:
                _terminal.Write(info.Message);
                foreach (var link in info.Links ?? [])
                {
                    var label = link.Label is not null ? $"{link.Label}: " : "";
                    _terminal.Write($"  {label}{AuthLoginLogic.DisplayUrl(link.Url)}");
                }

                break;

            default:
                if (@event is AuthEvent.Progress progress)
                {
                    _terminal.Write(progress.Message);
                }

                break;
        }
    }
}

/// <summary>对应 TS <c>runAuthLogin</c>。</summary>
internal static class AuthLoginCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, AuthLoginOptions options)
    {
        var (runtime, terminal, signal, appName) =
            (options.Runtime, options.Terminal, options.Signal, options.AppName);

        var parsed = AuthLoginLogic.ParseLoginRequest(args, appName);
        if (!parsed.Valid)
        {
            terminal.Error(parsed.Message!);
            return 1;
        }

        var request = parsed.Request!;
        var provider = AuthLoginLogic
            .FindMatchingProviders(runtime.GetProviders(), request.ProviderRef, p => p.Id, p => p.Name)
            .FirstOrDefault();
        if (provider is null)
        {
            var loginProviders = runtime.GetProviders()
                .Where(p => p.Auth?.OAuth is not null || p.Auth?.ApiKey?.HasLogin == true)
                .Select(p => p.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            terminal.Error($"Unknown provider \"{Ansi.ReplaceControlCharacters(request.ProviderRef)}\".");
            terminal.Error($"Providers: {string.Join(", ", loginProviders)}");
            return 1;
        }

        if (!terminal.Interactive)
        {
            terminal.Error($"\"{AuthLoginLogic.LoginUsage(appName)}\" requires an interactive terminal");
            return 1;
        }

        var choice = AuthLoginLogic.SelectAuthType(provider.Auth, provider.Name, request.ApiKey, appName);
        if (!choice.Available)
        {
            terminal.Error(choice.UnavailableMessage!);
            return 1;
        }

        var interaction = new LoginInteraction(provider.Id, choice.Type, request, terminal, signal, appName);
        var providerInteraction = new ProviderAuthInteraction(interaction, signal);
        try
        {
            await runtime.LoginAsync(
                provider.Id, choice.Type, providerInteraction,
                new LoginOptions { GetDeviceId = options.GetDeviceId }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var message = error.Message;
            if (error is CredentialSynchronizationError)
            {
                terminal.Error(
                    $"Logged in to {provider.Name}, but local model state could not be synchronized: {message}");
            }
            else if (signal.IsCancellationRequested || message == "Login cancelled")
            {
                terminal.Error("Login cancelled.");
            }
            else
            {
                terminal.Error($"Failed to log in to {provider.Name}: {message}");
            }

            return 1;
        }

        var headline = choice.Type == CredentialKind.OAuth
            ? $"Logged in to {provider.Name}"
            : $"Saved API key for {provider.Name}";
        terminal.Write($"{headline}. Credentials saved to {options.AuthPath}");
        await RefreshProviderCatalogAsync(provider, options).ConfigureAwait(false);
        if (provider.Id == Radius.ProviderId)
        {
            ConfigureRadiusMcpServer(provider, options);
        }

        return 0;
    }

    /// <summary>对应 TS <c>refreshProviderCatalog</c>。</summary>
    private static async Task RefreshProviderCatalogAsync(IProvider provider, AuthLoginOptions options)
    {
        void Cancelled() => options.Terminal.Write(
            $"Skipped the {provider.Name} model catalog refresh; {options.AppName} refreshes it on next start.");
        try
        {
            var result = await options.Runtime.RefreshAsync(provider.Id, options.Signal).ConfigureAwait(false);
            if (options.Signal.IsCancellationRequested)
            {
                Cancelled();
            }
            else if (result.Aborted)
            {
                options.Terminal.Write(
                    $"The {provider.Name} model catalog refresh timed out; {options.AppName} refreshes it on next start.");
            }
            else if (result.Errors.Count > 0)
            {
                options.Terminal.Write(
                    $"The {provider.Name} model catalog could not be refreshed; {options.AppName} retries on next start.");
            }
        }
        catch (Exception error)
        {
            if (options.Signal.IsCancellationRequested)
            {
                Cancelled();
                return;
            }

            options.Terminal.Write(
                $"The {provider.Name} model catalog could not be refreshed: {error.Message}");
        }
    }

    /// <summary>对应 TS <c>configureRadiusMcpServer</c>。</summary>
    private static void ConfigureRadiusMcpServer(IProvider provider, AuthLoginOptions options)
    {
        var mcpPath = Path.Combine(options.AgentDir, "mcp.json");
        var plan = AuthLoginLogic.PlanRadiusMcpServer(
            options.LoadMcpServers(), provider.Id, mcpPath, options.RadiusMcpUrl, options.McpNamespace);
        if (plan is null)
        {
            return;
        }

        try
        {
            var replaced = options.ApplyMcpPlan(plan);
            options.Terminal.Write(
                $"{(replaced ? "Updated" : "Added")} MCP server \"{plan.Name}\" in {plan.McpPath} " +
                $"to use the {provider.Name} login.");
        }
        catch (Exception error)
        {
            options.Terminal.Error($"Could not update {plan.McpPath}: {error.Message}");
        }
    }
}

/// <summary>对应 TS <c>runAuthLogout</c>。</summary>
internal static class AuthLogoutCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, AuthLogoutOptions options)
    {
        var (runtime, terminal, signal, appName) =
            (options.Runtime, options.Terminal, options.Signal, options.AppName);
        if (args.Count == 0 || args[0].StartsWith("-", StringComparison.Ordinal) || args.Count > 1)
        {
            terminal.Error($"Usage: {AuthLoginLogic.LogoutUsage(appName)}");
            return 1;
        }

        var providerRef = args[0];
        IReadOnlyList<CredentialInfo> stored;
        try
        {
            stored = await runtime.ListCredentialsAsync(signal).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            terminal.Error($"Could not read stored credentials: {error.Message}");
            return 1;
        }

        // 扩展注册的 provider 这里没加载，其存储凭据按 id 单独匹配。
        var provider = AuthLoginLogic
            .FindMatchingProviders(runtime.GetProviders(), providerRef, p => p.Id, p => p.Name)
            .FirstOrDefault();
        var credential = stored.FirstOrDefault(c => c.ProviderId == (provider?.Id ?? providerRef));
        if (provider is null && credential is null)
        {
            terminal.Error($"Unknown provider \"{Ansi.ReplaceControlCharacters(providerRef)}\".");
            var storedIds = stored.Select(c => c.ProviderId).OrderBy(id => id, StringComparer.Ordinal).ToList();
            terminal.Error($"Stored credentials: {(storedIds.Count > 0 ? string.Join(", ", storedIds) : "none")}");
            return 1;
        }

        var providerName = provider?.Name ?? providerRef;
        if (credential is null)
        {
            terminal.Write(
                $"No stored credentials for {providerName}. Environment variables and models.json config are unchanged.");
            return 0;
        }

        try
        {
            await runtime.LogoutAsync(credential.ProviderId, signal).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var message = error.Message;
            if (error is CredentialSynchronizationError)
            {
                terminal.Error(
                    $"Credentials removed for {providerName}, but local model state could not be synchronized: {message}");
            }
            else
            {
                terminal.Error($"Logout failed: {message}");
            }

            return 1;
        }

        terminal.Write(credential.Type == CredentialKind.OAuth
            ? $"Logged out of {providerName}."
            : $"Removed stored API key for {providerName}. Environment variables and models.json config are unchanged.");
        return 0;
    }
}
