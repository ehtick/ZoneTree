using ZoneTree.FasterBench;

try
{
  var config = BenchmarkConfig.Parse(args);
  new BenchmarkRunner(config).Run();
}
catch (HelpRequestedException)
{
  PrintHelp();
}
catch (Exception exception)
{
  Console.Error.WriteLine(exception);
  Environment.ExitCode = 1;
}

static void PrintHelp()
{
  Console.WriteLine(
      """
      ZoneTree vs FASTER insert and point-lookup benchmark

      Usage:
        dotnet run -c Release -- [options]

      Options:
        --records <count>            Records inserted per run (default: 1M)
        --reads <count>              Successful random lookups per run (default: 1M)
        --parallelism <list>         Worker counts (default: 1,16)
        --iterations <count>         Measured runs per engine/P level (default: 3)
        --warmup <count>             Warmup records per engine/P level (default: 20K)
        --engine <list>              zonetree,faster (default: both)
        --data <path>                Temporary data directory (default: data)
        --faster-index-size <count>  FASTER hash-index size (default: 1M)
        --faster-memory-bits <bits>  FASTER hybrid-log memory bits (default: 30)
        --seed <value>               Random lookup seed
        --help                       Show this help

      Counts accept K and M suffixes.
      """);
}
