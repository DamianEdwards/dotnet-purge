using System.Diagnostics;
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
    var properties = await DotnetCli.GetProperties(["Configurations", "TargetFrameworks", "BaseIntermediateOutputPath", "BaseOutputPath"]);
    var targetFrameworks = properties["TargetFrameworks"].Split(';');
    var configurations = properties["Configurations"].Split(';');

    // Run clean for each configuration
    var isMultiTargeted = targetFrameworks.Length > 1;
    foreach (var configuration in configurations)
    {
        if (isMultiTargeted)
        {
            foreach (var framework in targetFrameworks)
            {
                Write($"Running 'dotnet clean --configuration {configuration} --framework {framework}'...");
                await DotnetCli.Clean(["--configuration", configuration, "--framework", framework]);
                WriteLine(" done!", ConsoleColor.Green);
            }
        }
        else
        {
            Write($"Running 'dotnet clean --configuration {configuration}'...");
            await DotnetCli.Clean(["--configuration", configuration]);
            WriteLine(" done!", ConsoleColor.Green);
        }
    }

    // Delete intermediate and output directories
    var directortiesToDelete = new[] { properties["BaseIntermediateOutputPath"], properties["BaseOutputPath"] };

    foreach (var directory in directortiesToDelete)
    {
        var path = Path.GetFullPath(directory, DotnetCli.WorkingDirectory);
        if (Directory.Exists(path))
        {
            Write($"Deleting '{path}'...");
            Directory.Delete(path, recursive: true);
            WriteLine(" done!", ConsoleColor.Green);
        }
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

    public static async Task<Dictionary<string, string>> GetProperties(IEnumerable<string> properties)
    {
        var propertiesValue = string.Join(',', properties);
        string[] arguments = ["msbuild", $"-getProperty:{propertiesValue}"];
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
        var output = JsonSerializer.Deserialize(stringOutput, PurgeJsonContext.Default.MsBuildGetPropertyOutput);
        return output?.Properties ?? [];
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
