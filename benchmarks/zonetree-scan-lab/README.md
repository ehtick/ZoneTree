# ZoneTree Scan Lab

Small scan benchmark for profiler investigations. ZoneTree is the default
engine, and RocksDB can be selected for fast side-by-side scan comparisons. It
persists inserted profiles and indexes so scan/query runs can be repeated without rebuilding data.
The `--data` path is split by engine: `--data data` writes to `data\zonetree`
or `data\rocksdb`.
Created-at scans use random ranges by default to match the profile-store
benchmark. Use `--created-at fixed` to measure repeated access to one narrow
index region.

From `benchmarks\zonetree-scan-lab`:

```bash
dotnet run -c Release -- --mode insert --reset --profiles 1M --data data
dotnet run -c Release -- --mode read --read-order random --parallelism 16 --profiles 1M --data data
dotnet run -c Release -- --mode read --read-order clustered --parallelism 16 --profiles 1M --data data
dotnet run -c Release -- --mode read --read-order sequential --parallelism 16 --profiles 1M --data data
dotnet run -c Release -- --mode try-get --scan country-status --parallelism 16 --profiles 1M --data data
dotnet run -c Release -- --mode repeated-try-get --scan country-status --parallelism 16 --profiles 1M --data data
dotnet run -c Release -- --mode repeated-disk-try-get --scan country-status --parallelism 16 --profiles 1M --data data
dotnet run -c Release -- --mode seek --scan country-status --parallelism 16 --profiles 1M --data data --reuse-iterator
dotnet run -c Release -- --mode repeated-seek --scan country-status --parallelism 16 --profiles 1M --data data --reuse-iterator
dotnet run -c Release -- --mode prefix-seek --scan country-status --parallelism 16 --profiles 1M --data data --reuse-iterator
dotnet run -c Release -- --mode scan --scan country-status --parallelism 16 --profiles 1M --data data
dotnet run -c Release -- --mode scan --scan country-status --parallelism 16 --profiles 1M --data data --reuse-iterator
dotnet run -c Release -- --mode query --scan country-status --parallelism 16 --profiles 1M --data data
dotnet run -c Release -- --mode scan --scan created-at --parallelism 16 --profiles 1M --data data
dotnet run -c Release -- --mode query --scan created-at --parallelism 16 --profiles 1M --data data
dotnet run -c Release -- --mode scan --scan top-reputation --parallelism 16 --profiles 1M --data data
dotnet run -c Release -- --engine rocksdb --mode all --reset --scan country-status --parallelism 16 --profiles 1M --data data
```

Options:

* `--engine zonetree|rocksdb`
* `--mode insert|read|try-get|repeated-try-get|repeated-disk-try-get|seek|repeated-seek|prefix-seek|scan|query|all`
* `--read-order random|clustered|sequential`
* `--scan country-status|created-at|top-reputation|all`
* `--profiles <count>` accepts plain numbers, `K`, or `M`
* `--scan-count <count>`
* `--parallelism <count>`
* `--limit <count>`
* `--data <path>`
* `--reset`
* `--reuse-iterator` reuses one iterator per worker for country/status seek/scan modes
* `--sparse-step <count>`
* `--key-cache <count>`
* `--value-cache <count>`
* `--prefetch <count>`
* `--btree-lock node-monitor|no-lock|node-reader-writer|top-monitor|top-reader-writer`
* `--created-at random|fixed`
* `--mutable-max <count>`
* `--block-cache-minutes <count>`

Profile-read mode performs `scan-count * limit` direct primary-store reads.
The IDs are prepared before timing so both engines execute the same read and
deserialization work without random-number generation in the measured path:

```bash
dotnet run -c Release -- --mode read --read-order random --parallelism 16 --profiles 1M --data data --engine zonetree
dotnet run -c Release -- --mode read --read-order clustered --parallelism 16 --profiles 1M --data data --engine zonetree
dotnet run -c Release -- --mode read --read-order sequential --parallelism 16 --profiles 1M --data data --engine zonetree
dotnet run -c Release -- --mode read --read-order random --parallelism 16 --profiles 1M --data data --engine rocksdb
dotnet run -c Release -- --mode read --read-order clustered --parallelism 16 --profiles 1M --data data --engine rocksdb
dotnet run -c Release -- --mode read --read-order sequential --parallelism 16 --profiles 1M --data data --engine rocksdb
```

