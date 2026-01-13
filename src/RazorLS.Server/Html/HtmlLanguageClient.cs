using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorLS.Server.VirtualDocuments;
using StreamJsonRpc;

namespace RazorLS.Server.Html;

/// <summary>
/// Client for communicating with an external HTML language server.
/// Uses vscode-html-language-server (from vscode-html-languageservice npm package).
/// </summary>
public class HtmlLanguageClient : IAsyncDisposable
{
    private readonly ILogger<HtmlLanguageClient> _logger;
    private readonly VirtualDocumentManager _documentManager;
    private Process? _process;
    private JsonRpc? _rpc;
    private bool _initialized;
    private bool _disposed;

    public HtmlLanguageClient(ILogger<HtmlLanguageClient> logger, VirtualDocumentManager documentManager)
    {
        _logger = logger;
        _documentManager = documentManager;
    }

    public bool IsRunning => _process != null && !_process.HasExited;

    /// <summary>
    /// Starts the HTML language server.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Try to find vscode-html-language-server
        var serverPath = FindHtmlLanguageServer();
        if (serverPath == null)
        {
            _logger.LogWarning("HTML language server not found. HTML features will be limited.");
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
            JsonSerializerOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            }
        };

        var handler = new HeaderDelimitedMessageHandler(_process.StandardInput.BaseStream, _process.StandardOutput.BaseStream, formatter);
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
                    completion = new
                    {
                        completionItem = new
                        {
                            snippetSupport = true,
                            documentationFormat = new[] { "markdown", "plaintext" }
                        }
                    },
                    hover = new
                    {
                        contentFormat = new[] { "markdown", "plaintext" }
                    }
                }
            }
        };

        await _rpc.InvokeWithParameterObjectAsync<JsonElement>("initialize", initParams, cancellationToken);
        await _rpc.NotifyAsync("initialized");

        _initialized = true;
        _logger.LogInformation("HTML language server initialized");
    }

    /// <summary>
    /// Notifies the HTML server that a virtual document was opened.
    /// </summary>
    public async Task DidOpenAsync(VirtualDocument doc)
    {
        if (_rpc == null || !_initialized)
        {
            return;
        }

        await _rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new
        {
            textDocument = new
            {
                uri = doc.VirtualHtmlUri,
                languageId = "html",
                version = doc.Version,
                text = doc.HtmlContent
            }
        });
    }

    /// <summary>
    /// Notifies the HTML server that a virtual document changed.
    /// </summary>
    public async Task DidChangeAsync(VirtualDocument doc)
    {
        if (_rpc == null || !_initialized)
        {
            return;
        }

        await _rpc.NotifyWithParameterObjectAsync("textDocument/didChange", new
        {
            textDocument = new
            {
                uri = doc.VirtualHtmlUri,
                version = doc.Version
            },
            contentChanges = new[]
            {
                new { text = doc.HtmlContent }
            }
        });
    }

    /// <summary>
    /// Notifies the HTML server that a virtual document was closed.
    /// </summary>
    public async Task DidCloseAsync(string virtualUri)
    {
        if (_rpc == null || !_initialized)
        {
            return;
        }

        await _rpc.NotifyWithParameterObjectAsync("textDocument/didClose", new
        {
            textDocument = new { uri = virtualUri }
        });
    }

    /// <summary>
    /// Sends a request to the HTML language server.
    /// </summary>
    public async Task<JsonElement?> SendRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (_rpc == null || !_initialized)
        {
            return null;
        }

        try
        {
            return await _rpc.InvokeWithParameterObjectAsync<JsonElement?>(method, parameters, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending request to HTML LS: {Method}", method);
            return null;
        }
    }

    /// <summary>
    /// Gets completions from the HTML language server.
    /// </summary>
    public async Task<JsonElement?> GetCompletionsAsync(string virtualUri, int line, int character, CancellationToken cancellationToken)
    {
        if (_rpc == null || !_initialized)
        {
            return null;
        }

        var @params = new
        {
            textDocument = new { uri = virtualUri },
            position = new { line, character }
        };

        return await _rpc.InvokeWithParameterObjectAsync<JsonElement?>("textDocument/completion", @params, cancellationToken);
    }

    /// <summary>
    /// Gets hover information from the HTML language server.
    /// </summary>
    public async Task<JsonElement?> GetHoverAsync(string virtualUri, int line, int character, CancellationToken cancellationToken)
    {
        if (_rpc == null || !_initialized)
        {
            return null;
        }

        var @params = new
        {
            textDocument = new { uri = virtualUri },
            position = new { line, character }
        };

        return await _rpc.InvokeWithParameterObjectAsync<JsonElement?>("textDocument/hover", @params, cancellationToken);
    }

    private static string? FindHtmlLanguageServer()
    {
        // Common locations for vscode-html-language-server
        var possiblePaths = new[]
        {
            // Global npm install
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".npm-global", "lib", "node_modules", "vscode-langservers-extracted", "bin", "vscode-html-language-server"),
            // Yarn global
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".yarn", "bin", "vscode-html-language-server"),
            // Local node_modules
            Path.Combine(Directory.GetCurrentDirectory(),
                "node_modules", ".bin", "vscode-html-language-server"),
            // npm prefix
            "/usr/local/lib/node_modules/vscode-langservers-extracted/bin/vscode-html-language-server",
            "/usr/lib/node_modules/vscode-langservers-extracted/bin/vscode-html-language-server"
        };

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
            // Check with .js extension
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
                if (process.ExitCode == 0 && File.Exists(output))
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
                // Ignore - process may already be dead
            }
            _process.Dispose();
            _process = null;
        }

        return ValueTask.CompletedTask;
    }
}
