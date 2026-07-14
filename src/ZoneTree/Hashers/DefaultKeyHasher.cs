using System.Runtime.CompilerServices;

namespace ZoneTree.Hashers;

/// <summary>
/// Computes key hashes using the default equality comparer for the key type.
/// </summary>
/// <typeparam name="TKey">Key type</typeparam>
public sealed class DefaultKeyHasher<TKey> : IKeyHasher<TKey>
{
  readonly EqualityComparer<TKey> EqualityComparer =
      EqualityComparer<TKey>.Default;

  /// <inheritdoc/>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public int GetHashCode(in TKey key)
  {
    return EqualityComparer.GetHashCode(key);
  }
}
