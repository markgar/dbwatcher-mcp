using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using DbWatcher.Mcp.Services;

/// <summary>
/// MCP tools for historical SQL performance analysis using database watcher telemetry.
/// These tools query Kusto/ADX to analyze past workloads - safe, read-only analysis.
/// Uses dependency injection for thread-safe connection management.
/// </summary>
internal class HistoryTools
{
    private readonly IKustoConnectionService _connectionService;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public HistoryTools(IKustoConnectionService connectionService)
    {
        _connectionService = connectionService;
    }

    // List of benign waits to exclude from analysis
    private static readonly string[] BenignWaits = new[]
    {
        "WAITFOR", "LAZYWRITER_SLEEP", "SLEEP_TASK", "BROKER_TO_FLUSH",
        "CHECKPOINT_QUEUE", "CLR_AUTO_EVENT", "DISPATCHER_QUEUE_SEMAPHORE",
        "XE_DISPATCHER_WAIT", "XE_TIMER_EVENT", "SQLTRACE_BUFFER_FLUSH",
        "BROKER_EVENTHANDLER", "BROKER_RECEIVE_WAITFOR", "BROKER_TASK_STOP",
        "BROKER_TRANSMITTER", "CLR_MANUAL_EVENT", "CLR_SEMAPHORE",
        "DBMIRROR_DBM_EVENT", "DBMIRROR_DBM_MUTEX", "DBMIRRORING_CMD",
        "DIRTY_PAGE_POLL", "FT_IFTS_SCHEDULER_IDLE_WAIT", "HADR_FILESTREAM_IOMGR_IOCOMPLETION",
        "LOGMGR_QUEUE", "ONDEMAND_TASK_QUEUE", "REQUEST_FOR_DEADLOCK_SEARCH",
        "RESOURCE_QUEUE", "SERVER_IDLE_CHECK", "SLEEP_BPOOL_FLUSH",
        "SLEEP_DBSTARTUP", "SLEEP_DCOMSTARTUP", "SLEEP_MASTERDBREADY",
        "SLEEP_MASTERMDREADY", "SLEEP_MASTERUPGRADED", "SLEEP_MSDBSTARTUP",
        "SLEEP_SYSTEMTASK", "SLEEP_TEMPDBSTARTUP", "SNI_HTTP_ACCEPT",
        "SP_SERVER_DIAGNOSTICS_SLEEP", "SQLTRACE_INCREMENTAL_FLUSH_SLEEP",
        "SQLTRACE_WAIT_ENTRIES", "WAIT_FOR_RESULTS", "WAITFOR_TASKSHUTDOWN",
        "XE_BUFFERMGR_ALLPROCESSED_EVENT", "XE_LIVE_TARGET_TVF"
    };

    /// <summary>
    /// Builds a KQL declare query_parameters statement from the parameters dictionary.
    /// Maps .NET types to KQL types.
    /// </summary>
    private static string BuildParameterDeclaration(Dictionary<string, object> parameters)
    {
        if (parameters.Count == 0)
            return "";
            
        var declarations = parameters.Select(p => 
        {
            var kqlType = p.Value switch
            {
                int => "int",
                long => "long",
                double => "real",
                bool => "bool",
                DateTime => "datetime",
                _ => "string"
            };
            return $"{p.Key}:{kqlType}";
        });
        
        return $"declare query_parameters ({string.Join(", ", declarations)});";
    }

    /// <summary>
    /// Builds a parameterized time filter for sample_time_utc column.
    /// Returns the KQL filter clause and adds parameters to the dictionary.
    /// </summary>
    private static string GetTimeFilter(string? startTime, string? endTime, Dictionary<string, object> parameters)
    {
        if (string.IsNullOrEmpty(startTime) && string.IsNullOrEmpty(endTime))
        {
            return "sample_time_utc > ago(24h)";
        }
        if (!string.IsNullOrEmpty(startTime) && !string.IsNullOrEmpty(endTime))
        {
            parameters["StartTime"] = startTime;
            parameters["EndTime"] = endTime;
            return "sample_time_utc between (todatetime(StartTime) .. todatetime(EndTime))";
        }
        if (!string.IsNullOrEmpty(startTime))
        {
            parameters["StartTime"] = startTime;
            return "sample_time_utc >= todatetime(StartTime)";
        }
        parameters["EndTime"] = endTime!;
        return "sample_time_utc <= todatetime(EndTime)";
    }

