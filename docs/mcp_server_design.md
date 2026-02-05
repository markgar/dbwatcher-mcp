# SQL Server Diagnostic MCP Server — Design Document

## Overview

This MCP server provides tools for diagnosing Azure SQL performance issues using telemetry data from **database watcher** stored in Azure Data Explorer (Kusto) or Microsoft Fabric Real-Time Analytics.

The server uses **Entra ID authentication** exclusively — no passwords or connection strings required. Users authenticate once through VS Code (Azure Account extension) or Azure CLI, and the MCP server automatically uses those cached credentials.

---

## Implementation Phases

### Phase 1: Historical Analysis (Current Scope) ✅

**Goal:** Safe, read-only analysis of past workloads and test runs using database watcher telemetry.

| Aspect | Details |
|--------|---------|
| Data source | Kusto (database watcher telemetry) |
| Risk | Zero — querying a separate telemetry store, not production |
| Use case | Post-mortem analysis, test run evaluation, trend analysis |
| Tools | `connect_kusto`, `history_*` tools |

### Phase 2: Live Diagnostics (Future)

**Goal:** Real-time diagnostics for active incidents by querying SQL Server DMVs directly.

| Aspect | Details |
|--------|---------|
| Data source | Azure SQL DMVs (direct connection) |
| Risk | Low but non-zero — running queries on production |
| Use case | Active incident triage, "something is slow right now" |
| Tools | `connect_sql`, `live_*` tools |
| Status | **Not implemented** — documented for future reference |

---

## Authentication

All authentication uses **Entra ID** via `DefaultAzureCredential`. No configuration file needed.

### Prerequisites (One-Time Setup)

User must be authenticated via one of:
1. **VS Code Azure Account extension** — sign in through VS Code
2. **Azure CLI** — run `az login`

The MCP server automatically discovers cached credentials.

### How It Works

```
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│    VS Code      │     │   MCP Server    │     │     Kusto       │
│  (user signed   │     │                 │     │                 │
│   into Azure)   │     │                 │     │                 │
└────────┬────────┘     └────────┬────────┘     └────────┬────────┘
         │                       │                       │
         │                       │  connect_kusto(...)   │
         │                       │───────────────────────>
         │   Token request       │                       │
         │<──────────────────────│  DefaultAzureCredential
         │                       │  finds VS Code token  │
         │   Token               │                       │
         │──────────────────────>│  Authenticated        │
         │                       │<──────────────────────│
         │                       │                       │
         │                       │  history_waits(...)   │
         │                       │───────────────────────>
         │                       │  (reuses connection)  │
```

---

## Connection Workflow

The connection workflow guides users from having only partial information (e.g., just a cluster URI) to being ready for diagnostic analysis. This workflow should be followed by consuming agents.

### Workflow Steps

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                         CONNECTION WORKFLOW                                      │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                  │
│  Step 1: Discover Kusto Databases (if database name unknown)                    │
│  ─────────────────────────────────────────────────────────                      │
│  Tool: list_cluster_databases(cluster_uri)                                      │
│  Purpose: List all databases on the Kusto cluster                               │
│  When: User knows cluster URI but not the database name                         │
│                                                                                  │
│                              ↓                                                   │
│                                                                                  │
│  Step 2: Connect to Kusto                                                       │
│  ────────────────────────                                                       │
│  Tool: connect_kusto(cluster_uri, database)                                     │
│  Purpose: Establish authenticated connection to telemetry store                 │
│  When: User provides cluster URI and database name                              │
│                                                                                  │
│                              ↓                                                   │
│                                                                                  │
│  Step 3: Discover SQL Databases with Telemetry                                  │
│  ─────────────────────────────────────────────                                  │
│  Tool: list_monitored_databases()                                               │
│  Purpose: Find which SQL databases have telemetry data available                │
│  When: Always call after connecting to help user choose target database         │
│                                                                                  │
│                              ↓                                                   │
│                                                                                  │
│  Step 4: Begin Diagnostic Analysis                                              │
│  ─────────────────────────────────                                              │
│  Tools: history_waits, history_queries, history_resources, etc.                 │
│  Purpose: Analyze performance using the database_name from Step 3               │
│                                                                                  │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### Common Scenarios

