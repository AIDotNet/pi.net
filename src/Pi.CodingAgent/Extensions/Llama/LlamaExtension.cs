using Pi.Ai.Models;
using Pi.CodingAgent.Core.Extensions;

namespace Pi.CodingAgent.Extensions.Llama;

/// <summary>
/// The <c>llama.cpp</c> extension. Port of <c>extensions/llama/index.ts</c>: it registers the dynamic
/// <see cref="LlamaProvider"/> and a <c>/llama</c> command that manages the router's models.
/// </summary>
/// <remarks>
/// <para>
/// The command body is fully ported (catalog sync, load/unload/download orchestration) and drives the
/// <see cref="ILlamaUi"/> seam. The concrete TUI view from <c>ui.ts</c> (<c>LlamaView</c> /
/// <c>HuggingFaceSearch</c> / <c>showLlamaUi</c>) renders through the interactive theme and components
/// that land with 4f-2 / 4f-3, so it is supplied through
/// <see cref="LlamaExtensionOptions.ShowUi"/>; the default reports the pending batch.
/// </para>
/// </remarks>
public static class LlamaExtension
{
    /// <summary>The TS <c>AbortSignal.timeout(15_000)</c> around a catalog sync.</summary>
    private const int CatalogTimeoutMs = 15_000;

    /// <summary>Options for <see cref="CreateLlamaExtension"/>.</summary>
    public sealed record LlamaExtensionOptions
    {
        /// <summary>
        /// The <c>showLlamaUi(ctx, run)</c> seam. Defaults to the pending 4f-3 implementation; tests
        /// and the interactive mode supply the real view.
        /// </summary>
        public Func<IExtensionCommandContext, Func<ILlamaUi, Task>, Task>? ShowUi { get; init; }
    }

    /// <summary>The TS <c>default export llamaExtension(pi)</c>.</summary>
    public static ExtensionFactory CreateLlamaExtension(LlamaExtensionOptions? options = null)
    {
        options ??= new LlamaExtensionOptions();
        var showUi = options.ShowUi ?? DefaultShowUi;

        return async pi =>
        {
            var provider = new LlamaProvider();
            pi.RegisterProvider(provider);

            pi.RegisterCommand("llama", new CommandRegistration
            {
                Description = "Manage llama.cpp router models",
                Handler = async (_, ctx) =>
                {
                    if (ctx.Mode != ExtensionMode.Tui)
                    {
                        ctx.Ui.Notify("/llama is available in interactive mode", NotifyType.Warning);
                        return;
                    }

                    var client = await ConfiguredClientAsync(ctx).ConfigureAwait(false);
                    if (client is null)
                    {
                        return;
                    }

                    await showUi(ctx, async ui =>
                    {
                        async Task<IReadOnlyList<LlamaModelInfo>?> ReadCatalogAsync()
                        {
                            while (true)
                            {
                                try
                                {
                                    return await SyncCatalogAsync(ctx, provider, client).ConfigureAwait(false);
                                }
                                catch (Exception error)
                                {
                                    if (await ui.ConnectionError(client.ServerUrl, ConnectionErrorMessage(error))
                                        .ConfigureAwait(false) == "close")
                                    {
                                        return null;
                                    }
                                }
                            }
                        }

                        var catalog = await ReadCatalogAsync().ConfigureAwait(false);
                        if (catalog is null)
                        {
                            return;
                        }

                        while (true)
                        {
                            var action = await ui.ShowModels(client.ServerUrl, catalog).ConfigureAwait(false);
                            if (action is LlamaManagerAction.Close)
                            {
                                return;
                            }

                            Exception? actionError = null;
                            try
                            {
                                switch (action)
                                {
                                    case LlamaManagerAction.Download:
                                        await DownloadModelAsync(ctx, ui, client, provider).ConfigureAwait(false);
                                        break;
                                    case LlamaManagerAction.Model model when ModelIsLoaded(model.Value):
                                        await UnloadModelAsync(ctx, ui, client, provider, model.Value).ConfigureAwait(false);
                                        break;
                                    case LlamaManagerAction.Model model
                                        when model.Value.StatusValue == LlamaModelStatusValue.Unloaded:
                                        await LoadModelAsync(ctx, ui, client, provider, catalog, model.Value)
                                            .ConfigureAwait(false);
                                        break;
                                    case LlamaManagerAction.Model model:
                                        ctx.Ui.Notify(
                                            $"{model.Value.Id} is {model.Value.StatusValue}", NotifyType.Warning);
                                        break;
                                }
                            }
                            catch (Exception error)
                            {
                                actionError = error;
                            }

                            var refreshed = await ReadCatalogAsync().ConfigureAwait(false);
                            if (refreshed is null)
                            {
                                return;
                            }

                            catalog = refreshed;
                            if (actionError is not null && !IsConnectionError(actionError))
                            {
                                ctx.Ui.Notify(actionError.Message, NotifyType.Error);
                            }
                        }
                    }).ConfigureAwait(false);
                },
            });
        };
    }

