﻿using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

var targetDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
if (!Directory.Exists(targetDir))
{
    WriteError($"Directory '{targetDir}' does not exist.");
    Environment.Exit(1);
}
targetDir = Path.GetFullPath(targetDir);

// Detect if we are in a solution directory
string[] slnFileMask = ["*.sln", "*.slnx"];
var slnFiles =  slnFileMask.SelectMany(new DirectoryInfo(targetDir).EnumerateFiles).ToList();

if (slnFiles.Count > 0)
{
    if (slnFiles.Count > 1)
    {
        WriteError($"Multiple solution files found in '{targetDir}'.");
        Environment.Exit(1);
    }
    var projectDirs = await GetProjectDirs(slnFiles[0].FullName);
    foreach (var projectDir in projectDirs)
    {
        await PurgeProject(projectDir);
    }
}
else
{
    await PurgeProject(targetDir);
}

static async Task PurgeProject(string dir)
{
    DotnetCli.WorkingDirectory = dir;

    // Extract properties
    var properties = await DotnetCli.GetProperties(ProjectProperties.AllOutputDirs);

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

static async Task<List<string>> GetProjectDirs(string slnFilePath)
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

static void WriteError(string message) => WriteLine(message, ConsoleColor.Red);

static void WriteLine(string message, ConsoleColor? color = default)
{
    Write(message, color);
    Console.WriteLine();
}

static void Write(string message, ConsoleColor? color = default)
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

public class MsBuildGetPropertyOutput
{
    public Dictionary<string, string>? Properties { get; set; } = [];
}
