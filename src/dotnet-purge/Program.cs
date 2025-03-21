﻿using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using NuGet.Versioning;

var targetArgument = new Argument<string?>("TARGET")
{
    Description = "The path of the solution or project to purge. If not specified, the current directory will be used.",
    Arity = ArgumentArity.ZeroOrOne
};

var recurseOption = new Option<bool>("--recurse", "-r")
{
    Description = "Find projects in sub-directories and purge those too.",
    Arity = ArgumentArity.ZeroOrOne
};

var noCleanOption = new Option<bool>("--no-clean", "-n")
{
    Description = "Don't run `dotnet clean` before deleting the output directories.",
    Arity = ArgumentArity.ZeroOrOne
};

var vsOption = new Option<bool>("--vs")
{
    Description = "Delete temporary files & directories created by Visual Studio, e.g. .vs, *.csproj.user.",
    Arity = ArgumentArity.ZeroOrOne
};

var rootCommand = new RootCommand("Purges the specified solution or project.")
{
    targetArgument,
    recurseOption,
    noCleanOption,
    vsOption
};
rootCommand.SetAction(PurgeCommand);

var versionOption = rootCommand.Options.FirstOrDefault(o => o.Name == "--version");
if (versionOption is not null)
{
    versionOption.Action = new VersionOptionAction();
}

var result = rootCommand.Parse(args);
var exitCode = await result.InvokeAsync();

return exitCode;

async Task<int> PurgeCommand(ParseResult parseResult, CancellationToken cancellationToken)
{
    var detectNewerVersionTask = Task.Run(() => DetectNewerVersion(cancellationToken), cancellationToken);

    var targetValue = parseResult.GetValue(targetArgument);
    var recurseValue = parseResult.GetValue(recurseOption);
    var noCleanValue = parseResult.GetValue(noCleanOption);
    var vsValue = parseResult.GetValue(vsOption);

    var targetPath = targetValue ?? Directory.GetCurrentDirectory();
    if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
    {
        parseResult.Configuration.Error.WriteLine($"'{targetPath}' does not exist.");
        return 1;
    }
    targetPath = Path.GetFullPath(targetPath);

    var projectFiles = await GetProjectFiles(targetPath, recurseValue, cancellationToken);
    var projectCount = projectFiles.Count;

    WriteLine($"Found {projectCount} {ProjectOrProjects(projectCount)} to purge");
    WriteLine();

    if (projectCount == 0 && !recurseValue)
    {
        WriteLine("Use --recurse to search for projects in sub-directories.", ConsoleColor.DarkBlue);
    }

    var succeded = 0;
    var failed = 0;
    var cancelled = 0;
    foreach (var projectFile in projectFiles)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            var remaining = projectCount - succeded - failed - cancelled;
            cancelled += remaining;
            break;
        }

        try
        {
            await PurgeProject(projectFile, targetPath, noCleanValue, vsValue, cancellationToken);
            succeded++;
        }
        catch (OperationCanceledException)
        {
            cancelled++;
            break;
        }
        catch (Exception ex)
        {
            WriteError(
                $$"""
                Failed to purge project at path: {{projectFile}}
                {{ex.Message}}
                """);
            failed++;
            continue;
        }

        var relativePath = GetRelativePath(targetPath, projectFile);
        WriteLine($"({succeded}/{projectCount}) Purged {relativePath}");
    }

    if (vsValue)
    {
        DeleteVsDir(targetPath, cancellationToken);
    }

    var operationCancelled = cancelled > 0 || cancellationToken.IsCancellationRequested;

    if (succeded > 0)
    {
        WriteLine();
        WriteLine($"Finished purging {succeded} {ProjectOrProjects(succeded)}", ConsoleColor.Green);
    }

    if (cancelled > 0)
    {
        WriteLine();
        WriteLine($"Cancelled purging {cancelled} {ProjectOrProjects(cancelled)}", ConsoleColor.Yellow);
    }

    if (failed > 0)
    {
        WriteLine();
        WriteLine($"Failed purging {failed} {ProjectOrProjects(failed)}", ConsoleColor.Red);
    }

    // Process the detect newer version task
    try
    {
        var newerVersion = await detectNewerVersionTask;
        if (newerVersion is not null)
        {
            // TODO: Handle case when newer version is a pre-release version
            WriteLine();
            WriteLine($"A newer version ({newerVersion}) of dotnet-purge is available!", ConsoleColor.Yellow);
            WriteLine("Update by running 'dotnet tool update -g dotnet-purge'", ConsoleColor.Green);
        }
    }
    catch (Exception)
    {
        // Ignore exceptions from the detect newer version task
    }

    if (operationCancelled)
    {
        WriteLine();
        WriteLine("Operation cancelled", ConsoleColor.Yellow);
    }

    return failed > 0 || operationCancelled ? 1 : 0;
}

