using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;

namespace DbWatcher.Mcp.Services;

/// <summary>
/// Thread-safe Kusto connection service implementation.
/// Manages connection state and query execution for the MCP server.
/// Registered as a singleton to maintain connection state across tool invocations.
/// </summary>
public sealed class KustoConnectionService : IKustoConnectionService, IDisposable
{
    private readonly object _lock = new();
    private ICslQueryProvider? _queryProvider;
    private string? _currentClusterUri;
    private string? _currentDatabase;
    private bool _disposed;

    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _queryProvider != null;
            }
        }
    }

    public string? CurrentClusterUri
    {
        get
        {
            lock (_lock)
            {
                return _currentClusterUri;
            }
        }
    }

    public string? CurrentDatabase
    {
        get
        {
            lock (_lock)
            {
                return _currentDatabase;
            }
        }
    }

    public (bool success, string message) Connect(string clusterUri, string database)
    {
        lock (_lock)
        {
            try
            {
                // Dispose existing connection if any
                if (_queryProvider != null)
                {
                    _queryProvider.Dispose();
                    _queryProvider = null;
                    _currentClusterUri = null;
                    _currentDatabase = null;
                }

                // Build connection string with Azure CLI credential (DefaultAzureCredential)
                var connectionStringBuilder = new KustoConnectionStringBuilder(clusterUri, database)
                    .WithAadAzCliAuthentication();

                _queryProvider = KustoClientFactory.CreateCslQueryProvider(connectionStringBuilder);
                _currentClusterUri = clusterUri;
                _currentDatabase = database;

                return (true, $"Connected to Kusto cluster '{clusterUri}', database '{database}' using Entra ID authentication. Call diagnostic_strategy to see the available diagnostic workflow.");
            }
            catch (Exception ex)
            {
                return (false, $"Failed to connect to Kusto: {ex.Message}");
            }
        }
    }

    public string Disconnect()
    {
        lock (_lock)
        {
            if (_queryProvider == null)
            {
                return "Not connected to any Kusto cluster.";
            }

            _queryProvider.Dispose();
            _queryProvider = null;
            var cluster = _currentClusterUri;
            _currentClusterUri = null;
            _currentDatabase = null;

            return $"Disconnected from Kusto cluster '{cluster}'.";
        }
    }

    public (bool success, List<Dictionary<string, object?>> rows, string? error) ExecuteQuery(
        string query, 
        Dictionary<string, object>? parameters = null)
    {
        lock (_lock)
        {
            if (_queryProvider == null)
            {
                return (false, new List<Dictionary<string, object?>>(), "Not connected to Kusto. Call connect_kusto first.");
            }

            try
            {
                // Build ClientRequestProperties with parameters for safe query execution
                var clientRequestProperties = new ClientRequestProperties();
                
                if (parameters != null && parameters.Count > 0)
                {
                    foreach (var param in parameters)
                    {
                        clientRequestProperties.SetParameter(param.Key, param.Value?.ToString() ?? "");
                    }
                }

                using var reader = _queryProvider.ExecuteQuery(_currentDatabase, query, clientRequestProperties);
                var rows = new List<Dictionary<string, object?>>();
                
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
                    rows.Add(row);
                }

                return (true, rows, null);
            }
            catch (Exception ex)
            {
                return (false, new List<Dictionary<string, object?>>(), ex.Message);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            
            _queryProvider?.Dispose();
            _queryProvider = null;
            _currentClusterUri = null;
            _currentDatabase = null;
        }
    }
}
