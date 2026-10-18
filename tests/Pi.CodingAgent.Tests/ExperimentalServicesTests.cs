using Pi.Ai.Types;
using Pi.CodingAgent.Experimental.Services;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Contract tests for the experimental service layer (4g-3b). The TS side defines seven chord
/// services; the service ids, the local flags and the reply unions are the parts a port can get wrong
/// without any test failing elsewhere, so they are pinned here.
/// </summary>
public class ExperimentalServicesTests
{
    [Theory]
    [InlineData("pi.agent-controller", false)]
    [InlineData("pi.models", false)]
    [InlineData("pi.presentation-plugins", false)]
    [InlineData("pi.session-plugins", false)]
    [InlineData("pi.session-directory", false)]
    [InlineData("pi.session-management", false)]
    [InlineData("pi.transcript", false)]
    [InlineData("pi.local.presentation-ui", true)]
    [InlineData("pi.local.slash-commands", true)]
    public void ServiceIds_MatchTheTsDefinitions(string id, bool local)
    {
        // Every service the experimental layer defines, with its id and local flag.
        var services = new (string Id, bool Local)[]
        {
            (AgentController.Service.Id, AgentController.Service.Local),
            (Models.Service.Id, Models.Service.Local),
            (PresentationPlugins.Service.Id, PresentationPlugins.Service.Local),
            (SessionPlugins.Service.Id, SessionPlugins.Service.Local),
            (SessionDirectory.Service.Id, SessionDirectory.Service.Local),
            (SessionManagement.Service.Id, SessionManagement.Service.Local),
            (Transcript.Service.Id, Transcript.Service.Local),
            (PresentationUI.Service.Id, PresentationUI.Service.Local),
            (SlashCommands.Service.Id, SlashCommands.Service.Local),
        };

        Assert.Contains(services, entry => entry.Id == id && entry.Local == local);
    }