| User Says | Agent Action |
|-----------|--------------|
| "I want to diagnose SQL performance" | Ask for Kusto cluster URI. If they don't know the database, use `list_cluster_databases` first. |
| "Connect to my database watcher at https://xyz.kusto.windows.net" | Use `list_cluster_databases` to show available databases, then `connect_kusto` |
| "I'm connected, what databases can I analyze?" | Use `list_monitored_databases` to show SQL databases with telemetry |
| "Analyze the MyAppDB database" | Ensure connected, then use `history_waits(databaseName: "MyAppDB")` to begin |

### Information Required

| Level | Information | How to Obtain |
|-------|-------------|---------------|
| Kusto Cluster | Cluster URI (e.g., `https://xxx.region.kusto.windows.net`) | User provides, or check database watcher configuration in Azure portal |
| Kusto Database | Database name on the cluster | `list_cluster_databases` or user provides |
| SQL Database | SQL database name to analyze | `list_monitored_databases` shows all databases with telemetry |

---

## Tool Reference

### Connection Tools

#### `list_cluster_databases`

Lists all databases available on a Kusto cluster. Use this to discover which database contains the database watcher telemetry **before** establishing a full connection. This is useful when the user knows their cluster URI but not the database name.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `cluster_uri` | string | Yes | Kusto cluster URI (e.g., `https://mycluster.westus2.kusto.windows.net`) |

**Returns:**
```json
{
  "cluster": "https://mycluster.westus2.kusto.windows.net",
  "databases": [
    { "name": "sql_monitoring", "pretty_name": "SQL Monitoring", "access_mode": "ReadWrite" },
    { "name": "TestDB", "pretty_name": null, "access_mode": "ReadWrite" }
  ],
  "count": 2,
  "next_step": "Once you identify the database containing database watcher telemetry, call connect_kusto(clusterUri, databaseName) to connect."
}
```

---

#### `connect_kusto`

Establishes a connection to a Kusto cluster containing database watcher telemetry.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `cluster_uri` | string | Yes | Kusto cluster URI (e.g., `https://mycluster.westus2.kusto.windows.net`) |
| `database` | string | Yes | Kusto database name |

**Returns:**
```json
{
  "status": "connected",
  "cluster": "https://mycluster.westus2.kusto.windows.net",
  "database": "sql_monitoring",
  "authenticated_as": "user@contoso.com",
  "available_tools": ["history_waits", "history_queries", "history_blocking", "..."]
}
```

**Error (not authenticated):**
```json
{
  "status": "error",
  "error": "authentication_failed",
  "message": "No Azure credentials found. Sign in via VS Code Azure Account extension or run 'az login'."
}
```

---

#### `list_monitored_databases`

Lists all SQL databases that have telemetry data in the connected database watcher store. Use this **after** connecting to discover which SQL databases can be analyzed.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| (none) | | | Requires active Kusto connection |

**Returns:**
```json
{
  "connection": { "cluster": "https://mycluster.westus2.kusto.windows.net", "database": "sql_monitoring" },
  "databases": [
    { "database_name": "MyAppDB", "server_name": "myserver", "full_name": "myserver/MyAppDB", "telemetry_sources": ["resource_utilization", "wait_stats", "query_stats"] }
  ],
  "count": 1,
  "note": "Use the 'database_name' value when calling diagnostic tools like history_waits, history_queries, etc."
}
```

---

#### `connections`

Lists active connections and their status.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| (none) | | | |

**Returns:**
```json
{
  "kusto": {
    "connected": true,
    "cluster": "https://mycluster.westus2.kusto.windows.net",
    "database": "sql_monitoring",
    "authenticated_as": "user@contoso.com"
  },
  "sql": {
    "connected": false,
    "note": "Live SQL connection not implemented in Phase 1"
  }
}
```

---

#### `disconnect`

Closes the Kusto connection.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| (none) | | | |

**Returns:**
```json
{
  "status": "disconnected"
}
```

---

### Meta Tool

#### `diagnostic_strategy`

Returns the diagnostic methodology, workflow phases, decision logic, and guidance for using the other tools.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| (none) | | | |