    /// <summary>
    /// Builds a parameterized time filter for collection_time_utc column.
    /// Returns the KQL filter clause and adds parameters to the dictionary.
    /// </summary>
    private static string GetCollectionTimeFilter(string? startTime, string? endTime, Dictionary<string, object> parameters)
    {
        // Some tables use collection_time_utc instead of sample_time_utc
        if (string.IsNullOrEmpty(startTime) && string.IsNullOrEmpty(endTime))
        {
            return "collection_time_utc > ago(24h)";
        }
        if (!string.IsNullOrEmpty(startTime) && !string.IsNullOrEmpty(endTime))
        {
            parameters["StartTime"] = startTime;
            parameters["EndTime"] = endTime;
            return "collection_time_utc between (todatetime(StartTime) .. todatetime(EndTime))";
        }
        if (!string.IsNullOrEmpty(startTime))
        {
            parameters["StartTime"] = startTime;
            return "collection_time_utc >= todatetime(StartTime)";
        }
        parameters["EndTime"] = endTime!;
        return "collection_time_utc <= todatetime(EndTime)";
    }

    [McpServerTool(Name = "history_waits")]
    [Description("Analyze wait statistics distribution to identify bottleneck categories (CPU, IO, locks, parallelism). This is typically the first diagnostic step.")]
    public string HistoryWaits(
        [Description("The Azure SQL database name to analyze")] string databaseName,
        [Description("Start of analysis window (ISO 8601 datetime, e.g., '2026-02-03T14:00:00Z'). Defaults to 24 hours ago.")] string? startTime = null,
        [Description("End of analysis window (ISO 8601 datetime). Defaults to now.")] string? endTime = null,
        [Description("Number of top wait types to return (default: 10, max: 100)")] int topN = 10)
    {
        topN = Math.Clamp(topN, 1, 100);
        
        var parameters = new Dictionary<string, object>
        {
            ["DatabaseName"] = databaseName,
            ["TopN"] = topN
        };
        
        var benignWaitsList = string.Join("', '", BenignWaits);
        var timeFilter = GetTimeFilter(startTime, endTime, parameters);
        var paramDeclaration = BuildParameterDeclaration(parameters);

        // Using parameterized query - parameters are declared dynamically
        var query = $@"
{paramDeclaration}
let benign_waits = dynamic(['{benignWaitsList}']);
let filtered_waits = sqldb_database_wait_stats
    | where {timeFilter}
    | where database_name == DatabaseName
    | where wait_type !in (benign_waits);
let total_wait = toscalar(filtered_waits | summarize sum(wait_time_ms));
filtered_waits
| summarize 
    TotalWaitMs = sum(wait_time_ms), 
    WaitCount = sum(waiting_tasks_count)
    by wait_type
| extend PctOfTotal = round(toreal(TotalWaitMs) * 100.0 / toreal(total_wait), 2)
| order by TotalWaitMs desc
| take TopN
| project wait_type, TotalWaitMs, WaitCount, PctOfTotal";

        var (success, rows, error) = _connectionService.ExecuteQuery(query, parameters);
        if (!success)
        {
            return JsonSerializer.Serialize(new { error, hint = "Call connect_kusto first" }, JsonOptions);
        }

        // Add interpretation hints
        var interpretations = new Dictionary<string, string>
        {
            ["CXPACKET"] = "Parallelism waits - queries using parallel execution plans. Consider MAXDOP tuning or query optimization.",
            ["CXSYNC_PORT"] = "Parallelism sync waits - related to parallel query execution.",
            ["CXCONSUMER"] = "Parallelism consumer waits - typically benign, indicates parallel worker coordination.",
            ["PAGEIOLATCH_SH"] = "IO waits - reading data pages from disk. Indicates memory pressure or missing indexes.",
            ["PAGEIOLATCH_EX"] = "IO waits - writing data pages. Check for disk performance or memory pressure.",
            ["WRITELOG"] = "Transaction log write waits. Check log disk performance or transaction size.",
            ["LCK_M_S"] = "Shared lock waits - readers waiting for writers.",
            ["LCK_M_X"] = "Exclusive lock waits - writers waiting for other sessions. Blocking issue.",
            ["LCK_M_IX"] = "Intent exclusive lock waits - table-level contention.",
            ["SOS_SCHEDULER_YIELD"] = "CPU pressure - queries yielding CPU time. System may be CPU-bound.",
            ["ASYNC_NETWORK_IO"] = "Network waits - client not consuming results fast enough.",
            ["RESOURCE_SEMAPHORE"] = "Memory grant waits - queries waiting for memory. Memory pressure.",
            ["MEMORY_ALLOCATION_EXT"] = "Memory allocation waits - memory pressure on the instance."
        };

        var result = new
        {
            database_name = databaseName,
            time_window = new { start = startTime ?? "24h ago", end = endTime ?? "now" },
            total_samples = rows.Count,
            waits = rows.Select(r => new
            {
                wait_type = r["wait_type"]?.ToString(),
                total_wait_ms = r["TotalWaitMs"],
                wait_count = r["WaitCount"],
                pct_of_total = r["PctOfTotal"],
                interpretation = interpretations.TryGetValue(r["wait_type"]?.ToString() ?? "", out var hint) ? hint : null
            }),
            thresholds = new
            {
                parallelism_waits = new { healthy = "<5%", warning = "5-15%", critical = ">15%" },
                lock_waits = new { healthy = "<5%", warning = "5-15%", critical = ">15%" },
                io_waits = new { note = "High PAGEIOLATCH indicates memory pressure or missing indexes" }
            }
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "history_queries")]
    [Description("Find the top resource-consuming queries by CPU, reads, duration, or execution count.")]
    public string HistoryQueries(
        [Description("The Azure SQL database name to analyze")] string databaseName,
        [Description("Start of analysis window (ISO 8601 datetime). Defaults to 24 hours ago.")] string? startTime = null,
        [Description("End of analysis window (ISO 8601 datetime). Defaults to now.")] string? endTime = null,
        [Description("Order by: 'cpu', 'reads', 'duration', or 'executions' (default: cpu)")] string orderBy = "cpu",
        [Description("Number of top queries to return (default: 10, max: 100)")] int topN = 10)
    {
        topN = Math.Clamp(topN, 1, 100);
        
        var parameters = new Dictionary<string, object>
        {
            ["DatabaseName"] = databaseName,
            ["TopN"] = topN
        };
        
        var timeFilter = GetCollectionTimeFilter(startTime, endTime, parameters);
        var paramDeclaration = BuildParameterDeclaration(parameters);
        
        // Validate and map orderBy to prevent injection (orderColumn is not parameterizable in KQL)
        var orderColumn = orderBy.ToLower() switch
        {
            "reads" => "TotalReads",
            "duration" => "TotalDurationMs",
            "executions" => "Executions",
            _ => "TotalCpuUs"
        };

        var query = $@"
{paramDeclaration}
sqldb_database_query_runtime_stats
| where {timeFilter}
| where database_name == DatabaseName
| summarize 
    TotalCpuUs = sum(avg_cpu_time_us * count_executions),
    TotalReads = sum(avg_logical_io_reads * count_executions),
    TotalDurationUs = sum(avg_duration_us * count_executions),
    Executions = sum(count_executions),
    AvgCpuUs = avg(avg_cpu_time_us),
    AvgReads = avg(avg_logical_io_reads),
    AvgDurationUs = avg(avg_duration_us)
    by query_id, query_sql_text
| extend TotalCpuMs = round(toreal(TotalCpuUs) / 1000, 2)
| extend TotalDurationMs = round(toreal(TotalDurationUs) / 1000, 2)
| extend AvgCpuMs = round(toreal(AvgCpuUs) / 1000, 2)
| extend AvgDurationMs = round(toreal(AvgDurationUs) / 1000, 2)
| order by {orderColumn} desc
| take TopN
| project query_id, query_sql_text = substring(query_sql_text, 0, 500), 
          TotalCpuMs, AvgCpuMs, TotalReads, AvgReads = round(AvgReads, 0), 
          TotalDurationMs, AvgDurationMs, Executions";

        var (success, rows, error) = _connectionService.ExecuteQuery(query, parameters);
        if (!success)
        {
            return JsonSerializer.Serialize(new { error, hint = "Call connect_kusto first" }, JsonOptions);
        }

        var result = new
        {
            database_name = databaseName,
            time_window = new { start = startTime ?? "24h ago", end = endTime ?? "now" },
            ordered_by = orderBy,
            queries = rows.Select(r => new
            {
                query_id = r["query_id"],
                sql_text_preview = r["query_sql_text"]?.ToString(),
                total_cpu_ms = r["TotalCpuMs"],
                avg_cpu_ms = r["AvgCpuMs"],
                total_reads = r["TotalReads"],
                avg_reads = r["AvgReads"],
                total_duration_ms = r["TotalDurationMs"],
                avg_duration_ms = r["AvgDurationMs"],
                executions = r["Executions"]
            }),
            next_steps = new[]
            {
                "Use history_waits_by_query to see wait breakdown for specific queries",
                "Use history_indexes_missing to find index recommendations",
                "Consider query tuning for high CPU or read queries"
            }
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "history_waits_by_query")]
    [Description("Analyze per-query wait statistics to understand what each query is waiting on.")]
    public string HistoryWaitsByQuery(
        [Description("The Azure SQL database name to analyze")] string databaseName,
        [Description("Start of analysis window (ISO 8601 datetime). Defaults to 24 hours ago.")] string? startTime = null,
        [Description("End of analysis window (ISO 8601 datetime). Defaults to now.")] string? endTime = null,
        [Description("Filter to specific wait category (e.g., 'CPU', 'Lock', 'IO'). Optional.")] string? waitCategory = null,
        [Description("Number of top queries to return (default: 10, max: 100)")] int topN = 10)
    {
        topN = Math.Clamp(topN, 1, 100);
        
        var parameters = new Dictionary<string, object>
        {
            ["DatabaseName"] = databaseName,
            ["TopN"] = topN
        };
        
        var timeFilter = GetCollectionTimeFilter(startTime, endTime, parameters);
        
        // Build category filter with parameterization if provided
        var categoryFilter = "";
        if (!string.IsNullOrEmpty(waitCategory))
        {
            parameters["WaitCategory"] = waitCategory;
            categoryFilter = "| where wait_category == WaitCategory";
        }
        var paramDeclaration = BuildParameterDeclaration(parameters);

        var query = $@"
{paramDeclaration}
sqldb_database_query_wait_stats
| where {timeFilter}
| where database_name == DatabaseName
{categoryFilter}
| summarize 
    TotalWaitTimeMs = sum(total_query_wait_time_ms),
    AvgWaitTimeMs = avg(avg_query_wait_time_ms),
    WaitCount = sum(waiting_tasks_count)
    by query_id, wait_category
| order by TotalWaitTimeMs desc
| take TopN
| project query_id, wait_category, TotalWaitTimeMs, AvgWaitTimeMs = round(AvgWaitTimeMs, 2), WaitCount";

        var (success, rows, error) = _connectionService.ExecuteQuery(query, parameters);
        if (!success)
        {
            return JsonSerializer.Serialize(new { error, hint = "Call connect_kusto first" }, JsonOptions);
        }

        var result = new
        {
            database_name = databaseName,
            time_window = new { start = startTime ?? "24h ago", end = endTime ?? "now" },
            wait_category_filter = waitCategory ?? "all",
            query_waits = rows.Select(r => new
            {
                query_id = r["query_id"],
                wait_category = r["wait_category"]?.ToString(),
                total_wait_time_ms = r["TotalWaitTimeMs"],
                avg_wait_time_ms = r["AvgWaitTimeMs"],
                wait_count = r["WaitCount"]
            }),
            wait_categories = new
            {
                CPU = "Query execution time on CPU",
                Lock = "Waiting for locks - blocking",
                IO = "Waiting for data pages from disk",
                Memory = "Waiting for memory grants",
                Network = "Waiting for client to consume results",
                Parallelism = "Coordination between parallel workers"
            }
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "history_blocking")]
    [Description("Find blocking events where sessions were waiting on locks held by other sessions.")]
    public string HistoryBlocking(
        [Description("The Azure SQL database name to analyze")] string databaseName,
        [Description("Start of analysis window (ISO 8601 datetime). Defaults to 24 hours ago.")] string? startTime = null,
        [Description("End of analysis window (ISO 8601 datetime). Defaults to now.")] string? endTime = null,
        [Description("Minimum estimated blocking duration in seconds to include (default: 30)")] int minDurationSec = 30)
    {
        var parameters = new Dictionary<string, object>
        {
            ["DatabaseName"] = databaseName,
            ["MinDurationSec"] = minDurationSec
        };
        
        var timeFilter = GetTimeFilter(startTime, endTime, parameters);
        var paramDeclaration = BuildParameterDeclaration(parameters);

        var query = $@"
{paramDeclaration}
sqldb_database_active_sessions
| where {timeFilter}
| where database_name == DatabaseName
| where blocking_session_id > 0
| summarize 
    BlockedSamples = count(),
    EstimatedBlockedTimeSec = count() * 30,
    FirstSeen = min(sample_time_utc),
    LastSeen = max(sample_time_utc),
    WaitTypes = make_set(wait_type)
    by session_id, blocking_session_id
| where EstimatedBlockedTimeSec >= MinDurationSec
| order by EstimatedBlockedTimeSec desc
| take 20";

        var (success, rows, error) = _connectionService.ExecuteQuery(query, parameters);
        if (!success)
        {
            return JsonSerializer.Serialize(new { error, hint = "Call connect_kusto first" }, JsonOptions);
        }

        var result = new
        {
            database_name = databaseName,
            time_window = new { start = startTime ?? "24h ago", end = endTime ?? "now" },
            min_duration_filter_sec = minDurationSec,
            blocking_events = rows.Select(r => new
            {
                blocked_session_id = r["session_id"],
                blocking_session_id = r["blocking_session_id"],
                estimated_blocked_time_sec = r["EstimatedBlockedTimeSec"],
                blocked_samples = r["BlockedSamples"],
                first_seen = r["FirstSeen"],
                last_seen = r["LastSeen"],
                wait_types = r["WaitTypes"]
            }),
            note = "Estimated blocking time is based on 30-second sample intervals. Actual blocking duration may vary.",
            recommendations = rows.Count > 0 ? new[]
            {
                "Review queries from blocking sessions for optimization",
                "Consider shorter transactions or different isolation levels",
                "Check for missing indexes causing long-running queries"
            } : new[] { "No significant blocking detected in this time window" }
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "history_resources")]
    [Description("Analyze CPU, Data IO, and Log IO utilization over time to identify resource bottlenecks.")]
    public string HistoryResources(
        [Description("The Azure SQL database name to analyze")] string databaseName,
        [Description("Start of analysis window (ISO 8601 datetime). Defaults to 24 hours ago.")] string? startTime = null,
        [Description("End of analysis window (ISO 8601 datetime). Defaults to now.")] string? endTime = null,
        [Description("Time granularity: 'sample' (15s), 'minute', 'hour' (default: minute)")] string granularity = "minute")
    {
        var parameters = new Dictionary<string, object>
        {
            ["DatabaseName"] = databaseName
        };
        
        var timeFilter = GetTimeFilter(startTime, endTime, parameters);
        
        // Validate granularity to prevent injection (bin size is not parameterizable)
        var binSize = granularity.ToLower() switch
        {
            "sample" => "15s",
            "hour" => "1h",
            _ => "1m"
        };
        var paramDeclaration = BuildParameterDeclaration(parameters);

        var query = $@"
{paramDeclaration}
sqldb_database_resource_utilization
| where {timeFilter}
| where database_name == DatabaseName
| summarize 
    AvgCpu = round(avg(avg_cpu_percent), 2),
    MaxCpu = round(max(avg_cpu_percent), 2),
    AvgDataIO = round(avg(avg_data_io_percent), 2),
    MaxDataIO = round(max(avg_data_io_percent), 2),
    AvgLogIO = round(avg(avg_log_write_percent), 2),
    MaxLogIO = round(max(avg_log_write_percent), 2),
    Samples = count()
    by bin(sample_time_utc, {binSize})
| order by sample_time_utc asc";

        var (success, rows, error) = _connectionService.ExecuteQuery(query, parameters);
        if (!success)
        {
            return JsonSerializer.Serialize(new { error, hint = "Call connect_kusto first" }, JsonOptions);
        }

        // Calculate overall stats
        double avgCpu = 0, maxCpu = 0, avgDataIO = 0, maxDataIO = 0, avgLogIO = 0, maxLogIO = 0;
        if (rows.Count > 0)
        {
            avgCpu = rows.Average(r => Convert.ToDouble(r["AvgCpu"] ?? 0));
            maxCpu = rows.Max(r => Convert.ToDouble(r["MaxCpu"] ?? 0));
            avgDataIO = rows.Average(r => Convert.ToDouble(r["AvgDataIO"] ?? 0));
            maxDataIO = rows.Max(r => Convert.ToDouble(r["MaxDataIO"] ?? 0));
            avgLogIO = rows.Average(r => Convert.ToDouble(r["AvgLogIO"] ?? 0));
            maxLogIO = rows.Max(r => Convert.ToDouble(r["MaxLogIO"] ?? 0));
        }

        var result = new
        {
            database_name = databaseName,
            time_window = new { start = startTime ?? "24h ago", end = endTime ?? "now" },
            granularity,
            summary = new
            {
                cpu = new { avg = Math.Round(avgCpu, 2), max = Math.Round(maxCpu, 2) },
                data_io = new { avg = Math.Round(avgDataIO, 2), max = Math.Round(maxDataIO, 2) },
                log_io = new { avg = Math.Round(avgLogIO, 2), max = Math.Round(maxLogIO, 2) }
            },
            time_series = rows.Take(100).Select(r => new  // Limit to 100 points
            {
                time = r["sample_time_utc"],
                cpu_avg = r["AvgCpu"],
                cpu_max = r["MaxCpu"],
                data_io_avg = r["AvgDataIO"],
                data_io_max = r["MaxDataIO"],
                log_io_avg = r["AvgLogIO"],
                log_io_max = r["MaxLogIO"]
            }),
            thresholds = new
            {
                cpu = new { healthy = "<70%", warning = "70-85%", critical = ">85%" },
                data_io = new { healthy = "<70%", warning = "70-85%", critical = ">85%" },
                log_io = new { healthy = "<70%", warning = "70-85%", critical = ">85%" }
            },
            assessment = new
            {
                cpu = maxCpu > 85 ? "CRITICAL" : maxCpu > 70 ? "WARNING" : "HEALTHY",
                data_io = maxDataIO > 85 ? "CRITICAL" : maxDataIO > 70 ? "WARNING" : "HEALTHY",
                log_io = maxLogIO > 85 ? "CRITICAL" : maxLogIO > 70 ? "WARNING" : "HEALTHY"
            }
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "history_counters")]
    [Description("Analyze key performance counters like Page Life Expectancy (PLE) and Batch Requests/sec.")]
    public string HistoryCounters(
        [Description("The Azure SQL database name to analyze")] string databaseName,
        [Description("Start of analysis window (ISO 8601 datetime). Defaults to 24 hours ago.")] string? startTime = null,
        [Description("End of analysis window (ISO 8601 datetime). Defaults to now.")] string? endTime = null)
    {
        var parameters = new Dictionary<string, object>
        {
            ["DatabaseName"] = databaseName
        };
        
        var timeFilter = GetTimeFilter(startTime, endTime, parameters);
        var paramDeclaration = BuildParameterDeclaration(parameters);

        var query = $@"
{paramDeclaration}
sqldb_database_performance_counters_common
| where {timeFilter}
| where database_name == DatabaseName
| where counter_name in ('Page life expectancy', 'Batch Requests/sec', 'Buffer cache hit ratio', 
                          'Memory Grants Pending', 'SQL Compilations/sec', 'SQL Re-Compilations/sec')
| summarize 
    AvgValue = round(avg(cntr_value), 2),
    MinValue = round(min(cntr_value), 2),
    MaxValue = round(max(cntr_value), 2),
    Samples = count()
    by counter_name
| order by counter_name asc";

        var (success, rows, error) = _connectionService.ExecuteQuery(query, parameters);
        if (!success)
        {
            return JsonSerializer.Serialize(new { error, hint = "Call connect_kusto first" }, JsonOptions);
        }

        var countersDict = rows.ToDictionary(
            r => r["counter_name"]?.ToString() ?? "",
            r => new { avg = r["AvgValue"], min = r["MinValue"], max = r["MaxValue"] }
        );

        var result = new
        {
            database_name = databaseName,
            time_window = new { start = startTime ?? "24h ago", end = endTime ?? "now" },
            counters = rows.Select(r => new
            {
                counter_name = r["counter_name"]?.ToString(),
                avg_value = r["AvgValue"],
                min_value = r["MinValue"],
                max_value = r["MaxValue"],
                samples = r["Samples"]
            }),
            thresholds = new
            {
                page_life_expectancy = new { healthy = ">1000s", warning = "300-1000s", critical = "<300s" },
                buffer_cache_hit_ratio = new { healthy = ">95%", warning = "90-95%", critical = "<90%" },
                memory_grants_pending = new { healthy = "0", warning = "1-5", critical = ">5" }
            },
            interpretation = new
            {
                page_life_expectancy = "How long pages stay in buffer cache. Low values indicate memory pressure.",
                batch_requests_sec = "Workload intensity - queries being executed per second.",
                buffer_cache_hit_ratio = "Percentage of reads served from memory. Low values mean disk reads.",
                memory_grants_pending = "Queries waiting for memory. Should always be 0.",
                sql_compilations = "Query plan compilations. High values may indicate plan cache issues."
            }
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "history_disk")]
    [Description("Analyze disk I/O latency for data and log files.")]
    public string HistoryDisk(
        [Description("The Azure SQL database name to analyze")] string databaseName,
        [Description("Start of analysis window (ISO 8601 datetime). Defaults to 24 hours ago.")] string? startTime = null,
        [Description("End of analysis window (ISO 8601 datetime). Defaults to now.")] string? endTime = null)
    {
        var parameters = new Dictionary<string, object>
        {
            ["DatabaseName"] = databaseName
        };
        
        var timeFilter = GetTimeFilter(startTime, endTime, parameters);
        var paramDeclaration = BuildParameterDeclaration(parameters);

        var query = $@"
{paramDeclaration}
sqldb_database_storage_io
| where {timeFilter}
| where database_name == DatabaseName
| summarize 
    AvgReadLatencyMs = round(avg(io_stall_read_ms / case(num_of_reads == 0, 1.0, toreal(num_of_reads))), 2),
    MaxReadLatencyMs = round(max(io_stall_read_ms / case(num_of_reads == 0, 1.0, toreal(num_of_reads))), 2),
    AvgWriteLatencyMs = round(avg(io_stall_write_ms / case(num_of_writes == 0, 1.0, toreal(num_of_writes))), 2),
    MaxWriteLatencyMs = round(max(io_stall_write_ms / case(num_of_writes == 0, 1.0, toreal(num_of_writes))), 2),
    TotalReads = sum(num_of_reads),
    TotalWrites = sum(num_of_writes),
    TotalReadBytes = sum(num_of_bytes_read),
    TotalWriteBytes = sum(num_of_bytes_written)
    by file_type
| order by file_type asc";

        var (success, rows, error) = _connectionService.ExecuteQuery(query, parameters);
        if (!success)
        {
            return JsonSerializer.Serialize(new { error, hint = "Call connect_kusto first" }, JsonOptions);
        }

        double maxReadLatency = 0, maxWriteLatency = 0;
        if (rows.Count > 0)
        {
            maxReadLatency = rows.Max(r => Convert.ToDouble(r["MaxReadLatencyMs"] ?? 0));
            maxWriteLatency = rows.Max(r => Convert.ToDouble(r["MaxWriteLatencyMs"] ?? 0));
        }

        var result = new
        {
            database_name = databaseName,
            time_window = new { start = startTime ?? "24h ago", end = endTime ?? "now" },
            io_by_file_type = rows.Select(r => new
            {
                file_type = r["file_type"]?.ToString(),
                avg_read_latency_ms = r["AvgReadLatencyMs"],
                max_read_latency_ms = r["MaxReadLatencyMs"],
                avg_write_latency_ms = r["AvgWriteLatencyMs"],
                max_write_latency_ms = r["MaxWriteLatencyMs"],
                total_reads = r["TotalReads"],
                total_writes = r["TotalWrites"],
                total_read_gb = Math.Round(Convert.ToDouble(r["TotalReadBytes"] ?? 0) / 1073741824, 2),
                total_write_gb = Math.Round(Convert.ToDouble(r["TotalWriteBytes"] ?? 0) / 1073741824, 2)
            }),
            thresholds = new
            {
                read_latency_ms = new { excellent = "<5", good = "5-10", warning = "10-20", critical = ">20" },
                write_latency_ms = new { excellent = "<5", good = "5-10", warning = "10-20", critical = ">20" }
            },
            assessment = new
            {
                read_latency = maxReadLatency > 20 ? "CRITICAL" : maxReadLatency > 10 ? "WARNING" : maxReadLatency > 5 ? "GOOD" : "EXCELLENT",
                write_latency = maxWriteLatency > 20 ? "CRITICAL" : maxWriteLatency > 10 ? "WARNING" : maxWriteLatency > 5 ? "GOOD" : "EXCELLENT"
            }
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "history_indexes_missing")]
    [Description("Find missing index recommendations that could improve query performance.")]
    public string HistoryIndexesMissing(
        [Description("The Azure SQL database name to analyze")] string databaseName,
        [Description("Start of analysis window (ISO 8601 datetime). Defaults to 24 hours ago.")] string? startTime = null,
        [Description("End of analysis window (ISO 8601 datetime). Defaults to now.")] string? endTime = null,
        [Description("Number of top recommendations to return (default: 10, max: 100)")] int topN = 10)
    {
        topN = Math.Clamp(topN, 1, 100);
        
        var parameters = new Dictionary<string, object>
        {
            ["DatabaseName"] = databaseName,
            ["TopN"] = topN
        };
        
        var timeFilter = GetTimeFilter(startTime, endTime, parameters);
        var paramDeclaration = BuildParameterDeclaration(parameters);

        var query = $@"
{paramDeclaration}
sqldb_database_missing_indexes
| where {timeFilter}
| where database_name == DatabaseName
| extend table_name = strcat(schema_name, '.', object_name)
| summarize 
    AvgUserImpact = round(avg(avg_user_impact), 2),
    TotalUserSeeks = sum(user_seeks),
    TotalUserScans = sum(user_scans),
    LastSeen = max(sample_time_utc),
    SampleCount = count()
    by table_name, equality_columns, inequality_columns, included_columns
| extend ImpactScore = AvgUserImpact * (TotalUserSeeks + TotalUserScans)
| order by ImpactScore desc
| take TopN";

        var (success, rows, error) = _connectionService.ExecuteQuery(query, parameters);
        if (!success)
        {
            return JsonSerializer.Serialize(new { error, hint = "Call connect_kusto first" }, JsonOptions);
        }

        var result = new
        {
            database_name = databaseName,
            time_window = new { start = startTime ?? "24h ago", end = endTime ?? "now" },
            missing_indexes = rows.Select(r => new
            {
                table_name = r["table_name"]?.ToString(),
                equality_columns = r["equality_columns"]?.ToString(),
                inequality_columns = r["inequality_columns"]?.ToString(),
                included_columns = r["included_columns"]?.ToString(),
                avg_user_impact_pct = r["AvgUserImpact"],
                total_seeks = r["TotalUserSeeks"],
                total_scans = r["TotalUserScans"],
                impact_score = r["ImpactScore"],
                last_seen = r["LastSeen"],
                sample_count = r["SampleCount"]
            }),
            notes = new[]
            {
                "Impact score = avg_user_impact * (seeks + scans)",
                "Higher impact scores indicate more beneficial indexes",
                "Consider consolidating similar index recommendations",
                "Test index changes in non-production first"
            }
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "history_parameter_sniffing")]
    [Description("Detect queries with high execution time variance, which may indicate parameter sniffing issues.")]
    public string HistoryParameterSniffing(
        [Description("The Azure SQL database name to analyze")] string databaseName,
        [Description("Start of analysis window (ISO 8601 datetime). Defaults to 24 hours ago.")] string? startTime = null,
        [Description("End of analysis window (ISO 8601 datetime). Defaults to now.")] string? endTime = null,
        [Description("Max-to-average ratio threshold to flag as potential issue (default: 10)")] double varianceThreshold = 10,
        [Description("Minimum executions to consider (default: 100)")] int minExecutions = 100)
    {
        var parameters = new Dictionary<string, object>
        {
            ["DatabaseName"] = databaseName,
            ["VarianceThreshold"] = varianceThreshold,
            ["MinExecutions"] = minExecutions
        };
        
        var timeFilter = GetCollectionTimeFilter(startTime, endTime, parameters);
        var paramDeclaration = BuildParameterDeclaration(parameters);

        var query = $@"
{paramDeclaration}
sqldb_database_query_runtime_stats
| where {timeFilter}
| where database_name == DatabaseName
| summarize 
    AvgDurationUs = avg(avg_duration_us),
    MaxDurationUs = max(max_duration_us),
    MinDurationUs = min(min_duration_us),
    AvgCpuUs = avg(avg_cpu_time_us),
    MaxCpuUs = max(max_cpu_time_us),
    Executions = sum(count_executions)
    by query_id, query_sql_text
| where Executions >= MinExecutions
| extend DurationVariance = MaxDurationUs / AvgDurationUs
| extend CpuVariance = MaxCpuUs / AvgCpuUs
| where DurationVariance >= VarianceThreshold or CpuVariance >= VarianceThreshold
| order by DurationVariance desc
| take 20
| project query_id, query_sql_text = substring(query_sql_text, 0, 500),
          AvgDurationMs = round(AvgDurationUs / 1000, 2),
          MaxDurationMs = round(MaxDurationUs / 1000, 2),
          MinDurationMs = round(MinDurationUs / 1000, 2),
          DurationVariance = round(DurationVariance, 2),
          AvgCpuMs = round(AvgCpuUs / 1000, 2),
          MaxCpuMs = round(MaxCpuUs / 1000, 2),
          CpuVariance = round(CpuVariance, 2),
          Executions";

        var (success, rows, error) = _connectionService.ExecuteQuery(query, parameters);
        if (!success)
        {
            return JsonSerializer.Serialize(new { error, hint = "Call connect_kusto first" }, JsonOptions);
        }

        var result = new
        {
            database_name = databaseName,
            time_window = new { start = startTime ?? "24h ago", end = endTime ?? "now" },
            variance_threshold = varianceThreshold,
            min_executions = minExecutions,
            potential_parameter_sniffing = rows.Select(r => new
            {
                query_id = r["query_id"],
                sql_text_preview = r["query_sql_text"]?.ToString(),
                avg_duration_ms = r["AvgDurationMs"],
                max_duration_ms = r["MaxDurationMs"],
                min_duration_ms = r["MinDurationMs"],
                duration_variance = r["DurationVariance"],
                avg_cpu_ms = r["AvgCpuMs"],
                max_cpu_ms = r["MaxCpuMs"],
                cpu_variance = r["CpuVariance"],
                executions = r["Executions"]
            }),
            explanation = "High max-to-average ratio indicates the same query has very different execution times, often caused by parameter sniffing where a query plan optimized for one parameter value performs poorly for others.",
            recommendations = rows.Count > 0 ? new[]
            {
                "Consider using OPTION (RECOMPILE) for affected queries",
                "Use OPTIMIZE FOR hints to provide typical parameter values",
                "Consider plan guides or query store plan forcing",
                "Review parameter data distribution and consider query redesign"
            } : new[] { "No queries with high variance detected in this time window" }
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }
}
