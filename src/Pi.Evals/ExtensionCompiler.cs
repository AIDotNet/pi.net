using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.Evals;

public static partial class ExtensionCompiler
{
    private const int MaxSourceFiles = 32;
    private const long MaxSourceBytes = 512 * 1024;

    public static ToolDefinition CreateTool(string agentDir)
        => new()
        {
            Name = "compile_dotnet_extension",
            Label = "Build .NET extension",
            Description =
                "Compile C# extension source files from ~/.pi/agent/extensions/<name> into a Pi extension assembly. "
                + "Call this after writing a public IExtensionEntry implementation and before reloading the session.",
            PromptSnippet = "Compile a generated .NET Pi extension from its agent extensions directory",
            Parameters = new ToolSchema(new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["name"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["pattern"] = "^[a-z0-9][a-z0-9_-]{0,63}$",
                    },
                },
                ["required"] = new[] { "name" },
                ["additionalProperties"] = false,
            }),
            DefaultActive = true,
            Execute = async (_, arguments, cancellationToken, _, _) =>
            {
                try
                {
                    var name = GetString(arguments, "name");
                    var assemblyPath = await CompileAsync(agentDir, name, cancellationToken).ConfigureAwait(false);
                    return new AgentToolResult(
                        [new TextContent($"Built extension assembly: {assemblyPath}")],
                        Details: new { name, assemblyPath });
                }
                catch (Exception error)
                {
                    return AgentToolResult.FromError(error.Message);
                }
            },
        };

    public static async Task<string> CompileAsync(
        string agentDir,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!NamePattern().IsMatch(name))
        {
            throw new ArgumentException("Extension name must be a lowercase identifier.", nameof(name));
        }

        var agentRoot = Path.GetFullPath(agentDir);
        var extensionsDir = Path.GetFullPath(Path.Combine(agentRoot, "extensions"));
        var sourceRoot = Path.GetFullPath(Path.Combine(extensionsDir, name));
        if (!IsWithin(extensionsDir, sourceRoot))
        {
            throw new ArgumentException("Extension source directory escapes the agent extensions directory.", nameof(name));
        }

        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"Extension source directory does not exist: {sourceRoot}");
        }

        foreach (var directory in new[] { agentRoot, extensionsDir, sourceRoot })
        {
            if (Directory.Exists(directory)
                && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Extension source directories cannot be symbolic links: {directory}");
            }
        }

        var sourceFiles = CollectSourceFiles(sourceRoot);
        if (sourceFiles.Count == 0)
        {
            throw new InvalidOperationException($"Extension directory contains no C# source files: {sourceRoot}");
        }

        var scratch = Directory.CreateTempSubdirectory("pi-eval-extension-");
        try
        {
            var sourceDirectory = Path.Combine(scratch.FullName, "source");
            Directory.CreateDirectory(sourceDirectory);
            long bytesCopied = 0;
            foreach (var (relativePath, fullPath) in sourceFiles)
            {
                var contents = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
                bytesCopied += contents.Length;
                if (bytesCopied > MaxSourceBytes)
                {
                    throw new InvalidOperationException($"Extension source exceeds {MaxSourceBytes} bytes.");
                }

                var destination = Path.GetFullPath(Path.Combine(sourceDirectory, relativePath));
                if (!IsWithin(sourceDirectory, destination))
                {
                    throw new InvalidOperationException($"Extension source path escapes its project: {relativePath}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllBytesAsync(destination, contents, cancellationToken).ConfigureAwait(false);
            }

            var outputDirectory = Path.Combine(scratch.FullName, "output");
            Directory.CreateDirectory(outputDirectory);
            var projectPath = Path.Combine(scratch.FullName, "EvalExtension.csproj");
            await File.WriteAllTextAsync(
                projectPath,
                CreateProjectFile(name),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            await BuildProjectAsync(projectPath, scratch.FullName, outputDirectory, cancellationToken)
                .ConfigureAwait(false);

            var assemblyPath = Path.Combine(outputDirectory, $"{name}.dll");
            if (!File.Exists(assemblyPath))
            {
                throw new InvalidOperationException($"Extension build did not produce {name}.dll.");
            }

            Directory.CreateDirectory(extensionsDir);
            var destinationPath = Path.Combine(extensionsDir, $"{name}.dll");
            if (File.Exists(destinationPath) && (File.GetAttributes(destinationPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Refusing to replace a symbolic link in the extension directory.");
            }

            File.Move(assemblyPath, destinationPath, overwrite: true);
            return destinationPath;
        }
        finally
        {
            Directory.Delete(scratch.FullName, recursive: true);
        }
    }

    private static List<(string RelativePath, string FullPath)> CollectSourceFiles(string sourceRoot)
    {
        var files = new List<(string RelativePath, string FullPath)>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(sourceRoot);
        long totalBytes = 0;
        while (pendingDirectories.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException($"Extension sources cannot contain symbolic links: {path}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(path);
                    continue;
                }

                if (!string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var file = new FileInfo(path);
                totalBytes += file.Length;
                if (files.Count == MaxSourceFiles || totalBytes > MaxSourceBytes)
                {
                    throw new InvalidOperationException(
                        $"Extension source is limited to {MaxSourceFiles} files and {MaxSourceBytes} bytes.");
                }

                files.Add((Path.GetRelativePath(sourceRoot, path), path));
            }
        }

        return files;
    }

    private static string CreateProjectFile(string assemblyName)
    {
        var references = Directory.EnumerateFiles(AppContext.BaseDirectory, "Pi.*.dll")
            .Where(path => !string.Equals(Path.GetFileName(path), "Pi.Evals.dll", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                return $"    <Reference Include=\"{Xml(name)}\"><HintPath>{Xml(Path.GetFullPath(path))}</HintPath><Private>false</Private></Reference>";
            });

        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>{{Xml(assemblyName)}}</AssemblyName>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <LangVersion>latest</LangVersion>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
              </PropertyGroup>
              <ItemGroup>
            {{string.Join(Environment.NewLine, references)}}
              </ItemGroup>
            </Project>
            """;
    }

    private static async Task BuildProjectAsync(
        string projectPath,
        string workingDirectory,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("quiet");
        startInfo.ArgumentList.Add("-p:RestoreIgnoreFailedSources=true");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start the .NET compiler.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
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

        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var diagnostic = string.Join(
                Environment.NewLine,
                new[] { output.Trim(), error.Trim() }.Where(text => text.Length > 0));
            throw new InvalidOperationException(
                diagnostic.Length == 0 ? ".NET extension compilation failed." : diagnostic);
        }
    }

    private static string GetString(IReadOnlyDictionary<string, object?> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value))
        {
            throw new ArgumentException($"Missing required argument: {name}");
        }

        return value switch
        {
            string text => text,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element
                => element.GetString()!,
            _ => throw new ArgumentException($"Argument {name} must be a string."),
        };
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static string Xml(string value)
        => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
