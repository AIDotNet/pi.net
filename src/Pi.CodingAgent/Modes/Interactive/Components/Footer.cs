using System.Globalization;
using System.Text.RegularExpressions;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Extensions.Types;
using Pi.CodingAgent.Utils;
using JsNumber = Pi.CodingAgent.Utils.JsNumber;
using Pi.Tui;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Pure helpers of <c>footer.ts</c> (TS <c>sanitizeStatusText</c> / <c>formatTokens</c> /
/// <c>formatCwdForFooter</c>).
/// </summary>
public static partial class FooterFormatting
{
    [GeneratedRegex("[\r\n\t]")]
    private static partial Regex ControlCharacters();

    [GeneratedRegex(" +")]
    private static partial Regex SpaceRun();

    /// <summary>
    /// Sanitize text for display in a single-line status: newlines/tabs/CR become spaces,
    /// space runs collapse, then trim.
    /// </summary>
    public static string SanitizeStatusText(string text) =>
        SpaceRun().Replace(ControlCharacters().Replace(text, " "), " ").Trim();

    /// <summary>Format token counts for compact footer display.</summary>
    public static string FormatTokens(long count)
    {
        if (count < 1000) return count.ToString(CultureInfo.InvariantCulture);
        if (count < 10000) return JsNumber.ToFixed(count / 1000.0, 1) + "k";
        if (count < 1000000) return JsRound(count / 1000.0).ToString(CultureInfo.InvariantCulture) + "k";
        if (count < 10000000) return JsNumber.ToFixed(count / 1000000.0, 1) + "M";
        return JsRound(count / 1000000.0).ToString(CultureInfo.InvariantCulture) + "M";
    }

    /// <summary>Replace the home directory prefix with <c>~</c> (Node <c>path.relative</c> semantics).</summary>
    public static string FormatCwdForFooter(string cwd, string? home)
    {
        if (string.IsNullOrEmpty(home)) return cwd;

        var resolvedCwd = Path.GetFullPath(cwd);
        var resolvedHome = Path.GetFullPath(home);
        // Node's relative() yields "" for the same path; .NET yields ".".
        var relativeToHome = Path.GetRelativePath(resolvedHome, resolvedCwd);
        if (relativeToHome == ".") relativeToHome = "";

        var sep = Path.DirectorySeparatorChar;
        var isInsideHome =
            relativeToHome.Length == 0 ||
            (relativeToHome != ".." &&
             !relativeToHome.StartsWith($"..{sep}", StringComparison.Ordinal) &&
             !Path.IsPathRooted(relativeToHome));

        if (!isInsideHome) return cwd;
        return relativeToHome.Length == 0 ? "~" : $"~{sep}{relativeToHome}";
    }

    /// <summary>JS <c>Math.round</c> (half away from zero for positives; C# default is banker's).</summary>
    private static long JsRound(double value) => (long)Math.Floor(value + 0.5);
}

/// <summary>
/// Test seam: the slice of <see cref="AgentSession"/> the footer reads. TS tests build the session as
/// a plain object literal, which C# cannot do against a concrete class, so the port narrows the
/// dependency to this interface (same approach as the Batch E-2 <c>OpenOverride</c> seam).
/// </summary>
internal interface IFooterSessionView
{
    IReadOnlyList<SessionEntry> Entries { get; }

    int EntryCount { get; }

    string SessionId { get; }

    string? LeafId { get; }

    string? SessionName { get; }

    string Cwd { get; }

    Pi.Ai.Models.ModelSpec? Model { get; }

    ThinkingLevel ThinkingLevel { get; }

    ScopedModel? RoutedModel { get; }

    bool IsUsingSubscription(string provider);

    ContextUsage? GetContextUsage();
}

/// <summary>Adapter from the real session to <see cref="IFooterSessionView"/>.</summary>
internal sealed class AgentSessionFooterView(AgentSession session) : IFooterSessionView
{
    public IReadOnlyList<SessionEntry> Entries => session.SessionManager.GetEntries();

    public int EntryCount => session.SessionManager.GetEntryCount();

    public string SessionId => session.SessionManager.GetSessionId();

    public string? LeafId => session.SessionManager.GetLeafId();

    public string? SessionName => session.SessionManager.GetSessionName();

    public string Cwd => session.SessionManager.GetCwd();

    public Pi.Ai.Models.ModelSpec? Model => session.Model;

    public ThinkingLevel ThinkingLevel => session.ThinkingLevel;

    public ScopedModel? RoutedModel => session.RoutedModel;

    public bool IsUsingSubscription(string provider) => session.ModelRuntime.IsUsingSubscription(provider);

    public ContextUsage? GetContextUsage() => session.GetContextUsage();
}

/// <summary>
/// Footer component that shows pwd, token stats, and context usage
/// (TS <c>FooterComponent</c>). Computes token/context stats from the session and gets the
/// git branch and extension statuses from the provider.
/// </summary>
public sealed class FooterComponent : IComponent
{
    /// <summary>
    /// Usage totals and context usage scan the whole session, and the footer renders on every
    /// frame. Entries are append-only and every append moves the leaf, so the results only change
    /// with the session, leaf, entry count, or the model whose context window applies.
    /// </summary>
    private sealed record SessionStats(
        IFooterSessionView Session,
        string SessionId,
        string? LeafId,
        int EntryCount,
        Pi.Ai.Models.ModelSpec? LimitsModel,
        UsageTotals UsageTotals,
        double? LatestCacheHitRate,
        ContextUsage? ContextUsage);

    private bool _autoCompactEnabled = true;
    private IFooterSessionView _session;
    private readonly IReadonlyFooterDataProvider _footerData;
    private SessionStats? _sessionStats;

    public FooterComponent(AgentSession session, IReadonlyFooterDataProvider footerData)
        : this(new AgentSessionFooterView(session), footerData)
    {
    }

    internal FooterComponent(IFooterSessionView session, IReadonlyFooterDataProvider footerData)
    {
        _session = session;
        _footerData = footerData;
    }

    public void SetSession(AgentSession session) => _session = new AgentSessionFooterView(session);

    public void SetAutoCompactEnabled(bool enabled) => _autoCompactEnabled = enabled;

    /// <summary>No-op: git branch caching is handled by the provider (kept for call-site parity).</summary>
    public void Invalidate()
    {
    }

    /// <summary>No-op: git watcher cleanup is handled by the provider.</summary>
    public void Dispose()
    {
    }

    private SessionStats GetSessionStats()
    {
        var entryCount = _session.EntryCount;
        var sessionId = _session.SessionId;
        var leafId = _session.LeafId;
        var limitsModel = _session.RoutedModel?.Model ?? _session.Model;
        var cached = _sessionStats;
        if (cached is not null &&
            ReferenceEquals(cached.Session, _session) &&
            cached.SessionId == sessionId &&
            cached.LeafId == leafId &&
            cached.EntryCount == entryCount &&
            ReferenceEquals(cached.LimitsModel, limitsModel))
        {
            return cached;
        }

        // Cumulative usage from ALL session entries (not just post-compaction messages).
        var usageTotals = UsageTotalsApi.CreateUsageTotals();
        double? latestCacheHitRate = null;

        foreach (var entry in _session.Entries)
        {
            switch (entry)
            {
                case UsageEntry usageEntry:
                    UsageTotalsApi.AddUsageToTotals(usageTotals, usageEntry.Usage);
                    break;

                case SessionMessageEntry { Message: AssistantMessage assistant }:
                    if (assistant.UsageStats is { } assistantUsage)
                    {
                        UsageTotalsApi.AddUsageToTotals(usageTotals, assistantUsage);

                        var latestPromptTokens =
                            assistantUsage.Input + assistantUsage.CacheRead + assistantUsage.CacheWrite;
                        latestCacheHitRate = latestPromptTokens > 0
                            ? (double)assistantUsage.CacheRead / latestPromptTokens * 100
                            : null;
                    }

                    break;

                case SessionMessageEntry { Message: ToolResultMessage { UsageStats: { } toolUsage } }:
                    UsageTotalsApi.AddUsageToTotals(usageTotals, toolUsage);
                    break;

                case BranchSummaryEntry { UsageStats: { } branchUsage }:
                    UsageTotalsApi.AddUsageToTotals(usageTotals, branchUsage);
                    break;

                case CompactionEntry { UsageStats: { } compactionUsage }:
                    UsageTotalsApi.AddUsageToTotals(usageTotals, compactionUsage);
                    break;
            }
        }

        // Context usage from the session (handles compaction correctly). After compaction the
        // tokens are unknown until the next LLM response.
        var contextUsage = _session.GetContextUsage();
        _sessionStats = new SessionStats(
            _session, sessionId, leafId, entryCount, limitsModel, usageTotals, latestCacheHitRate, contextUsage);
        return _sessionStats;
    }

    public string[] Render(int width)
    {
        var theme = ThemeRuntime.Current;
        var stateModel = _session.Model;
        var stats = GetSessionStats();
        var usageTotals = stats.UsageTotals;
        var latestCacheHitRate = stats.LatestCacheHitRate;
        var contextUsage = stats.ContextUsage;

        var contextWindow = contextUsage?.ContextWindow ?? (double?)(stateModel?.ContextWindow) ?? 0;
        var contextPercentValue = contextUsage?.Percent ?? 0;
        // TS: `contextUsage?.percent !== null ? … : "?"` — undefined (no usage at all) takes the
        // numeric branch with 0, only an explicit null percent becomes "?".
        var contextPercent = contextUsage is not null && contextUsage.Percent is null
            ? "?"
            : JsNumber.ToFixed(contextPercentValue, 1);

        // Replace home directory with ~
        var pwd = FooterFormatting.FormatCwdForFooter(
            _session.Cwd,
            FirstNonEmpty(Environment.GetEnvironmentVariable("HOME"), Environment.GetEnvironmentVariable("USERPROFILE")));

        // Add git branch if available
        if (_footerData.GetGitBranch() is { Length: > 0 } branch)
        {
            pwd = $"{pwd} ({branch})";
        }

        // Add session name if set
        if (_session.SessionName is { Length: > 0 } sessionName)
        {
            pwd = $"{pwd} • {sessionName}";
        }

        // Build stats line
        var statsParts = new List<string>();
        if (usageTotals.Input != 0) statsParts.Add($"↑{FooterFormatting.FormatTokens(usageTotals.Input)}");
        if (usageTotals.Output != 0) statsParts.Add($"↓{FooterFormatting.FormatTokens(usageTotals.Output)}");
        if (usageTotals.CacheRead != 0) statsParts.Add($"R{FooterFormatting.FormatTokens(usageTotals.CacheRead)}");
        if (usageTotals.CacheWrite != 0) statsParts.Add($"W{FooterFormatting.FormatTokens(usageTotals.CacheWrite)}");
        if ((usageTotals.CacheRead > 0 || usageTotals.CacheWrite > 0) && latestCacheHitRate is { } hitRate)
        {
            statsParts.Add($"CH{JsNumber.ToFixed(hitRate, 1)}%");
        }

        // Kimi Coding is subscription-backed despite using API-key authentication.
        var usingSubscription = stateModel is { } model &&
            (model.Provider == "kimi-coding" || _session.IsUsingSubscription(model.Provider));
        if (usageTotals.Cost != 0 || usingSubscription)
        {
            statsParts.Add($"${JsNumber.ToFixed(usageTotals.Cost, 3)}{(usingSubscription ? " (sub)" : "")}");
        }

        // Colorize context percentage based on usage
        var autoIndicator = _autoCompactEnabled ? " (auto)" : "";
        var contextPercentDisplay = contextPercent == "?"
            ? $"?/{FooterFormatting.FormatTokens((long)contextWindow)}{autoIndicator}"
            : $"{contextPercent}%/{FooterFormatting.FormatTokens((long)contextWindow)}{autoIndicator}";
        var contextPercentStr =
            contextPercentValue > 90
                ? theme.Fg(ThemeColors.Error, contextPercentDisplay)
                : contextPercentValue > 70
                    ? theme.Fg(ThemeColors.Warning, contextPercentDisplay)
                    : contextPercentDisplay;
        statsParts.Add(contextPercentStr);
        if (Pi.CodingAgent.Core.Experimental.AreExperimentalFeaturesEnabled())
        {
            statsParts.Add($"{theme.Fg(ThemeColors.Dim, "•")} {theme.Bold(theme.Fg(ThemeColors.Warning, "xp"))}");
        }

        var statsLeft = string.Join(" ", statsParts);
        var statsLeftWidth = UnicodeWidth.VisibleWidth(statsLeft);

        // If statsLeft is too wide, truncate it
        if (statsLeftWidth > width)
        {
            statsLeft = TextLayout.TruncateToWidth(statsLeft, width, "...");
            statsLeftWidth = UnicodeWidth.VisibleWidth(statsLeft);
        }

        // Minimum 2 spaces between stats and model
        const int minPadding = 2;

        // Add thinking level indicator if model supports reasoning
        var modelName = stateModel?.Id ?? "no-model";
        var rightSideWithoutProvider = modelName;
        if (stateModel?.Reasoning == true)
        {
            var thinkingLevel = ThinkingLevels.ToText(_session.ThinkingLevel);
            rightSideWithoutProvider = thinkingLevel == "off"
                ? $"{modelName} • thinking off"
                : $"{modelName} • {thinkingLevel}";
        }

        // A virtual model routes each request; show where the latest response went.
        if (_session.RoutedModel is { } routed)
        {
            var level = routed.ThinkingLevel is { } routedLevel
                ? $" • {ThinkingLevels.ToText(routedLevel)}"
                : "";
            rightSideWithoutProvider += $" → {routed.Model.Id}{level}";
        }

        // Prepend the provider in parentheses if there are multiple providers and there's room
        var rightSide = rightSideWithoutProvider;
        if (_footerData.GetAvailableProviderCount() > 1 && stateModel is not null)
        {
            rightSide = $"({stateModel.Provider}) {rightSideWithoutProvider}";
            if (statsLeftWidth + minPadding + UnicodeWidth.VisibleWidth(rightSide) > width)
            {
                // Too wide, fall back
                rightSide = rightSideWithoutProvider;
            }
        }

        var rightSideWidth = UnicodeWidth.VisibleWidth(rightSide);
        var totalNeeded = statsLeftWidth + minPadding + rightSideWidth;

        string statsLine;
        if (totalNeeded <= width)
        {
            // Both fit - add padding to right-align model
            var padding = new string(' ', width - statsLeftWidth - rightSideWidth);
            statsLine = statsLeft + padding + rightSide;
        }
        else
        {
            // Need to truncate the right side
            var availableForRight = width - statsLeftWidth - minPadding;
            if (availableForRight > 0)
            {
                var truncatedRight = TextLayout.TruncateToWidth(rightSide, availableForRight, "");
                var truncatedRightWidth = UnicodeWidth.VisibleWidth(truncatedRight);
                var padding = new string(' ', Math.Max(0, width - statsLeftWidth - truncatedRightWidth));
                statsLine = statsLeft + padding + truncatedRight;
            }
            else
            {
                // Not enough space for the right side at all
                statsLine = statsLeft;
            }
        }

        // Apply dim to each part separately: statsLeft may contain color codes ending with a reset,
        // which would clear an outer dim wrapper.
        var dimStatsLeft = theme.Fg(ThemeColors.Dim, statsLeft);
        var remainder = statsLine[statsLeft.Length..]; // padding + rightSide
        var dimRemainder = theme.Fg(ThemeColors.Dim, remainder);

        var pwdLine = TextLayout.TruncateToWidth(
            theme.Fg(ThemeColors.Dim, pwd), width, theme.Fg(ThemeColors.Dim, "..."));
        var lines = new List<string> { pwdLine, dimStatsLeft + dimRemainder };

        // Add extension statuses on a single line, sorted by key alphabetically
        var extensionStatuses = _footerData.GetExtensionStatuses();
        if (extensionStatuses.Count > 0)
        {
            var sortedStatuses = extensionStatuses
                .OrderBy(pair => pair.Key, StringComparer.CurrentCulture)
                .Select(pair => FooterFormatting.SanitizeStatusText(pair.Value))
                .ToList();
            var statusLine = string.Join(" ", sortedStatuses);
            lines.Add(TextLayout.TruncateToWidth(statusLine, width, theme.Fg(ThemeColors.Dim, "...")));
        }

        return [.. lines];
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrEmpty(value));
}
