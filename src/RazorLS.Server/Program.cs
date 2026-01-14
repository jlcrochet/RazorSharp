using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RazorLS.Dependencies;
using RazorLS.Server;

// Parse command line arguments
var logLevel = LogLevel.Information;
string? logFile = null;
string? solutionPath = null;
int? hostPid = null;
bool skipDependencyCheck = false;

for (int i = 0; i < args.Length; i++)
{
    var arg = args[i];
    var nextArg = i + 1 < args.Length ? args[i + 1] : null;

    switch (arg)
    {
        case "-l" or "--loglevel" when nextArg != null:
            if (Enum.TryParse<LogLevel>(args[++i], true, out var level))
            {
                logLevel = level;
            }
            break;
        case "-v" or "--verbose":
            logLevel = LogLevel.Debug;
            break;
        case "--logFile" when nextArg != null:
            logFile = args[++i];
            break;
        case "-s" or "--source" when nextArg != null:
            solutionPath = args[++i];
            break;
        case "-hpid" or "--hostPID" when nextArg != null:
            if (int.TryParse(args[++i], out var pid))
            {
                hostPid = pid;
            }
            break;
        case "--skip-dependency-check":
            skipDependencyCheck = true;
            break;
        case "-?" or "-h" or "--help":
            PrintHelp();
            return 0;
        case "--version":
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var versionStr = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0";
            Console.WriteLine($"RazorLS {versionStr}");
            return 0;
    }
}

// Configure services
var services = new ServiceCollection();

services.AddLogging(builder =>
{
    builder.SetMinimumLevel(logLevel);

    if (logFile != null)
    {
        // Log to file - stderr is for errors only
        builder.AddProvider(new FileLoggerProvider(logFile));
    }
    else
    {
        // Log to stderr (stdout is for LSP communication)
        builder.AddProvider(new StderrLoggerProvider());
    }
});

services.AddSingleton(sp =>
    new DependencyManager(sp.GetRequiredService<ILogger<DependencyManager>>(), skipDependencyCheck: skipDependencyCheck));

services.AddSingleton<RazorLanguageServer>();

var serviceProvider = services.BuildServiceProvider();

// Run the server
var server = serviceProvider.GetRequiredService<RazorLanguageServer>();
server.SetLogLevel(logLevel);
if (solutionPath != null)
{
    server.SetSolutionPath(solutionPath);
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Monitor host process - shutdown when parent dies
if (hostPid.HasValue)
{
    _ = Task.Run(async () =>
    {
        try
        {
            var hostProcess = System.Diagnostics.Process.GetProcessById(hostPid.Value);
            await hostProcess.WaitForExitAsync(cts.Token);
            cts.Cancel();
        }
        catch
        {
            // Host process already dead or inaccessible - shutdown
            cts.Cancel();
        }
    });
}

try
{
    await server.RunAsync(cts.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal error: {ex}");
    return 1;
}
finally
{
    await server.DisposeAsync();
}

static void PrintHelp()
{
    Console.WriteLine("""
        RazorLS - Razor Language Server

        Usage: razorls [options]

        Options:
          -s|--source <path>       Solution or directory for RazorLS to point at
          -l|--loglevel <level>    Set log level (Trace, Debug, Information, Warning, Error)
          -v|--verbose             Set log level to Debug
          --logFile <path>         Write logs to file instead of stderr
          -hpid|--hostPID <pid>    Host process ID (shutdown when host exits)
          --skip-dependency-check  Skip automatic dependency download
          -?|-h|--help             Show this help message
          --version                Show version information

        The server communicates via Language Server Protocol over stdin/stdout.
        """);
}

// Simple stderr logger provider
internal class StderrLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName);
    public void Dispose() { }
}

internal class StderrLogger : ILogger
{
    readonly string _category;
    public StderrLogger(string category) => _category = category;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var shortCategory = _category.Contains('.') ? _category[((_category.LastIndexOf('.') + 1))..] : _category;
        Console.Error.WriteLine($"[{logLevel}] {shortCategory}: {formatter(state, exception)}");
        if (exception != null)
        {
            Console.Error.WriteLine(exception.ToString());
        }
    }
}

// Simple file logger provider
internal class FileLoggerProvider : ILoggerProvider
{
    readonly StreamWriter _writer;
    readonly object _lock = new();

    public FileLoggerProvider(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer, _lock);
    public void Dispose() => _writer.Dispose();
}

internal class FileLogger : ILogger
{
    readonly string _category;
    readonly StreamWriter _writer;
    readonly object _lock;

    public FileLogger(string category, StreamWriter writer, object @lock)
    {
        _category = category;
        _writer = writer;
        _lock = @lock;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var dotIndex = _category.LastIndexOf('.');
        var shortCategory = dotIndex != -1 ? _category.Substring(dotIndex + 1) : _category;
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{logLevel}] {shortCategory}: {formatter(state, exception)}";
        lock (_lock)
        {
            _writer.WriteLine(line);
            if (exception != null)
            {
                _writer.WriteLine(exception.ToString());
            }
        }
    }
}
