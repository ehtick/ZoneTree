﻿namespace Playground.Benchmark;

public static class TestConfig
{
    public static bool RecreateDatabases = true;

    public static int ThresholdForMergeOperationStart = 2_000_000;

    public static int MutableSegmentCount = 1_000_000;

    public static bool EnableIncrementalBackup = true;

    public static bool EnableDiskSegmentCompression = true;

    public static int WALCompressionBlockSize = 32768;
}