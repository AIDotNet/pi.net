using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

public sealed partial class DefaultPackageManager
{
    private const int NetworkTimeoutMs = 10000;
    private const int UpdateCheckConcurrency = 4;
    private const int GitUpdateConcurrency = 4;

    private string? _globalNpmRoot;
    private string? _globalNpmRootCommandKey;

    public async Task UpdateAsync(string? source = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = source is null ? null : GetPackageIdentity(source, null);
        var updateSources = new List<PackageEntry>();
        var matched = false;

        void AddSources(IReadOnlyList<JsonNode?> packages, string scope)
        {
            foreach (var package in packages)
            {
                var packageSource = GetPackageSourceString(package) ?? "";
                if (identity is not null && GetPackageIdentity(packageSource, scope) != identity)
                {
                    continue;
                }

                matched = true;
                updateSources.Add(new PackageEntry(package, scope));
            }
        }

        AddSources(GlobalPackageNodes(), SourceScope.User);
        AddSources(ProjectPackageNodes(), SourceScope.Project);

        if (source is not null && !matched)
        {
            throw new InvalidOperationException(
                BuildNoMatchingPackageMessage(source, [.. GlobalPackageNodes(), .. ProjectPackageNodes()]));
        }

        await UpdateConfiguredSourcesAsync(updateSources, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PackageUpdate>> CheckForAvailableUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsOfflineModeEnabled())
        {
            return [];
        }

        var allPackages = new List<PackageEntry>();
        allPackages.AddRange(ProjectPackageNodes().Select(package => new PackageEntry(package, SourceScope.Project)));
        allPackages.AddRange(GlobalPackageNodes().Select(package => new PackageEntry(package, SourceScope.User)));

        var packageSources = DedupePackages(allPackages);
        var checks = packageSources
            .Where(entry => entry.Scope != SourceScope.Temporary)
            .Select(entry => (Func<Task<PackageUpdate?>>)(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = GetPackageSourceString(entry.Package) ?? "";
                var parsed = ParseSource(source);
                if (parsed.Type == "local" || parsed.Pinned)
                {
                    return null;
                }

                if (parsed.Type == "npm")
                {
                    var installedPath = GetNpmInstallPath(parsed, entry.Scope);
                    if (!Directory.Exists(installedPath)
                        || !await NpmHasAvailableUpdateAsync(parsed, installedPath, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return null;
                    }

                    return new PackageUpdate(source, parsed.Name ?? source, "npm", entry.Scope);
                }

                var gitInstallPath = GetGitInstallPath(parsed, entry.Scope);
                if (!Directory.Exists(gitInstallPath)
                    || !await GitHasAvailableUpdateAsync(gitInstallPath, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return new PackageUpdate(
                    source,
                    $"{parsed.Host}/{parsed.Path}",
                    "git",
                    entry.Scope);
            }))
            .ToArray();

        var results = await RunWithConcurrencyAsync(checks, UpdateCheckConcurrency, cancellationToken)
            .ConfigureAwait(false);
        return results.Where(update => update is not null).Select(update => update!).ToArray();
    }

    private async Task UpdateConfiguredSourcesAsync(
        IReadOnlyList<PackageEntry> sources,
        CancellationToken cancellationToken)
    {
        if (IsOfflineModeEnabled() || sources.Count == 0)
        {
            return;
        }

        var npmCandidates = new List<NpmUpdateTarget>();
        var gitCandidates = new List<GitUpdateSource>();
        foreach (var entry in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = GetPackageSourceString(entry.Package) ?? "";
            var parsed = ParseSource(source);
            if (parsed.Type == "npm" && !parsed.Pinned)
            {
                npmCandidates.Add(new NpmUpdateTarget(source, entry.Scope, parsed));
            }
            else if (parsed.Type == "git")
            {
                gitCandidates.Add(new GitUpdateSource(source, entry.Scope, parsed));
            }
        }

        var npmChecks = npmCandidates
            .Select(entry => (Func<Task<(NpmUpdateTarget Entry, bool ShouldUpdate)>>)(async () =>
                (entry, await ShouldUpdateNpmSourceAsync(entry.Parsed, entry.Scope, cancellationToken)
                    .ConfigureAwait(false))))
            .ToArray();
        var npmResults = await RunWithConcurrencyAsync(
            npmChecks,
            UpdateCheckConcurrency,
            cancellationToken).ConfigureAwait(false);
        var userNpmUpdates = new List<NpmUpdateTarget>();
        var projectNpmUpdates = new List<NpmUpdateTarget>();
        foreach (var (entry, shouldUpdate) in npmResults)
        {
            if (!shouldUpdate)
            {
                continue;
            }

            if (entry.Scope == SourceScope.User)
            {
                userNpmUpdates.Add(entry);
            }
            else
            {
                projectNpmUpdates.Add(entry);
            }
        }

        var updates = new List<Task>();
        if (userNpmUpdates.Count > 0)
        {
            updates.Add(UpdateNpmBatchAsync(userNpmUpdates, SourceScope.User, cancellationToken));
        }

        if (projectNpmUpdates.Count > 0)
        {
            updates.Add(UpdateNpmBatchAsync(projectNpmUpdates, SourceScope.Project, cancellationToken));
        }

        if (gitCandidates.Count > 0)
        {
            var gitUpdates = gitCandidates
                .Select(entry => (Func<Task<bool>>)(async () =>
                {
                    await WithProgressAsync(
                        "update",
                        entry.Source,
                        $"Updating {entry.Source}...",
                        () => UpdateGitAsync(entry.Parsed, entry.Scope, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    return true;
                }))
                .ToArray();
            updates.Add(RunWithConcurrencyAsync(gitUpdates, GitUpdateConcurrency, cancellationToken));
        }

        await Task.WhenAll(updates).ConfigureAwait(false);
    }

    private async Task<bool> ShouldUpdateNpmSourceAsync(
        ParsedPackageSource source,
        string scope,
        CancellationToken cancellationToken)
    {
        var installedPath = GetManagedNpmInstallPath(source, scope);
        var installedVersion = Directory.Exists(installedPath) ? GetInstalledNpmVersion(installedPath) : null;
        if (installedVersion is null)
        {
            return true;
        }

        try
        {
            var targetVersion = await GetLatestNpmVersionAsync(source, cancellationToken).ConfigureAwait(false);
            return CompareVersions(targetVersion, installedVersion) > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return true;
        }
    }

    private async Task UpdateNpmBatchAsync(
        IReadOnlyList<NpmUpdateTarget> sources,
        string scope,
        CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
        {
            return;
        }

        var sourceLabel = sources.Count == 1 ? sources[0].Source : $"{scope} npm packages";
        var message = sources.Count == 1
            ? $"Updating {sources[0].Source}..."
            : $"Updating {scope} npm packages...";
        var specs = sources
            .Select(entry => entry.Parsed.Version is not null
                ? entry.Parsed.Spec
                : $"{entry.Parsed.Name}@latest")
            .ToArray();

        await WithProgressAsync(
            "update",
            sourceLabel,
            message,
            async () =>
            {
                var installRoot = GetNpmInstallRoot(scope, temporary: false);
                EnsureNpmProject(installRoot);
                await RunNpmCommandAsync(
                    GetNpmInstallArgs(specs, installRoot),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetLatestNpmVersionAsync(
        ParsedPackageSource source,
        CancellationToken cancellationToken)
    {
        var npm = GetNpmInvocation();
        var packageSpec = source.Version is not null ? source.Spec : source.Name ?? source.Spec;
        var output = await RunCommandCaptureAsync(
            npm.Command,
            [.. npm.Args, "view", packageSpec, "version", "--json"],
            workingDirectory: _cwd,
            timeoutMs: NetworkTimeoutMs,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var raw = output.Trim();
        if (raw.Length == 0)
        {
            throw new InvalidDataException("Empty response from npm view");
        }

        var parsed = JsonNode.Parse(raw);
        if (parsed is JsonValue value && value.TryGetValue<string>(out var version))
        {
            return version;
        }

        if (parsed is JsonArray versions)
        {
            string? latest = null;
            foreach (var item in versions)
            {
                if (item is not JsonValue versionValue
                    || !versionValue.TryGetValue<string>(out var candidate)
                    || string.IsNullOrEmpty(candidate)
                    || Semver.Valid(candidate) is null
                    || (source.Range is not null && !SemverRange.Satisfies(candidate, source.Range)))
                {
                    continue;
                }

                if (latest is null || CompareVersions(candidate, latest) > 0)
                {
                    latest = candidate;
                }
            }

            if (latest is not null)
            {
                return latest;
            }
        }

        throw new InvalidDataException("Unexpected response from npm view");
    }

    private async Task<bool> NpmHasAvailableUpdateAsync(
        ParsedPackageSource source,
        string installedPath,
        CancellationToken cancellationToken)
    {
        if (IsOfflineModeEnabled())
        {
            return false;
        }

        var installedVersion = GetInstalledNpmVersion(installedPath);
        if (installedVersion is null)
        {
            return false;
        }

        try
        {
            var targetVersion = await GetLatestNpmVersionAsync(source, cancellationToken).ConfigureAwait(false);
            return CompareVersions(targetVersion, installedVersion) > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> GitHasAvailableUpdateAsync(string installedPath, CancellationToken cancellationToken)
    {
        if (IsOfflineModeEnabled())
        {
            return false;
        }

        try
        {
            var localHead = await RunCommandCaptureAsync(
                "git",
                ["rev-parse", "HEAD"],
                workingDirectory: installedPath,
                timeoutMs: NetworkTimeoutMs,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var remoteHead = await GetRemoteGitHeadAsync(installedPath, cancellationToken).ConfigureAwait(false);
            return !string.Equals(localHead.Trim(), remoteHead.Trim(), StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> GetRemoteGitHeadAsync(string installedPath, CancellationToken cancellationToken)
    {
        var upstreamRef = await GetGitUpstreamRefAsync(installedPath, cancellationToken).ConfigureAwait(false);
        if (upstreamRef is not null)
        {
            var remoteOutput = await RunGitRemoteCommandAsync(
                installedPath,
                ["ls-remote", "origin", upstreamRef],
                cancellationToken).ConfigureAwait(false);
            var upstreamMatch = Regex.Match(remoteOutput, @"^([0-9a-f]{40})\s+", RegexOptions.Multiline);
            if (upstreamMatch.Success)
            {
                return upstreamMatch.Groups[1].Value;
            }
        }

        var headOutput = await RunGitRemoteCommandAsync(
            installedPath,
            ["ls-remote", "origin", "HEAD"],
            cancellationToken).ConfigureAwait(false);
        var headMatch = Regex.Match(headOutput, @"^([0-9a-f]{40})\s+HEAD$", RegexOptions.Multiline);
        if (!headMatch.Success)
        {
            throw new InvalidDataException("Failed to determine remote HEAD");
        }

        return headMatch.Groups[1].Value;
    }

    private async Task<string?> GetGitUpstreamRefAsync(
        string installedPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var upstream = await RunCommandCaptureAsync(
                "git",
                ["rev-parse", "--abbrev-ref", "@{upstream}"],
                workingDirectory: installedPath,
                timeoutMs: NetworkTimeoutMs,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var trimmed = upstream.Trim();
            if (!trimmed.StartsWith("origin/", StringComparison.Ordinal))
            {
                return null;
            }

            var branch = trimmed["origin/".Length..];
            return branch.Length > 0 ? $"refs/heads/{branch}" : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<LocalGitUpdateTarget> GetLocalGitUpdateTargetAsync(
        string installedPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var upstream = await RunCommandCaptureAsync(
                "git",
                ["rev-parse", "--abbrev-ref", "@{upstream}"],
                workingDirectory: installedPath,
                timeoutMs: NetworkTimeoutMs,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var trimmedUpstream = upstream.Trim();
            if (!trimmedUpstream.StartsWith("origin/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unsupported upstream remote: {trimmedUpstream}");
            }

            var branch = trimmedUpstream["origin/".Length..];
            if (branch.Length == 0)
            {
                throw new InvalidOperationException("Missing upstream branch name");
            }

            var head = await RunCommandCaptureAsync(
                "git",
                ["rev-parse", "@{upstream}"],
                workingDirectory: installedPath,
                timeoutMs: NetworkTimeoutMs,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new LocalGitUpdateTarget(
                "@{upstream}",
                head,
                ["fetch", "--prune", "--no-tags", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            try
            {
                await RunCommandAsync(
                    "git",
                    ["remote", "set-head", "origin", "-a"],
                    installedPath,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }

            var head = await RunCommandCaptureAsync(
                "git",
                ["rev-parse", "origin/HEAD"],
                workingDirectory: installedPath,
                timeoutMs: NetworkTimeoutMs,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            string originHeadRef;
            try
            {
                originHeadRef = await RunCommandCaptureAsync(
                    "git",
                    ["symbolic-ref", "refs/remotes/origin/HEAD"],
                    workingDirectory: installedPath,
                    timeoutMs: NetworkTimeoutMs,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                originHeadRef = "";
            }

            var branch = originHeadRef.Trim();
            const string originPrefix = "refs/remotes/origin/";
            if (branch.StartsWith(originPrefix, StringComparison.Ordinal))
            {
                branch = branch[originPrefix.Length..];
            }

            var fetchArgs = branch.Length > 0
                ? new[]
                {
                    "fetch", "--prune", "--no-tags", "origin",
                    $"+refs/heads/{branch}:refs/remotes/origin/{branch}",
                }
                : ["fetch", "--prune", "--no-tags", "origin", "+HEAD:refs/remotes/origin/HEAD"];
            return new LocalGitUpdateTarget("origin/HEAD", head, fetchArgs);
        }
    }

    private Task<string> RunGitRemoteCommandAsync(
        string installedPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => RunCommandCaptureAsync(
            "git",
            arguments,
            workingDirectory: installedPath,
            timeoutMs: NetworkTimeoutMs,
            env: new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0" },
            cancellationToken: cancellationToken);

    private async Task UpdateGitAsync(
        ParsedPackageSource source,
        string scope,
        CancellationToken cancellationToken)
    {
        var targetDir = GetGitInstallPath(source, scope);
        if (!Directory.Exists(targetDir))
        {
            await InstallGitAsync(source, scope, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrEmpty(source.Ref))
        {
            await EnsureGitRefAsync(
                targetDir,
                ["fetch", "origin", source.Ref],
                "FETCH_HEAD",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var target = await GetLocalGitUpdateTargetAsync(targetDir, cancellationToken).ConfigureAwait(false);
        await EnsureGitRefAsync(targetDir, target.FetchArgs, target.Ref, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureGitRefAsync(
        string targetDir,
        IReadOnlyList<string> fetchArgs,
        string reference,
        CancellationToken cancellationToken)
    {
        await RunCommandAsync("git", fetchArgs, targetDir, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var localHead = await RunCommandCaptureAsync(
            "git",
            ["rev-parse", "HEAD"],
            workingDirectory: targetDir,
            timeoutMs: NetworkTimeoutMs,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var commitRef = $"{reference}^{{commit}}";
        var targetHead = await RunCommandCaptureAsync(
            "git",
            ["rev-parse", commitRef],
            workingDirectory: targetDir,
            timeoutMs: NetworkTimeoutMs,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var markerPath = GetGitUpdateMarkerPath(targetDir);

        if (string.Equals(localHead.Trim(), targetHead.Trim(), StringComparison.Ordinal))
        {
            if (File.Exists(markerPath))
            {
                await CleanAndInstallGitDependenciesAsync(targetDir, markerPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await RepairMissingGitDependenciesAsync(targetDir, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        File.WriteAllText(markerPath, "");
        await RunCommandAsync("git", ["reset", "--hard", commitRef], targetDir, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await CleanAndInstallGitDependenciesAsync(targetDir, markerPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task CleanAndInstallGitDependenciesAsync(
        string targetDir,
        string markerPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunCommandAsync("git", ["clean", "-fdx"], targetDir, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await RepairMissingGitDependenciesAsync(targetDir, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }

            throw;
        }

        if (File.Exists(NodePath.Join(targetDir, "package.json")))
        {
            await RunNpmCommandAsync(
                GetGitDependencyInstallArgs(),
                targetDir,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        File.Delete(markerPath);
    }

    private async Task RepairMissingGitDependenciesAsync(string targetDir, CancellationToken cancellationToken)
    {
        if (!HasMissingGitDependencies(targetDir))
        {
            return;
        }

        await RunNpmCommandAsync(
            GetGitDependencyInstallArgs(),
            targetDir,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static bool HasMissingGitDependencies(string targetDir)
    {
        var packageJsonPath = NodePath.Join(targetDir, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            return false;
        }

        try
        {
            var manifest = JsonNode.Parse(Text.StripBom(File.ReadAllText(packageJsonPath)));
            if (manifest is not JsonObject package
                || package["dependencies"] is not JsonObject dependencies)
            {
                return false;
            }

            var nodeModulesDir = NodePath.Resolve(targetDir, "node_modules");
            foreach (var name in dependencies.Select(dependency => dependency.Key))
            {
                var dependencyPath = NodePath.Resolve(nodeModulesDir, name);
                if (!IsWithinDirectory(dependencyPath, nodeModulesDir))
                {
                    continue;
                }

                if (!File.Exists(dependencyPath) && !Directory.Exists(dependencyPath))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsWithinDirectory(string path, string root)
    {
        var normalizedRoot = NodePath.Resolve(root);
        var normalizedPath = NodePath.Resolve(path);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = normalizedRoot.EndsWith(NodePath.Separator)
            ? normalizedRoot
            : normalizedRoot + NodePath.Separator;
        return normalizedPath.StartsWith(prefix, StringComparison.Ordinal);
    }

    private async Task RefreshTemporaryGitSourceAsync(
        ParsedPackageSource source,
        string sourceText,
        CancellationToken cancellationToken)
    {
        if (IsOfflineModeEnabled())
        {
            return;
        }

        try
        {
            await WithProgressAsync(
                "pull",
                sourceText,
                $"Refreshing {sourceText}...",
                () => UpdateGitAsync(source, SourceScope.Temporary, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private async Task RemoveGitAsync(ParsedPackageSource source, string scope)
    {
        var targetDir = GetGitInstallPath(source, scope);
        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, recursive: true);
        }

        File.Delete(GetGitUpdateMarkerPath(targetDir));
        PruneEmptyGitParents(targetDir, GetGitInstallRoot(scope));
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static string GetGitUpdateMarkerPath(string targetDir)
        => NodePath.Join(
            NodePath.Dirname(targetDir),
            $".{NodePath.Basename(targetDir)}.pi-update-incomplete");

    private static void PruneEmptyGitParents(string targetDir, string? installRoot)
    {
        if (installRoot is null)
        {
            return;
        }

        var root = NodePath.Resolve(installRoot);
        var current = NodePath.Dirname(NodePath.Resolve(targetDir));
        while (!string.Equals(current, root, StringComparison.Ordinal)
            && current.StartsWith(root + NodePath.Separator, StringComparison.Ordinal)
            && Directory.Exists(current))
        {
            if (Directory.EnumerateFileSystemEntries(current).Any())
            {
                return;
            }

            Directory.Delete(current);
            current = NodePath.Dirname(current);
        }
    }

    private async Task<T> WithProgressAsync<T>(
        string action,
        string source,
        string message,
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        _progress?.Invoke(new PackageProgressEvent("start", action, source, message));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await operation().ConfigureAwait(false);
            _progress?.Invoke(new PackageProgressEvent("complete", action, source));
            return result;
        }
        catch (Exception exception)
        {
            _progress?.Invoke(new PackageProgressEvent("error", action, source, exception.Message));
            throw;
        }
    }

    private async Task WithProgressAsync(
        string action,
        string source,
        string message,
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        await WithProgressAsync(
            action,
            source,
            message,
            async () =>
            {
                await operation().ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T[]> RunWithConcurrencyAsync<T>(
        IReadOnlyList<Func<Task<T>>> tasks,
        int limit,
        CancellationToken cancellationToken)
    {
        if (tasks.Count == 0)
        {
            return [];
        }

        var results = new T[tasks.Count];
        var nextIndex = -1;
        var workerCount = Math.Max(1, Math.Min(limit, tasks.Count));
        var workers = Enumerable.Range(0, workerCount).Select(async _ =>
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= tasks.Count)
                {
                    return;
                }

                results[index] = await tasks[index]().ConfigureAwait(false);
            }
        });

        await Task.WhenAll(workers).ConfigureAwait(false);
        return results;
    }

    private async Task RunCommandAsync(
        string command,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        int? timeoutMs = null,
        IReadOnlyDictionary<string, string?>? env = null,
        CancellationToken cancellationToken = default)
    {
        _ = await RunCommandCaptureAsync(
            command,
            arguments,
            workingDirectory,
            timeoutMs,
            env,
            cancellationToken).ConfigureAwait(false);
    }

    private Task<string> RunCommandCaptureAsync(
        string command,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        int? timeoutMs = null,
        IReadOnlyDictionary<string, string?>? env = null,
        CancellationToken cancellationToken = default)
        => _runner.RunAsync(
            command,
            arguments,
            new PackageProcessOptions
            {
                WorkingDirectory = workingDirectory ?? _cwd,
                TimeoutMs = timeoutMs,
                Env = env,
            },
            cancellationToken);

    private Task RunNpmCommandAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        int? timeoutMs = null,
        IReadOnlyDictionary<string, string?>? env = null,
        CancellationToken cancellationToken = default)
    {
        var npm = GetNpmInvocation();
        return RunCommandAsync(
            npm.Command,
            [.. npm.Args, .. arguments],
            workingDirectory,
            timeoutMs,
            env,
            cancellationToken);
    }

    private string RunNpmCommandSync(IReadOnlyList<string> arguments)
    {
        var npm = GetNpmInvocation();
        return RunCommandCaptureAsync(
                npm.Command,
                [.. npm.Args, .. arguments],
                workingDirectory: _cwd,
                timeoutMs: NetworkTimeoutMs)
            .GetAwaiter()
            .GetResult();
    }

    private string GetPackageManagerName()
    {
        static string NormalizeCommandName(string command)
        {
            var name = NodePath.Basename(command);
            var extension = Path.GetExtension(name);
            return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    ? name[..^extension.Length]
                    : name;
        }

        var npm = GetNpmInvocation();
        var supported = new HashSet<string>(["npm", "pnpm", "bun"], StringComparer.Ordinal);
        var directCommand = NormalizeCommandName(npm.Command);
        var separatorIndex = Array.LastIndexOf(npm.Args, "--");
        if (separatorIndex >= 0)
        {
            var wrappedCommand = separatorIndex + 1 < npm.Args.Length
                ? npm.Args[separatorIndex + 1]
                : null;
            return wrappedCommand is null ? directCommand : NormalizeCommandName(wrappedCommand);
        }

        if (supported.Contains(directCommand))
        {
            return directCommand;
        }

        var wrappedManagers = npm.Args
            .Select(NormalizeCommandName)
            .Where(supported.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (wrappedManagers.Length > 1)
        {
            throw new InvalidOperationException(
                $"Ambiguous npmCommand package managers: {string.Join(", ", wrappedManagers)}");
        }

        return wrappedManagers.FirstOrDefault() ?? directCommand;
    }

    private string[] GetNpmInstallArgs(IReadOnlyList<string> specs, string installRoot)
    {
        var packageManager = GetPackageManagerName();
        if (packageManager == "bun")
        {
            return ["install", .. specs, "--cwd", installRoot, "--omit=peer"];
        }

        if (packageManager == "pnpm")
        {
            return
            [
                "install",
                .. specs,
                "--prefix",
                installRoot,
                "--config.auto-install-peers=false",
                "--config.strict-peer-dependencies=false",
                "--config.strict-dep-builds=false",
            ];
        }

        return ["install", .. specs, "--prefix", installRoot, "--legacy-peer-deps"];
    }

    private string[] GetNpmUninstallArgs(string packageName, string installRoot)
    {
        var packageManager = GetPackageManagerName();
        if (packageManager == "bun")
        {
            return ["uninstall", packageName, "--cwd", installRoot];
        }

        return packageManager == "pnpm"
            ? ["uninstall", packageName, "--prefix", installRoot]
            : ["uninstall", packageName, "--prefix", installRoot, "--legacy-peer-deps"];
    }

    private string[] GetGitDependencyInstallArgs()
        => GetPackageManagerName() switch
        {
            "bun" => ["install", "--omit=dev", "--omit=peer"],
            "pnpm" =>
            [
                "install",
                "--prod",
                "--config.auto-install-peers=false",
                "--config.strict-peer-dependencies=false",
                "--config.strict-dep-builds=false",
            ],
            "npm" => ["install", "--omit=dev", "--legacy-peer-deps"],
            _ => ["install"],
        };

    private string GetGlobalNpmRoot()
    {
        var npm = GetNpmInvocation();
        var commandKey = string.Join("\0", [npm.Command, .. npm.Args]);
        if (_globalNpmRoot is not null && _globalNpmRootCommandKey == commandKey)
        {
            return _globalNpmRoot;
        }

        if (GetPackageManagerName() == "bun")
        {
            var binDirectory = RunNpmCommandSync(["pm", "bin", "-g"]).Trim();
            _globalNpmRoot = NodePath.Join(
                NodePath.Dirname(binDirectory),
                "install",
                "global",
                "node_modules");
        }
        else
        {
            _globalNpmRoot = RunNpmCommandSync(["root", "-g"]).Trim();
        }

        _globalNpmRootCommandKey = commandKey;
        return _globalNpmRoot;
    }

    private string? GetPnpmGlobalPackagePath(string packageName)
    {
        if (GetPackageManagerName() != "pnpm")
        {
            return null;
        }

        var output = RunNpmCommandSync(["list", "-g", "--depth", "0", "--json"]);
        if (JsonNode.Parse(output) is not JsonArray entries)
        {
            return null;
        }

        foreach (var entry in entries)
        {
            if (entry?["dependencies"]?[packageName]?["path"] is JsonValue pathValue
                && pathValue.TryGetValue<string>(out var path))
            {
                return path;
            }
        }

        return null;
    }

    private string? GetLegacyGlobalNpmInstallPath(ParsedPackageSource source)
    {
        try
        {
            return GetPnpmGlobalPackagePath(source.Name ?? "")
                ?? NodePath.Join(GetGlobalNpmRoot(), source.Name ?? "");
        }
        catch
        {
            return null;
        }
    }

    private string BuildNoMatchingPackageMessage(string source, IReadOnlyList<JsonNode?> configuredPackages)
    {
        var suggestion = FindSuggestedConfiguredSource(source, configuredPackages);
        return suggestion is null
            ? $"No matching package found for {source}"
            : $"No matching package found for {source}. Did you mean {suggestion}?";
    }

    private string? FindSuggestedConfiguredSource(string source, IReadOnlyList<JsonNode?> configuredPackages)
    {
        var trimmedSource = source.Trim();
        foreach (var package in configuredPackages)
        {
            var configuredSource = GetPackageSourceString(package);
            if (configuredSource is null)
            {
                continue;
            }

            var parsed = ParseSource(configuredSource);
            if (parsed.Type == "npm")
            {
                if (trimmedSource == parsed.Name || trimmedSource == parsed.Spec)
                {
                    return configuredSource;
                }

                continue;
            }

            if (parsed.Type == "git")
            {
                var shorthand = $"{parsed.Host}/{parsed.Path}";
                var shorthandWithRef = parsed.Ref is null ? null : $"{shorthand}@{parsed.Ref}";
                if (trimmedSource == shorthand || trimmedSource == shorthandWithRef)
                {
                    return configuredSource;
                }
            }
        }

        return null;
    }

    private static int CompareVersions(string left, string right)
        => Semver.Compare(left, right)
            ?? throw new InvalidDataException($"Invalid semantic version comparison: {left} and {right}");

    private sealed record NpmUpdateTarget(string Source, string Scope, ParsedPackageSource Parsed);

    private sealed record GitUpdateSource(string Source, string Scope, ParsedPackageSource Parsed);

    private sealed record LocalGitUpdateTarget(string Ref, string Head, IReadOnlyList<string> FetchArgs);
}
