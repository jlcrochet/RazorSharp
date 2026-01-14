using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorLS.Server.Configuration;
using StreamJsonRpc;

namespace RazorLS.Server.Html;

/// <summary>
/// Client for communicating with an external HTML language server.
/// Uses vscode-html-language-server for HTML formatting in Razor files.
/// </summary>
public class HtmlLanguageClient : IAsyncDisposable
{
    private readonly ILogger<HtmlLanguageClient> _logger;
    private Process? _process;
    private JsonRpc? _rpc;
    private bool _initialized;
    private bool _disposed;
    private bool _enabled = true;

    // Track HTML projections by checksum (Roslyn uses checksums to identify HTML versions)
    private readonly ConcurrentDictionary<string, HtmlProjection> _projections = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public HtmlLanguageClient(ILogger<HtmlLanguageClient> logger)
    {
        _logger = logger;
    }

    public bool IsRunning => _process != null && !_process.HasExited;

    /// <summary>
    /// Starts the HTML language server (vscode-html-language-server).
    /// </summary>
    public async Task StartAsync(HtmlOptions? options, CancellationToken cancellationToken)
    {
        // Check if HTML LS is explicitly disabled
        if (options?.Enable == false)
        {
            _enabled = false;
            _logger.LogInformation("HTML language server disabled by configuration");
            return;
        }

        // Find vscode-html-language-server
        var serverPath = FindHtmlLanguageServer();
        if (serverPath == null)
        {
            _logger.LogWarning("HTML language server not found. HTML formatting will be limited. Install with: npm install -g vscode-langservers-extracted");
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = $"\"{serverPath}\" --stdio",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _process = Process.Start(psi);
        if (_process == null)
        {
            _logger.LogError("Failed to start HTML language server");
            return;
        }

        // Capture stderr
        _ = Task.Run(async () =>
        {
            while (_process != null && !_process.HasExited)
            {
                var line = await _process.StandardError.ReadLineAsync(cancellationToken);
                if (line != null)
                {
                    _logger.LogDebug("[HTML LS stderr] {Line}", line);
                }
            }
        }, cancellationToken);

        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = JsonOptions
        };

        var handler = new HeaderDelimitedMessageHandler(
            _process.StandardInput.BaseStream,
            _process.StandardOutput.BaseStream,
            formatter);
        _rpc = new JsonRpc(handler);
        _rpc.StartListening();

        _logger.LogInformation("HTML language server started (PID: {Pid})", _process.Id);
    }

    /// <summary>
    /// Initializes the HTML language server.
    /// </summary>
    public async Task InitializeAsync(string? rootUri, CancellationToken cancellationToken)
    {
        if (_rpc == null || _initialized)
        {
            return;
        }

        var initParams = new
        {
            processId = Environment.ProcessId,
            rootUri,
            capabilities = new
            {
                textDocument = new
                {
                    formatting = new { dynamicRegistration = true },
                    rangeFormatting = new { dynamicRegistration = true }
                }
            }
        };

        await _rpc.InvokeWithParameterObjectAsync<JsonElement>("initialize", initParams, cancellationToken);
        await _rpc.NotifyAsync("initialized");

        _initialized = true;
        _logger.LogInformation("HTML language server initialized");
    }

