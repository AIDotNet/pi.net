// ============================================================================
// InteractiveMode — the /bug flow, the remainder of batch 4f-4m-5c.
//
// Port of modes/interactive/bug-report.ts (298 lines) plus the /bug dispatch
// (interactive-mode.ts L3222-3227): consent, optional model-written summary,
// then upload to Radius or export as a zip archive.
// ============================================================================

using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive;

public partial class InteractiveMode
{
    private const string BugDisclaimer =
        "This report goes to the Pi developers (Earendil) and is not shared publicly. It includes your pi version, operating system, the current model and provider configuration (without API keys), loaded extensions, settings, and provider error diagnostics from this session.";
    private const string BugTranscriptNote =
        "The transcript contains your messages, model output, tool calls and their results, including file contents and command output read during this session.";
    private const string BugReportCustomEntryType = "pi.bug-report";

    /// <summary>TS <c>/bug</c> dispatch (L3222-3227)：<c>await handleBugCommand(hint ? hint : undefined)</c>。</summary>
    internal async Task HandleBugCommandAsync(string? hint)
        => await ReportBugAsync(hint).ConfigureAwait(true);

    /// <summary>TS <c>reportBug</c> (L50-105)：consent → optional summary → upload or zip。</summary>
    private async Task ReportBugAsync(string? initialHint)
    {
        var options = await PromptForBugOptionsAsync(initialHint).ConfigureAwait(true);
        if (options is null)
        {
            ShowStatus("Bug report cancelled");
            return;
        }

        if (options.Delivery == BugDelivery.Upload && IsOfflineEnv())
        {
            ShowError("Uploading bug reports requires online mode. Use Export as Zip instead.");
            return;
        }

        string? summary = null;
        if (options.IncludeSummary)
        {
            var loader = ShowBugLoader($"Writing summary with {Session.Model?.Name ?? "the current model"}...");
            try
            {
                summary = await Session.SummarizeForBugReportAsync(options.Hint, loader.Signal).ConfigureAwait(true);
            }
            catch (Exception error)
            {
                RestoreBugEditor(loader);
                if (loader.Signal.IsCancellationRequested)
                {
                    ShowStatus("Bug report cancelled");
                }
                else
                {
                    ShowError($"Failed to write bug report summary: {error.Message}");
                }

                return;
            }

            RestoreBugEditor(loader);
            if (loader.Signal.IsCancellationRequested)
            {
                ShowStatus("Bug report cancelled");
                return;
            }
        }

        BugReportMetadata metadata;
        BugReportBundle bundle;
        BugReportDiagnostics diagnostics;
        try
        {
            (bundle, metadata, diagnostics) = BuildBugBundle(options, summary);
        }
        catch (Exception error)
        {
            ShowError($"Failed to build bug report: {error.Message}");
            return;
        }

        if (options.Delivery == BugDelivery.Upload)
        {
            var failure = await UploadBugReportAsync(bundle, metadata, diagnostics).ConfigureAwait(true);
            if (failure is null)
            {
                return;
            }

            var fallback = await ChooseBugOptionAsync(
                "Upload failed",
                ["Export as Zip", "Cancel"],
                $"{failure}\n\nExport the report as a zip archive instead?").ConfigureAwait(true);
            if (fallback != "Export as Zip")
            {
                ShowStatus("Bug report cancelled");
                return;
            }
        }

        await ExportBugZipAsync(bundle, metadata, diagnostics).ConfigureAwait(true);
    }

