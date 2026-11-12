using System.Text;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Cli;

// ============================================================================
// `pi auth login/logout` 的真实接线：进程终端 I/O + ModelRuntime 包装。
// 对应 TS cli/auth-terminal.ts 的 createProcessLoginTerminal 与 main.ts 的运行期构造。
// ============================================================================

/// <summary>包装 <see cref="ModelRuntime"/> 为 <see cref="IAuthLoginRuntime"/>。</summary>
internal sealed class ModelRuntimeAuthLoginRuntime(ModelRuntime runtime) : IAuthLoginRuntime
{
    public IReadOnlyList<IProvider> GetProviders() => runtime.GetProviders();

    public Task<Credential> LoginAsync(
        string providerId, CredentialKind type, ProviderAuthInteraction interaction, LoginOptions? options)
        => runtime.LoginAsync(providerId, type, interaction, options);

    public Task LogoutAsync(string providerId, CancellationToken ct) => runtime.LogoutAsync(providerId, ct);

    public Task<IReadOnlyList<CredentialInfo>> ListCredentialsAsync(CancellationToken ct)
        => runtime.ListCredentialsAsync(ct);

    public Task<ModelsRefreshResult> RefreshAsync(string providerId, CancellationToken ct)
        => runtime.RefreshAsync(new ModelsRefreshOptions { Providers = [providerId], Signal = ct });
}

/// <summary>
/// 对应 TS <c>createProcessLoginTerminal(interrupt)</c>：Console 上的行式 I/O。
/// question 读一行（EOF/中断回调 <paramref name="onInterrupt"/>），secret 逐键无回显读取。
/// </summary>
internal sealed class ProcessLoginTerminal(TextWriter output, TextWriter error, Action onInterrupt) : ILoginTerminal
{
    public bool Interactive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public void Write(string line) => output.WriteLine(line);

    public void Error(string line) => error.WriteLine(line);

    public void OpenUrl(string url) => OpenBrowser.Open(url);

    public async Task<string> Question(string message, CancellationToken ct)
    {
        output.Write(message);
        var line = await Console.In.ReadLineAsync().WaitAsync(ct).ConfigureAwait(false);
        if (line is null)
        {
            onInterrupt();
            throw new OperationCanceledException(ct);
        }

        return line;
    }

    public async Task<string> Secret(string message, CancellationToken ct)
    {
        output.Write(message);
        if (Console.IsInputRedirected)
        {
            var line = await Console.In.ReadLineAsync().WaitAsync(ct).ConfigureAwait(false);
            output.WriteLine();
            return line ?? string.Empty;
        }

        var buffer = new StringBuilder();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    break;
                }

                if (key.Key == ConsoleKey.Backspace && buffer.Length > 0)
                {
                    buffer.Length--;
                }
                else if (key.KeyChar >= ' ')
                {
                    buffer.Append(key.KeyChar);
                }

                continue;
            }

            await Task.Delay(15, ct).ConfigureAwait(false);
        }

        output.WriteLine();
        return buffer.ToString();
    }
}
