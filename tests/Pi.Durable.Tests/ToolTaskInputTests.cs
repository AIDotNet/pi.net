using Pi.Ai.Types;
using Pi.Durable.Harness;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// pi.tool 的持久化输入（`tool.ts` 的 <c>ToolTaskInput</c>）：模型发起的调用与嵌套调用的联合体。
/// v1/v2 的行没有 <c>kind</c>，按模型发起的调用读——这条兼容是旧数据能继续跑的前提。
/// </summary>
public class ToolTaskInputTests
{
    [Fact]
    public void RoundTripsModelIssuedInputs()
    {
        var input = new ToolTaskInput.Model(EntryId.From(7), "call-1");
        var json = input.ToJson();

        Assert.Equal("model", json["kind"]);
        Assert.Equal(7L, json["assistant"]);
        Assert.Equal("call-1", json["callId"]);
        Assert.False(json.ContainsKey("unoffered"));

        var parsed = Assert.IsType<ToolTaskInput.Model>(ToolTaskInput.FromJson(json));
        Assert.Equal(7L, parsed.Assistant.Value);
        Assert.Equal("call-1", parsed.CallId);
        Assert.Null(parsed.Unoffered);
    }

    [Fact]
    public void KeepsTheUnofferedFlag()
    {
        var json = new ToolTaskInput.Model(EntryId.From(1), "c", Unoffered: true).ToJson();
        Assert.True(json["unoffered"] is true);

        var parsed = Assert.IsType<ToolTaskInput.Model>(ToolTaskInput.FromJson(json));
        Assert.True(parsed.Unoffered);
    }

    [Fact]
    public void ReadsInputsWithoutKindAsModelIssued()
    {
        // v1/v2 的形状：没有 kind。
        var json = new Dictionary<string, object?> { ["assistant"] = 3L, ["callId"] = "old-call" };

        var parsed = Assert.IsType<ToolTaskInput.Model>(ToolTaskInput.FromJson(json));
        Assert.Equal(3L, parsed.Assistant.Value);
        Assert.Equal("old-call", parsed.CallId);
    }

    [Fact]
    public void RoundTripsNestedInputs()
    {
        var call = new ToolCallContent(
            "call-1/1", "shell", new Dictionary<string, object?> { ["command"] = "ls" });
        var input = new ToolTaskInput.Nested(TaskId<object?>.From(9), "call-1", "1", call, Progress: false);
        var json = input.ToJson();

        Assert.Equal("nested", json["kind"]);
        Assert.Equal(9L, json["parent"]);
        Assert.Equal("call-1", json["parentCallId"]);
        Assert.Equal("1", json["key"]);
        Assert.Equal(false, json["progress"]);

        var parsed = Assert.IsType<ToolTaskInput.Nested>(ToolTaskInput.FromJson(json));
        Assert.Equal(9L, parsed.Parent.Value);
        Assert.Equal("call-1", parsed.ParentCallId);
        Assert.Equal("1", parsed.Key);
        Assert.Equal("shell", parsed.Call.Name);
        Assert.Equal(false, parsed.Progress);
    }

    [Fact]
    public void NestedCallDocumentsAreTaskScoped()
    {
        Assert.Equal("pi.tool.nested-call", ToolTask.NestedCallDoc.Definition.Kind);
        Assert.Equal(1, ToolTask.NestedCallDoc.Definition.Version);
        Assert.IsType<DocumentSemantics.TaskScope>(ToolTask.NestedCallDoc.Definition.Semantics);
        Assert.Empty(ToolTask.NestedCallDoc.Definition.Initial(null));
        // 文档随任务退休：从不按 checkpoint 条件存基线。
        Assert.False(ToolTask.NestedCallDoc.Definition.CheckpointWhen!(
            new Dictionary<string, object?>(), [], new CheckpointInfo { DeltasSinceBase = 0 }));
    }

    [Fact]
    public void ReadsLegacyResultsWithoutKindAsModelIssued()
    {
        // v1 的结果只有 entryId：老任务记录必须仍能读。
        var json = new Dictionary<string, object?> { ["entryId"] = 12L };
        var parsed = Assert.IsType<ToolTaskResult.Model>(ToolTaskResult.FromJson(json));
        Assert.Equal(12L, parsed.EntryId.Value);
        Assert.Null(parsed.Control);
    }

    [Fact]
    public void ReadsNestedResultsWithoutAnEntry()
    {
        var parsed = ToolTaskResult.FromJson(new Dictionary<string, object?> { ["kind"] = "nested" });
        Assert.IsType<ToolTaskResult.Nested>(parsed);
    }
}