    /// <summary>TS <c>promptForOptions</c> (L107-147)。</summary>
    private async Task<BugOptions?> PromptForBugOptionsAsync(string? initialHint)
    {
        var hint = await InputBugTextAsync(
            "Report a bug", $"{BugDisclaimer}\n\nWhat went wrong? (optional)", initialHint).ConfigureAwait(true);
        if (hint is null)
        {
            return null;
        }

        var transcript = await ChooseBugOptionAsync(
            "Include the session transcript?",
            ["Yes, include the transcript", "No"],
            BugTranscriptNote).ConfigureAwait(true);
        if (transcript is null)
        {
            return null;
        }

        var includeSession = transcript != "No";
        var includeSummary = false;
        if (!includeSession)
        {
            var model = Session.Model;
            var summary = await ChooseBugOptionAsync(
                $"Attach a summary written by {model?.Name ?? "the current model"} instead?",
                ["Yes, generate a summary", "No"],
                $"The transcript is sent to {model?.Provider ?? "your provider"} with your credentials and tokens. Only the generated summary is attached; the transcript stays on your machine.").ConfigureAwait(true);
            if (summary is null)
            {
                return null;
            }

            includeSummary = summary != "No";
        }

        var description = hint.Trim();
        var delivery = await ChooseBugOptionAsync(
            "Bug report",
            ["Upload Report", "Export as Zip", "Cancel"],
            $"Description: {(description.Length > 0 ? description : "none")}\n"
            + $"Transcript: {(includeSession ? "included" : "not included")}\n"
            + $"Summary: {(includeSummary ? $"written by {Session.Model?.Name ?? "the current model"}" : "none")}\n\n"
            + $"Upload sends the report to {new System.Uri(Radius.GetGatewayUrl()).Host}. Export writes a zip archive to the current directory instead.").ConfigureAwait(true);
        if (delivery is null || delivery == "Cancel")
        {
            return null;
        }

        return new BugOptions(
            description.Length > 0 ? description : null,
            includeSession,
            includeSummary,
            delivery == "Upload Report" ? BugDelivery.Upload : BugDelivery.Zip);
    }

    /// <summary>TS <c>buildBundle</c> (L149-175)。</summary>
    private (BugReportBundle Bundle, BugReportMetadata Metadata, BugReportDiagnostics Diagnostics) BuildBugBundle(
        BugOptions options, string? summary)
    {
        var extensions = Session.ResourceLoader.GetExtensions();
        var metadata = BugReport.CollectBugReportMetadata(new CollectBugReportMetadataOptions
        {
            Hint = options.Hint,
            SessionId = Session.SessionId,
            Cwd = SessionManager.GetCwd(),
            IncludeSession = options.IncludeSession,
            IncludeSummary = summary is not null,
            MessageCount = Session.Messages.Count,
            Model = Session.Model,
            ModelRuntime = Session.ModelRuntime,
            ThinkingLevel = Session.ThinkingLevel,
            Extensions = extensions.Extensions,
            ExtensionErrors = extensions.Errors,
            GlobalSettings = Session.SettingsManager.GetGlobalSettings(),
            ProjectSettings = Session.SettingsManager.GetProjectSettings(),
        });
        var diagnostics = BugReport.CollectBugReportDiagnostics(SessionManager, CrashLog.Read());
        var bundle = new BugReportBundle
        {
            Metadata = metadata.Value,
            Diagnostics = diagnostics.Value,
            Summary = summary,
            SessionJsonl = options.IncludeSession
                ? SessionExport.SerializeSessionBranch(SessionManager, CreateShareTrailingEntries)
                : null,
        };
        return (bundle, metadata, diagnostics);
    }

    /// <summary>TS <c>upload</c> (L177-199)：失败时返回错误文案（调用方决定是否回退 zip）。</summary>
    private async Task<string?> UploadBugReportAsync(
        BugReportBundle bundle, BugReportMetadata metadata, BugReportDiagnostics diagnostics)
    {
        var loader = ShowBugLoader("Uploading bug report...");
        try
        {
            var provider = Session.ModelRuntime.GetProvider(Radius.ProviderId);
            var token = provider is null
                ? null
                : AuthCredentials.GetAuthCredential(
                    await Session.ModelRuntime.GetAuthAsync(
                        Radius.ProviderId,
                        new ModelRuntimeAuthOverrides { MinOAuthValidityMs = 5 * 60_000 }).ConfigureAwait(true));
            var result = await BugReportUpload.UploadBugReportAsync(
                bundle,
                new BugReportUpload.Options { Token = token },
                cancellationToken: loader.Signal).ConfigureAwait(true);
            RestoreBugEditor(loader);
            RecordBugReportInSession(metadata, diagnostics, "upload", null);
            ShowStatus($"Bug report uploaded. Report ID: {result.Id}");
            return null;
        }
        catch (Exception error)
        {
            RestoreBugEditor(loader);
            if (loader.Signal.IsCancellationRequested)
            {
                ShowStatus("Bug report cancelled");
                return null;
            }

            return error.Message;
        }
    }