static string ProjectOrProjects(int count) => count == 1 ? "project" : "projects";

async Task<HashSet<string>> GetProjectFiles(string path, bool recurse, CancellationToken cancellationToken)
{
    var result = new HashSet<string>();

    if (File.Exists(path))
    {
        if (recurse)
        {
            WriteLine("The --recurse option is ignored when specifying a single project or solution file.", ConsoleColor.DarkBlue);
        }

        var extension = Path.GetExtension(path);

        if (extension == ".sln" || extension == ".slnx")
        {
            var projectFiles = await GetSlnProjectFiles(path, cancellationToken);
            foreach (var projectFile in projectFiles)
            {
                result.Add(projectFile);
            }
        }
        else if (extension == ".csproj" || extension == ".vbproj" || extension == ".fsproj" || extension == ".esproj" || extension == ".proj")
        {
            result.Add(path);
        }
    }
    else
    {
        // Find all sub-directories that contain solution or project files
        string[] projectFileMask = ["*.sln", "*.slnx", "*.csproj", "*.vbproj", "*.fsproj", "*.esproj", "*.proj"];

        foreach (var fileMask in projectFileMask)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var searchOption = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = new DirectoryInfo(path).EnumerateFiles(fileMask, searchOption);
            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (file.Extension == ".sln" || file.Extension == ".slnx")
                {
                    var projectFiles = await GetSlnProjectFiles(file.FullName, cancellationToken);
                    foreach (var projectFile in projectFiles)
                    {
                        result.Add(projectFile);
                    }
                }
                else
                {
                    result.Add(file.FullName);
                }
            }
        }
    }

    return result;
}

static async Task<List<string>> GetSlnProjectFiles(string slnFilePath, CancellationToken cancellationToken)
{
    var serializer = SolutionSerializers.Serializers.FirstOrDefault(s => s.IsSupported(slnFilePath))
        ?? throw new InvalidOperationException($"A solution file parser for file extension '{Path.GetExtension(slnFilePath)}' could not be not found.");
    var slnDir = Path.GetDirectoryName(slnFilePath) ?? throw new InvalidOperationException($"Solution directory could not be determined for path '{slnFilePath}'");
    var solution = await serializer.OpenAsync(slnFilePath, cancellationToken);
    return [.. solution.SolutionProjects.Select(p => Path.GetFullPath(p.FilePath, slnDir))];
}

static async Task PurgeProject(string projectFilePath, string targetPath, bool noClean, bool deleteVsFiles, CancellationToken cancellationToken)
{
    var projectDir = Path.GetDirectoryName(projectFilePath) ?? throw new InvalidOperationException($"Project directory could not be determined for path '{projectFilePath}'");

    // Extract properties
    var properties = await DotnetCli.GetProperties(projectFilePath, ProjectProperties.AllOutputDirs, cancellationToken);

    if (!noClean)
    {
        // Run `dotnet clean` for each configuration
        foreach (var key in properties.Keys)
        {
            var (configuration, targetFramework) = key;

            string[] cleanArgs = ["--configuration", configuration, "-p:BuildProjectReferences=false"];
            if (targetFramework is not null)
            {
                cleanArgs = [.. cleanArgs, "--framework", targetFramework];
            }

            // Calculate relative path from target directory to project file
            var relativePath = GetRelativePath(targetPath, projectFilePath);
            
            Write($"Running 'dotnet clean {relativePath} {string.Join(' ', cleanArgs)}'...");
            await DotnetCli.Clean(projectFilePath, cleanArgs);
            WriteLine(" done!", ConsoleColor.Green);
        }
    }

    // Delete the output directories for each configuration
    foreach (var key in properties.Keys)
    {
        var (configuration, _) = key;
        var outputDirs = properties[key];

        // Get the output directories paths
        var dirsToDelete = outputDirs.Values.ToList();

        var pathsToDelete = dirsToDelete
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => Path.GetFullPath(d, projectDir))
            .Where(d => Directory.Exists(d))
            .OrderDescending()
            .ToList();

        // Delete the output directories
        foreach (var dirPath in pathsToDelete)
        {
            if (Directory.Exists(dirPath) && !string.Equals(projectDir, dirPath, StringComparison.Ordinal))
            {
                Directory.Delete(dirPath, recursive: true);
                var relativePath = GetRelativePath(targetPath, dirPath);
                WriteLine($"Deleted '{relativePath}'");
            }
        }

        // Check if output directories parent directories are now empty and delete them recursively
        foreach (var dirPath in pathsToDelete)
        {
            DeleteEmptyParentDirectories(dirPath, targetPath);
        }
    }

    if (deleteVsFiles)
    {
        // Delete Visual Studio related directories & files for this project
        List<string> vsPaths = [
            Path.Combine(projectDir, ".vs"),
            $"{projectFilePath}.user"
        ];

        foreach (var path in vsPaths)
        {
            var deleted = false;
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                deleted = true;
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
                deleted = true;
            }

            if (deleted)
            {
                var relativePath = GetRelativePath(targetPath, path);
                WriteLine($"Deleted '{relativePath}'");
            }
        }
    }
}

