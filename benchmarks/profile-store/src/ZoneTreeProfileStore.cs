using System.Globalization;
using ZoneTree;
using ZoneTree.Comparers;
using ZoneTree.Logger;
using ZoneTree.Options;
using ZoneTree.Serializers;

namespace ProfileStore.Benchmark;

public sealed class ZoneTreeProfileStore : IProfileStoreEngine
{
  string RootDirectory = "";
  IZoneTree<long, string>? Profiles;
  IZoneTree<string, long>? EmailIndex;
  IZoneTree<string, long>? CountryStatusIndex;
  IZoneTree<string, long>? CreatedAtIndex;
  IZoneTree<string, long>? ReputationIndex;
  readonly List<IMaintainer> Maintainers = [];
  int MutableSegmentMaxItemCount;
  int SparseArrayStepSize;
  int KeyCacheSize;
  int ValueCacheSize;
  int IteratorPrefetchSize;
  TimeSpan BlockCacheLifeTime;

  public string Name => "ZoneTree";

  public string DurabilityDescription =>
      $"AsyncCompressed WAL default; MutableSegmentMaxItemCount={MutableSegmentMaxItemCount.ToString(CultureInfo.InvariantCulture)}; SparseArrayStepSize={SparseArrayStepSize.ToString(CultureInfo.InvariantCulture)}; KeyCacheSize={KeyCacheSize.ToString(CultureInfo.InvariantCulture)}; ValueCacheSize={ValueCacheSize.ToString(CultureInfo.InvariantCulture)}; IteratorPrefetchSize={IteratorPrefetchSize.ToString(CultureInfo.InvariantCulture)}; BlockCacheLifeTime={BlockCacheLifeTime.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes; application-managed secondary indexes; background maintainers enabled.";

  public bool RequiresReadStabilization => true;

  public Task InitializeAsync(BenchmarkConfig config, bool reset, CancellationToken ct)
  {
    RootDirectory = Path.Combine(config.DataDirectory, "zonetree");
    MutableSegmentMaxItemCount = config.ZoneTreeMutableSegmentMaxItemCount;
    SparseArrayStepSize = config.ZoneTreeSparseArrayStepSize;
    KeyCacheSize = config.ZoneTreeKeyCacheSize;
    ValueCacheSize = config.ZoneTreeValueCacheSize;
    IteratorPrefetchSize = config.ZoneTreeIteratorPrefetchSize;
    BlockCacheLifeTime = TimeSpan.FromMinutes(config.ZoneTreeBlockCacheLifeTimeMinutes);
    if (reset && Directory.Exists(RootDirectory))
      Directory.Delete(RootDirectory, true);
    Directory.CreateDirectory(RootDirectory);

    Profiles = OpenLongStringTree(Path.Combine(RootDirectory, "profiles"), MutableSegmentMaxItemCount, SparseArrayStepSize, KeyCacheSize, ValueCacheSize);
    EmailIndex = OpenStringLongTree(Path.Combine(RootDirectory, "email-index"), MutableSegmentMaxItemCount, SparseArrayStepSize, KeyCacheSize, ValueCacheSize);
    CountryStatusIndex = OpenStringLongTree(Path.Combine(RootDirectory, "country-status-index"), MutableSegmentMaxItemCount, SparseArrayStepSize, KeyCacheSize, ValueCacheSize);
    CreatedAtIndex = OpenStringLongTree(Path.Combine(RootDirectory, "created-at-index"), MutableSegmentMaxItemCount, SparseArrayStepSize, KeyCacheSize, ValueCacheSize);
    ReputationIndex = OpenStringLongTree(Path.Combine(RootDirectory, "reputation-index"), MutableSegmentMaxItemCount, SparseArrayStepSize, KeyCacheSize, ValueCacheSize);
    Maintainers.Clear();
    AddMaintainer(Profiles.CreateMaintainer());
    AddMaintainer(EmailIndex.CreateMaintainer());
    AddMaintainer(CountryStatusIndex.CreateMaintainer());
    AddMaintainer(CreatedAtIndex.CreateMaintainer());
    AddMaintainer(ReputationIndex.CreateMaintainer());
    return Task.CompletedTask;
  }

  public Task InsertBatchAsync(IReadOnlyList<UserProfile> profiles, CancellationToken ct)
  {
    foreach (var profile in profiles)
    {
      ct.ThrowIfCancellationRequested();
      Profiles!.Upsert(profile.UserId, ProfileCodec.Serialize(profile));
      EmailIndex!.Upsert(profile.Email, profile.UserId);
      CountryStatusIndex!.Upsert(ProfileKeys.CountryStatus(profile), profile.UserId);
      CreatedAtIndex!.Upsert(ProfileKeys.CreatedAt(profile), profile.UserId);
      ReputationIndex!.Upsert(ProfileKeys.Reputation(profile), profile.UserId);
    }
    return Task.CompletedTask;
  }

