namespace ZoneTree.Segments.Block;

public sealed class SingleBlockPin(DecompressedBlock device, bool contributeToTheBlockCache)
{
  public DecompressedBlock Device = device;

  public bool ContributeToTheBlockCache = contributeToTheBlockCache;

  public void SetDevice(DecompressedBlock device) { Device = device; }
}


