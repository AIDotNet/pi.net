using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pi.CodingAgent;
using Pi.CodingAgent.Core;

namespace Pi.Evals;

internal sealed record EvalDockerImage(string Name, string Id);

internal sealed record EvalDockerImages(EvalDockerImage WithoutDocs, EvalDockerImage WithDocs)
{
    public EvalDockerImage Get(DocumentationVariant variant)
        => variant switch
        {
            DocumentationVariant.WithoutDocs => WithoutDocs,
            DocumentationVariant.WithDocs => WithDocs,
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown documentation variant."),
        };
}

internal sealed record EvalDockerContext(
    string RepositoryRoot,
    EvalDockerImages Images,
    string AuthPath,
    string Provider,
    string Model,
    long RunsPerVariant);

internal static class EvalDockerRunner
{
    public static async Task<EvalDockerImages> BuildImagesAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var dockerfile = Path.Combine(repositoryRoot, "src", "Pi.Evals", "Docker", "Dockerfile");
        if (!File.Exists(dockerfile))
        {
            throw new FileNotFoundException("Eval Dockerfile was not found.", dockerfile);
        }

        var prefix = $"pi-evals-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(repositoryRoot)))[..12]}";
        var withoutDocs = await BuildImageAsync(
            repositoryRoot,
            dockerfile,
            prefix,
            "without_docs",
            cancellationToken).ConfigureAwait(false);
        var withDocs = await BuildImageAsync(
            repositoryRoot,
            dockerfile,
            prefix,
            "with_docs",
            cancellationToken).ConfigureAwait(false);
        if (string.Equals(withoutDocs.Id, withDocs.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Documentation variants resolved to the same Docker image.");
        }

        return new EvalDockerImages(withoutDocs, withDocs);
    }

    public static async Task<int> RunContainerAsync(
        EvalDockerContext context,
        DocumentationVariant variant,
        string outputDirectory,
        IReadOnlyList<string> containerArguments,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        RejectPathSeparatorsInMount(outputDirectory);
        RejectPathSeparatorsInMount(context.AuthPath);

        var args = new List<string>
        {
            "run",
            "--rm",
            "--read-only",
            "--tmpfs",
            "/tmp:rw,exec,mode=1777",
            "--mount",
            Mount(outputDirectory, "/artifacts"),
            "--env",
            "PI_EVAL_ARTIFACT_DIR=/artifacts",
            "--env",
            $"PI_EVAL_RUNS_PER_VARIANT={context.RunsPerVariant}",
            "--env",
            "PI_EVAL_SANDBOX_UID=65532",
            "--env",
            "PI_EVAL_SANDBOX_GID=65532",
            "--env",
            $"PI_PROVIDER={context.Provider}",
            "--env",
            $"PI_MODEL={context.Model}",
        };

        if (TryGetCurrentUnixIdentity(out var uid, out var gid))
        {
            args.Add("--env");
            args.Add($"PI_EVAL_ARTIFACT_UID={uid}");
            args.Add("--env");
            args.Add($"PI_EVAL_ARTIFACT_GID={gid}");
        }

        args.Add("--mount");
        args.Add($"{Mount(context.AuthPath, "/run/pi-eval-secrets/auth.json")},readonly");
        args.Add(context.Images.Get(variant).Name);
        args.AddRange(containerArguments);

        var result = await RunProcessAsync(
            "docker",
            args,
            context.RepositoryRoot,
            captureOutput: false,
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode;
    }

    public static string RequireEvalAuthFile(string provider)
    {
        var agentDirectory = Config.GetAgentDir();
        var path = Path.GetFullPath(Path.Combine(agentDirectory, "auth.json"));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Eval authentication file does not exist: {path}", path);
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException($"Eval authentication path must be a regular file: {path}");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(provider, out var credential)
            || credential.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidDataException($"Eval authentication file has no credential for provider {provider}.");
        }

        return path;
    }

