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

Need scripts/queries to intentionally generate problems for testing diagnostics:

- [ ] **Blocking/Lock contention** - Long-running transactions holding locks while other sessions wait
- [ ] **CPU pressure** - Expensive queries with lots of string manipulation, scalar UDFs, or missing indexes causing scans
- [ ] **IO pressure / Memory pressure** - Queries scanning large tables without adequate indexes, forcing page reads from disk
- [ ] **Parallelism waits** - Large scans/sorts that go parallel with high CXPACKET/CXSYNC_PORT
- [ ] **Parameter sniffing** - Stored proc with parameter that causes wildly different plans
- [ ] **Missing indexes** - Queries with predicates on non-indexed columns
- [ ] **TempDB contention** - Heavy temp table usage or spills

Ideas: Copilot can help generate these problem scenarios using AdventureWorksLT schema.