static void DeleteVsDir(string targetPath, CancellationToken cancellationToken)
{
    // Find the .vs directory by walking up the directory tree from the target directory until it's found
    // or a .git directory is found, and if found delete the .vs directory
    var dir = new DirectoryInfo(Path.GetDirectoryName(targetPath) ?? targetPath);
    while (dir is not null && dir.Exists)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            break;
        }

        var vsDir = new DirectoryInfo(Path.Combine(dir.FullName, ".vs"));
        if (vsDir.Exists)
        {
            vsDir.Delete(recursive: true);
            var relativePath = GetRelativePath(targetPath, vsDir.FullName);
            WriteLine($"Deleted '{relativePath}'");
            break;
        }

        if (dir.GetDirectories(".git").Length > 0)
        {
            break;
        }

        dir = dir.Parent;
    }
}

static void DeleteEmptyParentDirectories(string path, string targetPath)
{
    var dir = new DirectoryInfo(path).Parent;
    while (dir is not null && dir.Exists && dir.GetFileSystemInfos().Length == 0)
    {
        dir.Delete();
        string relativePath = GetRelativePath(targetPath, dir.FullName);
        WriteLine($"Deleted '{relativePath}'");
        dir = dir.Parent;
    }
}

static string GetRelativePath(string relativeTo, string path)
{
    if (File.Exists(path))
    {
        // It's a file so get the directory
        path = Path.GetDirectoryName(path) ?? path;
    }

    return Path.GetRelativePath(relativeTo, path);
}

static async Task<string?> DetectNewerVersion(CancellationToken cancellationToken)
{
    var currentVersionValue = VersionOptionAction.GetCurrentVersion();
    if (currentVersionValue is null || !SemanticVersion.TryParse(currentVersionValue, out var currentVersion))
    {
        return null;
    }

    var packageUrl = "https://api.nuget.org/v3-flatcontainer/dotnet-purge/index.json";
    using var httpClient = new HttpClient();
    var versions = await httpClient.GetFromJsonAsync(packageUrl, PurgeJsonContext.Default.NuGetVersions, cancellationToken: cancellationToken);

    if (versions?.Versions is null || versions.Versions.Length == 0)
    {
        return null;
    }

    var versionComparer = new VersionComparer();
    var latestVersion = currentVersion;
    foreach (var versionValue in versions.Versions)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            break;
        }

        if (SemanticVersion.TryParse(versionValue, out var version) && version > latestVersion)
        {
            latestVersion = version;
        }
    }

    return latestVersion > currentVersion ? latestVersion.ToString() : null;
}

static void WriteError(string message) => WriteLine(message, ConsoleColor.Red);

static void WriteLine(string? message = null, ConsoleColor? color = default)
{
    if (!string.IsNullOrEmpty(message))
    {
        Write(message, color);
    }
    Console.WriteLine();
}

static void Write(string? message = null, ConsoleColor? color = default)
{
    if (color is not null)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = color.Value;
        Console.Write(message);
        Console.ForegroundColor = originalColor;
    }
    else
    {
        Console.Write(message);
    }
}

static class ProjectProperties
{
    public readonly static string Configurations = nameof(Configurations);
    public readonly static string TargetFrameworks = nameof(TargetFrameworks);
    public readonly static string BaseIntermediateOutputPath = nameof(BaseIntermediateOutputPath);
    public readonly static string BaseOutputPath = nameof(BaseOutputPath);
    public readonly static string PackageOutputPath = nameof(PackageOutputPath);
    public readonly static string PublishDir = nameof(PublishDir);

    public readonly static string[] All = [Configurations, TargetFrameworks, BaseIntermediateOutputPath, BaseOutputPath, PackageOutputPath, PublishDir];
    public readonly static string[] AllOutputDirs = [BaseIntermediateOutputPath, BaseOutputPath, PackageOutputPath, PublishDir];
}

