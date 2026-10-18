// ============================================================================
// InteractiveMode — the /share flow, the remainder of batch 4f-4m-5c.
//
// Port of modes/interactive/session-share.ts (217 lines) plus the /share
// dispatch (interactive-mode.ts L3217-3221). The share uploads the session to
// Radius when a Radius login is available and otherwise falls back to a private
// GitHub gist rendered by the share viewer.
// ============================================================================

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive;

public partial class InteractiveMode
{
    /// <summary>TS <c>/share</c> dispatch (L3217-3221): <c>await handleShareCommand()</c>.</summary>
    internal async Task HandleShareCommandAsync()
    {
        // TS fs.mkdtempSync(path.join(os.tmpdir(), "pi-share-")).
        var tempDir = Path.Combine(Path.GetTempPath(), "pi-share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var jsonlFile = Path.Combine(tempDir, "session.jsonl");
        var htmlFile = Path.Combine(tempDir, "session.html");

        try
        {
            try
            {
                ExportSessionForShare(jsonlFile);
            }
            catch (Exception error)
            {
                ShowError($"Failed to export session: {error.Message}");
                return;
            }

            if (await TryShareViaRadiusAsync(jsonlFile).ConfigureAwait(true))
            {
                return;
            }

            try
            {
                var authResult = ChildProcess.SpawnSync(
                    "gh", ["auth", "status"], new SpawnSyncOptions { Encoding = "utf-8", WindowsHide = true });
                if (authResult.Status != 0)
                {
                    ShowError("GitHub CLI is not logged in. Run 'gh auth login' first.");
                    return;
                }
            }
            catch
            {
                ShowError("GitHub CLI (gh) is not installed. Install it from https://cli.github.com/");
                return;
            }

            try
            {
                ExportHtml.ExportSessionToHtml(SessionManager, new ExportHtmlOptions
                {
                    OutputPath = htmlFile,
                    ThemeName = ThemeRuntime.Current.Name,
                });
            }
            catch (Exception error)
            {
                ShowError($"Failed to export session: {error.Message}");
                return;
            }

            await ShareViaGistAsync(htmlFile).ConfigureAwait(true);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// TS <c>createShareTrailingEntries</c> (session-share.ts L25-47): the trailing
    /// <c>pi.share</c> entry carrying the system prompt and tool schemas for the session viewer.
    /// </summary>
    private IReadOnlyList<object> CreateShareTrailingEntries(string? parentId, string timestamp)
    {
        var tools = new JsonArray();
        foreach (var tool in Session.Agent.Tools)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = JsonSerializer.SerializeToNode(tool.Parameters.JsonSchema),
            });
        }

        var entry = new JsonObject
        {
            ["type"] = "custom",
            ["customType"] = "pi.share",
            // TS crypto.randomUUID().slice(0, 8) — the same convention the session entries use.
            ["id"] = Guid.NewGuid().ToString("N")[..8],
            ["timestamp"] = timestamp,
            ["data"] = new JsonObject
            {
                ["systemPrompt"] = Session.SystemPrompt,
                ["tools"] = tools,
            },
        };
        // A null parentId must stay an explicit JSON null (the indexer would drop the key).
        if (parentId is null)
        {
            entry.Add("parentId", null);
        }
        else
        {
            entry["parentId"] = parentId;
        }

        return [entry];
    }

    /// <summary>TS <c>exportSessionForShare</c> (L50-54): export the branch with the share entries.</summary>
    private void ExportSessionForShare(string filePath)
        => SessionExport.ExportSessionToJsonl(SessionManager, filePath, CreateShareTrailingEntries);

    /// <summary>Test seam: export with the share trailing entries and hand back the file path.</summary>
    internal string ExportSessionForShareForTests()
    {
        var filePath = Path.Combine(Path.GetTempPath(), "pi-share-test-" + Guid.NewGuid().ToString("N") + ".jsonl");
        ExportSessionForShare(filePath);
        return filePath;
    }

