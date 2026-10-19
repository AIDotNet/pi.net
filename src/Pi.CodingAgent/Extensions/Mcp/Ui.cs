// ============================================================================
// MCP manager UI contract — port of extensions/mcp/ui.ts
// ============================================================================
//
// This file defines the data and callback contract the `/mcp` manager is written against
// (`McpMenu`, `IMcpUi`). `McpManagerView` supplies the TUI implementation and is the default
// `McpExtensionOptions.ShowManager`; hosts can still replace it for tests or alternate front ends.
//
// Keeping the two halves apart is what makes the manager logic testable today: everything below is
// plain data, so a test drives `/mcp` end to end with a scripted UI instead of a terminal.

using Pi.Tui.Components;

namespace Pi.CodingAgent.Extensions.Mcp;

/// <summary>
/// One menu of the manager. TS <c>McpMenu</c>.
/// </summary>
public sealed record McpMenu
{
    public required string Title { get; init; }

    /// <summary>Shown below the title.</summary>
    public string? Details { get; init; }

    /// <summary>Shown below the details in the error color.</summary>
    public string? Error { get; init; }

    public required IReadOnlyList<SelectItem> Items { get; init; }

    /// <summary>Shown when there are no items.</summary>
    public string? Empty { get; init; }

    /// <summary>Value of the item selected when the menu opens.</summary>
    public string? Selected { get; init; }

    /// <summary>What the confirm key does, for the key hint.</summary>
    public required string ConfirmLabel { get; init; }

    /// <summary>What the cancel key does, for the key hint.</summary>
    public required string CancelLabel { get; init; }
}

/// <summary>
/// The interactive surface the manager uses. TS <c>McpUi</c>.
/// </summary>
/// <remarks>
/// TS passes the <c>AbortSignal</c> of the sign-in prompt as a parameter; the port passes a
/// <see cref="CancellationToken"/>, which is also what
/// <see cref="OAuth.IMcpSignInPrompt.PromptForRedirectUrlAsync"/> takes, so the two stay one value.
/// </remarks>
public interface IMcpUi
{
    /// <summary>
    /// Show a menu and resolve to the chosen item's value, or null when cancelled.
    /// <paramref name="subscribe"/> rebuilds the menu on every change, keeping the selected item.
    /// </summary>
    Task<string?> MenuAsync(Func<McpMenu> build, Func<Action, Action>? subscribe = null);

    /// <summary>Show a message while an operation runs.</summary>
    void Status(string title, string message);

    /// <summary>
    /// Show the authorization URL and wait for a pasted redirect URL. Resolves to null when
    /// cancelled or when <paramref name="signal"/> is cancelled (the browser reached the callback).
    /// </summary>
    Task<string?> RedirectUrlAsync(string title, string authorizationUrl, CancellationToken signal);
}
