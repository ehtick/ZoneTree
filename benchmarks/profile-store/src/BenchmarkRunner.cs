using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProfileStore.Benchmark;

public sealed class BenchmarkRunner(BenchmarkConfig config)
{
  readonly ProfileGenerator Generator = new(config.Seed);
  readonly WorkloadPlan Plan = WorkloadPlanner.Create(config, new ProfileGenerator(config.Seed));

  public async Task<IReadOnlyList<BenchmarkResult>> RunAsync(CancellationToken ct)
  {
    Directory.CreateDirectory(config.OutputDirectory);
    Directory.CreateDirectory(config.DataDirectory);
    var results = new List<BenchmarkResult>();
    foreach (var engine in ExpandEngines(config.Engine))
      results.Add(await RunEngineAsync(CreateEngine(engine), ct));
    return results;
  }

  public async Task<BenchmarkResult> RunSingleAsync(CancellationToken ct)
  {
    var engines = ExpandEngines(config.Engine);
    if (engines.Count != 1)
      throw new ArgumentException("Child benchmark runs must select exactly one engine.");
    Directory.CreateDirectory(config.OutputDirectory);
    Directory.CreateDirectory(config.DataDirectory);
    return await RunEngineAsync(CreateEngine(engines[0]), ct);
  }

  async Task<BenchmarkResult> RunEngineAsync(Func<IProfileStoreEngine> engineFactory, CancellationToken ct)
  {
    var runStopwatch = Stopwatch.StartNew();
    await using var memorySampler = ProcessMemorySampler.Start();
    using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    if (config.TimeoutSeconds > 0)
      runCts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
    var runCt = runCts.Token;

    var phases = new List<PhaseResult>();
    var startedAt = DateTime.UtcNow.ToString("O");
    var engineName = "Unknown";
    var durability = "Not available.";
    var completed = false;
    string? status = null;
    double? preReadStabilizeMs = null;
    double? postUpdateStabilizeMs = null;
    double? settleMs = null;
    long? storageSize = null;
    double? reopenMs = null;
    double? verifyMs = null;
    string? verifyChecksum = null;
    string? activeOperation = null;
    string? interruptedPhase = null;

    try
    {
      {
        await using var engine = engineFactory();
        engineName = engine.Name;
        Console.WriteLine();
        Console.WriteLine($"== {engine.Name} ==");
        activeOperation = "initialize";
        await engine.InitializeAsync(config, reset: true, runCt);
        durability = engine.DurabilityDescription;

        activeOperation = "insert profiles";
        phases.Add(await MeasurePhaseAsync(
            engine,
            "insert profiles",
            config.Profiles,
            async checksum =>
            {
              for (var userId = 1; userId <= config.Profiles; userId++)
              {
                runCt.ThrowIfCancellationRequested();
                var profile = Generator.Create(userId);
                await engine.InsertBatchAsync([profile], runCt);
                checksum.Add(profile.UserId);
              }
            },
            runCt));

        activeOperation = "pre-read stabilization";
        preReadStabilizeMs = await StabilizeForReadMeasurementsAsync(engine, "pre-read stabilization", runCt);

        activeOperation = "read by user id";
        phases.Add(await MeasureProfileReadPhaseAsync(
            engine,
            "read by user id",
            Plan.PointReadIds,
            (id, token) => engine.GetByUserIdAsync(id, token),
            runCt));

        activeOperation = "lookup by email";
        phases.Add(await MeasureProfileReadPhaseAsync(
            engine,
            "lookup by email",
            Plan.EmailReadIds,
            (id, token) => engine.GetByEmailAsync(Generator.Create(id).Email, token),
            runCt));

        activeOperation = "scan country/status index";
        phases.Add(await MeasureIndexQueryPhaseAsync(
            "scan country/status index",
            Plan.CountryStatusQueries,
            query => engine.ScanCountryStatusIndexAsync(query.Country, query.Status, config.QueryLimit, runCt),
            runCt));

        activeOperation = "query country/status";
        phases.Add(await MeasureQueryPhaseAsync(
            "query country/status",
            Plan.CountryStatusQueries,
            query => engine.QueryCountryStatusAsync(query.Country, query.Status, config.QueryLimit, runCt),
            runCt));

        activeOperation = "scan created-at index";
        phases.Add(await MeasureIndexQueryPhaseAsync(
            "scan created-at index",
            Plan.CreatedAtRangeQueries,
            query => engine.ScanCreatedAtRangeIndexAsync(query.FromUnixMs, query.ToUnixMs, config.QueryLimit, runCt),
            runCt));

        activeOperation = "query created-at range";
        phases.Add(await MeasureQueryPhaseAsync(
            "query created-at range",
            Plan.CreatedAtRangeQueries,
            query => engine.QueryCreatedAtRangeAsync(query.FromUnixMs, query.ToUnixMs, config.QueryLimit, runCt),
            runCt));

        activeOperation = "scan top reputation index";
        phases.Add(await MeasureRepeatedIndexQueryPhaseAsync(
            "scan top reputation index",
            config.QueryCount,
            () => engine.ScanTopReputationIndexAsync(config.QueryLimit, runCt),
            runCt));

        activeOperation = "query top reputation";
        phases.Add(await MeasureRepeatedQueryPhaseAsync(
            "query top reputation",
            config.QueryCount,
            () => engine.QueryTopReputationAsync(config.QueryLimit, runCt),
            runCt));

        activeOperation = "update profiles";
        phases.Add(await MeasurePhaseAsync(
            engine,
            "update profiles",
            Plan.UpdateIds.Length,
            async checksum =>
            {
              foreach (var id in Plan.UpdateIds)
              {
                runCt.ThrowIfCancellationRequested();
                var current = await engine.GetByUserIdAsync(id, runCt)
                    ?? throw new InvalidOperationException($"{engine.Name} missing profile {id}");
                var updated = Generator.Update(current);
                await engine.UpdateBatchAsync([updated], runCt);
                checksum.Add(updated);
              }
            },
            runCt));

        activeOperation = "post-update stabilization";
        postUpdateStabilizeMs = await StabilizeForReadMeasurementsAsync(engine, "post-update stabilization", runCt);

        activeOperation = "post-update read by user id";
        phases.Add(await MeasureProfileReadPhaseAsync(
            engine,
            "post-update read by user id",
            Plan.PostPointReadIds,
            (id, token) => engine.GetByUserIdAsync(id, token),
            runCt));

        activeOperation = "post-update lookup by email";
        phases.Add(await MeasureProfileReadPhaseAsync(
            engine,
            "post-update lookup by email",
            Plan.PostEmailReadIds,
            (id, token) => engine.GetByEmailAsync(Generator.Create(id).Email, token),
            runCt));

        activeOperation = "post-update scan country/status index";
        phases.Add(await MeasureIndexQueryPhaseAsync(
            "post-update scan country/status index",
            Plan.PostCountryStatusQueries,
            query => engine.ScanCountryStatusIndexAsync(query.Country, query.Status, config.QueryLimit, runCt),
            runCt));

        activeOperation = "post-update query country/status";
        phases.Add(await MeasureQueryPhaseAsync(
            "post-update query country/status",
            Plan.PostCountryStatusQueries,
            query => engine.QueryCountryStatusAsync(query.Country, query.Status, config.QueryLimit, runCt),
            runCt));

        activeOperation = "post-update scan top reputation index";
        phases.Add(await MeasureRepeatedIndexQueryPhaseAsync(
            "post-update scan top reputation index",
            config.PostQueryCount,
            () => engine.ScanTopReputationIndexAsync(config.QueryLimit, runCt),
            runCt));

        activeOperation = "post-update query top reputation";
        phases.Add(await MeasureRepeatedQueryPhaseAsync(
            "post-update query top reputation",
            config.PostQueryCount,
            () => engine.QueryTopReputationAsync(config.QueryLimit, runCt),
            runCt));

        activeOperation = "settle";
        (settleMs, _) = await Timing.MeasureAsync(async () =>
        {
          await engine.SettleAsync(runCt);
          return true;
        });
        storageSize = await engine.GetStorageSizeBytesAsync(runCt);
      }

      await using var reopened = engineFactory();
      activeOperation = "reopen";
      (reopenMs, _) = await Timing.MeasureAsync(async () =>
      {
        await reopened.InitializeAsync(config, reset: false, runCt);
        return true;
      });

      activeOperation = "verify";
      (verifyMs, verifyChecksum) = await Timing.MeasureAsync(async () =>
      {
        var checksum = new Checksum();
        checksum.Add(await reopened.CountProfilesAsync(runCt));
        foreach (var id in new[] { 1L, Math.Max(1, config.Profiles / 2), (long)config.Profiles })
        {
          runCt.ThrowIfCancellationRequested();
          var profile = await reopened.GetByUserIdAsync(id, runCt)
              ?? throw new InvalidOperationException($"{reopened.Name} missing profile {id} after reopen");
          checksum.Add(profile);
        }
        return checksum.Hex;
      });
      completed = true;
      status = "Completed";
      activeOperation = null;
    }
    catch (OperationCanceledException) when (runCts.IsCancellationRequested && !ct.IsCancellationRequested)
    {
      status = $"Timed out after {config.TimeoutSeconds:N0} seconds";
      interruptedPhase = activeOperation;
      Console.WriteLine(status);
    }

    runStopwatch.Stop();
    await memorySampler.StopAsync();

    return new BenchmarkResult(
        engineName,
        durability,
        startedAt,
        CreateWorkloadConfig(),
        CreateEngineSettings(engineName, config),
        completed,
        status,
        phases,
        runStopwatch.Elapsed.TotalMilliseconds,
        preReadStabilizeMs,
        postUpdateStabilizeMs,
        settleMs,
        storageSize,
        reopenMs,
        verifyMs,
        verifyChecksum,
        memorySampler.PeakWorkingSetBytes,
        GetEnvironment(memorySampler.InitialWorkingSetBytes))
    {
      InterruptedPhase = interruptedPhase
    };
  }

