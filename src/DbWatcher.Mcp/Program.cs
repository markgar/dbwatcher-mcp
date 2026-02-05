using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DbWatcher.Mcp.Resources;
using DbWatcher.Mcp.Services;

var builder = Host.CreateApplicationBuilder(args);

// Configure all logs to go to stderr (stdout is used for the MCP protocol messages).
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Register the Kusto connection service as a singleton (maintains connection state across requests)
builder.Services.AddSingleton<IKustoConnectionService, KustoConnectionService>();

// Add the MCP services: the transport to use (stdio) and the tools to register.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<KustoTools>()
    .WithTools<HistoryTools>()
    .WithResources<DiagnosticResources>();

await builder.Build().RunAsync();
