// ============================================================================
// Radius relay authentication — port of experimental/radius-auth.ts (4g-3d-1)
// ============================================================================
//
// The relay connects to the Radius gateway on behalf of a server, and needs a token for every
// attempt: an explicitly supplied one (inline or from a file) wins, otherwise the stored Radius
// credential is resolved through a model runtime that is created once and shared. Credentials are
// resolved *anew per attempt* — a token that expires mid-session must not be cached here.
//
// Offline mode short-circuits before anything else: a required relay connection fails loudly, an
// optional one degrades to "no relay".

using System.Text;
using Pi.Ai.Auth.OAuth;
using Pi.CodingAgent.Cli;
using Pi.CodingAgent.Cli.Experimental;
using Pi.CodingAgent.Core;

namespace Pi.CodingAgent.Experimental;

/// <summary>TS <c>RadiusRelayAuth</c>.</summary>
public sealed record RadiusRelayAuth(string Gateway, string Token);

/// <summary>Options of <see cref="RadiusRelayAuthResolver.ResolveAsync"/>. TS the inline object type.</summary>
public sealed record ResolveRadiusRelayAuthOptions
{
    /// <summary>Whether the caller cannot run without a relay connection.</summary>
    public required bool Required { get; init; }

    /// <summary>Optional cancellation, checked before every await point that can block.</summary>
    public CancellationToken Signal { get; init; }
}

/// <summary>
/// Resolve explicit or stored Radius credentials anew for every relay connection attempt.
/// Port of <c>RadiusRelayAuthResolver</c>.
/// </summary>
public sealed class RadiusRelayAuthResolver
{
    private readonly AuthInput? _input;
    private readonly string _gateway;
    private Task<ModelRuntime>? _modelRuntime;

    public RadiusRelayAuthResolver(AuthInput? input = null, string? gateway = null)
    {
        _input = input;
        _gateway = RadiusConfig.NormalizeGatewayUrl(gateway ?? Radius.GetGatewayUrl());
    }

    /// <summary>The normalized gateway origin this resolver hands out.</summary>
    public string Gateway => _gateway;

    /// <summary>
    /// Test seam (InternalsVisibleTo): replaces the on-demand model runtime. TS creates its runtime
    /// inline from <c>ModelRuntime.create</c>; the port needs an injection point because the built-in
    /// Radius provider is not part of the test process.
    /// </summary>
    internal Func<Task<ModelRuntime>>? RuntimeFactory { get; init; }

    /// <summary>
    /// Test seam (InternalsVisibleTo): the <c>process.env</c> lookup for the offline check. Instance
    /// scoped so parallel tests cannot observe each other's environment.
    /// </summary>
    internal Func<string, string?>? Env { get; init; }

    /// <summary>TS <c>resolve</c>.</summary>
    public async Task<RadiusRelayAuth?> ResolveAsync(ResolveRadiusRelayAuthOptions options)
    {
        options.Signal.ThrowIfCancellationRequested();
        if ((Env ?? Environment.GetEnvironmentVariable)("PI_OFFLINE") is not null)
        {
            if (options.Required)
            {
                throw new InvalidOperationException("Radius relay connections are unavailable in offline mode");
            }

            return null;
        }

        var explicitToken = await ExplicitTokenAsync(options.Signal).ConfigureAwait(false);
        if (explicitToken is not null)
        {
            return new RadiusRelayAuth(_gateway, explicitToken);
        }

        _modelRuntime ??= RuntimeFactory is not null
            ? RuntimeFactory()
            : ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
            {
                RefreshOnCreate = false,
                AllowModelNetwork = false,
            });
        var runtime = await _modelRuntime.ConfigureAwait(false);
        options.Signal.ThrowIfCancellationRequested();
        var token = AuthCredentials.GetAuthCredential(await runtime.GetAuthAsync(Radius.ProviderId,
            new ModelRuntimeAuthOverrides
            {
                MinOAuthValidityMs = 5 * 60_000,
                Signal = options.Signal,
            }).ConfigureAwait(false));
        if (token is not null && token.Length > 0)
        {
            return new RadiusRelayAuth(_gateway, token);
        }

        if (options.Required)
        {
            throw new InvalidOperationException(
                "Radius authentication is required; start Pi and run /login radius, then retry");
        }

        return null;
    }

    /// <summary>TS <c>#explicitToken</c>: the inline token, or the trimmed contents of the file.</summary>
    private async Task<string?> ExplicitTokenAsync(CancellationToken signal)
    {
        if (_input is null) return null;
        var value = _input switch
        {
            AuthInput.Token token => token.Value,
            AuthInput.File file => await File.ReadAllTextAsync(
                Utils.Paths.ResolvePath(file.Path), Encoding.UTF8, signal).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported Radius auth input: {_input.GetType().Name}"),
        };
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("Radius authentication token must not be empty");
        }

        return trimmed;
    }
}
