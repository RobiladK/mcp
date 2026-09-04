// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Azure.Mcp.Server.Tests.Infrastructure;

public sealed class MonitorLogSearchServerModeTests
{
    private const string SearchTool = "monitor_workspace_log_search";
    private const string ConsolidatedTool = "get_azure_resource_and_app_health_status";
    private const string ConsolidatedSearchCommand =
        "get_azure_resource_and_app_health_status_monitor_workspace_log_search";

    private static string AzmcpPath =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "azmcp.exe" : "azmcp");

    [Theory]
    [InlineData("server start --mode all --namespace monitor --structured-output-mode duplicated")]
    [InlineData("server start --tool monitor_workspace_log_search --structured-output-mode duplicated")]
    public async Task DirectModes_ExposeSearchToolWithOutputSchema(string arguments)
    {
        await using var server = await StdioServer.StartAsync(arguments);

        // tools/list is intentionally stateless and must work without initialize.
        var result = await server.RequestAsync(1, "tools/list", new { });
        var tools = result.GetProperty("tools").EnumerateArray().ToList();
        var search = Assert.Single(tools, tool => tool.GetProperty("name").GetString() == SearchTool);
        var schema = search.GetProperty("outputSchema");

        AssertTypeIncludes(schema.GetProperty("type"), "object");
        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("rows", out var rows));
        AssertTypeIncludes(rows.GetProperty("type"), "array");
        AssertTypeIncludes(rows.GetProperty("items").GetProperty("type"), "array");

        var errorType = properties.GetProperty("error").GetProperty("type");
        AssertTypeIncludes(errorType, "object");
        AssertTypeIncludes(errorType, "null");
    }

    [Theory]
    [InlineData("server start --namespace monitor --structured-output-mode duplicated")]
    [InlineData("server start --mode namespace --namespace monitor --structured-output-mode duplicated")]
    public async Task NamespaceModes_WithMonitorFilter_DiscoverSearchCommand(string arguments)
    {
        await using var server = await StdioServer.StartAsync(arguments);
        await server.InitializeAsync();

        var listed = await server.RequestAsync(2, "tools/list", new { });
        var monitor = Assert.Single(
            listed.GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "monitor");
        AssertAggregateSchema(monitor.GetProperty("outputSchema"), includesTool: false);

        var called = await server.RequestAsync(
            3,
            "tools/call",
            new
            {
                name = "monitor",
                arguments = new { intent = "Discover Monitor log search commands.", learn = true }
            });

        Assert.Equal("tool-list", called.GetProperty("structuredContent").GetProperty("kind").GetString());
        AssertContainsCommand(called.GetProperty("structuredContent"), SearchTool);
    }

    [Fact]
    public async Task SingleMode_WithMonitorFilter_DiscoversSearchCommand()
    {
        await using var server = await StdioServer.StartAsync(
            "server start --mode single --namespace monitor --structured-output-mode duplicated");
        await server.InitializeAsync();

        var listed = await server.RequestAsync(2, "tools/list", new { });
        var azure = Assert.Single(listed.GetProperty("tools").EnumerateArray());
        Assert.Equal("azure", azure.GetProperty("name").GetString());
        AssertAggregateSchema(azure.GetProperty("outputSchema"), includesTool: true);

        var called = await server.RequestAsync(
            3,
            "tools/call",
            new
            {
                name = "azure",
                arguments = new
                {
                    intent = "Discover Monitor log search commands.",
                    tool = "monitor",
                    learn = true
                }
            });

        Assert.Equal("tool-list", called.GetProperty("structuredContent").GetProperty("kind").GetString());
        AssertContainsCommand(called.GetProperty("structuredContent"), SearchTool);
    }

    [Fact]
    public async Task ConsolidatedMode_WithMonitorFilter_DiscoversMappedSearchCommand()
    {
        await using var server = await StdioServer.StartAsync(
            "server start --mode consolidated --namespace monitor --structured-output-mode duplicated");
        await server.InitializeAsync();

        var listed = await server.RequestAsync(2, "tools/list", new { });
        var consolidated = Assert.Single(
            listed.GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == ConsolidatedTool);
        Assert.Contains(
            "search Basic or Auxiliary Log Analytics tables",
            consolidated.GetProperty("description").GetString(),
            StringComparison.OrdinalIgnoreCase);
        AssertAggregateSchema(consolidated.GetProperty("outputSchema"), includesTool: false);

        var called = await server.RequestAsync(
            3,
            "tools/call",
            new
            {
                name = ConsolidatedTool,
                arguments = new { intent = "Discover Monitor log search commands.", learn = true }
            });

        Assert.Equal("tool-list", called.GetProperty("structuredContent").GetProperty("kind").GetString());
        AssertContainsCommand(called.GetProperty("structuredContent"), ConsolidatedSearchCommand);
    }

    private static void AssertContainsCommand(JsonElement structuredContent, string expectedCommand)
    {
        var commands = structuredContent.GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("command").GetString())
            .ToList();

        Assert.Contains(expectedCommand, commands);
    }

    private static void AssertTypeIncludes(JsonElement type, string expected)
    {
        if (type.ValueKind == JsonValueKind.String)
        {
            Assert.Equal(expected, type.GetString());
            return;
        }

        Assert.Contains(
            expected,
            type.EnumerateArray().Select(item => item.GetString()));
    }

    private static void AssertAggregateSchema(JsonElement schema, bool includesTool)
    {
        var variants = schema.GetProperty("oneOf").EnumerateArray().ToList();
        var toolResult = Assert.Single(
            variants,
            variant =>
                variant.GetProperty("properties")
                    .GetProperty("kind")
                    .GetProperty("const")
                    .GetString() == "tool-result");
        var required = toolResult.GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToList();

        Assert.Contains("command", required);
        Assert.Contains("result", required);
        Assert.Equal(includesTool, required.Contains("tool"));
    }

    private sealed class StdioServer(Process process, string arguments) : IAsyncDisposable
    {
        private int _nextId = 10;

        public static async Task<StdioServer> StartAsync(string arguments)
        {
            Assert.True(File.Exists(AzmcpPath), $"Executable not found at {AzmcpPath}.");

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = AzmcpPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            Assert.NotNull(process);

            await Task.Delay(500, TestContext.Current.CancellationToken);
            return new(process, arguments);
        }

        public async Task InitializeAsync()
        {
            await RequestAsync(
                _nextId++,
                "initialize",
                new
                {
                    protocolVersion = "2025-11-25",
                    capabilities = new { },
                    clientInfo = new { name = "mode-test", version = "1.0" }
                });
            await WriteAsync(
                new { jsonrpc = "2.0", method = "notifications/initialized" },
                TestContext.Current.CancellationToken);
        }

        public async Task<JsonElement> RequestAsync(int id, string method, object parameters)
        {
            await WriteAsync(
                new { jsonrpc = "2.0", id, method, @params = parameters },
                TestContext.Current.CancellationToken);

            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            cancellationSource.CancelAfter(TimeSpan.FromSeconds(60));

            while (true)
            {
                using var cancellationRegistration = cancellationSource.Token.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // The process exited between the state check and the kill request.
                    }
                });
                var line = await process.StandardOutput.ReadLineAsync(cancellationSource.Token);
                if (line is null)
                {
                    var exitCode = process.HasExited ? process.ExitCode.ToString() : "running";
                    var standardError = process.HasExited
                        ? await process.StandardError.ReadToEndAsync()
                        : string.Empty;
                    Assert.Fail(
                        $"Server exited before responding. Arguments: {arguments}. ExitCode: {exitCode}. StdErr: {standardError}");
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var responseId) &&
                    responseId.ValueKind == JsonValueKind.Number &&
                    responseId.GetInt32() == id)
                {
                    if (root.TryGetProperty("error", out var error))
                    {
                        Assert.Fail(error.GetRawText());
                    }

                    return root.GetProperty("result").Clone();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            process.StandardInput.Close();
            using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await process.WaitForExitAsync(cancellationSource.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            }

            process.Dispose();
        }

        private async Task WriteAsync(object message, CancellationToken cancellationToken)
        {
            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(message).AsMemory(),
                cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
    }
}
