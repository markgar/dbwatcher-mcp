# TODO

## Time Span Filtering
- [ ] Verify all tools correctly filter to specified `startTime` and `endTime` parameters
- [ ] Test with various time ranges (last hour, specific date range, etc.)
- [ ] Ensure consistent behavior when no time params provided (defaults to 24h)

## Add SQL Target Support

### Azure SQL Managed Instance
- [ ] Research exact column schemas for `sqlmi_*` tables (query actual telemetry DB)
- [ ] Determine which datasets have database-level filtering vs instance-level only
- [ ] Add MI-specific tools or make existing tools target-aware
- [ ] Update tool descriptions to indicate MI support

### Elastic Pools
- [ ] Add tools for `sqldb_elastic_pool_*` tables
- [ ] Filter by `elastic_pool_name` instead of `database_name`
- [ ] Pool-level: waits, counters, resource utilization, storage IO
- [ ] Document that per-database Query Store stats still work for DBs in pools

## Test Workload Generation (AdventureWorksLT)

SQL scripts (run via `sqlcmd`) to intentionally generate problems for testing diagnostics.
PowerShell orchestrator to run them concurrently and generate combined resource pressure.

### Scripts to Create (`tests/workload/`)

- [ ] **cpu-pressure.sql** - Cross joins, string manipulation, scalar UDFs on SalesOrderDetail
- [ ] **io-pressure.sql** - Full table scans with no covering index, large result sets forced to disk
- [ ] **missing-indexes.sql** - Queries with WHERE on non-indexed columns (Product.Color, SalesOrderDetail.UnitPrice, etc.)
- [ ] **blocking.sql** - Two-part script: session 1 holds locks via open transaction, session 2 tries to read/update same rows
- [ ] **parameter-sniffing.sql** - Stored proc with parameter on skewed CustomerID distribution (few orders vs many orders)
- [ ] **tempdb-contention.sql** - Heavy #temp table creation, large sorts/hashes that spill to TempDB
- [ ] **parallelism.sql** - Large scans/sorts that go parallel with high CXPACKET/CXSYNC_PORT (tier-dependent)
- [ ] **run-workload.ps1** - PowerShell orchestrator: runs all scripts concurrently via sqlcmd, generates sustained load for `history_resources` spikes

### Notes
- Blocking requires two concurrent `sqlcmd` sessions — orchestrator handles this
- Parallelism depends on service tier (needs multiple cores)
- Database watcher needs ~30-60s to collect telemetry after workload runs
- All scripts should be safe to run repeatedly (idempotent, no permanent schema changes outside cleanup)