  BenchmarkWorkloadConfig CreateWorkloadConfig()
  {
    return new BenchmarkWorkloadConfig(
        config.Profiles,
        config.ReadCount,
        config.EmailReadCount,
        config.QueryCount,
        config.UpdateCount,
        config.PostReadCount,
        config.PostEmailReadCount,
        config.PostQueryCount,
        config.QueryLimit,
        config.Seed,
        config.TimeoutSeconds);
  }

  public static IReadOnlyDictionary<string, string> CreateEngineSettings(string engineName, BenchmarkConfig config)
  {
    return engineName switch
    {
      "ZoneTree" => new Dictionary<string, string>
      {
        ["MutableSegmentMaxItemCount"] = config.ZoneTreeMutableSegmentMaxItemCount.ToString(),
        ["SparseArrayStepSize"] = config.ZoneTreeSparseArrayStepSize.ToString(),
        ["KeyCacheSize"] = config.ZoneTreeKeyCacheSize.ToString(),
        ["ValueCacheSize"] = config.ZoneTreeValueCacheSize.ToString(),
        ["IteratorPrefetchSize"] = config.ZoneTreeIteratorPrefetchSize.ToString(),
        ["BlockCacheLifeTime"] = $"{config.ZoneTreeBlockCacheLifeTimeMinutes} minutes",
        ["ReadStabilization"] = "Settle before read/query phases"
      },
      "SQLite" => new Dictionary<string, string>
      {
        ["JournalMode"] = "WAL",
        ["Synchronous"] = "NORMAL",
        ["CacheMb"] = config.SqliteCacheMb.ToString(),
        ["MmapMb"] = config.SqliteMmapMb.ToString(),
        ["TempStore"] = "MEMORY"
      },
      "RocksDB" => new Dictionary<string, string>
      {
        ["Databases"] = "profiles,email-index,country-status-index,created-at-index,reputation-index",
        ["Compression"] = "Zstd",
        ["WriteBufferMb"] = config.RocksDbWriteBufferMb.ToString(),
        ["MaxWriteBufferNumber"] = config.RocksDbMaxWriteBufferNumber.ToString(),
        ["WriteSync"] = "false",
        ["ReadStabilization"] = "Compact before read/query phases"
      },
      "MySQL" => new Dictionary<string, string>
      {
        ["Host"] = config.MySqlHost,
        ["Port"] = config.MySqlPort.ToString(),
        ["Database"] = config.MySqlDatabase,
        ["User"] = config.MySqlUser
      },
      _ => new Dictionary<string, string>()
    };
  }

