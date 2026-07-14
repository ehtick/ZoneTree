namespace ZoneTree.Hashers;

/// <summary>
/// Defines a method that computes a hash code for a key.
/// </summary>
/// <typeparam name="TKey">Key type</typeparam>
/// <remarks>
/// Keys considered equal by the configured key comparer must produce the same
/// hash code. Custom comparer and hasher compatibility is the caller's
/// responsibility.
/// </remarks>
public interface IKeyHasher<TKey>
{
  /// <summary>
  /// Computes a hash code for the specified key.
  /// </summary>
  /// <param name="key">The key to hash.</param>
  /// <returns>The hash code for the key.</returns>
  int GetHashCode(in TKey key);
}