    /// <summary>The TS <c>showLlamaUi</c> default until the 4f view lands.</summary>
    private static Task DefaultShowUi(IExtensionCommandContext ctx, Func<ILlamaUi, Task> run)
        => throw new NotSupportedException(
            "The llama.cpp manager UI (extensions/llama/ui.ts LlamaView / HuggingFaceSearch / showLlamaUi) " +
            "renders through the interactive theme and components that land with batch 4f-2 / 4f-3 of the " +
            "porting plan (docs/migration-remaining-plan.md). Supply a view through " +
            "LlamaExtensionOptions.ShowUi; the provider registration and the command orchestration are " +
            "already ported.");

    /// <summary>The TS <c>modelIsLoaded(model)</c>.</summary>
    private static bool ModelIsLoaded(LlamaModelInfo model)
        => model.StatusValue is LlamaModelStatusValue.Loaded or LlamaModelStatusValue.Sleeping;

    /// <summary>The TS <c>isConnectionError(error)</c>.</summary>
    private static bool IsConnectionError(Exception error)
    {
        var name = error is AggregateException ? "AggregateError" : "Error";
        var message = $"{name} {error.Message}".ToLowerInvariant();
        return message.Contains("fetch failed", StringComparison.Ordinal)
            || message.Contains("timeout", StringComparison.Ordinal)
            || message.Contains("network", StringComparison.Ordinal);
    }

    /// <summary>The TS <c>connectionErrorMessage(error)</c>.</summary>
    private static string ConnectionErrorMessage(Exception error)
        => IsConnectionError(error) ? "Could not connect to the server." : error.Message;

    /// <summary>The TS <c>parseHuggingFaceModel(value)</c>.</summary>
    internal static (string Repository, string? Quantization) ParseHuggingFaceModel(string value)
    {
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        var colon = value.IndexOf(':', slash + 1);
        return colon < 0 ? (value, null) : (value[..colon], value[(colon + 1)..]);
    }

    /// <summary>The TS <c>configuredClient(ctx)</c>.</summary>
    private static async Task<LlamaClient?> ConfiguredClientAsync(IExtensionCommandContext ctx)
    {
        var result = await ctx.ModelRegistry.GetProviderAuthAsync(LlamaProvider.ProviderId).ConfigureAwait(false);
        if (result is null)
        {
            ctx.Ui.Notify($"Configure llama.cpp with /login {LlamaProvider.ProviderId}", NotifyType.Warning);
            return null;
        }

        var configuredUrl = result.Env is not null && result.Env.TryGetValue(LlamaProvider.BaseUrlEnv, out var value)
            ? value
            : null;
        var serverUrl = LlamaModels.NormalizeLlamaServerUrl(
            !string.IsNullOrEmpty(configuredUrl) ? configuredUrl : result.Auth.BaseUrl ?? "");
        return new LlamaClient(serverUrl, result.Auth.ApiKey);
    }

    /// <summary>The TS <c>syncCatalog(ctx, client, catalog)</c>.</summary>
    private static async Task<IReadOnlyList<LlamaModelInfo>> SyncCatalogAsync(
        IExtensionCommandContext ctx, LlamaProvider provider, LlamaClient client,
        IReadOnlyList<LlamaModelInfo>? catalog = null)
    {
        using var timeout = new CancellationTokenSource(CatalogTimeoutMs);
        var signal = timeout.Token;
        var current = catalog ?? await client.ListAsync(signal).ConfigureAwait(false);
        provider.SetCatalog(current, client.ServerUrl);
        var result = await ctx.ModelRegistry.RefreshAsync(new ModelsRefreshOptions
        {
            Providers = [LlamaProvider.ProviderId],
            // /llama already contacted the configured llama.cpp server, so keep this refresh live even in PI_OFFLINE.
            AllowNetwork = true,
            Signal = signal,
        }).ConfigureAwait(false);
        if (result.Aborted)
        {
            throw new InvalidOperationException("Model catalog refresh timed out.");
        }

        if (result.Errors.TryGetValue(LlamaProvider.ProviderId, out var refreshError))
        {
            throw refreshError;
        }

        return current;
    }