    public static string FindRepositoryRoot(string? startDirectory = null)
    {
        var starts = new[]
        {
            startDirectory,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        };

        foreach (var start in starts.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var current = new DirectoryInfo(Path.GetFullPath(start!));
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Pi.slnx"))
                    && Directory.Exists(Path.Combine(current.FullName, "src", "Pi.Evals")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Pi.NET repository. Run the eval command from the repository or its build output.");
    }

    private static async Task<EvalDockerImage> BuildImageAsync(
        string repositoryRoot,
        string dockerfile,
        string prefix,
        string target,
        CancellationToken cancellationToken)
    {
        var imageName = $"{prefix}-{target.Replace('_', '-')}:local";
        var buildResult = await RunProcessAsync(
            "docker",
            [
                "build",
                "--target",
                target,
                "--tag",
                imageName,
                "--file",
                dockerfile,
                repositoryRoot,
            ],
            repositoryRoot,
            captureOutput: false,
            cancellationToken).ConfigureAwait(false);
        if (buildResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Docker image build failed for target {target}."
                + (string.IsNullOrWhiteSpace(buildResult.StandardError)
                    ? string.Empty
                    : $"{Environment.NewLine}{buildResult.StandardError.Trim()}"));
        }

        var inspected = await RunProcessAsync(
            "docker",
            ["image", "inspect", "--format", "{{.Id}}", imageName],
            repositoryRoot,
            captureOutput: true,
            cancellationToken).ConfigureAwait(false);
        if (inspected.ExitCode != 0 || string.IsNullOrWhiteSpace(inspected.StandardOutput))
        {
            throw new InvalidOperationException($"Cannot inspect Docker image {imageName}.");
        }

        return new EvalDockerImage(imageName, inspected.StandardOutput.Trim());
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string command,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool captureOutput,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start {command}.");
            }
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new InvalidOperationException(
                $"Could not start {command}. Install Docker CLI and ensure its daemon is running.",
                error);
        }

        var standardOutput = captureOutput
            ? process.StandardOutput.ReadToEndAsync(cancellationToken)
            : Task.FromResult(string.Empty);
        var standardError = captureOutput
            ? process.StandardError.ReadToEndAsync(cancellationToken)
            : Task.FromResult(string.Empty);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    private static string Mount(string source, string target)
        => $"type=bind,source={Path.GetFullPath(source)},target={target}";

    private static void RejectPathSeparatorsInMount(string path)
    {
        if (path.Contains(',', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Docker mount paths cannot contain commas: {path}",
                nameof(path));
        }
    }

    private static bool TryGetCurrentUnixIdentity(out uint uid, out uint gid)
    {
        uid = 0;
        gid = 0;
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        uid = NativeMethods.getuid();
        gid = NativeMethods.getgid();
        return true;
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "getuid")]
        internal static extern uint getuid();

        [DllImport("libc", EntryPoint = "getgid")]
        internal static extern uint getgid();
    }
}

internal sealed record EvalContainerWorkerRequest
{
    public required string Operation { get; init; }

    public IReadOnlyList<string> Files { get; init; } = [];

    public string? TestNamePattern { get; init; }

    public EvalTask? Task { get; init; }
}

internal static class EvalContainerSupervisor
{
    private const string ArtifactDirectory = "/artifacts";
    private const string AuthSource = "/run/pi-eval-secrets/auth.json";
    private const string HostAgentDirectory = "/tmp/pi-eval-host-agent";

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        RequireRootLinuxProcess();
        var variant = EvalHarness.ResolveDocumentationVariant();
        VerifyImageLayout(variant);

        var request = ParseRequest(arguments, variant);
        var (sandboxUid, sandboxGid) = ReadPositivePair(
            "PI_EVAL_SANDBOX_UID",
            "PI_EVAL_SANDBOX_GID");
        var artifactOwner = ReadOptionalIdentityPair(
            "PI_EVAL_ARTIFACT_UID",
            "PI_EVAL_ARTIFACT_GID");
        var provider = RequiredEnvironment("PI_PROVIDER");
        var model = RequiredEnvironment("PI_MODEL");
        if (request.Task is { } task && task.Model != $"{provider}/{model}")
        {
            throw new InvalidOperationException("Container task model does not match PI_PROVIDER and PI_MODEL.");
        }

