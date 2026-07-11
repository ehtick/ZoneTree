# Profile Store Benchmark

This benchmark compares ZoneTree, RocksDB, SQLite, and MySQL on a realistic profile-store workload:

* insert profile records,
* maintain secondary indexes,
* query by primary key,
* query by indexed fields,
* scan ordered index ranges,
* update profile fields,
* verify deterministic checksums,
* report final storage size.

The goal is not to prove that one database is universally faster. The goal is to show the tradeoffs for a local/service-side profile database with high write volume, secondary indexes, and ordered scans.

## Run With Docker

From this directory:

```bash
docker compose up --build
```

The bundled Docker MySQL service uses `mysql:9.7.1` with benchmark-oriented
InnoDB settings: binary logging disabled, `innodb_flush_log_at_trx_commit=2`,
`sync_binlog=0`, 8 GB buffer pool, and 4 GB redo capacity.

Or override the workload:

```bash
docker compose run --rm benchmark \
  --engine all \
  --seed 20260706 \
  --clean \
  --output /workspace/benchmarks/profile-store/results \
  --profiles 100K,1M
```

Results are written to:

```text
benchmarks/profile-store/results/profiles-<count>/
```

`--profiles` accepts one count or a comma-separated list. `K` and `M` suffixes
are supported, for example `--profiles 10000,100K,500K,1M,2M,10M`.

## Run Locally

For a Linux host setup without Docker, see
[bare-metal-server-setup.md](bare-metal-server-setup.md).

Start MySQL separately if you want to include it. A ready-to-paste Docker setup
is available in [mysql-benchmark-tuning.md](mysql-benchmark-tuning.md). Then run:

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- \
  --engine all \
  --clean \
  --output ./results \
  --mysql-host YOUR_VM_PUBLIC_IP \
  --mysql-port 3306 \
  --mysql-user root \
  --mysql-password "DevMySql_123456!" \
  --mysql-database profilebench \
  --profiles 100K,1M
```

You can omit the MySQL arguments when the matching `MYSQL_HOST`,
`MYSQL_PORT`, `MYSQL_DATABASE`, `MYSQL_USER`, and `MYSQL_PASSWORD`
environment variables are already set.

For embedded engines only:

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- --engine zonetree,rocksdb,sqlite
```

## Workload

Multi-engine runs start a separate benchmark process for each engine and then
aggregate the child results into one report. Each engine run resets its own data
store before measuring.

Default workload counts:

| Setting | Value |
| --- | ---: |
| Profiles | 100,000 |
| Profile writes | individual operations |
| UserId reads | profiles |
| Email lookups | profiles |
| Country/status index scans | query count |
| Country/status profile queries | query count |
| Created-at index scans | query count |
| Created-at profile queries | query count |
| Top reputation index scans | query count |
| Top reputation profile queries | query count |
| Profile updates | profiles |
| Post-update UserId reads | profiles |
| Post-update email lookups | profiles |
| Post-update country/status index scans | post-query count |
| Post-update country/status profile queries | post-query count |
| Post-update top reputation index scans | post-query count |
| Post-update top reputation profile queries | post-query count |
| Query limit | 100 |

By default, `query count` and `post-query count` are equal to `profiles` for
runs up to 10,000 profiles. Above 10,000 profiles, both default to
`profiles / 4` to keep larger reference runs practical. Override them with
`--query-count` and `--post-query-count` when you need a shorter smoke run or a
heavier query-focused run.

The measured phase order is:

1. Insert profiles.
2. Stabilize for read measurements when the engine requires it.
3. Read by user id.
4. Lookup by email.
5. Scan and query country/status.
6. Scan and query created-at ranges.
7. Scan and query top reputation.
8. Update profiles.
9. Stabilize again when the engine requires it.
10. Post-update read by user id.
11. Post-update lookup by email.
12. Post-update scan and query country/status.
13. Post-update scan and query top reputation.
14. Settle/checkpoint, reopen, and verify.

## Cleanup

Use `--clean` to delete the configured data and results directories before the
run:

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- --engine zonetree,rocksdb,sqlite --clean
```

Use `--clean-data` or `--clean-results` when you only want one side removed.

## Timeout

Use `--timeout-seconds` to limit each engine run. If an engine times out, the
benchmark writes a partial report with completed phases, marks that engine as
timed out, compares checksums across completed engines only, and continues with
the next requested engine.

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- --engine all --timeout-seconds 900 --clean --profiles 10M
```

Use smaller smoke settings during development:

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- \
  --engine zonetree,rocksdb,sqlite \
  --read-count 2000 \
  --email-read-count 2000 \
  --query-count 100 \
  --update-count 1000 \
  --post-read-count 2000 \
  --post-email-read-count 2000 \
  --post-query-count 100 \
  --profiles 10000
```

## Optional .NET GC Memory Tuning

.NET GC behavior can be adjusted with environment variables. These settings are
optional and should be reported with the benchmark result when used, because
they can change both memory usage and throughput.

```bash
DOTNET_GCConserveMemory=1
DOTNET_GCHighMemPercent=0x50
```

`DOTNET_GCConserveMemory` accepts values from `0` to `9`.
`0` is the default and means no extra memory conservation. Higher values make
the GC try harder to keep the managed heap smaller, usually by doing more GC
work. This can reduce reported process peak memory, but may slow the benchmark.

`DOTNET_GCHighMemPercent` sets the physical-memory usage percentage where the
GC starts treating the machine as under high memory load. Environment variable
values are hexadecimal, so `0x50` means `80%`, `0x5A` means `90%`, and `0x4B`
means `75%`. Lower values make the GC react earlier.

These are runtime-level controls, not the first ZoneTree tuning knobs. For
application memory tuning, prefer ZoneTree storage and cache options first, such
as sparse-array step size, block cache behavior, block size, and cache sizes.

## Data Model

Each engine stores:

```csharp
UserProfile(
    UserId,
    Email,
    Country,
    Status,
    CreatedAtUnixMs,
    LastLoginUnixMs,
    Reputation,
    DisplayName,
    Bio)
