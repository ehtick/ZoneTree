using ZoneTree.Segments.Disk;

namespace ZoneTree.UnitTests;

public sealed class CircularCacheTests
{
  [Test]
  public void DistributesIndexesWithIdenticalLowBits()
  {
    var cache = new CircularCache<int>(8, 10_000);
    var first = 1;
    var second = 2;

    cache.TryAdd(0, ref first);
    cache.TryAdd(8, ref second);

    Assert.That(cache.TryGet(0, out var firstResult), Is.True);
    Assert.That(firstResult, Is.EqualTo(first));
    Assert.That(cache.TryGet(8, out var secondResult), Is.True);
    Assert.That(secondResult, Is.EqualTo(second));
  }
}