Country/status try-get mode performs `scan-count` exact point lookups against
the country/status index without creating or using iterators. Existing keys and
their UTF-8 representations are prepared before timing:

```bash
dotnet run -c Release -- --mode try-get --scan country-status --parallelism 1 --profiles 1M --data data --engine zonetree
dotnet run -c Release -- --mode try-get --scan country-status --parallelism 16 --profiles 1M --data data --engine zonetree
dotnet run -c Release -- --mode try-get --scan country-status --parallelism 1 --profiles 1M --data data --engine rocksdb
dotnet run -c Release -- --mode try-get --scan country-status --parallelism 16 --profiles 1M --data data --engine rocksdb
```

Repeated-try-get mode uses direct point lookups against the same 128 complete
keys used by repeated-seek mode:

```bash
dotnet run -c Release -- --mode repeated-try-get --parallelism 1 --engine zonetree
dotnet run -c Release -- --mode repeated-try-get --parallelism 16 --engine zonetree
dotnet run -c Release -- --mode repeated-try-get --parallelism 1 --engine rocksdb
dotnet run -c Release -- --mode repeated-try-get --parallelism 16 --engine rocksdb
```

Repeated-disk-try-get is a ZoneTree-only diagnostic that sends the same
128-key plan directly to `Maintenance.DiskSegment.TryGet`:

```bash
dotnet run -c Release -- --mode repeated-disk-try-get --parallelism 1 --engine zonetree
dotnet run -c Release -- --mode repeated-disk-try-get --parallelism 16 --engine zonetree
```

Country/status seek mode uses those same exact keys through each engine's
iterator API and reads one index value. Add `--reuse-iterator` to isolate seek
from iterator creation and disposal:

```bash
dotnet run -c Release -- --mode seek --scan country-status --parallelism 1 --profiles 1M --data data --engine zonetree --reuse-iterator
dotnet run -c Release -- --mode seek --scan country-status --parallelism 16 --profiles 1M --data data --engine zonetree --reuse-iterator
dotnet run -c Release -- --mode seek --scan country-status --parallelism 1 --profiles 1M --data data --engine rocksdb --reuse-iterator
dotnet run -c Release -- --mode seek --scan country-status --parallelism 16 --profiles 1M --data data --engine rocksdb --reuse-iterator
```

Repeated-seek mode selects one complete existing key from each of the 128
country/status groups and repeatedly seeks among that fixed key set:

```bash
dotnet run -c Release -- --mode repeated-seek --parallelism 1 --engine zonetree --reuse-iterator
dotnet run -c Release -- --mode repeated-seek --parallelism 16 --engine zonetree --reuse-iterator
dotnet run -c Release -- --mode repeated-seek --parallelism 1 --engine rocksdb --reuse-iterator
dotnet run -c Release -- --mode repeated-seek --parallelism 16 --engine rocksdb --reuse-iterator
```

Prefix-seek mode performs the same single-value iterator operation using the
128 possible country/status prefixes. It does not perform a prefix comparison
or scan loop:

```bash
dotnet run -c Release -- --mode prefix-seek --parallelism 1 --engine zonetree --reuse-iterator
dotnet run -c Release -- --mode prefix-seek --parallelism 16 --engine zonetree --reuse-iterator
dotnet run -c Release -- --mode prefix-seek --parallelism 1 --engine rocksdb --reuse-iterator
dotnet run -c Release -- --mode prefix-seek --parallelism 16 --engine rocksdb --reuse-iterator
```

Diagnostic p16 query run with mutable BTree locking removed:

```bash
dotnet run -c Release -- --mode insert --reset --profiles 1M --data data --engine zonetree --key-cache 20000 --value-cache 20000 --btree-lock no-lock
dotnet run -c Release -- --mode query --scan country-status --parallelism 16 --profiles 1M --data data --engine zonetree --key-cache 20000 --value-cache 20000 --btree-lock no-lock
```

```bash
dotnet build ZoneTree.ScanLab.csproj -c Release
dotnet-trace collect --output traces\country-status-p16.nettrace -- dotnet bin\Release\net10.0\ZoneTree.ScanLab.dll --mode scan --scan country-status --parallelism 16 --profiles 1M --data data
dotnet-trace collect --output traces\country-status-p1.nettrace -- dotnet bin\Release\net10.0\ZoneTree.ScanLab.dll --mode scan --scan country-status --parallelism 1 --profiles 1M --data data
```
