using Pi.CodingAgent.Experimental;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <see cref="ExperimentalServer.AcquireServerProfileAsync"/> — port of
/// experimental-server-profile.test.ts (5 cases).
/// </summary>
/// <remarks>
/// TS asserts the lockfile artifacts are directories (<c>launcher-*.lock</c>); the C#
/// <see cref="Core.NodeLock"/> port expresses the same mutual exclusion through exclusive files, a
/// difference recorded in its own remarks.
/// </remarks>
public class ServerProfileTests : IAsyncDisposable
{
    private const string FirstServerId = "00000000-0000-4000-8000-000000000001";
    private const string SecondServerId = "00000000-0000-4000-8000-000000000002";

    private readonly List<string> _directories = [];

    private string MakeDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"psp-{Guid.NewGuid():N}"[..16]);
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var directory in _directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A stubborn artifact must not mask the test result.
            }
            catch (UnauthorizedAccessException)
            {
                // Ditto.
            }
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task SerializesLaunchersAndPreservesTheServerIdentity()
    {
        var directory = MakeDirectory();
        var first = await ExperimentalServer.AcquireServerProfileAsync(directory);

        var acquired = false;
        var pending = ExperimentalServer.AcquireServerProfileAsync(directory)
            .ContinueWith(task =>
            {
                acquired = true;
                return task.Result;
            }, TaskScheduler.Default);

        // The second launcher is queued behind the first, not rejected.
        await Task.Delay(100);
        Assert.False(acquired);

        await first.Release();

        var second = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(acquired);
        Assert.Equal(first.ServerId, second.ServerId);
        Assert.Equal(first.ServerId,
            (await File.ReadAllTextAsync(Path.Combine(directory, "default-server-id"))).Trim());
        await second.Release();
    }

    [Fact]
    public async Task DoesNotSerializeDifferentServerIdsInOneDirectory()
    {
        var directory = MakeDirectory();
        var first = await ExperimentalServer.AcquireServerProfileAsync(directory, FirstServerId);
        var second = await ExperimentalServer.AcquireServerProfileAsync(directory, SecondServerId);

        Assert.Equal(FirstServerId, first.ServerId);
        Assert.Equal(SecondServerId, second.ServerId);
        Assert.Equal(
            [$"launcher-{FirstServerId}.lock", $"launcher-{SecondServerId}.lock"],
            Directory.GetFiles(directory).Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal).ToList());

        await first.Release();
        await second.Release();
    }

    [Fact]
    public async Task DoesNotShareTheDefaultIdentityAcrossServerDirectories()
    {
        var firstDirectory = MakeDirectory();
        var secondDirectory = MakeDirectory();

        var first = await ExperimentalServer.AcquireServerProfileAsync(firstDirectory);
        var second = await ExperimentalServer.AcquireServerProfileAsync(secondDirectory);

        Assert.NotEqual(first.ServerId, second.ServerId);

        await first.Release();
        await second.Release();
    }

    [Fact]
    public async Task RejectsACorruptDefaultIdentity()
    {
        var directory = MakeDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory, "default-server-id"), "invalid\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExperimentalServer.AcquireServerProfileAsync(directory));
        Assert.Contains("Invalid default experimental server identity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAnInvalidExplicitServerId()
    {
        var directory = MakeDirectory();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExperimentalServer.AcquireServerProfileAsync(directory, "invalid"));
        Assert.Contains("Invalid experimental server ID", error.Message, StringComparison.Ordinal);
    }
}
