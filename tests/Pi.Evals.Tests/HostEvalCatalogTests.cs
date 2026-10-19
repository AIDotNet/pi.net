using System.Text.Json;
using Xunit;

namespace Pi.Evals.Tests;

public sealed class HostEvalCatalogTests
{
    [Fact]
    public void GetCases_FiltersSmokeAndDocumentationPagesByName()
    {
        var repositoryRoot = NewRepositoryRoot();
        var docsRoot = Path.Combine(repositoryRoot, "src", "Pi.CodingAgent", "Assets", "docs");
        Directory.CreateDirectory(Path.Combine(docsRoot, "nested"));
        File.WriteAllText(Path.Combine(docsRoot, "models.md"), "Models");
        File.WriteAllText(Path.Combine(docsRoot, "nested", "extensions.md"), "Extensions");

        try
        {
            var cases = HostEvalCatalog.GetCases(
                repositoryRoot,
                ["evals/documentation-audit.eval.cs"],
                "nested/extensions");

            var selected = Assert.Single(cases);
            Assert.Equal(
                "Audit documentation against implementation > nested/extensions.md matches the implementation",
                selected.FullName);
            Assert.Equal(Path.Combine(docsRoot, "nested", "extensions.md"), selected.DocumentationPath);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public void GetCases_RejectsUnsupportedHostEvalFiles()
    {
        var repositoryRoot = NewRepositoryRoot();
        try
        {
            var error = Assert.Throws<ArgumentException>(
                () => HostEvalCatalog.GetCases(repositoryRoot, ["evals/unknown.eval.cs"]));

            Assert.Contains("Unsupported host eval file", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public void GetAuditFailure_ReportsTheSubmittedMismatch()
    {
        var error = HostEvalRunner.GetAuditFailure(AuditEvents("mismatch", "The page describes an unavailable option."));

        Assert.Equal(
            "Documentation audit verdict was 'mismatch': The page describes an unavailable option.",
            error);
    }

    [Fact]
    public void GetAuditFailure_AcceptsACompletedMatch()
    {
        Assert.Null(HostEvalRunner.GetAuditFailure(AuditEvents("match", "The documented behavior is implemented.")));
    }

    private static EvalTranscriptEvent[] AuditEvents(string verdict, string explanation)
    {
        const string callId = "audit-call";
        return
        [
            new EvalTranscriptEvent
            {
                Type = "tool_call",
                Id = callId,
                Name = "submit_documentation_audit",
                Arguments = JsonSerializer.SerializeToElement(new { verdict, explanation }, EvalJson.Options),
            },
            new EvalTranscriptEvent
            {
                Type = "tool_result",
                ToolCallId = callId,
            },
        ];
    }

    private static string NewRepositoryRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pi-eval-host-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