        var artifactDirectory = RequiredEnvironment("PI_EVAL_ARTIFACT_DIR");
        if (!Path.GetFullPath(artifactDirectory).Equals(ArtifactDirectory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Eval artifacts must be mounted at {ArtifactDirectory}.");
        }

        if (!Directory.Exists(ArtifactDirectory)
            || (File.GetAttributes(ArtifactDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Eval artifact mount is missing or is a symbolic link.");
        }

        var artifactsChowned = false;
        try
        {
            ValidateCredentialSource(provider);
            PrepareAgentDirectory(sandboxUid, sandboxGid);
            artifactsChowned = true;
            ChownTree(ArtifactDirectory, sandboxUid, sandboxGid);
            return await StartWorkerAsync(
                request,
                artifactDirectory,
                sandboxUid,
                sandboxGid,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                DeleteTreeWithoutFollowingLinks(HostAgentDirectory);
            }
            finally
            {
                if (artifactsChowned && artifactOwner is { } owner)
                {
                    ChownTree(ArtifactDirectory, owner.Uid, owner.Gid);
                }
            }
        }
    }

    public static async Task<int> RunWorkerAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        RequireRootLinuxProcess();
        if (arguments.Count != 2 || arguments[0] != "--request-base64")
        {
            throw new ArgumentException("Container worker requires --request-base64 <request>.");
        }

        if (Environment.GetEnvironmentVariable("PI_EVAL_CONTAINER") != "1")
        {
            throw new InvalidOperationException("Container worker can only run inside an eval image.");
        }

        var requestBytes = Convert.FromBase64String(arguments[1]);
        var request = JsonSerializer.Deserialize<EvalContainerWorkerRequest>(
            requestBytes,
            EvalJson.Options)
            ?? throw new InvalidDataException("Container worker request is empty.");
        var (sandboxUid, sandboxGid) = ReadPositivePair(
            "PI_EVAL_SANDBOX_UID",
            "PI_EVAL_SANDBOX_GID");
        var artifactDirectory = RequiredEnvironment("PI_EVAL_ARTIFACT_DIR");
        if (!Path.GetFullPath(artifactDirectory).Equals(ArtifactDirectory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Eval artifacts must be mounted at {ArtifactDirectory}.");
        }

        if (request.Operation == "discover")
        {
            EvalHarness.EnterToolSandbox(
                ArtifactDirectory,
                sandboxUid,
                sandboxGid,
                readOnlyImage: true);
            var cases = EvalScenarioCatalog.GetDocumentationCases(request.Files, request.TestNamePattern);
            await WriteArtifactAsync(
                "discovered-tests.json",
                cases.Select(evalCase => new { name = evalCase.FullName, file = evalCase.File }).ToArray(),
                cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (request.Operation != "task" || request.Task is null)
        {
            throw new InvalidDataException("Container worker request has an unsupported operation.");
        }

        var task = request.Task;
        var variant = EvalHarness.ResolveDocumentationVariant();
        if (task.Variant != variant)
        {
            throw new InvalidOperationException("Container task variant does not match the eval image.");
        }

        var expectedModel = $"{RequiredEnvironment("PI_PROVIDER")}/{RequiredEnvironment("PI_MODEL")}";
        if (!string.Equals(task.Model, expectedModel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Container task model does not match PI_PROVIDER and PI_MODEL.");
        }

        var exactCase = EvalScenarioCatalog.GetDocumentationCases([task.File])
            .SingleOrDefault(evalCase =>
                evalCase.FullName == task.FullName
                && evalCase.EvalSet == task.EvalSet
                && evalCase.CaseId == task.CaseId);
        if (exactCase is null)
        {
            throw new InvalidDataException("Container task is not in the documentation eval catalog.");
        }

        var report = await EvalScenarioCatalog.RunDocumentationTaskAsync(task, cancellationToken)
            .ConfigureAwait(false);
        await WriteArtifactAsync("vitest.json", report, cancellationToken).ConfigureAwait(false);
        var passed = report.Assertions.Count == 1 && report.Assertions[0].Status == "passed";
        Console.WriteLine($"{(passed ? "passed" : "failed")}: {task.EvalSet} {task.CaseId}");
        return passed ? 0 : 1;
    }

    private static EvalContainerWorkerRequest ParseRequest(
        IReadOnlyList<string> arguments,
        DocumentationVariant variant)
    {
        var discover = false;
        string? project = null;
        string? pattern = null;
        var files = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--discover")
            {
                if (discover) throw new ArgumentException("--discover may only be specified once.");
                discover = true;
                continue;
            }

            if (argument == "--project")
            {
                project = RequiredValue(arguments, ref index, argument);
                if (project != "docs")
                {
                    throw new ArgumentException("Container eval project must be docs.");
                }

                continue;
            }

            if (argument is "-t" or "--testNamePattern")
            {
                pattern = RequiredValue(arguments, ref index, argument);
                continue;
            }

            if (argument.StartsWith("--testNamePattern=", StringComparison.Ordinal))
            {
                pattern = argument["--testNamePattern=".Length..];
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    throw new ArgumentException("Missing value for --testNamePattern.");
                }

                continue;
            }

            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unsupported container eval argument: {argument}");
            }

            files.Add(EvalScenarioCatalog.NormalizeFile(argument));
        }

        if (project != "docs")
        {
            throw new ArgumentException("Container task requires --project docs.");
        }

        if (files.Count == 0)
        {
            throw new ArgumentException("Container runner requires an explicit documentation eval file.");
        }

        if (discover)
        {
            return new EvalContainerWorkerRequest
            {
                Operation = "discover",
                Files = files,
                TestNamePattern = pattern,
            };
        }

        if (files.Count != 1)
        {
            throw new ArgumentException("A container task must select exactly one documentation eval file.");
        }

        var selectedCases = EvalScenarioCatalog.GetDocumentationCases(files, pattern);
        if (selectedCases.Count != 1)
        {
            throw new InvalidOperationException(
                $"A container task must match exactly one eval case; found {selectedCases.Count}.");
        }

        var selected = selectedCases[0];
        var provider = RequiredEnvironment("PI_PROVIDER");
        var model = RequiredEnvironment("PI_MODEL");
        return new EvalContainerWorkerRequest
        {
            Operation = "task",
            Files = files,
            TestNamePattern = pattern,
            Task = new EvalTask
            {
                File = selected.File,
                FullName = selected.FullName,
                EvalSet = selected.EvalSet,
                CaseId = selected.CaseId,
                Variant = variant,
                Model = $"{provider}/{model}",
                RunNumber = 1,
            },
        };
    }

    private static async Task<int> StartWorkerAsync(
        EvalContainerWorkerRequest request,
        string artifactDirectory,
        uint sandboxUid,
        uint sandboxGid,
        CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(assembly) || !File.Exists(assembly))
        {
            throw new InvalidOperationException("Cannot locate the eval worker assembly.");
        }

        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request, EvalJson.Options));
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(assembly);
        startInfo.ArgumentList.Add("worker");
        startInfo.ArgumentList.Add("--request-base64");
        startInfo.ArgumentList.Add(payload);
        startInfo.Environment["PI_EVAL_CONTAINER"] = "1";
        startInfo.Environment["PI_CODING_AGENT_DIR"] = HostAgentDirectory;
        startInfo.Environment["PI_EVAL_ARTIFACT_DIR"] = artifactDirectory;
        startInfo.Environment["PI_EVAL_SANDBOX_UID"] = sandboxUid.ToString();
        startInfo.Environment["PI_EVAL_SANDBOX_GID"] = sandboxGid.ToString();
        startInfo.Environment["HOME"] = "/tmp/pi-eval-bootstrap";
        startInfo.Environment["USERPROFILE"] = "/tmp/pi-eval-bootstrap";

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start the eval worker process.");
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        return process.ExitCode;
    }

    private static void VerifyImageLayout(DocumentationVariant variant)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("Eval images must run on Linux.");
        }

        var baseDirectory = AppContext.BaseDirectory;
        var docsDirectory = Path.Combine(baseDirectory, "docs");
        var examplesDirectory = Path.Combine(baseDirectory, "examples");
        var readme = Path.Combine(baseDirectory, "README.md");
        var changelog = Path.Combine(baseDirectory, "CHANGELOG.md");
        if (variant == DocumentationVariant.WithoutDocs)
        {
            if (Directory.Exists(docsDirectory)
                || Directory.Exists(examplesDirectory)
                || File.Exists(readme)
                || File.Exists(changelog))
            {
                throw new InvalidOperationException("without_docs eval image contains documentation assets.");
            }
        }
        else if (variant == DocumentationVariant.WithDocs)
        {
            foreach (var path in new[]
                     {
                         readme,
                         changelog,
                         Path.Combine(docsDirectory, "models.md"),
                         Path.Combine(docsDirectory, "dotnet-extensions.md"),
                         Path.Combine(examplesDirectory, "README.md"),
                     })
            {
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    throw new InvalidOperationException($"with_docs eval image is missing documentation: {path}");
                }
            }
        }
        else
        {
            throw new InvalidOperationException("Eval image has an invalid documentation variant.");
        }

        // 文档与示例目录按设计携带上游的 .ts 示例，它们正是 with_docs 实验材料；
        // 评估器源码本身只允许存在于受保护的 Pi.Evals.dll 中。
        var sourceFile = Directory.EnumerateFiles(baseDirectory, "*", SearchOption.AllDirectories)
            .FirstOrDefault(path => !IsUnder(path, docsDirectory)
                && !IsUnder(path, examplesDirectory)
                && Path.GetExtension(path) is ".cs" or ".csproj" or ".ts");
        if (sourceFile is not null)
        {
            throw new InvalidOperationException($"Eval image contains source code outside the protected assembly: {sourceFile}");
        }

        var assembly = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(assembly) || !File.Exists(assembly))
        {
            throw new InvalidOperationException("Cannot locate the eval runner assembly.");
        }

        // TS assertRootOnly(evalRoot/src/docker/configs) 的对应物：.NET 镜像不携带源码，
        // 评估器定义编译进 Pi.Evals.dll，因此该程序集就是唯一需要 root-only 的评估器源。
        EvalFileProbe.AssertRootOnly(assembly);
    }

    private static bool IsUnder(string path, string directory)
        => path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static void ValidateCredentialSource(string provider)
    {
        if (!File.Exists(AuthSource))
        {
            throw new FileNotFoundException("Read-only eval authentication mount is missing.", AuthSource);
        }

        var attributes = File.GetAttributes(AuthSource);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException("Eval authentication mount must be a regular file.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(AuthSource));
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(provider, out var credential)
            || credential.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidDataException($"Eval authentication file has no credential for provider {provider}.");
        }
    }

    private static void PrepareAgentDirectory(uint uid, uint gid)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("The eval agent directory requires Linux file permissions.");
        }

        if (Directory.Exists(HostAgentDirectory) || File.Exists(HostAgentDirectory))
        {
            throw new IOException($"Eval agent directory already exists: {HostAgentDirectory}");
        }

        Directory.CreateDirectory(HostAgentDirectory);
        File.SetUnixFileMode(HostAgentDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Copy(AuthSource, Path.Combine(HostAgentDirectory, "auth.json"), overwrite: false);
        File.SetUnixFileMode(
            Path.Combine(HostAgentDirectory, "auth.json"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        ChownTree(HostAgentDirectory, uid, gid);
    }

    private static async Task WriteArtifactAsync<T>(
        string name,
        T value,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(ArtifactDirectory, name);
        if (File.Exists(path) || Directory.Exists(path))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path);
                else File.Delete(path);
            }
            else
            {
                throw new IOException($"Eval artifact already exists: {path}");
            }
        }

        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, value, EvalJson.Options, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("\n"), cancellationToken).ConfigureAwait(false);
    }

    private static void ChownTree(string path, uint uid, uint gid)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0
            && (attributes & FileAttributes.ReparsePoint) == 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                ChownTree(entry, uid, gid);
            }
        }

        if (NativeMethods.lchown(path, uid, gid) != 0)
        {
            throw new IOException(
                $"Cannot transfer eval artifact ownership: {path} (errno {Marshal.GetLastPInvokeError()}).");
        }
    }

    private static void DeleteTreeWithoutFollowingLinks(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path);
            else File.Delete(path);
            return;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                DeleteTreeWithoutFollowingLinks(entry);
            }

            Directory.Delete(path);
        }
        else
        {
            File.Delete(path);
        }
    }

    private static string RequiredValue(IReadOnlyList<string> arguments, ref int index, string option)
    {
        if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        return arguments[++index];
    }

    private static (uint Uid, uint Gid) ReadPositivePair(string uidName, string gidName)
    {
        var uidText = RequiredEnvironment(uidName);
        var gidText = RequiredEnvironment(gidName);
        if (!uint.TryParse(uidText, out var uid) || uid == 0
            || !uint.TryParse(gidText, out var gid) || gid == 0)
        {
            throw new InvalidOperationException($"{uidName} and {gidName} must be positive integers.");
        }

        return (uid, gid);
    }

    private static (uint Uid, uint Gid)? ReadOptionalIdentityPair(string uidName, string gidName)
    {
        var uidText = Environment.GetEnvironmentVariable(uidName);
        var gidText = Environment.GetEnvironmentVariable(gidName);
        if (uidText is null && gidText is null)
        {
            return null;
        }

        // 调用者可以是 root（0/0），与 TS 直接透传 getuid/getgid 一致。
        if (!uint.TryParse(uidText, out var uid) || !uint.TryParse(gidText, out var gid))
        {
            throw new InvalidOperationException(
                $"{uidName} and {gidName} must both be non-negative integers, or neither may be set.");
        }

        return (uid, gid);
    }

    private static string RequiredEnvironment(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required eval environment variable is missing: {name}");

    private static void RequireRootLinuxProcess()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("Documentation eval containers require Linux.");
        }

        if (NativeMethods.getuid() != 0 || NativeMethods.geteuid() != 0)
        {
            throw new InvalidOperationException("Eval container supervisor must start as root.");
        }
    }

    private static partial class NativeMethods
    {
        [DllImport("libc", EntryPoint = "getuid", SetLastError = true)]
        internal static extern uint getuid();

        [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
        internal static extern uint geteuid();

        [DllImport("libc", EntryPoint = "lchown", SetLastError = true)]
        internal static extern int lchown(
            [MarshalAs(UnmanagedType.LPUTF8Str)]
            string path,
            uint owner,
            uint group);
    }
}

