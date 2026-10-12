# Query performance baseline

Run `tests/benchmarks/run-query-performance.ps1` to reproduce the benchmark. The default workload seeds 36 SQLite month databases covering 2024–2026, with 10,000 records per month (360,000 total), then compares a 10,000-row deep-page walk using cursors against the equivalent `Skip` page. It checks the returned `(MonthKey, Id)` sequences match for both `TriggerTime` and `DurationMs` sorting. All databases are created under a unique temporary directory and removed after the run.

The benchmark reports seed throughput, cold first-page time, cumulative cursor-walk time to the deep page, equivalent one-shot offset time, managed heap, and process working set. Cursor walking performs multiple page requests, so its total time is not directly comparable to one offset request as a per-page latency; both approaches return the same target page. Results vary with hardware, SQLite cache state, and build configuration.

The checked-in baseline below is from the local Windows/.NET 8 Release run on 2026-10-12. The machine and storage configuration are not controlled, so treat it as a reproducible workload record rather than a cross-machine performance guarantee.

```text
seed_rows=360000; seed_ms=41408; seed_rows_per_second=8693
time_first_cold_ms=78; first_page_count=500; total=360000
time_cursor_deep_ms=296; time_offset_deep_ms=72; deep_page_count=500; rows_match=true
duration_first_page_count=500; duration_cursor_deep_ms=7325; duration_offset_deep_ms=1487; deep_page_count=500; rows_match=true
memory|phase=after-seed|managed_mb=53.1|working_set_mb=266.4
memory|phase=after-time-first-page|managed_mb=44.9|working_set_mb=267.8
memory|phase=after-time-deep-pages|managed_mb=58.7|working_set_mb=269.2
memory|phase=after-duration-deep-pages|managed_mb=208.6|working_set_mb=420.7
```
