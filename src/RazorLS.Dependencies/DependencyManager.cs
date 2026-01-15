using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RazorLS.Dependencies;

/// <summary>
/// Manages downloading and caching of Roslyn and Razor dependencies.
/// </summary>
public class DependencyManager
{
    readonly ILogger<DependencyManager> _logger;
    readonly string _basePath;
    readonly HttpClient _httpClient;
    readonly bool _skipDependencyCheck;

    // VS Code C# extension version - update this when newer versions are available
    const string ExtensionVersion = "2.111.2";

    public DependencyManager(ILogger<DependencyManager> logger, string? basePath = null, bool skipDependencyCheck = false)
    {
        _logger = logger;
        _basePath = basePath ?? GetDefaultBasePath();
        _skipDependencyCheck = skipDependencyCheck;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", $"RazorLS/{VersionHelper.GetAssemblyVersion()}");
    }

    public string BasePath => _basePath;
    public string RoslynPath => Path.Combine(_basePath, "roslyn");
    public string RazorExtensionPath => Path.Combine(_basePath, "razorExtension");
    public string VersionFilePath => Path.Combine(_basePath, "version.json");

    /// <summary>
    /// Gets the path to the Roslyn language server DLL (requires dotnet to run).
    /// </summary>
    public string RoslynServerDllPath =>
        Path.Combine(RoslynPath, "Microsoft.CodeAnalysis.LanguageServer.dll");

    /// <summary>
    /// Gets the path to the Razor source generator DLL.
    /// </summary>
    public string RazorSourceGeneratorPath =>
        Path.Combine(RazorExtensionPath, "Microsoft.CodeAnalysis.Razor.Compiler.dll");

    /// <summary>
    /// Gets the path to the Razor design-time targets.
    /// </summary>
    public string RazorDesignTimePath =>
        Path.Combine(RazorExtensionPath, "Targets", "Microsoft.NET.Sdk.Razor.DesignTime.targets");

    /// <summary>
    /// Gets the path to the Razor extension DLL.
    /// </summary>
    public string RazorExtensionDllPath =>
        Path.Combine(RazorExtensionPath, "Microsoft.VisualStudioCode.RazorExtension.dll");

    /// <summary>
    /// Ensures all dependencies are downloaded and ready.
    /// </summary>
    public async Task<bool> EnsureDependenciesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(_basePath);

            var installedVersion = GetInstalledVersion();
            if (installedVersion?.Version == ExtensionVersion && AreDependenciesComplete())
            {
                _logger.LogInformation("Dependencies are up to date (version {Version})", ExtensionVersion);
                return true;
            }

            if (_skipDependencyCheck)
            {
                _logger.LogWarning("Dependency check skipped. Dependencies may be missing or outdated.");
                return AreDependenciesComplete();
            }

            _logger.LogInformation("Downloading dependencies (version {Version})...", ExtensionVersion);

            await DownloadVsCodeExtensionAsync(cancellationToken);

            SaveVersionInfo(ExtensionVersion);

