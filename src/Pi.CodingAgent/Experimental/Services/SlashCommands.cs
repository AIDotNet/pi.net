// ============================================================================
// Slash command service contract — port of experimental/services/slash-commands.ts (4g-3b)
// ============================================================================
//
// A presentation contributes slash commands through this local service. `register` returns an
// unsubscribe handle; `replace` stages a same-name replacement while the previous facet generation
// retires, so a reload never leaves the command missing.

using Pi.Chord;
using Pi.Chord.Facets;
using Pi.Chord.Context;

namespace Pi.CodingAgent.Experimental.Services;

/// <summary>One argument completion. TS <c>SlashCommandCompletion</c>.</summary>
public sealed record SlashCommandCompletion(string Value, string Label, string? Description = null);

/// <summary>
/// One registered command. TS <c>SlashCommandContribution</c>; <c>getArgumentCompletions</c> is an
/// optional member there, and an optional property here (C314).
/// </summary>
public interface ISlashCommandContribution
{
    /// <summary>The command name, without the leading slash.</summary>
    string Name { get; }

    /// <summary>The one-line description shown in the command list.</summary>
    string? Description { get; }

    /// <summary>The argument hint shown next to the name, if any.</summary>
    string? ArgumentHint { get; }

    /// <summary>
    /// Optional argument completions. TS allows the implementation to answer synchronously or with a
    /// promise; the port always awaits, so a synchronous implementation returns a completed task.
    /// </summary>
    Func<string, Task<IReadOnlyList<SlashCommandCompletion>?>>? GetArgumentCompletions { get; }

    /// <summary>Run the command.</summary>
    Task<AgentSubmissionResponse?> RunAsync(string args, Context context);
}

/// <summary>Port of the TS <c>SlashCommands</c> interface.</summary>
public interface ISlashCommands
{
    /// <summary>Register a command; dispose the handle to remove it.</summary>
    IDisposable Register(ISlashCommandContribution command);

    /// <summary>Stage a same-name replacement while the previous facet generation retires.</summary>
    IDisposable Replace(ISlashCommandContribution command);

    /// <summary>The currently registered commands.</summary>
    IReadOnlyList<ISlashCommandContribution> List();

    /// <summary>Observe the command list; dispose the handle to stop observing.</summary>
    IDisposable Subscribe(Action<IReadOnlyList<ISlashCommandContribution>> listener);
}

/// <summary>TS <c>export const SlashCommands = defineService&lt;SlashCommands&gt;(…, { local: true })</c>.</summary>
public static class SlashCommands
{
    /// <summary>The chord service handle for <see cref="ISlashCommands"/>.</summary>
    public static readonly Service<ISlashCommands> Service =
        Api.DefineService<ISlashCommands>("pi.local.slash-commands", local: true);
}
