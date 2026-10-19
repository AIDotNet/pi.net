using System.Security.Cryptography;
using System.Text;
using Pi.CodingAgent.Experimental.Vacation;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class VacationSessionsTests
{
    [Fact]
    public async Task SelectAsyncCreatesIsolatedSessionForWorkingDirectory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cwd = Path.Combine(root, "work");
            var agentDirectory = Path.Combine(root, "agent");
            Directory.CreateDirectory(cwd);

            await using var session = await VacationSessions.SelectAsync(cwd, false, agentDirectory);

            var expectedHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(cwd))))[..24];
            Assert.Equal(Path.GetFullPath(cwd), session.Cwd);
            Assert.Equal(expectedHash, Path.GetFileName(Path.GetDirectoryName(session.Directory)));
            Assert.True(session.Created);
            Assert.Equal(Path.Combine(session.Directory, "session.sqlite"), session.Database);
            Assert.True(File.Exists(Path.Combine(session.Directory, ".vacation.lock")));
            Assert.Matches(@"^[0-9]{13}-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", session.Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ContinueSelectsNewestWellFormedSessionAndIgnoresMalformedDirectories()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cwd = Path.Combine(root, "work");
            var agentDirectory = Path.Combine(root, "agent");
            Directory.CreateDirectory(cwd);

            var hash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(cwd))))[..24];
            var sessionRoot = Path.Combine(agentDirectory, "experimental", "vacation-sessions", hash);
            var older = Path.Combine(sessionRoot, "0000000000001-00000000-0000-0000-0000-000000000001");
            var newer = Path.Combine(sessionRoot, "0000000000002-00000000-0000-0000-0000-000000000002");
            Directory.CreateDirectory(older);
            Directory.CreateDirectory(newer);
            Directory.CreateDirectory(Path.Combine(sessionRoot, "9999999999999-not-a-session"));

            await using var session = await VacationSessions.SelectAsync(cwd, true, agentDirectory);

            Assert.Equal(Path.GetFileName(newer), session.Id);
            Assert.Equal(newer, session.Directory);
            Assert.False(session.Created);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ContinueWithoutSessionFailsAndOpenLockCanBeReacquiredAfterRelease()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cwd = Path.Combine(root, "work");
            var agentDirectory = Path.Combine(root, "agent");
            Directory.CreateDirectory(cwd);

            var missing = await Assert.ThrowsAsync<InvalidOperationException>(
                () => VacationSessions.SelectAsync(cwd, true, agentDirectory));
            Assert.Contains("No vacation session exists", missing.Message, StringComparison.Ordinal);

            var first = await VacationSessions.SelectAsync(cwd, false, agentDirectory);
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => VacationSessions.SelectAsync(cwd, true, agentDirectory, cancellationToken: cancellation.Token));
            }
            finally
            {
                await first.DisposeAsync();
            }

            await using var reopened = await VacationSessions.SelectAsync(cwd, true, agentDirectory);
            Assert.Equal(first.Id, reopened.Id);
            Assert.False(reopened.Created);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pi-vacation-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
