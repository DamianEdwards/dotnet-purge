using System.Collections.Concurrent;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using NuGet.Versioning;
using Spectre.Console;

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

var dryRunOption = new Option<bool>("--dry-run", "-d")
{
    Description = "Show what would be deleted without actually deleting anything.",
    Arity = ArgumentArity.ZeroOrOne
};

var rootCommand = new RootCommand("Purges the specified solution or project.")
{
    targetArgument,
    recurseOption,
    noCleanOption,
    vsOption,
    dryRunOption
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
    var dryRunValue = parseResult.GetValue(dryRunOption);

    if (dryRunValue)
    {
        AnsiConsole.MarkupLine("[aqua]Dry run mode - no files will be deleted[/]");
        AnsiConsole.WriteLine();
    }

    var targetPath = targetValue ?? Directory.GetCurrentDirectory();
    if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
    {
        AnsiConsole.MarkupLineInterpolated($"[red]'{targetPath}' does not exist.[/]");
        return 1;
    }

    var succeeded = 0;
    var failed = 0;
    var cancelled = 0;

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .SpinnerStyle(Style.Parse("aqua"))
        .StartAsync("Finding projects...", async ctx =>
        {
            targetPath = Path.GetFullPath(targetPath);

            var projectFiles = await GetProjectFiles(targetPath, recurseValue, cancellationToken);
            var projectCount = projectFiles.Count;

            AnsiConsole.MarkupInterpolated($"Found {projectCount} {ProjectOrProjects(projectCount)} to purge");
            AnsiConsole.WriteLine();

            if (projectCount == 0 && !recurseValue)
            {
                AnsiConsole.MarkupLine("[aqua]Use --recurse to search for projects in sub-directories.[/]");
            }

            ctx.Spinner(Spinner.Known.BouncingBar);
            ctx.SpinnerStyle(Style.Parse("lime"));
            ctx.Status("Detecting project configurations...");

            ConcurrentDictionary<string, Dictionary<(string Configuration, string? TargetFramework), Dictionary<string, string>>> projectProperties = new();
            ConcurrentQueue<string> failedQueue = new();

            await Parallel.ForEachAsync(projectFiles, new ParallelOptions { CancellationToken = cancellationToken }, async (projectFile, ct) =>
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                // Get the project output directories
                try
                {
                    var properties = await DotnetCli.GetProperties(projectFile, ProjectProperties.AllOutputDirs, ct);
                    projectProperties.AddOrUpdate(projectFile, properties, (_, _) => properties);
                }
                catch (OperationCanceledException)
                {
                    cancelled++;
                }
                catch (Exception ex)
                {
                    var relativePath = GetRelativePath(Directory.GetCurrentDirectory(), projectFile);
                    AnsiConsole.MarkupLineInterpolated(
                        $$"""
                        [red]❌ Failed to detect project configurations at path: {{relativePath}}
                        > {{ex.Message}}[/]
                        """);
                    failed++;
                    failedQueue.Enqueue(projectFile);
                }
            });

            // Handle cancellation
            if (cancellationToken.IsCancellationRequested)
            {
                // We haven't deleted anything yet so set cancelled count to count of all project files
                cancelled = projectFiles.Count;
                return;
            }

            // Handle failed projects
            foreach (var projectFile in failedQueue)
            {
                projectProperties.Remove(projectFile, out _);
            }
            failedQueue.Clear();

            if (!noCleanValue)
            {
                ctx.Status("Cleaning projects...");

                // Run `dotnet clean` for each configuration
                await Parallel.ForEachAsync(projectProperties, new ParallelOptions { CancellationToken = cancellationToken }, async (kvp, ct) =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    var projectFilePath = kvp.Key;
                    var projectConfig = kvp.Value.ToDictionary(k => k, v => v);

                    // Run `dotnet clean` for each configuration
                    await Parallel.ForEachAsync(projectConfig.Keys, new ParallelOptions { CancellationToken = ct }, async (config, ct) =>
                    {
                        if (ct.IsCancellationRequested)
                        {
                            return;
                        }

                        var (configuration, targetFramework) = config.Key;

                        string[] cleanArgs = ["--configuration", configuration, "-p:BuildProjectReferences=false"];
                        if (targetFramework is not null)
                        {
                            cleanArgs = [.. cleanArgs, "--framework", targetFramework];
                        }

                        // Calculate relative path from target directory to project file
                        var relativePath = GetRelativePath(Directory.GetCurrentDirectory(), projectFilePath);
                        
                        var frameworkSuffix = targetFramework is not null ? $", {targetFramework}" : "";

                        if (dryRunValue)
                        {
                            AnsiConsole.MarkupLineInterpolated($"[aqua]🔍 Would run `dotnet clean` on [italic]{relativePath}[/] ({configuration}{frameworkSuffix})[/]");
                        }
                        else
                        {
                            AnsiConsole.MarkupLineInterpolated($"🧹 Cleaning [italic]{relativePath}[/] ({configuration}{frameworkSuffix}) ...");

                            try
                            {
                                await DotnetCli.Clean(projectFilePath, cleanArgs);
                            }
                            catch (OperationCanceledException)
                            {
                                cancelled++;
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLineInterpolated(
                                    $$"""
                                    [red]❌ Failed to clean project at path: {{relativePath}}
                                    > {{ex.Message}}[/]
                                    """);
                                failed++;
                                failedQueue.Enqueue(projectFilePath);
                            }
                        }
                    });
                });

                // Handle cancellation
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = projectFiles.Count;
                    return;
                }

                // Handle failed projects
                foreach (var projectFile in failedQueue)
                {
                    projectProperties.Remove(projectFile, out _);
                }
                failedQueue.Clear();
            }

            ctx.Status("Deleting output directories...");

            // Delete the output directories for each configuration
            var allOutputDirs = projectProperties.Values // (config, targetFramework), propertyName, directory
                .SelectMany(d => d.Values) // propertyName, directory
                .SelectMany(d => d.Values) // directory
                .OrderDescending()
                .Distinct()
                .ToList();

            // Delete the output directories
            Parallel.ForEach(allOutputDirs, (dirPath, state) =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    state.Stop();
                    return;
                }

                if (Directory.Exists(dirPath))
                {
                    var relativePath = GetRelativePath(Directory.GetCurrentDirectory(), dirPath);
                    if (dryRunValue)
                    {
                        AnsiConsole.MarkupLineInterpolated($"[aqua]🔍 Would delete [italic]{relativePath}[/][/]");
                    }
                    else
                    {
                        try
                        {
                            Directory.Delete(dirPath, recursive: true);
                            AnsiConsole.MarkupLineInterpolated($"[green]✅ Deleted [italic] {relativePath} [/][/]");
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLineInterpolated(
                                $$"""
                                [red]❌ Failed to delete output directory at path: {{relativePath}}
                                > {{ex.Message}}[/]
                                """);
                            
                            failed++;
                            failedQueue.Enqueue(dirPath);
                        }
                    }
                }
            });

            // Handle cancellation
            if (cancellationToken.IsCancellationRequested)
            {
                //cancelled = projectFiles.Count;
                return;
            }

            // Handle failed projects
            foreach (var projectFile in failedQueue)
            {
                projectProperties.Remove(projectFile, out _);
            }
            failedQueue.Clear();

            // Delete VS directories for each project
            if (vsValue)
            {
                ctx.Status("🧹 Deleting VS directories...");

                var vsPaths = projectProperties.Keys // Project file path
                    .SelectMany(p =>
                    {
                        var projectDir = Path.GetDirectoryName(p) ?? throw new InvalidOperationException($"Project directory could not be determined for path '{p}'");
                        return new List<string> {
                            Path.Combine(projectDir, ".vs"),
                            $"{p}.user"
                        };
                    })
                    .OrderDescending()
                    .Distinct()
                    .ToList();

                if (vsPaths.Count > 0)
                {
                    Parallel.ForEach(vsPaths, (path, state) =>
                    {
                        var exists = Directory.Exists(path) || File.Exists(path);
                        if (exists)
                        {
                            var relativePath = GetRelativePath(Directory.GetCurrentDirectory(), path);
                            if (dryRunValue)
                            {
                                AnsiConsole.MarkupLineInterpolated($"[aqua]🔍 Would delete [italic]{relativePath}[/][/]");
                            }
                            else
                            {
                                if (Directory.Exists(path))
                                {
                                    Directory.Delete(path, recursive: true);
                                }
                                else
                                {
                                    File.Delete(path);
                                }
                                AnsiConsole.MarkupLineInterpolated($"[green]✅ Deleted [italic]{relativePath}[/][/]");
                            }
                        }
                    });
                }

                // Delete the .vs dir at the sln or repo root
                DeleteVsDir(targetPath, dryRunValue, cancellationToken);
            }

            // Check if output directories parent directories are now empty and delete them recursively
            if (dryRunValue)
            {
                AnsiConsole.MarkupLine("[aqua]🔍 Would delete any empty parent directories remaining after deletions[/]");
            }
            else
            {
                foreach (var dirPath in allOutputDirs)
                {
                    DeleteEmptyParentDirectories(dirPath, targetPath, dryRunValue);
                }
            }
        });

    // TODO: Rethink and update how to report the results
    var operationCancelled = cancelled > 0 || cancellationToken.IsCancellationRequested;

    if (succeeded > 0)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLineInterpolated($"[lime]Finished purging {succeeded} {ProjectOrProjects(succeeded)}.[/]");
    }

    if (cancelled > 0)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLineInterpolated($"[yellow]Cancelled purging {cancelled} {ProjectOrProjects(cancelled)}.[/]");
    }

    if (failed > 0)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLineInterpolated($"[red]Failed purging {failed} {ProjectOrProjects(failed)}.[/]");
    }

    // Process the detect newer version task
    try
    {
        var newerVersion = await detectNewerVersionTask;
        if (newerVersion is not null)
        {
            // TODO: Handle case when newer version is a pre-release version
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLineInterpolated($"[yellow]A newer version ({newerVersion}) of dotnet-purge is available![/]");
            AnsiConsole.MarkupLine("[lime]Update by running 'dotnet tool update -g dotnet-purge'[/]");
        }
    }
    catch (Exception)
    {
        // Ignore exceptions from the detect newer version task
    }

    if (operationCancelled)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]🛑 Operation cancelled[/]");
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
            AnsiConsole.MarkupLine("[aqua]The --recurse option is ignored when specifying a single project or solution file.[/]");
        }

        var extension = Path.GetExtension(path);

        if (extension is ".sln" or ".slnx")
        {
            var projectFiles = await GetSlnProjectFiles(path, cancellationToken);
            foreach (var projectFile in projectFiles)
            {
                result.Add(projectFile);
            }
        }
        else if (extension is ".csproj" or ".vbproj" or ".fsproj" or ".esproj" or ".proj")
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

                if (file.Extension is ".sln" or ".slnx")
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
    return [.. solution.SolutionProjects
        .Where(p => !Path.GetExtension(p.FilePath).Equals(".shproj", StringComparison.OrdinalIgnoreCase))
        .Select(p => Path.GetFullPath(p.FilePath, slnDir))];
}

