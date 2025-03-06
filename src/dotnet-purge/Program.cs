using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// Extract properties
var properties = await DotnetCli.GetProperties(["Configurations", "BaseIntermediatePath", "BaseOutputPath"]);
var configurations = properties["Configurations"].Split(';');

// Run clean for each configuration
foreach (var configuration in configurations)
{
    await DotnetCli.Clean(["--configuration", configuration]);
}

// Delete intermediate and output directories
var intermediatePath = properties["BaseIntermediatePath"];
var outputPath = properties["BaseOutputPath"];
Directory.Delete(intermediatePath, recursive: true);
Directory.Delete(outputPath, recursive: true);

static class DotnetCli
{
    private static readonly string[] CleanArgs = ["clean"];

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

        var process = Start(startInfo);

        var sb = new StringBuilder();
        process.OutputDataReceived += (sender, e) =>
        {
            sb.AppendLine(e.Data);
        };
        await process.WaitForExitAsync();
        var output = JsonSerializer.Deserialize(sb.ToString(), PurgeJsonContext.Default.MsBuildGetPropertyOutput);
        return output?.Properties ?? [];
    }

    private static Process Start(IEnumerable<string> arguments) => Start(GetProcessStartInfo(arguments));

    private static Process Start(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo);

        return process ?? throw new Exception("Failed to start process");
    }

    private static ProcessStartInfo GetProcessStartInfo(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

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