**Returns:**
```json
{
  "current_phase": "Phase 1 - Historical Analysis Only",
  "connection_required": "Call connect_kusto(cluster_uri, database) first",
  "workflow": [
    "1. connect_kusto → establish connection to telemetry store",
    "2. history_waits → identify bottleneck category (where did time go?)",
    "3. history_queries → find the expensive queries",
    "4. history_waits_by_query → per-query wait breakdown",
    "5. history_blocking → were there blocking events?",
    "6. history_resources → CPU/IO/memory trends",
    "7. history_indexes_missing → optimization opportunities"
  ],
  "phases": {
    "1": { "name": "Wait Stats", "goal": "Identify bottleneck category", "tools": ["history_waits", "history_waits_by_query"] },
    "2": { "name": "Query Analysis", "goal": "Find the expensive queries", "tools": ["history_queries", "history_blocking"] },
    "3": { "name": "Resource Health", "goal": "Check capacity limits", "tools": ["history_resources", "history_counters", "history_disk"] },
    "4": { "name": "Indexing", "goal": "Find optimization opportunities", "tools": ["history_indexes_missing", "history_parameter_sniffing"] }
  },
  "thresholds": {
    "CXPACKET_pct": { "healthy": "<5%", "warning": "5-15%", "critical": ">15%" },
    "LCK_M_pct": { "healthy": "<5%", "warning": "5-15%", "critical": ">15%" },
    "PLE": { "healthy": ">1000s", "warning": "300-1000s", "critical": "<300s" },
    "disk_latency_ms": { "healthy": "<5", "warning": "10-20", "critical": ">20" }
  }
}
```

**Use case:** Entry point for agents. Call this first to understand the workflow.

---

## Common Parameters

### Time Window

All `history_*` tools accept time window parameters:

| Parameter | Type | Description |
|-----------|------|-------------|
| `start_time` | ISO 8601 datetime | Start of analysis window |
| `end_time` | ISO 8601 datetime | End of analysis window |
| `database_name` | string | Azure SQL database name to analyze |

**Behavior:**
- Both times omitted → last 24 hours
- Only `start_time` → from start to now
- Only `end_time` → from oldest available to end
- Both provided → exact window

**Time Precision:** Database watcher collects telemetry at fixed intervals (10-30 seconds depending on dataset). You get all samples within your requested window — no bucket alignment issues.

### Standard Response Envelope

All tools return:

```json
{
  "metadata": {
    "source": "database_watcher",
    "cluster": "https://mycluster.kusto.windows.net",
    "database_name": "AppDB",
    "executed_at": "2026-02-03T14:30:00Z",
    "requested_window": {
      "start": "2026-02-02T13:00:00Z",
      "end": "2026-02-02T14:00:00Z"
    },
    "samples_in_window": 360
  },
  "data": [ ... ],
  "thresholds": { ... },
  "interpretation": "..."
}
```

### Error Response (No Connection)

If a `history_*` tool is called without first calling `connect_kusto`:

```json
{
  "error": "no_connection",
  "message": "No Kusto connection. Call connect_kusto(cluster_uri, database) first."
}
```

---

## Historical Tools (Phase 1)

These tools query database watcher telemetry in Azure Data Explorer / Fabric Real-Time Analytics. Safe, read-only analysis of past events.

### Data Collection Reference

Database watcher collects data at these intervals:

| Dataset | Interval | Kusto Table |
|---------|----------|-------------|
| Wait statistics | 10 sec | `sqldb_database_wait_stats` |
| Performance counters | 10 sec | `sqldb_database_performance_counters_common` |
| Resource utilization | 15 sec | `sqldb_database_resource_utilization` |
| Storage I/O | 10 sec | `sqldb_database_storage_io` |
| Active sessions | 30 sec | `sqldb_database_active_sessions` |
| Query runtime stats | 15 min | `sqldb_database_query_runtime_stats` |
| Query wait stats | 15 min | `sqldb_database_query_wait_stats` |
| Missing indexes | 15 min | `sqldb_database_missing_indexes` |
| Index metadata | 30 min | `sqldb_database_index_metadata` |

---

### Phase 1: Wait Stats

#### `history_waits`

Wait statistics distribution over a time window.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `database_name` | string | Yes | Database to analyze |
| `start_time` | datetime | No | Window start |
| `end_time` | datetime | No | Window end |
| `top_n` | integer | No | Number of wait types (default: 10) |

**Returns:**
- Top wait types ranked by total wait time
- Percentage of total wait time per category
- Wait count
- Interpretation hints for each wait type

**Key Indicators:**
- `CXPACKET` / `CXSYNC_PORT` > 5% → parallelism issue
- `PAGEIOLATCH_XX` → memory pressure (reading from disk into buffer pool)
- `LCK_M_XX` → blocking/contention
- `SOS_SCHEDULER_YIELD` → CPU pressure
- `ASYNC_NETWORK_IO` → client not consuming results fast enough

**Data source:** `sqldb_database_wait_stats` (10-second samples)

