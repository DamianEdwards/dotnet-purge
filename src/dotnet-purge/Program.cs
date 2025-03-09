using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

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
var exitCode = await result.InvokeAsync().ConfigureAwait(false);
return exitCode;

async Task<int> PurgeCommand(ParseResult parseResult, CancellationToken cancellationToken)
{
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

    var projectDirs = GetProjectDirs(targetDir, recurseValue);
    var projectCount = projectDirs.Count;

    WriteLine($"Found {projectCount} projects to purge");
    WriteLine();

    var count = 0;
    foreach (var dir in projectDirs)
    {
        await PurgeProject(dir, noCleanValue);
        count++;

        WriteLine($"({count}/{projectCount}) Purged {dir}");
    }

    WriteLine();
    WriteLine($"Finished purging {projectCount} projects");

    return 0;
}

HashSet<string> GetProjectDirs(string path, bool recurse)
{
    var result = new HashSet<string>();

    // Find all sub-directories that contain solution or project files
    string[] projectFileMask = ["*.sln", "*.slnx", "*.csproj", "*.vbproj", "*.fsproj", "*.esproj", "*.proj"];
    var projectDirs = projectFileMask
        .SelectMany(mask => new DirectoryInfo(path).EnumerateFiles(mask, SearchOption.AllDirectories))
        .Select(file => file.DirectoryName)
        .ToList();

    foreach (var dir in projectDirs)
    {
        if (!string.IsNullOrEmpty(dir))
        {
            AddProjectDirs(dir, result);
        }
    }

    return result;
}

async void AddProjectDirs(string path, HashSet<string> dirs)
{
    // Detect if we are in a solution directory
    string[] slnFileMask = ["*.sln", "*.slnx"];
    var slnFiles = slnFileMask.SelectMany(new DirectoryInfo(path).EnumerateFiles).ToList();

    if (slnFiles.Count > 0)
    {
        // If we are in a solution directory, add the project directories to the list
        foreach (var slnFile in slnFiles)
        {
            var projectDirs = await GetSlnProjectDirs(slnFiles[0].FullName);
            foreach (var projectDir in projectDirs)
            {
                dirs.Add(projectDir);
            }
        }
    }
    else
    {
        // Just add the directory to the list
        dirs.Add(path);
    }
}

static async Task<List<string>> GetSlnProjectDirs(string slnFilePath)
{
    var serializer = SolutionSerializers.Serializers.FirstOrDefault(s => s.IsSupported(slnFilePath));
    if (serializer is null)
    {
        WriteError($"Solution file parsers for file extension '{Path.GetExtension(slnFilePath)}' not found.");
        Environment.Exit(1);
    }
    var slnDir = Path.GetDirectoryName(slnFilePath) ?? throw new InvalidOperationException("Solution directory could not be determined.");
    var solution = await serializer.OpenAsync(slnFilePath, default);
    return [.. solution.SolutionProjects.Select(p => Path.GetDirectoryName(Path.GetFullPath(p.FilePath, slnDir)))];
}

static async Task PurgeProject(string dir, bool noClean)
{
    DotnetCli.WorkingDirectory = dir;

    // Extract properties
    var properties = await DotnetCli.GetProperties(ProjectProperties.AllOutputDirs);

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
        var (configuration, targetFramework) = key;
        var outputDirs = properties[key];

        // Get the output directories paths
        var dirsToDelete = outputDirs.Values.ToList();

        var pathsToDelete = dirsToDelete
            .Select(d => Path.GetFullPath(d, DotnetCli.WorkingDirectory))
            .Where(d => Directory.Exists(d))
            .OrderDescending()
            .ToList();

        // Delete the output directories
        foreach (var dirPath in pathsToDelete)
        {
            if (Directory.Exists(dirPath))
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

    public static async Task<Dictionary<(string Configuration, string? TargetFramework), Dictionary<string, string>>> GetProperties(IEnumerable<string> properties)
    {
        // Get configurations first
        var configurations = (await GetProperties(null, null, [ProjectProperties.Configurations]))[ProjectProperties.Configurations]
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        string[]? targetFrameworks = null;
        var targetFrameworksProps = (await GetProperties(null, null, [ProjectProperties.TargetFrameworks]));
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
                    var configurationProperties = await GetProperties(configuration, targetFramework, properties);
                    result[(configuration, targetFramework)] = configurationProperties;
                }
            }
            else
            {
                var configurationProperties = await GetProperties(configuration, null, properties);
                result[(configuration, null)] = configurationProperties;
            }
        }

        return result;
    }

    public static async Task<Dictionary<string, string>> GetProperties(string? configuration, string? targetFramework, IEnumerable<string> properties)
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

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            Console.Write(stderr.ToString());
            Console.Write(stdout.ToString());
            Environment.Exit(process.ExitCode);
        }

        var stringOutput = stdout.ToString();
        if (properties.Count() > 1)
        {
            var output = JsonSerializer.Deserialize(stringOutput, PurgeJsonContext.Default.MsBuildGetPropertyOutput);
            return output?.Properties ?? [];
        }

        return new() { { properties.First().Trim(), stringOutput } };
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
internal partial class PurgeJsonContext : JsonSerializerContext
{

}

internal class MsBuildGetPropertyOutput
{
    public Dictionary<string, string>? Properties { get; set; } = [];
}

internal sealed class VersionOptionAction : SynchronousCommandLineAction
{
    public override int Invoke(ParseResult parseResult)
    {
        var assembly = typeof(Program).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informationalVersion))
        {
            // Remove the commit hash from the version string
            var versionParts = informationalVersion.Split('+');
            parseResult.Configuration.Output.WriteLine(versionParts[0]);
        }
        else
        {
            parseResult.Configuration.Output.WriteLine(assembly.GetName().Version?.ToString() ?? "<unknown>");
        }

        return 0;
    }
}
