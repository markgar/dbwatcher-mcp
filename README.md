# dbwatcher-mcp

An MCP (Model Context Protocol) server for **historical performance analysis** of Azure SQL databases monitored by [database watcher](https://learn.microsoft.com/en-us/azure/azure-sql/database-watcher-overview). 

Database watcher collects telemetry from Azure SQL resources and stores it in a Kusto (ADX) cluster. This MCP server queries that telemetry to help you analyze past workloads, identify performance bottlenecks, and troubleshoot issues — all through natural language conversations with Copilot.

> **Supported targets**: Currently supports **Azure SQL Database** telemetry only. Azure SQL Managed Instance support is planned for a future release.

> **Note**: This is a read-only, forensic analysis tool. It analyzes historical telemetry data — it does not connect directly to your SQL databases or make any changes.

## Features

- **Wait Stats Analysis** - Identify bottleneck categories (CPU, IO, locks, parallelism)
- **Query Analysis** - Find top resource-consuming queries by CPU, reads, duration
- **Per-Query Wait Breakdown** - Understand what specific queries are waiting on
- **Blocking Detection** - Find sessions waiting on locks held by other sessions
- **Resource Utilization** - Analyze CPU, Data IO, and Log IO trends over time
- **Performance Counters** - Monitor PLE, buffer cache hit ratio, memory grants
- **Disk I/O Latency** - Analyze read/write latency for data and log files
- **Missing Indexes** - Get index recommendations to improve query performance
- **Parameter Sniffing Detection** - Identify queries with high execution time variance
- **Diagnostic Strategy** - Built-in methodology guide for systematic troubleshooting

## Prerequisites

- .NET 10.0 SDK (for building from source)
- Azure CLI (`az login`) or VS Code Azure Account extension for authentication
- Access to a Kusto cluster with database watcher telemetry

## Using in VS Code

### Step 1: Configure the MCP Server

Create or edit `.vscode/mcp.json` in your workspace:

```json
{
  "servers": {
    "dbwatcher": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/path/to/dbwatcher-mcp/src/DbWatcher.Mcp"
      ]
    }
  }
}
```

### Step 2: Authenticate to Azure

Before using the server, ensure you're logged in:

```bash
az login
```

Or use the VS Code Azure Account extension and sign in.

### Step 3: Start a Diagnostic Session

Open Copilot Chat in VS Code and start with:

1. **"Connect to my Kusto cluster at https://mycluster.eastus.kusto.windows.net, database 'dbwatcher'"** - Establish connection
2. **"List the monitored databases"** - See which SQL databases have telemetry

> **Tip**: The server exposes a `diagnostic-strategy` resource with the recommended diagnostic methodology. Copilot can read this automatically when you ask about troubleshooting approaches.

### Step 4: Analyze Performance

Once connected, ask questions like:

- "Analyze wait stats for my database over the last 24 hours"
- "What are the top 10 queries by CPU usage?"
- "Show me blocking events from yesterday between 2pm and 4pm"
- "Are there any missing index recommendations?"
- "Check for parameter sniffing issues"

The server includes built-in thresholds and interpretations to help you understand the results.

## Example Workflow

```
You: Connect to https://mycluster.kusto.windows.net database telemetry-db

Copilot: Connected to Kusto cluster.

You: What databases are being monitored?

Copilot: [Lists databases with telemetry]

You: Analyze wait stats for production-db

Copilot: [Shows wait stats with interpretations - e.g., "PAGEIOLATCH_SH is 35% 
         of waits, indicating memory pressure or missing indexes"]

You: Find the top queries causing those IO waits

Copilot: [Shows top queries sorted by reads with recommendations]
```

## Building from Source

```bash
# Clone the repository
git clone https://github.com/markgar/dbwatcher-mcp.git
cd dbwatcher-mcp/src/DbWatcher.Mcp

# Build
dotnet build

# Run directly (for testing)
dotnet run
```

Then configure VS Code to use the built project as shown in "Using in VS Code" above.

---

## Publishing to NuGet

```bash
# Create the NuGet package
dotnet pack -c Release

# Publish to NuGet.org
dotnet nuget push bin/Release/*.nupkg --api-key <your-api-key> --source https://api.nuget.org/v3/index.json
```

See [aka.ms/nuget/mcp/guide](https://aka.ms/nuget/mcp/guide) for the full guide.

---

## Platform Support

The MCP server is built as a self-contained application. By default configured for:
- `win-x64`, `win-arm64`
- `osx-arm64`
- `linux-x64`, `linux-arm64`, `linux-musl-x64`

## More Information

- [MCP Official Documentation](https://modelcontextprotocol.io/)
- [MCP C# SDK](https://modelcontextprotocol.github.io/csharp-sdk)
- [Database Watcher Overview](https://learn.microsoft.com/en-us/azure/azure-sql/database-watcher-overview)
- [VS Code MCP Servers](https://code.visualstudio.com/docs/copilot/chat/mcp-servers)