            _logger.LogInformation("Dependencies downloaded successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure dependencies");
            return false;
        }
    }

    /// <summary>
    /// Checks if all required dependency files exist.
    /// </summary>
    public bool AreDependenciesComplete()
    {
        var complete = File.Exists(RoslynServerDllPath)
            && File.Exists(RazorSourceGeneratorPath)
            && File.Exists(RazorExtensionDllPath);

        // Design-time targets may not exist in all versions, so just warn
        if (complete && !File.Exists(RazorDesignTimePath))
        {
            _logger.LogWarning("Razor design-time targets not found at {Path}", RazorDesignTimePath);
        }

        return complete;
    }

    private async Task DownloadVsCodeExtensionAsync(CancellationToken cancellationToken)
    {
        // Download the universal (platform-neutral) VS Code C# extension
        // This requires 'dotnet' to be installed to run the Roslyn language server
        var extensionUrl = GetExtensionDownloadUrl();

        _logger.LogInformation("Downloading C# extension from {Url}", extensionUrl);

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"csharp-extension-{Guid.NewGuid()}.vsix");
        try
        {
            await DownloadFileAsync(extensionUrl, tempZipPath, cancellationToken);
            await ExtractExtensionAsync(tempZipPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                File.Delete(tempZipPath);
            }
        }
    }

    private async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;

            if (totalBytes.HasValue)
            {
                var percent = (int)(totalRead * 100 / totalBytes.Value);
                _logger.LogDebug("Download progress: {Percent}%", percent);
            }
        }
    }

    private async Task ExtractExtensionAsync(string zipPath, CancellationToken cancellationToken)
    {
        var tempExtractPath = Path.Combine(Path.GetTempPath(), $"csharp-extension-extract-{Guid.NewGuid()}");
        try
        {
            _logger.LogInformation("Extracting extension...");

            // VSIX is just a ZIP file
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tempExtractPath), cancellationToken);

            // Find and copy Roslyn language server
            var roslynSource = Path.Combine(tempExtractPath, "extension", ".roslyn");
            if (Directory.Exists(roslynSource))
            {
                if (Directory.Exists(RoslynPath))
                {
                    Directory.Delete(RoslynPath, recursive: true);
                }
                CopyDirectory(roslynSource, RoslynPath);
                _logger.LogInformation("Extracted Roslyn language server to {Path}", RoslynPath);
            }
            else
            {
                _logger.LogError("Roslyn language server not found in extension at {Path}", roslynSource);
            }

            // Find and copy Razor extension
            var razorSource = Path.Combine(tempExtractPath, "extension", ".razorExtension");
            if (Directory.Exists(razorSource))
            {
                if (Directory.Exists(RazorExtensionPath))
                {
                    Directory.Delete(RazorExtensionPath, recursive: true);
                }
                CopyDirectory(razorSource, RazorExtensionPath);
                _logger.LogInformation("Extracted Razor extension to {Path}", RazorExtensionPath);
            }
            else
            {
                _logger.LogError("Razor extension not found in extension at {Path}", razorSource);
            }
        }
        finally
        {
            if (Directory.Exists(tempExtractPath))
            {
                Directory.Delete(tempExtractPath, recursive: true);
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destDir = Path.Combine(destinationDir, Path.GetFileName(dir));
            CopyDirectory(dir, destDir);
        }
    }

    private static string GetExtensionDownloadUrl()
    {
        // Download from VS Code marketplace using the vsassets URL
        // This gets the universal (platform-neutral) package
        return $"https://ms-dotnettools.gallery.vsassets.io/_apis/public/gallery/publisher/ms-dotnettools/extension/csharp/{ExtensionVersion}/assetbyname/Microsoft.VisualStudio.Services.VSIXPackage";
    }

    private static string GetDefaultBasePath()
    {
        // Use XDG_CACHE_HOME if set (Linux/macOS)
        var cacheDir = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrEmpty(cacheDir))
        {
            return Path.Combine(cacheDir, "razorls");
        }

        // On Windows, use LocalApplicationData
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "razorls");
        }

        // Linux/macOS fallback: ~/.cache
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".cache", "razorls");
    }

    private DependencyVersionInfo? GetInstalledVersion()
    {
        if (!File.Exists(VersionFilePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(VersionFilePath);
            return JsonSerializer.Deserialize<DependencyVersionInfo>(json);
        }
        catch
        {
            return null;
        }
    }

    private void SaveVersionInfo(string version)
    {
        var info = new DependencyVersionInfo
        {
            Version = version,
            InstalledAt = DateTime.UtcNow,
            Platform = RuntimeInformation.RuntimeIdentifier
        };

        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(VersionFilePath, json);
    }
}

public class DependencyVersionInfo
{
    public string? Version { get; set; }
    public DateTime InstalledAt { get; set; }
    public string? Platform { get; set; }
}