  async Task<PhaseResult> MeasurePhaseAsync(
      IProfileStoreEngine engine,
      string name,
      long operations,
      Func<Checksum, Task> action,
      CancellationToken ct)
  {
    Console.Write($"  {name}... ");
    var checksum = new Checksum();
    var sw = Stopwatch.StartNew();
    await action(checksum);
    sw.Stop();
    var throughput = operations / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
    Console.WriteLine($"{sw.Elapsed.TotalSeconds:N2}s ({throughput:N0}/s)");
    return new PhaseResult(name, operations, sw.Elapsed.TotalMilliseconds, throughput, checksum.Hex);
  }

  static async Task<double?> StabilizeForReadMeasurementsAsync(
      IProfileStoreEngine engine,
      string name,
      CancellationToken ct)
  {
    if (!engine.RequiresReadStabilization)
      return null;

    Console.Write($"  {name}... ");
    var sw = Stopwatch.StartNew();
    await engine.StabilizeForReadMeasurementsAsync(ct);
    sw.Stop();
    Console.WriteLine($"{sw.Elapsed.TotalSeconds:N2}s");
    return sw.Elapsed.TotalMilliseconds;
  }

  async Task<PhaseResult> MeasureProfileReadPhaseAsync(
      IProfileStoreEngine engine,
      string name,
      IReadOnlyList<long> ids,
      Func<long, CancellationToken, Task<UserProfile?>> read,
      CancellationToken ct)
  {
    return await MeasurePhaseAsync(
        engine,
        name,
        ids.Count,
        async checksum =>
        {
          foreach (var id in ids)
          {
            ct.ThrowIfCancellationRequested();
            var profile = await read(id, ct)
                ?? throw new InvalidOperationException($"{engine.Name} missing profile {id}");
            checksum.Add(profile);
          }
        },
        ct);
  }

