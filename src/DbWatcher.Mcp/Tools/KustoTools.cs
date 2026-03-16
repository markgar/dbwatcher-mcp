using System.ComponentModel;
using System.Text.Json;
using Azure.Identity;
using Kusto.Data;
using Kusto.Data.Net.Client;
using ModelContextProtocol.Server;
using DbWatcher.Mcp.Services;

/// <summary>
/// MCP tools for Kusto connection management and cluster operations.
/// Uses dependency injection for thread-safe connection state management.
/// </summary>
internal class KustoTools
{
    private readonly IKustoConnectionService _connectionService;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public KustoTools(IKustoConnectionService connectionService)
    {
        _connectionService = connectionService;
    }

    [McpServerTool(Name = "connect_kusto")]
    [Description("Connect to a Kusto cluster containing database watcher telemetry. Uses Entra ID authentication via VS Code Azure Account or az login.")]
    public string ConnectKusto(
        [Description("The Kusto cluster URI (e.g., https://yourcluster.region.kusto.windows.net)")] string clusterUri,
        [Description("The database name containing database watcher telemetry")] string database)
    {
        var (success, message) = _connectionService.Connect(clusterUri, database);
        return message;
    }

    [McpServerTool(Name = "list_cluster_databases")]
    [Description("List all databases available on a Kusto cluster. Use this to discover which database contains the database watcher telemetry before connecting. Does not require an existing connection.")]
    public string ListClusterDatabases(
        [Description("The Kusto cluster URI (e.g., https://yourcluster.region.kusto.windows.net)")] string clusterUri)
    {
        try
        {
            // Create a temporary connection to query the cluster's databases
            // We connect to the default 'NetDefaultDB' which always exists
            var credential = new ChainedTokenCredential(
                new DefaultAzureCredential(),
                new InteractiveBrowserCredential());
            var connectionStringBuilder = new KustoConnectionStringBuilder(clusterUri, "NetDefaultDB")
                .WithAadAzureTokenCredentialsAuthentication(credential);

            using var tempProvider = KustoClientFactory.CreateCslQueryProvider(connectionStringBuilder);
            
            // Query to list all databases on the cluster (no projection to maximize compatibility)
            using var reader = tempProvider.ExecuteQuery(".show databases");
            
            var databases = new List<Dictionary<string, object?>>();
            var columnCount = reader.FieldCount;
            var columns = new string[columnCount];
            for (int i = 0; i < columnCount; i++)
            {
                columns[i] = reader.GetName(i);
            }

            while (reader.Read())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < columnCount; i++)
                {
                    row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                databases.Add(row);
            }

            var result = new
            {
                cluster = clusterUri,
                databases = databases.Select(d => new
                {
                    name = d.GetValueOrDefault("DatabaseName")?.ToString(),
                    pretty_name = d.GetValueOrDefault("PrettyName")?.ToString(),
                    access_mode = d.GetValueOrDefault("DatabaseAccessMode")?.ToString()
                }),
                count = databases.Count,
                next_step = "Once you identify the database containing database watcher telemetry, call connect_kusto(clusterUri, databaseName) to connect."
            };

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new 
            { 
                error = $"Failed to list databases: {ex.Message}",
                hint = "Ensure you are logged in via 'az login' or VS Code Azure Account extension."
            }, JsonOptions);
        }
    }

    [McpServerTool(Name = "connection_status")]
    [Description("Show the current Kusto connection status.")]
    public string ConnectionStatus()
    {
        if (!_connectionService.IsConnected)
        {
            return "Not connected to any Kusto cluster.";
        }

        return $"Connected to:\n  Cluster: {_connectionService.CurrentClusterUri}\n  Database: {_connectionService.CurrentDatabase}";
    }

    [McpServerTool(Name = "disconnect")]
    [Description("Disconnect from the current Kusto cluster.")]
    public string Disconnect()
    {
        return _connectionService.Disconnect();
    }

    [McpServerTool(Name = "list_monitored_databases")]
    [Description("List all SQL databases that have telemetry data in the connected database watcher store. Use this to discover which databases can be analyzed.")]
    public string ListMonitoredDatabases()
    {
        if (!_connectionService.IsConnected)
        {
            return JsonSerializer.Serialize(new { error = "Not connected to Kusto. Call connect_kusto first." }, JsonOptions);
        }

        // Query multiple telemetry tables to find all databases with data
        var query = @"
let resource_dbs = sqldb_database_resource_utilization 
    | where sample_time_utc > ago(7d)
    | distinct database_name, logical_server_name
    | extend source = 'resource_utilization';
let wait_dbs = sqldb_database_wait_stats 
    | where sample_time_utc > ago(7d)
    | distinct database_name, logical_server_name
    | extend source = 'wait_stats';
let query_dbs = sqldb_database_query_runtime_stats 
    | where collection_time_utc > ago(7d)
    | distinct database_name, logical_server_name
    | extend source = 'query_stats';
union resource_dbs, wait_dbs, query_dbs
| summarize 
    sources = make_set(source),
    first_seen = min(now())
    by database_name, logical_server_name
| extend full_name = strcat(logical_server_name, '/', database_name)
| project database_name, logical_server_name, full_name, telemetry_sources = sources
| order by logical_server_name asc, database_name asc";

        var (success, rows, error) = _connectionService.ExecuteQuery(query);
        if (!success)
        {
            return JsonSerializer.Serialize(new { error }, JsonOptions);
        }

        var result = new
        {
            connection = new { cluster = _connectionService.CurrentClusterUri, database = _connectionService.CurrentDatabase },
            databases = rows.Select(r => new
            {
                database_name = r["database_name"]?.ToString(),
                server_name = r["logical_server_name"]?.ToString(),
                full_name = r["full_name"]?.ToString(),
                telemetry_sources = r["telemetry_sources"]
            }),
            count = rows.Count,
            note = "Use the 'database_name' value when calling diagnostic tools like history_waits, history_queries, etc."
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "run_kql")]
    [Description("Execute a read-only KQL query against the connected database watcher data store. Use this for ad-hoc exploration, schema discovery (e.g., 'tablename | getschema'), or queries not covered by the curated diagnostic tools. The query runs against the connected Kusto database. Results are limited to 500 rows.")]
    public string RunKql(
        [Description("The KQL query to execute. Must be a read-only query (no .set, .append, .drop, etc.).")] string query)
    {
        if (!_connectionService.IsConnected)
        {
            return JsonSerializer.Serialize(new { error = "Not connected to Kusto. Call connect_kusto first." }, JsonOptions);
        }

        // Block management/control commands that could modify data
        var trimmed = query.TrimStart();
        if (trimmed.StartsWith('.'))
        {
            return JsonSerializer.Serialize(new { error = "Management commands (starting with '.') are not allowed. Only read-only KQL queries are supported." }, JsonOptions);
        }

        var (success, rows, error) = _connectionService.ExecuteQuery(query);
        if (!success)
        {
            return JsonSerializer.Serialize(new { error }, JsonOptions);
        }

        const int maxRows = 500;
        var truncated = rows.Count > maxRows;
        var result = new
        {
            row_count = rows.Count,
            truncated,
            truncated_to = truncated ? maxRows : (int?)null,
            rows = rows.Take(maxRows)
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
