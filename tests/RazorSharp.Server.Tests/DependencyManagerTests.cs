using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;

namespace RazorSharp.Server.Tests;

[Collection("Environment")]
public class DependencyManagerTests
{
    [Fact]
    public void BasePath_UsesXdgCacheHome_OnNonWindows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var tempRoot = CreateTempDir();
        var oldCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");

        try
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", tempRoot);
            using var manager = new DependencyManager(CreateLogger(), "1.0.0-test");
            var expected = Path.Combine(tempRoot, "razorsharp");
            Assert.Equal(expected, manager.BasePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", oldCache);
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void BasePath_UsesLocalAppData_OnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        using var manager = new DependencyManager(CreateLogger(), "1.0.0-test");
        var expectedRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(expectedRoot, manager.BasePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("razorsharp"), manager.BasePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetRazorExtensionSourcePath_PrefersLegacyRazorExtensionDirectory()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var extensionRoot = Path.Combine(tempRoot, "extension");
            var legacyPath = Path.Combine(extensionRoot, ".razorExtension");
            var roslynPath = Path.Combine(extensionRoot, ".roslyn");
            Directory.CreateDirectory(legacyPath);
            Directory.CreateDirectory(roslynPath);

            Assert.Equal(legacyPath, DependencyManager.GetRazorExtensionSourcePath(tempRoot));
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void GetRazorExtensionSourcePath_FallsBackToRoslynDirectory()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var roslynPath = Path.Combine(tempRoot, "extension", ".roslyn");
            Directory.CreateDirectory(roslynPath);

            Assert.Equal(roslynPath, DependencyManager.GetRazorExtensionSourcePath(tempRoot));
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    private static ILogger<DependencyManager> CreateLogger()
    {
        var loggerFactory = LoggerFactory.Create(builder => { });
        return loggerFactory.CreateLogger<DependencyManager>();
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "razorsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDir(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