static class DotnetCli
{
    private static readonly string[] CleanArgs = ["clean"];

    public static Task Clean(string projectFilePath, string[] args)
    {
        var arguments = new List<string>(CleanArgs);
        arguments.Add(projectFilePath);
        arguments.AddRange(args);
        
        var process = Start(arguments);

        return process.WaitForExitAsync();
    }

    public static async Task<Dictionary<(string Configuration, string? TargetFramework), Dictionary<string, string>>> GetProperties(string projectFilePath, IEnumerable<string> properties, CancellationToken cancellationToken)
    {
        // Get configurations first
        var configurations = (await GetProperties(projectFilePath, null, null, [ProjectProperties.Configurations], cancellationToken))[ProjectProperties.Configurations]
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // Detect multi-targeting
        string[]? targetFrameworks = null;
        var targetFrameworksProps = (await GetProperties(projectFilePath, null, null, [ProjectProperties.TargetFrameworks], cancellationToken));
        if (targetFrameworksProps.TryGetValue(ProjectProperties.TargetFrameworks, out var value) && !string.IsNullOrEmpty(value))
        {
            targetFrameworks = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
        var isMultiTargeted = targetFrameworks?.Length > 1;

        var result = new Dictionary<(string Configuration, string? TargetFramework), Dictionary<string, string>>();

        foreach (var configuration in configurations)
        {
            if (isMultiTargeted)
            {
                foreach (var targetFramework in targetFrameworks!)
                {
                    var configurationProperties = await GetProperties(projectFilePath, configuration, targetFramework, properties, cancellationToken);
                    result[(configuration, targetFramework)] = configurationProperties;
                }
            }
            else
            {
                var configurationProperties = await GetProperties(projectFilePath, configuration, null, properties, cancellationToken);
                result[(configuration, null)] = configurationProperties;
            }
        }

        return result;
    }

    public static async Task<Dictionary<string, string>> GetProperties(string projectFilePath, string? configuration, string? targetFramework, IEnumerable<string> properties, CancellationToken cancellationToken)
    {
        var propertiesValue = string.Join(',', properties);
        var arguments = new List<string>
        {
            "msbuild",
            projectFilePath,
            $"-getProperty:{propertiesValue}",
            "-p:BuildProjectReferences=false"
        };

        if (configuration is not null)
        {
            arguments.Add($"-p:Configuration={configuration}");
        }
        if (targetFramework is not null)
        {
            arguments.Add($"-p:TargetFramework={targetFramework}");
        }

        var startInfo = GetProcessStartInfo(arguments);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        var process = Start(startInfo);

        var stdout = new StringBuilder();
        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $$"""
                Error evaluating project properties at path: '{{projectFilePath}}'.
                Process exited with code: {{process.ExitCode}}
                Stdout:
                    {{stdout}}
                Stderr:
                    {{stderr}}
                """);
        }

        var stringOutput = stdout.ToString().Trim();
        if (properties.Count() > 1)
        {
            var output = JsonSerializer.Deserialize(stringOutput, PurgeJsonContext.Default.MsBuildGetPropertyOutput);
            return output?.Properties ?? [];
        }

        return new() { { properties.First(), stringOutput } };
    }

    private static Process Start(IEnumerable<string> arguments) => Start(GetProcessStartInfo(arguments));

    private static Process Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo };

        return process.Start() ? process : throw new Exception("Failed to start process");
    }

    private static ProcessStartInfo GetProcessStartInfo(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
        {
            info.ArgumentList.Add(arg);
        }

        return info;
    }
}

[JsonSerializable(typeof(MsBuildGetPropertyOutput))]
[JsonSerializable(typeof(NuGetVersions))]
internal partial class PurgeJsonContext : JsonSerializerContext
{

}

internal class MsBuildGetPropertyOutput
{
    public Dictionary<string, string>? Properties { get; set; } = [];
}

internal class NuGetVersions
{
    [JsonPropertyName("versions")]
    public string[] Versions { get; set; } = [];
}

internal sealed class VersionOptionAction : SynchronousCommandLineAction
{
    public override int Invoke(ParseResult parseResult)
    {
        var currentVersion = GetCurrentVersion();
        parseResult.Configuration.Output.WriteLine(currentVersion ?? "<unknown>");

        return 0;
    }

    public static string? GetCurrentVersion()
    {
        var assembly = typeof(Program).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informationalVersion))
        {
            // Remove the commit hash from the version string
            var versionParts = informationalVersion.Split('+');
            return versionParts[0];
        }

        return assembly.GetName().Version?.ToString();
    }
}
