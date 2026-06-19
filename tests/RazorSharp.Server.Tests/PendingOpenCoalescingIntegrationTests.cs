using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorSharp.Dependencies;
using RazorSharp.Protocol;
using RazorSharp.Server;

namespace RazorSharp.Server.Tests;

public class PendingOpenCoalescingIntegrationTests
{
    [Fact]
    public async Task DidChangeBeforeInit_CoalescesIntoDidOpen()
    {
        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        var notifications = new List<(string Method, object? Params)>();

        server.SetForwardToRoslynNotificationOverrideForTests((method, @params) =>
        {
            notifications.Add((method, @params));
            return Task.CompletedTask;
        });

        try
        {
            var didOpen = JsonSerializer.SerializeToElement(new
            {
                textDocument = new
                {
                    uri = "file:///test.cs",
                    languageId = "c-sharp",
                    version = 1,
                    text = "abc\n123"
                }
            });

            await server.HandleDidOpenAsync(didOpen);

            var didChange = JsonSerializer.SerializeToElement(new
            {
                textDocument = new { uri = "file:///test.cs", version = 2 },
                contentChanges = new[]
                {
                    new
                    {
                        range = new
                        {
                            start = new { line = 1, character = 0 },
                            end = new { line = 1, character = 3 }
                        },
                        text = "456"
                    }
                }
            });

            await server.HandleDidChangeAsync(didChange);

            await server.HandleRoslynNotificationForTests(LspMethods.ProjectInitializationComplete, null, CancellationToken.None);

            var opens = notifications.Where(entry => entry.Method == LspMethods.TextDocumentDidOpen).ToList();
            var changes = notifications.Where(entry => entry.Method == LspMethods.TextDocumentDidChange).ToList();

            Assert.Single(opens);
            Assert.Empty(changes);

            var openParams = ToJsonElement(opens[0].Params);
            var textDocument = openParams.GetProperty("textDocument");
            Assert.Equal("abc\n456", textDocument.GetProperty("text").GetString());
            Assert.Equal(2, textDocument.GetProperty("version").GetInt32());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task DidChangeDuringDidOpenReplay_IsBufferedUntilReplayCompletes()
    {
        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        var firstReplayEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstReplay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondChangeForwarded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forwardedDidChangeVersions = new List<int>();
        var forwardedDidOpenCount = 0;
        var lockObj = new object();

        server.SetForwardToRoslynNotificationOverrideForTests(async (method, @params) =>
        {
            var json = ToJsonElement(@params);
            if (method == LspMethods.TextDocumentDidOpen)
            {
                lock (lockObj)
                {
                    forwardedDidOpenCount++;
                }
                return;
            }

            if (method != LspMethods.TextDocumentDidChange)
            {
                return;
            }

            var version = json.GetProperty("textDocument").GetProperty("version").GetInt32();
            lock (lockObj)
            {
                forwardedDidChangeVersions.Add(version);
            }

            if (version == 2)
            {
                firstReplayEntered.TrySetResult(true);
                await releaseFirstReplay.Task;
            }
            else if (version == 3)
            {
                secondChangeForwarded.TrySetResult(true);
            }
        });

        try
        {
            var didOpen = JsonSerializer.SerializeToElement(new
            {
                textDocument = new
                {
                    uri = "file:///test.cs",
                    languageId = "c-sharp",
                    version = 1,
                    text = "initial"
                }
            });

            var firstBufferedChange = JsonSerializer.SerializeToElement(new
            {
                textDocument = new { uri = "file:///test.cs", version = 2 },
                contentChanges = new
                {
                    invalid = true
                }
            });

            var secondBufferedChange = JsonSerializer.SerializeToElement(new
            {
                textDocument = new { uri = "file:///test.cs", version = 3 },
                contentChanges = new
                {
                    invalid = true
                }
            });

            await server.HandleDidOpenAsync(didOpen);
            await server.HandleDidChangeAsync(firstBufferedChange);

            var flushTask = server.HandleRoslynNotificationForTests(
                LspMethods.ProjectInitializationComplete,
                null,
                CancellationToken.None);

            await AwaitOrTimeout(firstReplayEntered.Task, 2000, "First replayed didChange was not forwarded.");

            await server.HandleDidChangeAsync(secondBufferedChange);
            await Task.Delay(100);
            Assert.False(secondChangeForwarded.Task.IsCompleted);

            releaseFirstReplay.TrySetResult(true);

            await AwaitOrTimeout(flushTask, 2000, "Pending didOpen replay did not complete.");
            await AwaitOrTimeout(secondChangeForwarded.Task, 2000, "Second didChange was not forwarded.");

            lock (lockObj)
            {
                Assert.Equal(1, forwardedDidOpenCount);
                Assert.Equal(new[] { 2, 3 }, forwardedDidChangeVersions.ToArray());
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task WorkspaceInitializationTimeout_FlushesPendingDidOpen()
    {
        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        var didOpenForwarded = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.SetInitializeParamsForTests(new RazorSharp.Protocol.Messages.InitializeParams());
        server.SetStartRoslynOverrideForTests(_ => Task.FromResult(true));
        server.SetWorkspaceInitProgressTimeoutForTests(50);
        server.SetForwardToRoslynNotificationOverrideForTests((method, @params) =>
        {
            if (method == LspMethods.TextDocumentDidOpen)
            {
                didOpenForwarded.TrySetResult(ToJsonElement(@params));
            }

            return Task.CompletedTask;
        });

        try
        {
            server.HandleInitialized();

            var didOpen = JsonSerializer.SerializeToElement(new
            {
                textDocument = new
                {
                    uri = "file:///loose.cs",
                    languageId = "c-sharp",
                    version = 1,
                    text = "class C { }"
                }
            });

            await server.HandleDidOpenAsync(didOpen);

            var forwarded = await AwaitOrTimeout(
                didOpenForwarded.Task,
                2000,
                "Pending didOpen was not flushed when workspace initialization timed out.");
            var textDocument = forwarded.GetProperty("textDocument");
            Assert.Equal("file:///loose.cs", textDocument.GetProperty("uri").GetString());
            Assert.Equal("csharp", textDocument.GetProperty("languageId").GetString());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task FastStartRequest_FlushesPendingDidOpenBeforeForwarding()
    {
        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        var didOpenForwarded = false;
        var hoverForwardedAfterDidOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.SetInitializeParamsForTests(new RazorSharp.Protocol.Messages.InitializeParams());
        server.SetForwardToRoslynNotificationOverrideForTests((method, _) =>
        {
            if (method == LspMethods.TextDocumentDidOpen)
            {
                didOpenForwarded = true;
            }

            return Task.CompletedTask;
        });
        server.SetForwardToRoslynOverrideForTests((method, _, _) =>
        {
            if (method == LspMethods.TextDocumentHover && didOpenForwarded)
            {
                hoverForwardedAfterDidOpen.TrySetResult(true);
            }

            return Task.FromResult<JsonElement?>(null);
        });

        try
        {
            var didOpen = JsonSerializer.SerializeToElement(new
            {
                textDocument = new
                {
                    uri = "file:///loose.cs",
                    languageId = "c-sharp",
                    version = 1,
                    text = "class C { }"
                }
            });

            await server.HandleDidOpenAsync(didOpen);

            var hover = JsonSerializer.SerializeToElement(new
            {
                textDocument = new { uri = "file:///loose.cs" },
                position = new { line = 0, character = 6 }
            });

            await server.HandleHoverAsync(hover, CancellationToken.None);
            await AwaitOrTimeout(
                hoverForwardedAfterDidOpen.Task,
                2000,
                "Hover was not forwarded after flushing pending didOpen.");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task FirstHoverReturningNull_IsRetriedOnceAfterDidOpen()
    {
        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var deps = new DependencyManager(loggerFactory.CreateLogger<DependencyManager>(), "test");
        var server = new RazorLanguageServer(loggerFactory, deps);
        var hoverCalls = 0;

        server.SetForwardToRoslynNotificationOverrideForTests((_, _) => Task.CompletedTask);
        server.SetForwardToRoslynOverrideForTests((method, _, _) =>
        {
            if (method != LspMethods.TextDocumentHover)
            {
                return Task.FromResult<JsonElement?>(null);
            }

            hoverCalls++;
            if (hoverCalls == 1)
            {
                return Task.FromResult<JsonElement?>(
                    JsonSerializer.SerializeToElement((object?)null));
            }

            return Task.FromResult<JsonElement?>(
                JsonSerializer.SerializeToElement(new { contents = "ready" }));
        });

        try
        {
            await server.HandleRoslynNotificationForTests(LspMethods.ProjectInitializationComplete, null, CancellationToken.None);

            var didOpen = JsonSerializer.SerializeToElement(new
            {
                textDocument = new
                {
                    uri = "file:///project/Submission.cs",
                    languageId = "c-sharp",
                    version = 1,
                    text = "class Submission { }"
                }
            });
            await server.HandleDidOpenAsync(didOpen);

            var hover = JsonSerializer.SerializeToElement(new
            {
                textDocument = new { uri = "file:///project/Submission.cs" },
                position = new { line = 0, character = 6 }
            });

            var result = await server.HandleHoverAsync(hover, CancellationToken.None);

            Assert.Equal(2, hoverCalls);
            Assert.NotNull(result);
            Assert.Equal("ready", result.Value.GetProperty("contents").GetString());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    static async Task AwaitOrTimeout(Task task, int timeoutMs, string message)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)) == task;
        Assert.True(completed, message);
    }

    static async Task<T> AwaitOrTimeout<T>(Task<T> task, int timeoutMs, string message)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)) == task;
        Assert.True(completed, message);
        return await task;
    }

    static JsonElement ToJsonElement(object? value)
    {
        if (value is JsonElement json)
        {
            return json;
        }

        return JsonSerializer.SerializeToElement(value);
    }
}