    /// <summary>The TS <c>loadModel(ctx, ui, client, catalog, target)</c>.</summary>
    private static async Task LoadModelAsync(
        IExtensionCommandContext ctx, ILlamaUi ui, LlamaClient client, LlamaProvider provider,
        IReadOnlyList<LlamaModelInfo> catalog, LlamaModelInfo target)
    {
        var loaded = catalog.Where(model => model.Id != target.Id && ModelIsLoaded(model)).ToList();
        var replace = false;
        if (loaded.Count > 0)
        {
            var choice = await ui.Select(
                $"{loaded.Count} model{(loaded.Count == 1 ? " is" : "s are")} loaded",
                ["Unload all and load", "Keep loaded and load", "Cancel"]).ConfigureAwait(false);
            if (string.IsNullOrEmpty(choice) || choice == "Cancel")
            {
                return;
            }

            replace = choice == "Unload all and load";
        }

        async Task RestoreLoadedAsync()
        {
            ctx.Ui.Notify("Restoring previously loaded models");
            foreach (var model in loaded)
            {
                await client.LoadAndWaitAsync(model.Id, _ => { }).ConfigureAwait(false);
            }

            await SyncCatalogAsync(ctx, provider, client).ConfigureAwait(false);
        }

        if (replace)
        {
            foreach (var model in loaded)
            {
                await client.UnloadAndWaitAsync(model.Id).ConfigureAwait(false);
            }
        }

        try
        {
            var result = await LlamaUi.RunWithProgressAsync(ui, new LlamaRunOptions<LlamaModelInfo>
            {
                Title = "Loading model",
                Model = target.Id,
                InitialMessage = "Starting…",
                CancelTitle = "Stop loading?",
                CancelMessage = target.Id,
                Run = (signal, update) => client.LoadAndWaitAsync(target.Id, update, signal),
                Cancel = () => client.UnloadAsync(target.Id),
            }).ConfigureAwait(false);
            if (result.Cancelled)
            {
                if (replace)
                {
                    await RestoreLoadedAsync().ConfigureAwait(false);
                }

                return;
            }

            var refreshed = await SyncCatalogAsync(ctx, provider, client).ConfigureAwait(false);
            var loadedModel = refreshed.FirstOrDefault(model => model.Id == target.Id);
            ctx.Ui.Notify(loadedModel?.StatusValue == LlamaModelStatusValue.Loaded
                ? $"Loaded {target.Id}"
                : $"Load started for {target.Id}");
        }
        catch
        {
            if (replace)
            {
                try
                {
                    await RestoreLoadedAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original load error.
                }
            }

            throw;
        }
    }

    /// <summary>The TS <c>unloadModel(ctx, ui, client, model)</c>.</summary>
    private static async Task UnloadModelAsync(
        IExtensionCommandContext ctx, ILlamaUi ui, LlamaClient client, LlamaProvider provider, LlamaModelInfo model)
    {
        if (!await ui.Confirm("Unload model?", model.Id).ConfigureAwait(false))
        {
            return;
        }

        await client.UnloadAndWaitAsync(model.Id).ConfigureAwait(false);
        await SyncCatalogAsync(ctx, provider, client).ConfigureAwait(false);
        ctx.Ui.Notify($"Unloaded {model.Id}");
    }

    /// <summary>The TS <c>downloadModel(ctx, ui, client)</c>.</summary>
    private static async Task DownloadModelAsync(
        IExtensionCommandContext ctx, ILlamaUi ui, LlamaClient client, LlamaProvider provider)
    {
        var huggingFace = new HuggingFaceClient(await HuggingFace.FindHuggingFaceTokenAsync().ConfigureAwait(false));
        var selected = await ui.SearchModels((query, signal) => huggingFace.SearchAsync(query, signal)).ConfigureAwait(false);
        if (selected is null)
        {
            return;
        }

        var parsed = ParseHuggingFaceModel(selected);
        ui.ShowStatus("Loading model details", parsed.Repository);
        var details = await huggingFace.DetailsAsync(parsed.Repository).ConfigureAwait(false);
        if (details.Gated != "false")
        {
            var approval = details.Gated == "manual" ? "Manual approval is required" : "Accept the access terms";
            var choice = await ui.Select(
                $"Hugging Face access required\n{details.Id}\n\n{approval} at:\nhttps://huggingface.co/{details.Id}\n\n" +
                "The llama.cpp server needs HF_TOKEN with access.",
                ["Continue", "Back"]).ConfigureAwait(false);
            if (choice != "Continue")
            {
                return;
            }
        }

        var quantization = parsed.Quantization;
        if (quantization is null && details.Quantizations.Count > 0)
        {
            var options = details.Quantizations.Select(entry =>
            {
                var detail = string.Join(" · ", new[]
                {
                    entry.Size is null ? null : LlamaModels.FormatBytes(entry.Size.Value),
                    entry.Name == "Q4_K_M" ? "recommended" : null,
                }.Where(part => part is not null));
                return detail.Length > 0 ? $"{entry.Name} · {detail}" : entry.Name;
            }).ToList();
            var choice = await ui.Select($"Select quantization\n{details.Id}", options).ConfigureAwait(false);
            if (string.IsNullOrEmpty(choice))
            {
                return;
            }

            var index = options.IndexOf(choice);
            quantization = index >= 0 ? details.Quantizations[index].Name : null;
            if (quantization is null)
            {
                return;
            }
        }

        var model = quantization is null ? details.Id : $"{details.Id}:{quantization}";
        var result = await LlamaUi.RunWithProgressAsync(ui, new LlamaRunOptions<IReadOnlyList<LlamaModelInfo>>
        {
            Title = "Downloading model",
            Model = model,
            InitialMessage = "Starting…",
            CancelTitle = "Stop download?",
            CancelMessage = model,
            Run = (signal, update) => client.DownloadAndWaitAsync(model, update, signal),
            Cancel = () => client.UnloadAsync(model),
        }).ConfigureAwait(false);
        if (result.Cancelled)
        {
            return;
        }

        await SyncCatalogAsync(ctx, provider, client, result.Value).ConfigureAwait(false);
        ctx.Ui.Notify($"Downloaded {model}");
    }
}
