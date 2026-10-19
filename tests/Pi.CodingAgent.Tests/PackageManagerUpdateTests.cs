using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class PackageManagerUpdateTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pi-pkg-update-" + Guid.NewGuid().ToString("N"));

    private readonly List<SettingsManager> _settingsManagers = [];

    public PackageManagerUpdateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var settings in _settingsManagers)
        {
            try
            {
                settings.FlushAsync().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task UpdateAsync_InstallsNewerNpmVersionAndEmitsProgress()
    {
        var (cwd, agentDir, settings) = NewEnvironment();
        settings.SetPackages([JsonValue.Create("npm:widget")]);
        WriteInstalledNpm(agentDir, "widget", "1.0.0");

        var runner = new FakeRunner(call =>
        {
            if (call.Arguments.SequenceEqual(["view", "widget", "version", "--json"]))
            {
                return Task.FromResult("\"2.0.0\"");
            }

            if (call.Arguments[0] == "install")
            {
                return Task.FromResult("");
            }

            throw new InvalidOperationException($"Unexpected command: {call.Command} {string.Join(' ', call.Arguments)}");
        });
        var manager = new DefaultPackageManager(settings, cwd, agentDir, commandRunner: runner);
        var progress = new List<PackageProgressEvent>();
        manager.SetProgressCallback(progress.Add);

        await manager.UpdateAsync();

        var view = Assert.Single(runner.Calls, call => call.Arguments[0] == "view");
        Assert.Equal(10000, view.Options.TimeoutMs);
        Assert.Equal(cwd, view.Options.WorkingDirectory);
        Assert.Equal(
            ["install", "widget@latest", "--prefix", Path.Combine(agentDir, "npm"), "--legacy-peer-deps"],
            Assert.Single(runner.Calls, call => call.Arguments[0] == "install").Arguments);
        Assert.Contains(progress, item => item.Type == "start" && item.Action == "update");
        Assert.Contains(progress, item => item.Type == "complete" && item.Action == "update");
    }

    [Fact]
    public async Task UpdateAsync_SelectsHighestVersionWithinConfiguredSemverRange()
    {
        var (cwd, agentDir, settings) = NewEnvironment();
        settings.SetPackages([JsonValue.Create("npm:widget@^1.0.0")]);
        WriteInstalledNpm(agentDir, "widget", "1.0.0");

        var runner = new FakeRunner(call => call.Arguments[0] switch
        {
            "view" => Task.FromResult("""["1.1.0","2.0.0"]"""),
            "install" => Task.FromResult(""),
            _ => throw new InvalidOperationException($"Unexpected command: {call.Command}"),
        });
        var manager = new DefaultPackageManager(settings, cwd, agentDir, commandRunner: runner);

        await manager.UpdateAsync();

        var view = Assert.Single(runner.Calls, call => call.Arguments[0] == "view");
        Assert.Equal(["view", "widget@^1.0.0", "version", "--json"], view.Arguments);
        var install = Assert.Single(runner.Calls, call => call.Arguments[0] == "install");
        Assert.Equal("widget@^1.0.0", install.Arguments[1]);
    }

    [Fact]
    public async Task UpdateAsync_SkipsExactNpmVersions()
    {
        var (cwd, agentDir, settings) = NewEnvironment();
        settings.SetPackages([JsonValue.Create("npm:widget@1.0.0")]);
        var runner = new FakeRunner(_ =>
            throw new InvalidOperationException("Pinned versions must not run package commands."));
        var manager = new DefaultPackageManager(settings, cwd, agentDir, commandRunner: runner);

        await manager.UpdateAsync();

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task UpdateAsync_BoundsConcurrentNpmVersionChecksToFour()
    {
        var (cwd, agentDir, settings) = NewEnvironment();
        var packageNames = Enumerable.Range(0, 7).Select(index => $"widget-{index}").ToArray();
        settings.SetPackages(packageNames.Select(name => (JsonNode?)JsonValue.Create($"npm:{name}")).ToArray());
        foreach (var name in packageNames)
        {
            WriteInstalledNpm(agentDir, name, "1.0.0");
        }

        var active = 0;
        var maxActive = 0;
        var runner = new FakeRunner(async call =>
        {
            if (call.Arguments[0] == "view")
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maxActive, current);
                try
                {
                    await Task.Delay(25);
                    return "\"2.0.0\"";
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }

            if (call.Arguments[0] == "install")
            {
                return "";
            }

            throw new InvalidOperationException($"Unexpected command: {call.Command}");
        });
        var manager = new DefaultPackageManager(settings, cwd, agentDir, commandRunner: runner);

        await manager.UpdateAsync();

        Assert.InRange(maxActive, 2, 4);
        Assert.Equal(packageNames.Length, runner.Calls.Count(call => call.Arguments[0] == "view"));
    }

    [Fact]
    public async Task CheckForAvailableUpdatesAsync_ReportsNpmAndGitButSkipsPinnedSources()
    {
        var (cwd, agentDir, settings) = NewEnvironment();
        const string gitSource = "https://github.com/acme/repo.git";
        settings.SetPackages(
        [
            JsonValue.Create("npm:widget"),
            JsonValue.Create("npm:fixed@1.0.0"),
            JsonValue.Create(gitSource),
        ]);
        WriteInstalledNpm(agentDir, "widget", "1.0.0");
        WriteInstalledNpm(agentDir, "fixed", "1.0.0");
        var manager = new DefaultPackageManager(
            settings,
            cwd,
            agentDir,
            commandRunner: new FakeRunner(call =>
            {
                if (call.Command == "npm" && call.Arguments[0] == "view")
                {
                    return Task.FromResult("\"2.0.0\"");
                }

                if (call.Command == "git" && call.Arguments.SequenceEqual(["rev-parse", "HEAD"]))
                {
                    return Task.FromResult(new string('a', 40));
                }

                if (call.Command == "git"
                    && call.Arguments.SequenceEqual(["rev-parse", "--abbrev-ref", "@{upstream}"]))
                {
                    return Task.FromResult("origin/main");
                }

                if (call.Command == "git"
                    && call.Arguments.SequenceEqual(["ls-remote", "origin", "refs/heads/main"]))
                {
                    return Task.FromResult($"{new string('b', 40)}\trefs/heads/main\n");
                }

                throw new InvalidOperationException(
                    $"Unexpected command: {call.Command} {string.Join(' ', call.Arguments)}");
            }));
        Directory.CreateDirectory(manager.GetGitInstallPath(DefaultPackageManager.ParseSource(gitSource), "user"));

        var updates = await manager.CheckForAvailableUpdatesAsync();

        Assert.Equal(2, updates.Count);
        Assert.Equal("npm:widget", updates[0].Source);
        Assert.Equal("widget", updates[0].DisplayName);
        Assert.Equal("npm", updates[0].Type);
        Assert.Equal("user", updates[0].Scope);
        Assert.Equal(gitSource, updates[1].Source);
        Assert.Equal("github.com/acme/repo", updates[1].DisplayName);
        Assert.Equal("git", updates[1].Type);
    }

    [Fact]
    public async Task UpdateAsync_ResetsGitCheckoutAndReinstallsDependencies()
    {
        var (cwd, agentDir, settings) = NewEnvironment();
        const string source = "https://github.com/acme/repo.git";
        settings.SetPackages([JsonValue.Create(source)]);
        var markerPath = "";
        var runner = new FakeRunner(call =>
        {
            if (call.Command == "npm" && call.Arguments[0] == "install")
            {
                return Task.FromResult("");
            }

            if (call.Command == "git"
                && call.Arguments.SequenceEqual(["rev-parse", "--abbrev-ref", "@{upstream}"]))
            {
                return Task.FromResult("origin/main");
            }

            if (call.Command == "git" && call.Arguments.SequenceEqual(["rev-parse", "@{upstream}"]))
            {
                return Task.FromResult("remote-head");
            }

            if (call.Command == "git" && call.Arguments.SequenceEqual(["rev-parse", "HEAD"]))
            {
                return Task.FromResult("local-head");
            }

            if (call.Command == "git"
                && call.Arguments.SequenceEqual(["rev-parse", "@{upstream}^{commit}"]))
            {
                return Task.FromResult("remote-head");
            }

            if (call.Command == "git" && call.Arguments[0] == "reset")
            {
                Assert.True(File.Exists(markerPath));
                return Task.FromResult("");
            }

            if (call.Command == "git"
                && (call.Arguments[0] is "fetch" or "clean"))
            {
                return Task.FromResult("");
            }

            throw new InvalidOperationException(
                $"Unexpected command: {call.Command} {string.Join(' ', call.Arguments)}");
        });
        var manager = new DefaultPackageManager(settings, cwd, agentDir, commandRunner: runner);
        var gitPath = manager.GetGitInstallPath(DefaultPackageManager.ParseSource(source), "user");
        Directory.CreateDirectory(gitPath);
        File.WriteAllText(
            Path.Combine(gitPath, "package.json"),
            """{"name":"repo","dependencies":{"example-dep":"1.0.0"}}""");
        markerPath = Path.Combine(
            Directory.GetParent(gitPath)!.FullName,
            $".{Path.GetFileName(gitPath)}.pi-update-incomplete");

        await manager.UpdateAsync();

        Assert.Contains(
            runner.Calls,
            call => call.Command == "git"
                && call.Arguments.SequenceEqual(["reset", "--hard", "@{upstream}^{commit}"]));
        Assert.Contains(
            runner.Calls,
            call => call.Command == "git" && call.Arguments.SequenceEqual(["clean", "-fdx"]));
        Assert.Contains(
            runner.Calls,
            call => call.Command == "npm"
                && call.Arguments.SequenceEqual(["install", "--omit=dev", "--legacy-peer-deps"])
                && call.Options.WorkingDirectory == gitPath);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task UpdateAsync_ResumesCleanupWhenAnIncompleteGitMarkerExists()
    {
        var (cwd, agentDir, settings) = NewEnvironment();
        const string source = "https://github.com/acme/repo.git";
        settings.SetPackages([JsonValue.Create(source)]);
        var runner = new FakeRunner(call =>
        {
            if (call.Command == "npm" && call.Arguments[0] == "install")
            {
                return Task.FromResult("");
            }

            if (call.Command == "git"
                && call.Arguments.SequenceEqual(["rev-parse", "--abbrev-ref", "@{upstream}"]))
            {
                return Task.FromResult("origin/main");
            }

            if (call.Command == "git"
                && (call.Arguments.SequenceEqual(["rev-parse", "@{upstream}"])
                    || call.Arguments.SequenceEqual(["rev-parse", "HEAD"])
                    || call.Arguments.SequenceEqual(["rev-parse", "@{upstream}^{commit}"])))
            {
                return Task.FromResult("same-head");
            }

            if (call.Command == "git"
                && (call.Arguments[0] is "fetch" or "clean"))
            {
                return Task.FromResult("");
            }

            throw new InvalidOperationException(
                $"Unexpected command: {call.Command} {string.Join(' ', call.Arguments)}");
        });
        var manager = new DefaultPackageManager(settings, cwd, agentDir, commandRunner: runner);
        var gitPath = manager.GetGitInstallPath(DefaultPackageManager.ParseSource(source), "user");
        Directory.CreateDirectory(gitPath);
        File.WriteAllText(
            Path.Combine(gitPath, "package.json"),
            """{"name":"repo","dependencies":{"example-dep":"1.0.0"}}""");
        var markerPath = Path.Combine(
            Directory.GetParent(gitPath)!.FullName,
            $".{Path.GetFileName(gitPath)}.pi-update-incomplete");
        File.WriteAllText(markerPath, "");

        await manager.UpdateAsync();

        Assert.Contains(runner.Calls, call =>
            call.Command == "git" && call.Arguments.SequenceEqual(["clean", "-fdx"]));
        Assert.Contains(runner.Calls, call =>
            call.Command == "npm"
            && call.Arguments.SequenceEqual(["install", "--omit=dev", "--legacy-peer-deps"])
            && call.Options.WorkingDirectory == gitPath);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task UpdateAsync_FetchesAndResetsToAnExplicitGitRef()
    {
        var (cwd, agentDir, settings) = NewEnvironment();
        const string source = "https://github.com/acme/repo.git#v2";
        settings.SetPackages([JsonValue.Create(source)]);
        var runner = new FakeRunner(call =>
        {
            if (call.Command == "npm" && call.Arguments[0] == "install")
            {
                return Task.FromResult("");
            }

            if (call.Command == "git" && call.Arguments[0] == "fetch")
            {
                Assert.Equal(["fetch", "origin", "v2"], call.Arguments);
                return Task.FromResult("");
            }

            if (call.Command == "git"
                && call.Arguments.SequenceEqual(["rev-parse", "HEAD"]))
            {
                return Task.FromResult("old-head");
            }

            if (call.Command == "git"
                && call.Arguments.SequenceEqual(["rev-parse", "FETCH_HEAD^{commit}"]))
            {
                return Task.FromResult("new-head");
            }

            if (call.Command == "git" && call.Arguments[0] == "reset")
            {
                Assert.Equal(["reset", "--hard", "FETCH_HEAD^{commit}"], call.Arguments);
                return Task.FromResult("");
            }

            if (call.Command == "git" && call.Arguments.SequenceEqual(["clean", "-fdx"]))
            {
                return Task.FromResult("");
            }

            throw new InvalidOperationException(
                $"Unexpected command: {call.Command} {string.Join(' ', call.Arguments)}");
        });
        var manager = new DefaultPackageManager(settings, cwd, agentDir, commandRunner: runner);
        var parsed = DefaultPackageManager.ParseSource(source);
        Assert.Equal("v2", parsed.Ref);
        var gitPath = manager.GetGitInstallPath(parsed, "user");
        Directory.CreateDirectory(gitPath);
        File.WriteAllText(Path.Combine(gitPath, "package.json"), """{"name":"repo"}""");

        await manager.UpdateAsync();

        Assert.Contains(runner.Calls, call =>
            call.Command == "git"
            && call.Arguments.SequenceEqual(["reset", "--hard", "FETCH_HEAD^{commit}"]));
        Assert.Contains(runner.Calls, call =>
            call.Command == "npm"
            && call.Arguments.SequenceEqual(["install", "--omit=dev", "--legacy-peer-deps"])
            && call.Options.WorkingDirectory == gitPath);
    }

    [Fact]
    public void GetNpmInstallPath_FallsBackToLegacyGlobalNpmRoot()
    {
        var (cwd, agentDir, settings) = NewEnvironment();
        var globalRoot = Path.Combine(_root, "legacy-npm", "node_modules");
        var legacyPackage = Path.Combine(globalRoot, "legacy-package");
        Directory.CreateDirectory(legacyPackage);
        var runner = new FakeRunner(call =>
        {
            if (call.Arguments.SequenceEqual(["root", "-g"]))
            {
                return Task.FromResult(globalRoot);
            }

            throw new InvalidOperationException($"Unexpected command: {call.Command} {string.Join(' ', call.Arguments)}");
        });
        var manager = new DefaultPackageManager(settings, cwd, agentDir, commandRunner: runner);

        Assert.Equal(legacyPackage, manager.GetInstalledPath("npm:legacy-package", "user"));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public void GetNpmInstallPath_UsesPnpmGlobalPackageLocation()
    {
        var (cwd, agentDir, settings) = NewEnvironment();
        settings.SetNpmCommand(["pnpm"]);
        var legacyPackage = Path.Combine(_root, "pnpm-global", "legacy-package");
        Directory.CreateDirectory(legacyPackage);
        var runner = new FakeRunner(call =>
        {
            if (call.Arguments.SequenceEqual(["list", "-g", "--depth", "0", "--json"]))
            {
                var output = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        dependencies = new Dictionary<string, object>
                        {
                            ["legacy-package"] = new { path = legacyPackage },
                        },
                    },
                });
                return Task.FromResult(output);
            }

            throw new InvalidOperationException($"Unexpected command: {call.Command}");
        });
        var manager = new DefaultPackageManager(settings, cwd, agentDir, commandRunner: runner);

        Assert.Equal(legacyPackage, manager.GetInstalledPath("npm:legacy-package", "user"));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task UpdateAsync_RecognizesPnpmWrappedByEnvironmentManager()
    {
        var (cwd, agentDir, settings) = NewEnvironment();
        settings.SetNpmCommand(["mise", "exec", "node@20", "--", "pnpm"]);
        settings.SetPackages([JsonValue.Create("npm:widget")]);
        WriteInstalledNpm(agentDir, "widget", "1.0.0");
        var runner = new FakeRunner(call => call.Arguments[^1] == "--json"
            ? Task.FromResult("\"2.0.0\"")
            : Task.FromResult(""));
        var manager = new DefaultPackageManager(settings, cwd, agentDir, commandRunner: runner);

        await manager.UpdateAsync();

        Assert.Contains(runner.Calls, call =>
            call.Command == "mise"
            && call.Arguments.SequenceEqual(
                ["exec", "node@20", "--", "pnpm", "install", "widget@latest", "--prefix",
                    Path.Combine(agentDir, "npm"), "--config.auto-install-peers=false",
                    "--config.strict-peer-dependencies=false", "--config.strict-dep-builds=false"]));
    }

    [Fact]
    public async Task UpdateAsync_SuggestsSourcePrefixForUnqualifiedPackageName()
    {
        var (cwd, agentDir, settings) = NewEnvironment();
        settings.SetPackages([JsonValue.Create("npm:example")]);
        var manager = new DefaultPackageManager(settings, cwd, agentDir);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.UpdateAsync("example"));

        Assert.Equal(
            "No matching package found for example. Did you mean npm:example?",
            error.Message);
    }

    private (string Cwd, string AgentDir, SettingsManager Settings) NewEnvironment()
    {
        var cwd = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var agentDir = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(agentDir);
        var settings = SettingsManager.Create(cwd, agentDir);
        _settingsManagers.Add(settings);
        return (cwd, agentDir, settings);
    }

    private static void WriteInstalledNpm(string agentDir, string packageName, string version)
    {
        var packagePath = Path.Combine(
            agentDir,
            "npm",
            "node_modules",
            packageName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(packagePath);
        File.WriteAllText(
            Path.Combine(packagePath, "package.json"),
            JsonSerializer.Serialize(new { name = packageName, version }));
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        var observed = Volatile.Read(ref maximum);
        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, value, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    private sealed record ProcessCall(
        string Command,
        IReadOnlyList<string> Arguments,
        PackageProcessOptions Options);

    private sealed class FakeRunner(Func<ProcessCall, Task<string>> run) : IPackageCommandRunner
    {
        public ConcurrentQueue<ProcessCall> Calls { get; } = new();

        public Task<string> RunAsync(
            string command,
            IReadOnlyList<string> arguments,
            PackageProcessOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = new ProcessCall(
                command,
                [.. arguments],
                options ?? new PackageProcessOptions());
            Calls.Enqueue(call);
            return run(call);
        }
    }
}
