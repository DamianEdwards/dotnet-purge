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

var targetArgument = new Argument<string?>("TARGETDIR")
{
    Description = "The directory that contains the solution or project file to purge. If not specified, the current directory will be used.",
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

var rootCommand = new RootCommand("Purges the solution or project in the specified directory.")
{
    targetArgument,
    recurseOption,
    noCleanOption
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

    var targetDir = targetValue ?? Directory.GetCurrentDirectory();
    if (!Directory.Exists(targetDir))
    {
        parseResult.Configuration.Error.WriteLine($"Directory '{targetDir}' does not exist.");
        return 1;
    }
    targetDir = Path.GetFullPath(targetDir);

    var projectDirs = await GetProjectDirs(targetDir, recurseValue, cancellationToken);
    var projectCount = projectDirs.Count;

    WriteLine($"Found {projectCount} projects to purge");
    WriteLine();

    var succeded = 0;
    var failed = 0;
    var cancelled = 0;
    foreach (var dir in projectDirs)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            break;
        }

        try
        {
            await PurgeProject(dir, noCleanValue, cancellationToken);
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
                Failed to purge project at path: {{dir}}
                {{ex.Message}}
                """);
            failed++;
            continue;
        }

        WriteLine($"({succeded}/{projectCount}) Purged {dir}");
    }

    var operationCancelled = cancelled > 0 || cancellationToken.IsCancellationRequested;

    if (succeded > 0)
    {
        WriteLine();
        WriteLine($"Finished purging {succeded} projects", ConsoleColor.Green);
    }

    if (cancelled > 0)
    {
        WriteLine();
        WriteLine($"Cancelled purging {cancelled} projects", ConsoleColor.Yellow);
    }

    if (failed > 0)
    {
        WriteLine();
        WriteLine($"Failed purging {failed} projects", ConsoleColor.Red);
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

async Task<HashSet<string>> GetProjectDirs(string path, bool recurse, CancellationToken cancellationToken)
{
    var result = new HashSet<string>();

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
                var projectDirs = await GetSlnProjectDirs(file.FullName, cancellationToken);
                foreach (var projectDir in projectDirs)
                {
                    result.Add(projectDir);
                }
            }
            else
            {
                var dir = file.DirectoryName;
                if (!string.IsNullOrEmpty(dir))
                {
                    result.Add(dir);
                }
            }
        }
    }

    return result;
}

static async Task<List<string>> GetSlnProjectDirs(string slnFilePath, CancellationToken cancellationToken)
{
    var serializer = SolutionSerializers.Serializers.FirstOrDefault(s => s.IsSupported(slnFilePath))
        ?? throw new InvalidOperationException($"A solution file parser for file extension '{Path.GetExtension(slnFilePath)}' could not be not found.");
    var slnDir = Path.GetDirectoryName(slnFilePath) ?? throw new InvalidOperationException($"Solution directory could not be determined for path '{slnFilePath}'");
    var solution = await serializer.OpenAsync(slnFilePath, cancellationToken);
    return [.. solution.SolutionProjects.Select(p => Path.GetDirectoryName(Path.GetFullPath(p.FilePath, slnDir)))];
}

static async Task PurgeProject(string dir, bool noClean, CancellationToken cancellationToken)
{
    DotnetCli.WorkingDirectory = dir;

    // Extract properties
    var properties = await DotnetCli.GetProperties(ProjectProperties.AllOutputDirs, cancellationToken);

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

            Write($"Running '{dir}{Path.DirectorySeparatorChar}dotnet clean {string.Join(' ', cleanArgs)}'...");
            await DotnetCli.Clean(cleanArgs);
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
            .Select(d => Path.GetFullPath(d, DotnetCli.WorkingDirectory))
            .Where(d => Directory.Exists(d))
            .OrderDescending()
            .ToList();

        // Delete the output directories
        foreach (var dirPath in pathsToDelete)
        {
            if (Directory.Exists(dirPath) && !string.Equals(dir, dirPath, StringComparison.Ordinal))
            {
                Directory.Delete(dirPath, recursive: true);
                WriteLine($"Deleted '{dirPath}'");
            }
        }

        // Check if output directories parent directories are now empty and delete them recursively
        foreach (var dirPath in pathsToDelete)
        {
            DeleteEmptyParentDirectories(dirPath);
        }
    }
}

static void DeleteEmptyParentDirectories(string path)
{
    var dir = new DirectoryInfo(path).Parent;
    while (dir is not null && dir.Exists && dir.GetFileSystemInfos().Length == 0)
    {
        dir.Delete();
        WriteLine($"Deleted '{dir.FullName}'");
        dir = dir.Parent;
    }
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

    public static string? WorkingDirectory { get; set; }

    public static Task Clean(string[] args)
    {
        var arguments = CleanArgs.Concat(args);
        var process = Start(arguments);

        return process.WaitForExitAsync();
    }

    public static async Task<Dictionary<(string Configuration, string? TargetFramework), Dictionary<string, string>>> GetProperties(IEnumerable<string> properties, CancellationToken cancellationToken)
    {
        // Get configurations first
        var configurations = (await GetProperties(null, null, [ProjectProperties.Configurations], cancellationToken))[ProjectProperties.Configurations]
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // Detect multi-targeting
        string[]? targetFrameworks = null;
        var targetFrameworksProps = (await GetProperties(null, null, [ProjectProperties.TargetFrameworks], cancellationToken));
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
                    var configurationProperties = await GetProperties(configuration, targetFramework, properties, cancellationToken);
                    result[(configuration, targetFramework)] = configurationProperties;
                }
            }
            else
            {
                var configurationProperties = await GetProperties(configuration, null, properties, cancellationToken);
                result[(configuration, null)] = configurationProperties;
            }
        }

        return result;
    }

    public static async Task<Dictionary<string, string>> GetProperties(string? configuration, string? targetFramework, IEnumerable<string> properties, CancellationToken cancellationToken)
    {
        var propertiesValue = string.Join(',', properties);
        string[] arguments = ["msbuild", $"-getProperty:{propertiesValue}", "-p:BuildProjectReferences=false"];

        if (configuration is not null)
        {
            arguments = [.. arguments, $"-p:Configuration={configuration}"];
        }
        if (targetFramework is not null)
        {
            arguments = [.. arguments, $"-p:TargetFramework={targetFramework}"];
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
                Error evaluating project properties at path: '{{WorkingDirectory}}'.
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

        if (WorkingDirectory is not null)
        {
            info.WorkingDirectory = WorkingDirectory;
        }

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