  public Task<UserProfile?> GetByUserIdAsync(long userId, CancellationToken ct)
  {
    return Task.FromResult(TryGetProfile(userId, out var profile) ? profile : null);
  }

  public Task<UserProfile?> GetByEmailAsync(string email, CancellationToken ct)
  {
    if (!EmailIndex!.TryGet(email, out var userId))
      return Task.FromResult<UserProfile?>(null);
    return GetByUserIdAsync(userId, ct);
  }

  public Task<IReadOnlyList<UserProfile>> QueryCountryStatusAsync(
      string country,
      string status,
      int limit,
      CancellationToken ct)
  {
    var prefix = ProfileKeys.CountryStatusPrefix(country, status);
    return Task.FromResult(ScanStringIndex(CountryStatusIndex!, prefix, key => key.StartsWith(prefix, StringComparison.Ordinal), limit, ct));
  }

  public Task<IReadOnlyList<long>> ScanCountryStatusIndexAsync(
      string country,
      string status,
      int limit,
      CancellationToken ct)
  {
    var prefix = ProfileKeys.CountryStatusPrefix(country, status);
    return Task.FromResult(ScanStringIndexValues(CountryStatusIndex!, prefix, key => key.StartsWith(prefix, StringComparison.Ordinal), limit, ct));
  }

  public Task<IReadOnlyList<UserProfile>> QueryCreatedAtRangeAsync(
      long fromUnixMs,
      long toUnixMs,
      int limit,
      CancellationToken ct)
  {
    var start = ProfileKeys.CreatedAt(fromUnixMs, 0);
    var end = ProfileKeys.CreatedAt(toUnixMs, long.MaxValue);
    return Task.FromResult(ScanStringIndex(CreatedAtIndex!, start, key => string.CompareOrdinal(key, end) <= 0, limit, ct));
  }

  public Task<IReadOnlyList<long>> ScanCreatedAtRangeIndexAsync(
      long fromUnixMs,
      long toUnixMs,
      int limit,
      CancellationToken ct)
  {
    var start = ProfileKeys.CreatedAt(fromUnixMs, 0);
    var end = ProfileKeys.CreatedAt(toUnixMs, long.MaxValue);
    return Task.FromResult(ScanStringIndexValues(CreatedAtIndex!, start, key => string.CompareOrdinal(key, end) <= 0, limit, ct));
  }

  public Task<IReadOnlyList<UserProfile>> QueryTopReputationAsync(int limit, CancellationToken ct)
  {
    return Task.FromResult(ScanStringIndex(ReputationIndex!, "", _ => true, limit, ct));
  }

  public Task<IReadOnlyList<long>> ScanTopReputationIndexAsync(int limit, CancellationToken ct)
  {
    return Task.FromResult(ScanStringIndexValues(ReputationIndex!, "", _ => true, limit, ct));
  }

  public async Task UpdateBatchAsync(IReadOnlyList<UserProfile> profiles, CancellationToken ct)
  {
    foreach (var profile in profiles)
    {
      ct.ThrowIfCancellationRequested();
      var old = await GetByUserIdAsync(profile.UserId, ct)
          ?? throw new InvalidOperationException($"Missing profile {profile.UserId}");

      CountryStatusIndex!.ForceDelete(ProfileKeys.CountryStatus(old));
      ReputationIndex!.ForceDelete(ProfileKeys.Reputation(old));

      Profiles!.Upsert(profile.UserId, ProfileCodec.Serialize(profile));
      CountryStatusIndex.Upsert(ProfileKeys.CountryStatus(profile), profile.UserId);
      ReputationIndex.Upsert(ProfileKeys.Reputation(profile), profile.UserId);
    }
  }

  public Task StabilizeForReadMeasurementsAsync(CancellationToken ct) => SettleAsync(ct);

  public async Task SettleAsync(CancellationToken ct)
  {
    foreach (var maintainer in Maintainers)
    {
      ct.ThrowIfCancellationRequested();
      maintainer.EvictToDisk();
    }
    foreach (var maintainer in Maintainers)
    {
      ct.ThrowIfCancellationRequested();
      await maintainer.WaitForBackgroundThreadsAsync();
    }
    SaveMetaData();
  }

  public Task<long> CountProfilesAsync(CancellationToken ct)
  {
    return Task.FromResult(Profiles!.Count());
  }

  public Task<long> GetStorageSizeBytesAsync(CancellationToken ct)
  {
    return Task.FromResult(Directory.Exists(RootDirectory) ? GetDirectorySize(RootDirectory) : 0L);
  }

  public async ValueTask DisposeAsync()
  {
    foreach (var maintainer in Maintainers)
    {
      await maintainer.WaitForBackgroundThreadsAsync();
    }
    foreach (var maintainer in Maintainers)
      maintainer.Dispose();
    Maintainers.Clear();

    Profiles?.Dispose();
    EmailIndex?.Dispose();
    CountryStatusIndex?.Dispose();
    CreatedAtIndex?.Dispose();
    ReputationIndex?.Dispose();
  }