```

Secondary indexes:

* email -> user id,
* country + status + user id,
* created-at + user id,
* reputation descending + user id.

SQLite and MySQL use native SQL indexes. RocksDB uses separate databases for profile and index data. ZoneTree uses application-managed secondary index trees.

## Fairness Notes

* MySQL is a server-database baseline, not an embedded peer.
* MySQL process peak memory covers this benchmark's .NET client process only;
  the MySQL server/container is outside this measurement.
* RocksDB is embedded, uses Zstd compression, and uses separate databases/writes for profile and index data to mirror ZoneTree's tree layout.
* SQLite is embedded and maintains indexes internally.
* SQLite uses WAL, `synchronous=NORMAL`, a 1 GB page cache, 1 GB memory-mapped I/O allowance, and memory temp storage by default.
* ZoneTree secondary indexes are application-managed in this benchmark.
* ZoneTree uses live background maintainers during the workload.
* ZoneTree maintainer block cache lifetime is 1 minute by default and can be changed with `--zonetree-block-cache-lifetime-minutes`.
* ZoneTree and RocksDB are stabilized after insert and update phases before measuring read/query phases, so background compaction work does not distort lookup and query throughput.
* Durability settings are printed in the report. They are similar enough for a practical comparison, but not identical.
* Results depend on storage, Docker overhead, CPU, memory, and durability mode.
* Every phase contributes to deterministic checksums so incorrect implementations cannot win by skipping work.

## Output

The benchmark writes both JSON and Markdown reports:

```text
profiles-<count>/
profile-store-YYYYMMDD-HHMMSS.json
profile-store-YYYYMMDD-HHMMSS.md
profile-store-YYYYMMDD-HHMMSS-execution-time.svg
profile-store-YYYYMMDD-HHMMSS-write-throughput.svg
profile-store-YYYYMMDD-HHMMSS-lookup-throughput.svg
profile-store-YYYYMMDD-HHMMSS-index-scan-throughput.svg
profile-store-YYYYMMDD-HHMMSS-query-throughput.svg
profile-store-YYYYMMDD-HHMMSS-resources.svg
```

Multi-engine child process results are kept under the same profile-count folder
in `.runs/`.

The Markdown report includes environment information, configuration, durability settings, SVG charts, phase timings, throughput, read-stabilization time, final settle/checkpoint time, reopen time, verify time, storage size, process peak memory, and checksum status.

The write throughput chart shows both raw write phases and derived stabilized
write-readiness bars:

* `insert` uses only the measured insert phase.
* `insert + stabilize` adds the following pre-read stabilization time.
* `update` uses only the measured update phase.
* `update + stabilize` adds the following post-update stabilization time.

This keeps raw write throughput visible while also showing when the engine is
settled for the next read/query phase.

Generated reports under `results/` are local artifacts. Commit only curated
reference results under `reference/<os>/profiles-<count>/`, after a clean run
on a documented machine.

To regenerate SVG charts for an existing JSON report without rerunning the
benchmark:

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- --render-charts results/profiles-10000/profile-store-YYYYMMDD-HHMMSS.json
```

To regenerate only the Markdown report from an existing JSON file without
rewriting the JSON or SVG files:

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- --render-markdown results/profiles-10000/profile-store-YYYYMMDD-HHMMSS.json
```

To regenerate every committed reference report from existing `latest.json`
files without rerunning benchmarks:

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- --render-reference-reports
```

You can also pass a specific reference directory:

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- --render-reference-reports reference/linux
```

To publish the current run as the committed latest reference:

```bash
#windows
dotnet run --project src\ProfileStore.Benchmark.csproj -c Release -- --mysql-host 192.168.178.25 --mysql-port 3306 --mysql-user root --mysql-password "DevMySql_123456!" --mysql-database profilebench --output results --data data --update-latest --timeout-seconds 120000 --engine all --profiles 100K,500K,1M,2M,5M,10M

#linux
nohup dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- --mysql-host 127.0.0.1 --mysql-port 3306 --mysql-user root --mysql-password "DevMySql_123456!" --mysql-database profilebench --output results --data data --update-latest --timeout-seconds 120000 --engine all --profiles 100K,500K,1M,2M

nohup dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- --mysql-host 127.0.0.1 --mysql-port 3306 --mysql-user root --mysql-password "DevMySql_123456!" --mysql-database profilebench --output results --data data --update-latest --timeout-seconds 120000 --engine all --profiles 5M,10M,50M

nohup dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- --mysql-host 127.0.0.1 --mysql-port 3306 --mysql-user root --mysql-password "DevMySql_123456!" --mysql-database profilebench --output results --data data --update-latest --timeout-seconds 120000 --engine all --profiles 100M --zonetree-sparse-array-step-size 32
```

This updates `reference/<os>/profiles-<count>/latest.md` and the matching
`latest.json` and `latest-*.svg` files for each requested profile count. The
`<os>` directory is selected from the current operating system, such as `win`
or `linux`.
