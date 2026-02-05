using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DbWatcher.Mcp.Resources;

/// <summary>
/// MCP Resources for static reference content.
/// Resources are the proper MCP pattern for static documentation/guidance content,
/// as opposed to Tools which are for actions that change state or query data.
/// </summary>
[McpServerResourceType]
public class DiagnosticResources
{
    // Path to the diagnostic strategy markdown file (copied to output directory)
    private static readonly string StrategyFilePath = Path.Combine(
        AppContext.BaseDirectory, "Resources", "sql_diagnostic_mcp_strategy.md");

    /// <summary>
    /// The SQL diagnostic methodology and workflow document.
    /// Clients should read this resource first to understand how to diagnose SQL performance issues.
    /// </summary>
    [McpServerResource(UriTemplate = "docs://dbwatcher-mcp/diagnostic-strategy")]
    [Description("SQL diagnostic methodology and workflow. Read this first to understand how to diagnose SQL performance issues.")]
    public static string DiagnosticStrategy()
    {
        // Try to read the strategy markdown file
        try
        {
            // Try multiple potential paths for the strategy file
            var possiblePaths = new[]
            {
                StrategyFilePath,
                Path.Combine(Directory.GetCurrentDirectory(), "Resources", "sql_diagnostic_mcp_strategy.md"),
                Path.Combine(AppContext.BaseDirectory, "sql_diagnostic_mcp_strategy.md")
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }
            }

            // File not found - return error message
            return GetFallbackContent();
        }
        catch (Exception ex)
        {
            // On any error reading the file, return the fallback
            return GetFallbackContent(ex.Message);
        }
    }

    private static string GetFallbackContent(string? error = null)
    {
        var errorDetail = error != null ? $"Error: {error}" : "File not found";

        return $@"> **Error**: Could not load diagnostic strategy document.
> {errorDetail}
> Expected at: {StrategyFilePath}

Please ensure `sql_diagnostic_mcp_strategy.md` is in the Resources folder.
";
    }
}