  static IZoneTree<long, string> OpenLongStringTree(
      string path,
      int mutableSegmentMaxItemCount,
      int sparseArrayStepSize,
      int keyCacheSize,
      int valueCacheSize)
  {
    return new ZoneTreeFactory<long, string>()
        .SetDataDirectory(path)
        .SetMutableSegmentMaxItemCount(mutableSegmentMaxItemCount)
        .SetLogLevel(LogLevel.Error)
        .SetComparer(new Int64ComparerAscending())
        .SetKeySerializer(new Int64Serializer())
        .SetValueSerializer(new Utf8StringSerializer())
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DefaultSparseArrayStepSize = sparseArrayStepSize;
          options.KeyCacheSize = keyCacheSize;
          options.ValueCacheSize = valueCacheSize;
        })
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.AsyncCompressed)
        .OpenOrCreate();
  }

  static IZoneTree<string, long> OpenStringLongTree(
      string path,
      int mutableSegmentMaxItemCount,
      int sparseArrayStepSize,
      int keyCacheSize,
      int valueCacheSize)
  {
    return new ZoneTreeFactory<string, long>()
        .SetDataDirectory(path)
        .SetMutableSegmentMaxItemCount(mutableSegmentMaxItemCount)
        .SetLogLevel(LogLevel.Error)
        .SetComparer(new StringOrdinalComparerAscending())
        .SetKeySerializer(new Utf8StringSerializer())
        .SetValueSerializer(new Int64Serializer())
        .ConfigureDiskSegmentOptions(options =>
        {
          options.DefaultSparseArrayStepSize = sparseArrayStepSize;
          options.KeyCacheSize = keyCacheSize;
          options.ValueCacheSize = valueCacheSize;
        })
        .ConfigureWriteAheadLogOptions(options => options.WriteAheadLogMode = WriteAheadLogMode.AsyncCompressed)
        .OpenOrCreate();
  }

  bool TryGetProfile(long userId, out UserProfile profile)
  {
    if (Profiles!.TryGet(userId, out var value))
    {
      profile = ProfileCodec.Deserialize(value);
      return true;
    }
    profile = null!;
    return false;
  }

  IReadOnlyList<UserProfile> ScanStringIndex(
      IZoneTree<string, long> index,
      string startKey,
      Func<string, bool> keepGoing,
      int limit,
      CancellationToken ct)
  {
    var results = new List<UserProfile>(limit);
    using var iterator = CreateIndexIterator(index);
    if (startKey.Length == 0)
      iterator.SeekFirst();
    else
      iterator.Seek(startKey);

    while (results.Count < limit && iterator.Next())
    {
      ct.ThrowIfCancellationRequested();
      if (!keepGoing(iterator.CurrentKey))
        break;
      if (TryGetProfile(iterator.CurrentValue, out var profile))
        results.Add(profile);
    }
    return results;
  }

  IReadOnlyList<long> ScanStringIndexValues(
      IZoneTree<string, long> index,
      string startKey,
      Func<string, bool> keepGoing,
      int limit,
      CancellationToken ct)
  {
    var results = new List<long>(limit);
    using var iterator = CreateIndexIterator(index);
    if (startKey.Length == 0)
      iterator.SeekFirst();
    else
      iterator.Seek(startKey);

    while (results.Count < limit && iterator.Next())
    {
      ct.ThrowIfCancellationRequested();
      if (!keepGoing(iterator.CurrentKey))
        break;
      results.Add(iterator.CurrentValue);
    }
    return results;
  }

  void SaveMetaData()
  {
    Profiles!.Maintenance.SaveMetaData();
    EmailIndex!.Maintenance.SaveMetaData();
    CountryStatusIndex!.Maintenance.SaveMetaData();
    CreatedAtIndex!.Maintenance.SaveMetaData();
    ReputationIndex!.Maintenance.SaveMetaData();
  }

  IZoneTreeIterator<string, long> CreateIndexIterator(IZoneTree<string, long> index)
  {
    return index.CreateIterator(new IteratorOptions
    {
      IteratorType = IteratorType.NoRefresh,
      ContributeToTheBlockCache = true,
      DiskSegmentPrefetchSize = IteratorPrefetchSize
    });
  }

  void AddMaintainer(IMaintainer maintainer)
  {
    maintainer.BlockCacheLifeTime = BlockCacheLifeTime;
    maintainer.InactiveBlockCacheCleanupInterval = TimeSpan.FromMilliseconds(BlockCacheLifeTime.TotalMilliseconds / 2);
    Maintainers.Add(maintainer);
  }

  static long GetDirectorySize(string path)
  {
    return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .Sum(file => new FileInfo(file).Length);
  }
}