**Sample KQL:**
```kql
let benign_waits = dynamic([
    'WAITFOR', 'LAZYWRITER_SLEEP', 'SLEEP_TASK', 'BROKER_TO_FLUSH',
    'CHECKPOINT_QUEUE', 'CLR_AUTO_EVENT', 'DISPATCHER_QUEUE_SEMAPHORE',
    'XE_DISPATCHER_WAIT', 'XE_TIMER_EVENT', 'SQLTRACE_BUFFER_FLUSH', 'CXCONSUMER'
]);
sqldb_database_wait_stats
| where sample_time_utc between (datetime({start_time}) .. datetime({end_time}))
| where database_name == "{database_name}"
| where wait_type !in (benign_waits)
| summarize TotalWait_ms = sum(wait_time_ms), WaitCount = sum(waiting_tasks_count) by wait_type
| extend Pct = round(TotalWait_ms * 100.0 / toscalar(
    sqldb_database_wait_stats
    | where sample_time_utc between (datetime({start_time}) .. datetime({end_time}))
    | where database_name == "{database_name}"
    | where wait_type !in (benign_waits)
    | summarize sum(wait_time_ms)
), 2)
| order by TotalWait_ms desc
| take {top_n}
```

---

#### `history_waits_by_query`

Per-query wait breakdown over a time window.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `database_name` | string | Yes | Database to analyze |
| `start_time` | datetime | No | Window start |
| `end_time` | datetime | No | Window end |
| `wait_category` | string | No | Filter to specific wait category |
| `top_n` | integer | No | Number of queries (default: 10) |

**Returns:**
- Top queries ranked by wait time
- Wait category breakdown per query
- Query text and query ID
- Execution count

**Data source:** `sqldb_database_query_wait_stats` (15-minute aggregations from Query Store)

---

### Phase 2: Query Analysis

#### `history_queries`

Top resource-consuming queries over a time window.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `database_name` | string | Yes | Database to analyze |
| `start_time` | datetime | No | Window start |
| `end_time` | datetime | No | Window end |
| `order_by` | string | No | `cpu`, `reads`, `duration`, `executions` (default: `cpu`) |
| `top_n` | integer | No | Number of queries (default: 10) |

**Returns:**
- Query ID and SQL text
- Total and average CPU, reads, duration
- Execution count
- First/last execution time in window

**Data source:** `sqldb_database_query_runtime_stats` (15-minute aggregations)

**Sample KQL:**
```kql
sqldb_database_query_runtime_stats
| where sample_time_utc between (datetime({start_time}) .. datetime({end_time}))
| where database_name == "{database_name}"
| summarize 
    TotalCPU_ms = sum(avg_cpu_time * count_executions) / 1000,
    TotalReads = sum(avg_logical_io_reads * count_executions),
    TotalDuration_ms = sum(avg_duration * count_executions) / 1000,
    Executions = sum(count_executions),
    FirstExec = min(first_execution_time),
    LastExec = max(last_execution_time)
    by query_id, query_sql_text
| extend AvgCPU_ms = TotalCPU_ms / Executions
| order by TotalCPU_ms desc  // or TotalReads, TotalDuration_ms, Executions
| take {top_n}
| project query_id, query_sql_text = substring(query_sql_text, 0, 500), 
          TotalCPU_ms, AvgCPU_ms, TotalReads, TotalDuration_ms, Executions
```

---

#### `history_blocking`

Blocking events over a time window.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `database_name` | string | Yes | Database to analyze |
| `start_time` | datetime | No | Window start |
| `end_time` | datetime | No | Window end |
| `min_duration_sec` | integer | No | Only show blocking > X seconds |

**Returns:**
- Time of blocking sample
- Waiting session and blocking session
- Wait type
- Estimated blocking duration (based on sample frequency)
- Query text (if available)

**Data source:** `sqldb_database_active_sessions` (30-second samples)

**Note:** Each row represents a point-in-time snapshot. If a session appears blocked in 4 consecutive samples, estimated blocking duration = 4 × 30 = 120 seconds.

**Sample KQL:**
```kql
sqldb_database_active_sessions
| where sample_time_utc between (datetime({start_time}) .. datetime({end_time}))
| where database_name == "{database_name}"
| where blocking_session_id > 0
| summarize 
    BlockedSamples = count(),
    EstimatedBlockedTime_sec = count() * 30,
    FirstSeen = min(sample_time_utc),
    LastSeen = max(sample_time_utc)
    by session_id, blocking_session_id, wait_type
| where EstimatedBlockedTime_sec >= {min_duration_sec}
| order by EstimatedBlockedTime_sec desc
```