  async Task<PhaseResult> MeasureQueryPhaseAsync<TQuery>(
      string name,
      IReadOnlyList<TQuery> queries,
      Func<TQuery, Task<IReadOnlyList<UserProfile>>> query,
      CancellationToken ct)
  {
    Console.Write($"  {name}... ");
    var checksum = new Checksum();
    var sw = Stopwatch.StartNew();
    foreach (var item in queries)
    {
      ct.ThrowIfCancellationRequested();
      var profiles = await query(item);
      foreach (var profile in profiles)
        checksum.Add(profile);
    }
    sw.Stop();
    var throughput = queries.Count / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
    Console.WriteLine($"{sw.Elapsed.TotalSeconds:N2}s ({throughput:N0}/s)");
    return new PhaseResult(name, queries.Count, sw.Elapsed.TotalMilliseconds, throughput, checksum.Hex);
  }

  async Task<PhaseResult> MeasureIndexQueryPhaseAsync<TQuery>(
      string name,
      IReadOnlyList<TQuery> queries,
      Func<TQuery, Task<IReadOnlyList<long>>> query,
      CancellationToken ct)
  {
    Console.Write($"  {name}... ");
    var checksum = new Checksum();
    var sw = Stopwatch.StartNew();
    foreach (var item in queries)
    {
      ct.ThrowIfCancellationRequested();
      var userIds = await query(item);
      foreach (var userId in userIds)
        checksum.Add(userId);
    }
    sw.Stop();
    var throughput = queries.Count / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
    Console.WriteLine($"{sw.Elapsed.TotalSeconds:N2}s ({throughput:N0}/s)");
    return new PhaseResult(name, queries.Count, sw.Elapsed.TotalMilliseconds, throughput, checksum.Hex);
  }