/// <summary>
/// TS <c>docker/entrypoint.ts</c> 的运行时权限探针：<c>assertRootOnly</c> 与
/// <c>assertSandboxCannotRead</c>。.NET 镜像把评估器定义编译进 <c>Pi.Evals.dll</c>，
/// 不再携带 <c>evals/src/docker</c> 源码，因此探针对象是入口程序集。
/// </summary>
internal static class EvalFileProbe
{
    private const UnixFileMode SharedBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    [DllImport("libc", SetLastError = true, EntryPoint = "stat")]
    private static extern int Stat(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        byte[] buffer);

    /// <summary>对应 TS <c>assertRootOnly</c>：文件/目录必须属 root，且组与其他用户无任何权限。</summary>
    public static void AssertRootOnly(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("Evaluator source permission checks require Linux.");
        }

        var mode = File.GetUnixFileMode(path);
        if ((mode & UnixFileMode.UserRead) == 0)
        {
            throw new InvalidOperationException($"Evaluator source must be readable by root: {path}");
        }

        if ((mode & SharedBits) != 0)
        {
            throw new InvalidOperationException($"Evaluator source must be readable only by root: {path}");
        }

        if (UnixOwner(path) != 0)
        {
            throw new InvalidOperationException($"Evaluator source must be owned by root: {path}");
        }
    }

    /// <summary>
    /// 对应 TS <c>assertSandboxCannotRead</c>：降权到 sandbox UID 后尝试读取路径，成功即失败。
    /// 调用方必须已 <c>setuid</c>，否则该探针会误报。
    /// </summary>
    public static void AssertSandboxCannotRead(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("The eval sandbox permission probe requires Linux.");
        }

        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            {
                using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                _ = entries.MoveNext();
            }
            else
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        throw new InvalidOperationException($"The model-facing user can read evaluator source: {path}");
    }

    private static uint UnixOwner(string path)
    {
        var buffer = new byte[256];
        if (Stat(path, buffer) != 0)
        {
            throw new IOException($"Could not stat {path} (errno {Marshal.GetLastWin32Error()}).");
        }

        // glibc struct stat 的布局并非全架构一致：x86_64 为 mode@24/uid@28，
        // arm64 走 asm-generic，为 mode@16/uid@24。
        var uidOffset = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 24 : 28;
        return BitConverter.ToUInt32(buffer, uidOffset);
    }
}