    [Fact]
    public void ServiceIds_AreUnique()
    {
        var ids = new[]
        {
            AgentController.Service.Id,
            Models.Service.Id,
            PresentationPlugins.Service.Id,
            SessionPlugins.Service.Id,
            SessionDirectory.Service.Id,
            SessionManagement.Service.Id,
            Transcript.Service.Id,
            PresentationUI.Service.Id,
            SlashCommands.Service.Id,
        };

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // ---------------------------------------------------------------- submission replies

    [Fact]
    public void OperationAccepted_CarriesTheOperationIdAndNoError()
    {
        AgentOperationResponse response = new AgentOperationResponse.Accepted("op-1");

        Assert.True(response.IsAccepted);
        Assert.Null(response.Error);
        Assert.Equal("op-1", Assert.IsType<AgentOperationResponse.Accepted>(response).OperationId);
    }

    [Fact]
    public void OperationRejected_CarriesTheErrorAndIsNotAccepted()
    {
        AgentOperationResponse response = new AgentOperationResponse.Rejected(new AgentOperationError("busy", "Run active"));

        Assert.False(response.IsAccepted);
        var error = Assert.IsType<AgentOperationError>(response.Error);
        Assert.Equal("busy", error.Code);
        Assert.Equal("Run active", error.Message);
    }

    [Fact]
    public void QueueAccepted_CarriesTheEntryId()
    {
        AgentQueueResponse response = new AgentQueueResponse.Accepted("entry-1");

        Assert.True(response.IsAccepted);
        Assert.Null(response.Error);
        Assert.Equal("entry-1", Assert.IsType<AgentQueueResponse.Accepted>(response).EntryId);
    }

    [Fact]
    public void QueueRejected_CarriesTheError()
    {
        AgentQueueResponse response = new AgentQueueResponse.Rejected(new AgentOperationError("busy", "Run active"));

        Assert.False(response.IsAccepted);
        Assert.Equal("busy", response.Error!.Code);
    }

    [Fact]
    public void BothReplyUnionsShareTheSubmissionBase()
    {
        // TS: SlashCommandRunResult = AgentOperationResponse | AgentQueueResponse | undefined, so a
        // slash command's reply has to be handled through the shared base (C313).
        AgentSubmissionResponse operation = new AgentOperationResponse.Accepted("op");
        AgentSubmissionResponse queue = new AgentQueueResponse.Accepted("entry");

        Assert.True(operation.IsAccepted);
        Assert.True(queue.IsAccepted);
        Assert.IsType<AgentOperationResponse.Accepted>(operation);
        Assert.IsType<AgentQueueResponse.Accepted>(queue);
    }

    [Fact]
    public void PromptResult_IsEitherDoneOrUnanswered()
    {
        AgentPromptResult done = new AgentPromptResult.Done("hi");
        AgentPromptResult unanswered = new AgentPromptResult.Unanswered("aborted");

        Assert.Equal("hi", Assert.IsType<AgentPromptResult.Done>(done).Text);
        Assert.Equal("aborted", Assert.IsType<AgentPromptResult.Unanswered>(unanswered).Reason);
    }

    [Fact]
    public void PromptImage_PinsTheImageDiscriminant() =>
        // TS: { type: "image"; data; mimeType } — the literal never varies.
        Assert.Equal("image", new AgentPromptImage("data", "image/png").Type);

    // ---------------------------------------------------------------- models

    [Fact]
    public void ModelSummary_ExtendsModelRefWithoutBeingEqual()
    {
        var summary = new ModelSummary("anthropic", "claude", "Claude", true);
        ModelRef reference = summary;

        Assert.Equal("anthropic", reference.Provider);
        Assert.Equal("claude", reference.ModelId);
        // record equality requires the same runtime type, which matches TS's structural comparison here:
        // a summary has two extra fields, so it never equals a bare ref.
        Assert.NotEqual<ModelRef>(new ModelRef("anthropic", "claude"), summary);
        Assert.Equal(new ModelSummary("anthropic", "claude", "Claude", true), summary);
    }

    [Fact]
    public void ModelsRefreshState_HasFourVariants()
    {
        ModelsRefreshState[] states =
        [
            new ModelsRefreshState.Idle(),
            new ModelsRefreshState.Refreshing(),
            new ModelsRefreshState.Done(),
            new ModelsRefreshState.Warning(new Dictionary<string, string> { ["anthropic"] = "offline" }),
        ];

        Assert.Collection(
            states,
            state => Assert.IsType<ModelsRefreshState.Idle>(state),
            state => Assert.IsType<ModelsRefreshState.Refreshing>(state),
            state => Assert.IsType<ModelsRefreshState.Done>(state),
            state => Assert.Equal("offline", Assert.IsType<ModelsRefreshState.Warning>(state).Errors["anthropic"]));
    }

    [Fact]
    public void ModelsState_AllowsAnUnselectedModel()
    {
        var state = new ModelsState(
            new ModelsCatalogState(0, []),
            new ModelsConfigurationState(null, ThinkingLevel.Off),
            new ModelsRefreshState.Idle());

        Assert.Null(state.Configuration.Model);
        Assert.Equal(ThinkingLevel.Off, state.Configuration.ThinkingLevel);
        Assert.Empty(state.Catalog.AvailableModels);
    }

    // ---------------------------------------------------------------- sessions

    [Fact]
    public void SessionSummary_ExtendsTheAddress()
    {
        var summary = new SessionSummary("server-1", "s1", 1_700_000_000_000);
        SessionAddress address = summary;

        Assert.Equal("server-1", address.ServerId);
        Assert.Equal("s1", address.SessionId);
        Assert.Equal(1_700_000_000_000d, summary.CreatedAt);
    }

    [Fact]
    public void SessionCreateOptions_DefaultsToAGeneratedId() =>
        // TS: { id?: string } — omitted means the catalog generates a UUID.
        Assert.Null(new SessionCreateOptions().Id);

    // ---------------------------------------------------------------- presentation surface

    [Fact]
    public void PresentationSelectItem_TreatsDescriptionAsOptional()
    {
        var without = new PresentationSelectItem("v", "label");
        var with = new PresentationSelectItem("v", "label", "why");

        Assert.Null(without.Description);
        Assert.Equal("why", with.Description);
    }

    [Fact]
    public void SlashCommandCompletion_TreatsDescriptionAsOptional()
    {
        Assert.Null(new SlashCommandCompletion("v", "label").Description);
        Assert.Equal("why", new SlashCommandCompletion("v", "label", "why").Description);
    }

    [Fact]
    public void PrepareSessionRequest_AllowsNullPackagePaths() =>
        // TS: packagePaths: readonly string[] | null — null means "whatever the Session already has".
        Assert.Null(new PrepareSessionRequest("s1", null).PackagePaths);
}