    /// <summary>TS <c>tryShareViaRadius</c> (L98-157): upload to Radius when a login is available.</summary>
    private async Task<bool> TryShareViaRadiusAsync(string tmpFile)
    {
        var provider = Session.ModelRuntime.GetProvider(Radius.ProviderId);
        if (provider is null)
        {
            return false;
        }

        var token = AuthCredentials.GetAuthCredential(
            await Session.ModelRuntime.GetAuthAsync(
                Radius.ProviderId,
                new ModelRuntimeAuthOverrides { MinOAuthValidityMs = 5 * 60_000 }).ConfigureAwait(true));
        if (token is null)
        {
            return false;
        }

        var loader = new BorderedLoader(_ui, ThemeRuntime.Current, "Uploading to Radius...");
        _editorContainer.Clear();
        _editorContainer.AddChild(loader);
        _ui.SetFocus(loader);
        _ui.RequestRender();
        loader.OnAbort = () =>
        {
            RestoreShareEditor(loader);
            ShowStatus("Share cancelled");
        };

        try
        {
            var body = File.ReadAllBytes(tmpFile);
            // TS new URL("/v1/artifacts", DEFAULT_RADIUS_GATEWAY) + searchParams.set(...);
            // searchParams percent-encodes the value, so the title's space becomes %20.
            var url = $"{RadiusConfig.NormalizeGatewayUrl(RadiusConfig.DefaultRadiusGateway)}/v1/artifacts"
                + $"?visibility=organization&title={Uri.EscapeDataString("Pi session")}";
            using var client = new HttpClient();
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-ndjson");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await client.SendAsync(request, loader.Signal).ConfigureAwait(true);
            if (loader.Signal.IsCancellationRequested)
            {
                return true;
            }

            JsonNode? json = null;
            try
            {
                json = JsonNode.Parse(await response.Content.ReadAsStringAsync(loader.Signal).ConfigureAwait(true));
            }
            catch
            {
                // TS .catch(() => null)
            }

            if (loader.Signal.IsCancellationRequested)
            {
                return true;
            }

            RestoreShareEditor(loader);
            var shareUrl = json?["artifact"]?["canonical_url"]?.GetValue<string>();
            if (!response.IsSuccessStatusCode || shareUrl is null)
            {
                // TS `json?.error || response.statusText || response.status` — an empty error falls through.
                var detail = json?["error"]?.GetValue<string>();
                var reason = !string.IsNullOrEmpty(detail)
                    ? detail
                    : response.ReasonPhrase ?? response.StatusCode.ToString();
                ShowError($"Failed to upload Radius artifact: {reason}");
                return true;
            }

            ShowStatus($"Share URL: {TerminalImage.Hyperlink(shareUrl, shareUrl)}");
            return true;
        }
        catch (Exception error)
        {
            if (!loader.Signal.IsCancellationRequested)
            {
                RestoreShareEditor(loader);
                ShowError($"Failed to upload Radius artifact: {error.Message}");
            }

            return true;
        }
    }

    /// <summary>TS <c>shareViaGist</c> (L159-210): create a private gist and show the viewer link.</summary>
    private async Task ShareViaGistAsync(string tmpFile)
    {
        var loader = new BorderedLoader(_ui, ThemeRuntime.Current, "Creating gist...");
        _editorContainer.Clear();
        _editorContainer.AddChild(loader);
        _ui.SetFocus(loader);
        _ui.RequestRender();

        ChildProcessHandle? proc = null;
        loader.OnAbort = () =>
        {
            proc?.Kill();
            RestoreShareEditor(loader);
            ShowStatus("Share cancelled");
        };

        try
        {
            proc = ChildProcess.SpawnProcess(
                "gh",
                ["gist", "create", "--public=false", tmpFile],
                new SpawnOptions { WindowsHide = true });
            // WaitAsync also drains the stdio pipes (with the exit grace), so the whole captured
            // output is available right after it.
            var code = await proc.WaitAsync().ConfigureAwait(true);
            var stdout = await proc.ReadStdoutAsync().ConfigureAwait(true);
            var stderr = await proc.ReadStderrAsync().ConfigureAwait(true);

            if (loader.Signal.IsCancellationRequested)
            {
                return;
            }

            RestoreShareEditor(loader);

            if (code != 0)
            {
                var detail = stderr.Trim().Length > 0 ? stderr.Trim() : "Unknown error";
                ShowError($"Failed to create gist: {detail}");
                return;
            }

            var gistUrl = stdout.Trim();
            var gistId = gistUrl.Split('/').LastOrDefault();
            if (string.IsNullOrEmpty(gistId))
            {
                ShowError("Failed to parse gist ID from gh output");
                return;
            }

            var previewUrl = Config.GetShareViewerUrl(gistId);
            ShowStatus($"Share URL: {TerminalImage.Hyperlink(previewUrl, previewUrl)}\nGist: {TerminalImage.Hyperlink(gistUrl, gistUrl)}");
        }
        catch (Exception error)
        {
            if (!loader.Signal.IsCancellationRequested)
            {
                RestoreShareEditor(loader);
                ShowError($"Failed to create gist: {error.Message}");
            }
        }
    }

    /// <summary>
    /// TS <c>restoreEditor</c> (session-share.ts L212-217) — note: unlike the bug-report flow's
    /// helper, this one does not request a render.
    /// </summary>
    private void RestoreShareEditor(BorderedLoader loader)
    {
        loader.DisposeLoader();
        _editorContainer.Clear();
        _editorContainer.AddChild(_editor);
        _ui.SetFocus(_editor);
    }
}