    /// <summary>
    /// Updates the HTML projection for a Razor document.
    /// Called when we receive razor/updateHtml from Roslyn.
    /// </summary>
    public async Task UpdateHtmlProjectionAsync(string razorUri, string checksum, string htmlContent)
    {
        if (!_enabled)
        {
            return;
        }

        if (_rpc == null || !_initialized)
        {
            // Still store the projection even if HTML LS isn't running
            _projections[checksum] = new HtmlProjection(razorUri, checksum, htmlContent, 1);
            return;
        }

        var virtualUri = GetVirtualHtmlUri(razorUri);
        var existingByUri = _projections.Values.FirstOrDefault(p => p.RazorUri == razorUri);

        if (existingByUri != null)
        {
            // Update existing document
            var newVersion = existingByUri.Version + 1;
            _projections.TryRemove(existingByUri.Checksum, out _);
            _projections[checksum] = new HtmlProjection(razorUri, checksum, htmlContent, newVersion);

            await _rpc.NotifyWithParameterObjectAsync("textDocument/didChange", new
            {
                textDocument = new { uri = virtualUri, version = newVersion },
                contentChanges = new[] { new { text = htmlContent } }
            });

            _logger.LogDebug("Updated HTML projection for {Uri} (checksum: {Checksum})", razorUri, checksum);
        }
        else
        {
            // Open new document
            _projections[checksum] = new HtmlProjection(razorUri, checksum, htmlContent, 1);

            await _rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new
            {
                textDocument = new
                {
                    uri = virtualUri,
                    languageId = "html",
                    version = 1,
                    text = htmlContent
                }
            });

            _logger.LogDebug("Opened HTML projection for {Uri} (checksum: {Checksum})", razorUri, checksum);
        }
    }

    /// <summary>
    /// Formats an HTML document and returns the text edits.
    /// </summary>
    public async Task<JsonElement?> FormatAsync(string razorUri, string checksum, JsonElement options, CancellationToken cancellationToken)
    {
        if (!_enabled || _rpc == null || !_initialized)
        {
            _logger.LogDebug("HTML LS not available for formatting");
            return null;
        }

        if (!_projections.TryGetValue(checksum, out var projection))
        {
            _logger.LogDebug("No HTML projection found for checksum {Checksum}", checksum);
            return null;
        }

        var virtualUri = GetVirtualHtmlUri(razorUri);

        try
        {
            var @params = new
            {
                textDocument = new { uri = virtualUri },
                options
            };

            var result = await _rpc.InvokeWithParameterObjectAsync<JsonElement?>(
                "textDocument/formatting",
                @params,
                cancellationToken);

            _logger.LogDebug("HTML formatting returned {Count} edits",
                result?.ValueKind == JsonValueKind.Array ? result.Value.GetArrayLength() : 0);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting HTML formatting");
            return null;
        }
    }

    /// <summary>
    /// Formats a range of an HTML document and returns the text edits.
    /// </summary>
    public async Task<JsonElement?> FormatRangeAsync(
        string razorUri,
        string checksum,
        JsonElement range,
        JsonElement options,
        CancellationToken cancellationToken)
    {
        if (!_enabled || _rpc == null || !_initialized)
        {
            return null;
        }

        if (!_projections.TryGetValue(checksum, out _))
        {
            _logger.LogDebug("No HTML projection found for checksum {Checksum}", checksum);
            return null;
        }

        var virtualUri = GetVirtualHtmlUri(razorUri);

        try
        {
            var @params = new
            {
                textDocument = new { uri = virtualUri },
                range,
                options
            };

            return await _rpc.InvokeWithParameterObjectAsync<JsonElement?>(
                "textDocument/rangeFormatting",
                @params,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting HTML range formatting");
            return null;
        }
    }

    /// <summary>
    /// Gets the HTML projection by checksum.
    /// </summary>
    public HtmlProjection? GetProjection(string checksum)
    {
        _projections.TryGetValue(checksum, out var projection);
        return projection;
    }

    /// <summary>
    /// Gets the HTML projection by Razor URI.
    /// </summary>
    public HtmlProjection? GetProjectionByRazorUri(string razorUri)
    {
        return _projections.Values.FirstOrDefault(p => p.RazorUri == razorUri);
    }

    private static string GetVirtualHtmlUri(string razorUri)
    {
        return razorUri + "__virtual.html";
    }

    private static string? FindHtmlLanguageServer()
    {
        var possiblePaths = new[]
        {
            // Global npm install
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".npm-global", "lib", "node_modules", "vscode-langservers-extracted", "bin", "vscode-html-language-server"),
            // npm prefix (common on Linux)
            "/usr/local/lib/node_modules/vscode-langservers-extracted/bin/vscode-html-language-server",
            "/usr/lib/node_modules/vscode-langservers-extracted/bin/vscode-html-language-server",
            // Yarn global
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".yarn", "bin", "vscode-html-language-server"),
            // pnpm global
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "pnpm", "vscode-html-language-server"),
        };

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
            var jsPath = path + ".js";
            if (File.Exists(jsPath))
            {
                return jsPath;
            }
        }

        // Try to find via 'which' command on Unix
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = "vscode-html-language-server",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    return output;
                }
            }
        }
        catch
        {
            // Ignore
        }

        return null;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _rpc?.Dispose();
        _rpc = null;

        if (_process != null && !_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore
            }
            _process.Dispose();
            _process = null;
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Represents an HTML projection of a Razor document.
/// </summary>
public record HtmlProjection(string RazorUri, string Checksum, string HtmlContent, int Version);