  async Task<PhaseResult> MeasureRepeatedQueryPhaseAsync(
      string name,
      int count,
      Func<Task<IReadOnlyList<UserProfile>>> query,
      CancellationToken ct)
  {
    Console.Write($"  {name}... ");
    var checksum = new Checksum();
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < count; i++)
    {
      ct.ThrowIfCancellationRequested();
      var profiles = await query();
      foreach (var profile in profiles)
        checksum.Add(profile);
    }
    sw.Stop();
    var throughput = count / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
    Console.WriteLine($"{sw.Elapsed.TotalSeconds:N2}s ({throughput:N0}/s)");
    return new PhaseResult(name, count, sw.Elapsed.TotalMilliseconds, throughput, checksum.Hex);
  }

  async Task<PhaseResult> MeasureRepeatedIndexQueryPhaseAsync(
      string name,
      int count,
      Func<Task<IReadOnlyList<long>>> query,
      CancellationToken ct)
  {
    Console.Write($"  {name}... ");
    var checksum = new Checksum();
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < count; i++)
    {
      ct.ThrowIfCancellationRequested();
      var userIds = await query();
      foreach (var userId in userIds)
        checksum.Add(userId);
    }
    sw.Stop();
    var throughput = count / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
    Console.WriteLine($"{sw.Elapsed.TotalSeconds:N2}s ({throughput:N0}/s)");
    return new PhaseResult(name, count, sw.Elapsed.TotalMilliseconds, throughput, checksum.Hex);
  }

  public static IReadOnlyList<string> ExpandEngines(string engine)
  {
    var requested = engine
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.ToLowerInvariant())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (requested.Contains("all"))
      requested = ["zonetree", "rocksdb", "sqlite", "mysql"];

    var engines = new List<string>();
    if (requested.Contains("zonetree"))
      engines.Add("zonetree");
    if (requested.Contains("rocksdb"))
      engines.Add("rocksdb");
    if (requested.Contains("sqlite"))
      engines.Add("sqlite");
    if (requested.Contains("mysql"))
      engines.Add("mysql");
    if (engines.Count == 0)
      throw new ArgumentException($"No supported engine selected: {engine}");
    return engines;
  }

  static Func<IProfileStoreEngine> CreateEngine(string engine)
  {
    return engine switch
    {
      "zonetree" => () => new ZoneTreeProfileStore(),
      "rocksdb" => () => new RocksDbProfileStore(),
      "sqlite" => () => new SqliteProfileStore(),
      "mysql" => () => new MySqlProfileStore(),
      _ => throw new ArgumentException($"Unsupported engine: {engine}")
    };
  }

  public static string GetEnvironment(long? initialProcessWorkingSetBytes)
  {
    return string.Join(Environment.NewLine,
        $"* OS: {RuntimeInformation.OSDescription}",
        $"* Architecture: {RuntimeInformation.ProcessArchitecture}",
        $"* .NET: {Environment.Version}",
        $"* CPU: {GetCpuDescription()}",
        $"* Logical processors: {Environment.ProcessorCount}",
        $"* Total available memory: {FormatOptionalBytes(GetTotalAvailableMemoryBytes())}",
        $"* Initial process working set: {FormatOptionalBytes(initialProcessWorkingSetBytes)}");
  }

  static string GetCpuDescription()
  {
    try
    {
      if (OperatingSystem.IsWindows())
      {
        var value = Microsoft.Win32.Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            "ProcessorNameString",
            null);
        return value?.ToString()?.Trim() ?? "n/a";
      }

      if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
      {
        foreach (var line in File.ReadLines("/proc/cpuinfo"))
        {
          if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
          {
            var separator = line.IndexOf(':');
            if (separator >= 0)
              return line[(separator + 1)..].Trim();
          }
        }
      }

      if (OperatingSystem.IsMacOS())
        return RunProcessForSingleLine("sysctl", "-n machdep.cpu.brand_string") ?? "n/a";
    }
    catch
    {
    }
    return "n/a";
  }

  static long? GetTotalAvailableMemoryBytes()
  {
    var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    return memory > 0 ? memory : null;
  }

  static string FormatOptionalBytes(long? bytes) =>
      bytes.HasValue ? ResultWriter.FormatBytes(bytes.Value) : "n/a";

  static string? RunProcessForSingleLine(string fileName, string arguments)
  {
    try
    {
      using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
      {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
      });
      if (process == null)
        return null;
      var line = process.StandardOutput.ReadLine();
      process.WaitForExit(1000);
      return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
    }
    catch
    {
      return null;
    }
  }
}
