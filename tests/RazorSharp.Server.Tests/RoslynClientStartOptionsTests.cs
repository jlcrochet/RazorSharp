using RazorSharp.Server.Roslyn;

namespace RazorSharp.Server.Tests;

public class RoslynClientStartOptionsTests
{
    [Fact]
    public void CreateProcessStartInfo_DoesNotPassRemovedRazorOptions()
    {
        var options = new RoslynStartOptions
        {
            ServerDllPath = "/cache/roslyn/Microsoft.CodeAnalysis.LanguageServer.dll",
            RazorExtensionPath = "/cache/razorExtension/Microsoft.VisualStudioCode.RazorExtension.dll",
            LogLevel = "Information"
        };

        var psi = RoslynClient.CreateProcessStartInfo(options);
        var args = psi.ArgumentList.ToArray();

        Assert.Contains("--stdio", args);
        Assert.Contains("--logLevel=Information", args);
        Assert.Contains("--extension", args);
        Assert.Contains(options.RazorExtensionPath, args);
        Assert.DoesNotContain(args, arg => arg.StartsWith("--razorSourceGenerator", StringComparison.Ordinal));
        Assert.DoesNotContain(args, arg => arg.StartsWith("--razorDesignTimePath", StringComparison.Ordinal));
    }
}