static void DeleteVsDir(string targetPath, bool dryRun, CancellationToken cancellationToken)
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
            var relativePath = GetRelativePath(Directory.GetCurrentDirectory(), vsDir.FullName);
            if (dryRun)
            {
                AnsiConsole.MarkupLineInterpolated($"[aqua]🔍 Would delete [italic]{relativePath}[/][/]");
            }
            else
            {
                try
                {
                    vsDir.Delete(recursive: true);
                    AnsiConsole.MarkupLineInterpolated($"[green]✅ Deleted [italic]{relativePath}[/][/]");
                }
                catch (IOException iox)
                {
                    AnsiConsole.MarkupLineInterpolated(
                        $$"""
                        [red]❌ Failed to delete .vs directory at path: {{relativePath}}
                        > {{iox.Message}}[/]
                        """);
                }
            }
            
            break;
        }

        if (dir.GetDirectories(".git").Length > 0)
        {
            break;
        }

        dir = dir.Parent;
    }
}

static void DeleteEmptyParentDirectories(string path, string targetPath, bool dryRun)
{
    var dir = new DirectoryInfo(path).Parent;
    while (dir is not null && dir.Exists && dir.GetFileSystemInfos().Length == 0)
    {
        var relativePath = GetRelativePath(targetPath, dir.FullName);
        if (dryRun)
        {
            AnsiConsole.MarkupLineInterpolated($"[aqua]🔍 Would delete [italic]{relativePath}[/][/]");
        }
        else
        {
            dir.Delete();
            AnsiConsole.MarkupLineInterpolated($"[green]✅ Deleted [italic]{relativePath}[/][/]");
        }
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
        List<string> arguments = [.. CleanArgs, projectFilePath, .. args];
        
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
        List<string> arguments = ["msbuild", projectFilePath, $"-getProperty:{propertiesValue}", "-p:BuildProjectReferences=false"];

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
        parseResult.InvocationConfiguration.Output.WriteLine(currentVersion ?? "<unknown>");

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
