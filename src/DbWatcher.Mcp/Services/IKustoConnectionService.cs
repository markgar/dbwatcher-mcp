using Kusto.Data.Common;

namespace DbWatcher.Mcp.Services;

/// <summary>
/// Interface for managing Kusto cluster connections.
/// Provides thread-safe connection state management for MCP tools.
/// </summary>
public interface IKustoConnectionService
{
    /// <summary>
    /// Gets whether there is an active connection to a Kusto cluster.
    /// </summary>
    bool IsConnected { get; }
    
    /// <summary>
    /// Gets the current cluster URI, or null if not connected.
    /// </summary>
    string? CurrentClusterUri { get; }
    
    /// <summary>
    /// Gets the current database name, or null if not connected.
    /// </summary>
    string? CurrentDatabase { get; }
    
    /// <summary>
    /// Connects to a Kusto cluster using Entra ID (Azure CLI) authentication.
    /// </summary>
    /// <param name="clusterUri">The Kusto cluster URI</param>
    /// <param name="database">The database name</param>
    /// <returns>A result indicating success or failure with a message</returns>
    (bool success, string message) Connect(string clusterUri, string database);
    
    /// <summary>
    /// Disconnects from the current Kusto cluster.
    /// </summary>
    /// <returns>A message indicating the result</returns>
    string Disconnect();
    
    /// <summary>
    /// Executes a KQL query with optional parameterized inputs.
    /// </summary>
    /// <param name="query">The KQL query with parameter placeholders</param>
    /// <param name="parameters">Optional dictionary of parameter names and values</param>
    /// <returns>A tuple with success flag, result rows, and error message if failed</returns>
    (bool success, List<Dictionary<string, object?>> rows, string? error) ExecuteQuery(
        string query, 
        Dictionary<string, object>? parameters = null);
}