    /// <summary>TS <c>exportZip</c> (L201-211)。</summary>
    private async Task ExportBugZipAsync(
        BugReportBundle bundle, BugReportMetadata metadata, BugReportDiagnostics diagnostics)
    {
        var archivePath = NodePath.Join(Directory.GetCurrentDirectory(), BugReport.BugReportArchiveFileName(metadata.Id));
        try
        {
            await BugReport.WriteBugReportArchiveAsync(archivePath, bundle).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            ShowError($"Failed to write bug report: {error.Message}");
            return;
        }

        RecordBugReportInSession(metadata, diagnostics, "zip", archivePath);
        ShowStatus($"Bug report exported to: {archivePath}\nReport ID: {metadata.Id}");
    }

    /// <summary>TS <c>recordInSession</c> (L213-227)。</summary>
    private void RecordBugReportInSession(
        BugReportMetadata metadata, BugReportDiagnostics diagnostics, string delivery, string? path)
    {
        // TS 从 bundle.metadata / bundle.diagnostics 读字段；端口的具形记录直接给。
        var data = new JsonObject
        {
            ["id"] = metadata.Id,
            ["createdAt"] = metadata.CreatedAt,
            ["sessionIncluded"] = metadata.SessionIncluded,
            ["summaryIncluded"] = metadata.SummaryIncluded,
            ["delivery"] = delivery,
        };
        // TS 恒写 hint（可为 null）；JsonObject 的索引器赋 null 会丢键，故显式补 null。
        if (metadata.Hint is null)
        {
            data.Add("hint", null);
        }
        else
        {
            data["hint"] = metadata.Hint;
        }

        // TS 的 delivery 展开只在上传失败回退时带 path；zip 分支恒有 path。
        if (path is not null)
        {
            data["path"] = path;
        }

        SessionManager.AppendCustomEntry(BugReportCustomEntryType, data);
        if (diagnostics.CrashCount > 0)
        {
            CrashLog.Clear();
        }
    }

    /// <summary>TS <c>input</c> (L229-253)。</summary>
    private Task<string?> InputBugTextAsync(string title, string description, string? initialValue)
    {
        var response = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ExtensionEditorComponent? component = null;
        void Finish(string? value)
        {
            RestoreBugEditor(component!);
            response.TrySetResult(value);
        }

        component = new ExtensionEditorComponent(
            _ui,
            _keybindings,
            title,
            initialValue,
            value => Finish(value),
            () => Finish(null),
            new ExtensionEditorOptions { Description = description },
            SettingsManager.GetExternalEditorCommand());
        ShowBugOverlay(component);
        return response.Task;
    }

    /// <summary>TS <c>choose</c> (L255-273)。</summary>
    private Task<string?> ChooseBugOptionAsync(string title, IReadOnlyList<string> options, string? description)
    {
        var response = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ExtensionSelectorComponent? component = null;
        void Finish(string? value)
        {
            RestoreBugEditor(component!);
            response.TrySetResult(value);
        }

        component = new ExtensionSelectorComponent(
            title,
            options,
            value => Finish(value),
            () => Finish(null),
            new ExtensionSelectorOptions { Tui = _ui, Description = description });
        ShowBugOverlay(component);
        return response.Task;
    }

    /// <summary>TS <c>showLoader</c> (L275-279)。</summary>
    private BorderedLoader ShowBugLoader(string message)
    {
        var loader = new BorderedLoader(_ui, ThemeRuntime.Current, message);
        ShowBugOverlay(loader);
        return loader;
    }

    /// <summary>TS <c>showOverlay</c> (L281-286)。</summary>
    private void ShowBugOverlay(IComponent component)
    {
        _editorContainer.Clear();
        _editorContainer.AddChild(component);
        _ui.SetFocus(component);
        _ui.RequestRender();
    }

    /// <summary>TS <c>restoreEditor</c> (L288-294)——与 share 的同名助手不同，这里会请求重渲。</summary>
    private void RestoreBugEditor(IComponent component)
    {
        switch (component)
        {
            case BorderedLoader loader:
                loader.DisposeLoader();
                break;
            case ExtensionSelectorComponent selector:
                selector.Dispose();
                break;
        }

        _editorContainer.Clear();
        _editorContainer.AddChild(_editor);
        _ui.SetFocus(_editor);
        _ui.RequestRender();
    }

    /// <summary>TS <c>BugReportOptions</c>。</summary>
    private sealed record BugOptions(string? Hint, bool IncludeSession, bool IncludeSummary, BugDelivery Delivery);

    /// <summary>TS <c>BugReportOptions["delivery"]</c>。</summary>
    private enum BugDelivery
    {
        Upload,
        Zip,
    }
}