---

### Phase 3: Resource Health

#### `history_resources`

Resource utilization over a time window (CPU, I/O, memory).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `database_name` | string | Yes | Database to analyze |
| `start_time` | datetime | No | Window start |
| `end_time` | datetime | No | Window end |
| `granularity` | string | No | `sample` (15s), `minute`, `hour` (default: `minute`) |

**Returns:**
- Time series of CPU %, Data IO %, Log IO %, Memory %
- Avg, min, max for each metric
- DTU/vCore consumption (if applicable)

**Data source:** `sqldb_database_resource_utilization` (15-second samples)

**Sample KQL:**
```kql
sqldb_database_resource_utilization
| where sample_time_utc between (datetime({start_time}) .. datetime({end_time}))
| where database_name == "{database_name}"
| summarize 
    AvgCPU = round(avg(avg_cpu_percent), 2),
    MaxCPU = round(max(avg_cpu_percent), 2),
    AvgDataIO = round(avg(avg_data_io_percent), 2),
    MaxDataIO = round(max(avg_data_io_percent), 2),
    AvgLogIO = round(avg(avg_log_write_percent), 2),
    MaxLogIO = round(max(avg_log_write_percent), 2)
    by bin(sample_time_utc, 1m)
| order by sample_time_utc asc
```

---

#### `history_counters`

Performance counter trends over a time window.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `database_name` | string | Yes | Database to analyze |
| `start_time` | datetime | No | Window start |
| `end_time` | datetime | No | Window end |
| `counters` | string[] | No | Specific counters to include (default: key counters) |

**Returns:**
- Time series of PLE, Batch Requests/sec, Memory Grants Pending
- Min/max/avg for each counter

**Thresholds:**
- PLE: healthy > 1000s, danger < 300s. Formula: `(BufferPool_GB / 4) × 300`
- Buffer Cache Hit Ratio: target > 95%
- Memory Grants Pending: must be 0

**Data source:** `sqldb_database_performance_counters_common` (10-second samples)

---

#### `history_disk`

Disk I/O latency over a time window.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `database_name` | string | Yes | Database to analyze |
| `start_time` | datetime | No | Window start |
| `end_time` | datetime | No | Window end |

**Returns:**
- Time series of read/write latency by file
- Avg, min, max, P95 latency

**Thresholds:**
- Excellent: < 5ms
- Warning: 10-20ms
- Crisis: > 20ms

**Data source:** `sqldb_database_storage_io` (10-second samples)

---

### Phase 4: Indexing

#### `history_indexes_missing`

Missing index recommendations over a time window.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `database_name` | string | Yes | Database to analyze |
| `start_time` | datetime | No | Window start |
| `end_time` | datetime | No | Window end |
| `top_n` | integer | No | Number of suggestions (default: 10) |

**Returns:**
- Table name
- Equality, inequality, and included columns
- User seeks and avg user impact
- Trend: is this recommendation new or persistent?

**Data source:** `sqldb_database_missing_indexes` (15-minute snapshots)

---

#### `history_parameter_sniffing`

Detect queries with high execution time variance (likely parameter sniffing).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `database_name` | string | Yes | Database to analyze |
| `start_time` | datetime | No | Window start |
| `end_time` | datetime | No | Window end |
| `variance_threshold` | number | No | Max/Avg ratio to flag (default: 10) |
| `min_executions` | integer | No | Minimum executions to consider (default: 100) |

**Returns:**
- Query ID and text preview
- Average and max CPU/duration
- Max-to-average ratio
- Execution count

**Data source:** `sqldb_database_query_runtime_stats`

---

## Tool Summary (Phase 1)

| Tool | Diagnostic Phase | Purpose |
|------|------------------|---------|
| `connect_kusto` | Setup | Connect to telemetry store |
| `connections` | Setup | List active connections |
| `disconnect` | Setup | Close connection |
| `diagnostic_strategy` | Meta | Get workflow guidance |
| `history_waits` | 1 - Wait Stats | Wait distribution in time window |
| `history_waits_by_query` | 1 - Wait Stats | Per-query wait breakdown |
| `history_queries` | 2 - Query Analysis | Top resource consumers |
| `history_blocking` | 2 - Query Analysis | Blocking events over time |
| `history_resources` | 3 - Resource Health | CPU/IO/memory time-series |
| `history_counters` | 3 - Resource Health | PLE, batch requests trends |
| `history_disk` | 3 - Resource Health | I/O latency trends |
| `history_indexes_missing` | 4 - Indexing | Missing index trends |
| `history_parameter_sniffing` | 4 - Indexing | High variance query detection |

