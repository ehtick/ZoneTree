namespace ZoneTree.Hashers;

public ref struct KeyHashProvider<TKey>
{
  // Hash code 0 is the missing sentinel. If a key's hash is exactly 0
  // (1 / 2^32 for a well-distributed hash), it may be recalculated.
  const int MissingHashCode = 0;

  int HashCode;

  public KeyHashProvider()
  {
    HashCode = MissingHashCode;
  }

  public int GetHashCode(in TKey key, IKeyHasher<TKey> keyHasher)
  {
    if (HashCode == MissingHashCode)
      HashCode = keyHasher.GetHashCode(in key);
    return HashCode;
  }
}