---

## Workflow Example

```
User: "Analyze the load test that ran yesterday from 2-3 PM on the AppDB database"

Agent:
1. diagnostic_strategy()
   → "Call connect_kusto first, then follow the workflow"

2. connect_kusto(cluster_uri="https://mycluster.kusto.windows.net", database="sql_monitoring")
   → "Connected as user@contoso.com"

3. history_waits(database_name="AppDB", start_time="2026-02-02T14:00:00Z", end_time="2026-02-02T15:00:00Z")
   → "PAGEIOLATCH_SH was 35% of waits — indicates memory pressure"

4. history_resources(database_name="AppDB", start_time="...", end_time="...")
   → "CPU avg 45%, Data IO avg 92% — high I/O utilization"

5. history_queries(database_name="AppDB", start_time="...", end_time="...", order_by="reads")
   → "Query 12345 did 85% of all reads"

6. history_counters(database_name="AppDB", start_time="...", end_time="...")
   → "PLE dropped from 2000 to 180 at 2:15 PM — memory pressure spike"

7. history_indexes_missing(database_name="AppDB", start_time="...", end_time="...")
   → "Missing index on Orders.CustomerID would reduce reads by 78%"

Agent Summary:
"Query 12345 is causing memory pressure due to excessive reads. 
A missing index on Orders.CustomerID is the likely root cause. 
The PLE drop at 2:15 PM correlates with peak load during the test."
```

---

## Requirements

### Phase 1 (Historical Analysis)
- **Database watcher** configured and collecting data for target Azure SQL databases
- Data stored in **Azure Data Explorer** or **Fabric Real-Time Analytics**
- User authenticated via **Azure CLI** or **VS Code Azure Account extension**
- User must have query access to the Kusto database

### Database Watcher Setup Reference

- [Overview](https://learn.microsoft.com/en-us/azure/azure-sql/database-watcher-overview)
- [Data collection and datasets](https://learn.microsoft.com/en-us/azure/azure-sql/database-watcher-data)
- [Analyze monitoring data](https://learn.microsoft.com/en-us/azure/azure-sql/database-watcher-analyze)

---

## Thresholds Reference

| Metric | Healthy | Warning | Critical |
|--------|---------|---------|----------|
| Page Life Expectancy | > 1000s | 300-1000s | < 300s |
| Buffer Cache Hit Ratio | > 95% | 90-95% | < 90% |
| Memory Grants Pending | 0 | 1-5 | > 5 |
| Disk Latency (read/write) | < 5ms | 10-20ms | > 20ms |
| Compilations/Batch Requests | < 10% | 10-25% | > 25% |
| `CXPACKET` % of waits | < 5% | 5-15% | > 15% |
| `LCK_M_*` % of waits | < 5% | 5-15% | > 15% |
| CPU % | < 70% | 70-90% | > 90% |
| Data IO % | < 70% | 70-90% | > 90% |

---

## Future: Phase 2 — Live Diagnostics

> **Status:** Not implemented. Documented here for future reference.

Phase 2 will add real-time diagnostics by querying Azure SQL DMVs directly. This is useful for active incident triage when you need to see what's happening right now.

### Additional Connection Tool

#### `connect_sql` (Future)

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `server` | string | Yes | Azure SQL server (e.g., `prod.database.windows.net`) |
| `database` | string | Yes | Database name |
| `readonly` | boolean | No | Read-only mode (default: true) |

### Additional Tools (Future)

| Tool | Purpose |
|------|---------|
| `live_waits` | Cumulative wait stats (since restart) |
| `live_requests` | Currently executing queries |
| `live_blocking` | Active blocking chains |
| `live_sessions` | Active session states |
| `live_memory` | Current PLE, memory grants |
| `live_cpu` | Current batch requests, compilations |
| `live_tempdb` | Current TempDB usage |
| `live_disk` | Cumulative I/O latency |

### Why Phase 2 Is Separate

| Concern | Phase 1 (Kusto) | Phase 2 (DMV) |
|---------|-----------------|---------------|
| Risk to production | None — separate telemetry store | Low — queries run on production |
| Use case | Post-mortem, test analysis | Active incident |
| Query load | On Kusto cluster | On SQL Server |
| Implementation complexity | Simple | Requires connection pooling, timeouts |
