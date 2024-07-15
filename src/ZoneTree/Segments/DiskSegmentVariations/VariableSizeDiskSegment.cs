﻿using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using Tenray.ZoneTree.Exceptions;
using Tenray.ZoneTree.Options;
using Tenray.ZoneTree.Segments.Disk;
using Tenray.ZoneTree.Segments.Model;
using Tenray.ZoneTree.Segments.RandomAccess;
using Tenray.ZoneTree.Serializers;

namespace Tenray.ZoneTree.Segments.DiskSegmentVariations;

public sealed partial class VariableSizeDiskSegment<TKey, TValue> : DiskSegment<TKey, TValue>
{
    readonly IRandomAccessDevice DataHeaderDevice;

    CircularCache<TKey> CircularCache { get; }

    public override int ReadBufferCount =>
        (DataDevice?.ReadBufferCount ?? 0) + (DataHeaderDevice?.ReadBufferCount ?? 0);

    readonly ZoneTreeOptions<TKey, TValue> Options;

    public VariableSizeDiskSegment(
        long segmentId,
        ZoneTreeOptions<TKey, TValue> options) : base(segmentId, options)
    {
        Options = options;
        EnsureKeyAndValueTypesAreSupported();
        var randomDeviceManager = options.RandomAccessDeviceManager;
        var diskOptions = options.DiskSegmentOptions;
        DataHeaderDevice = randomDeviceManager
                .GetReadOnlyDevice(
                    SegmentId,
                    DiskSegmentConstants.DataHeaderCategory,
                    diskOptions.EnableCompression,
                    diskOptions.CompressionBlockSize,
                    diskOptions.BlockCacheLimit,
                    diskOptions.CompressionMethod,
                    diskOptions.CompressionLevel,
                    diskOptions.BlockCacheReplacementWarningDuration
                    );
        DataDevice = randomDeviceManager
            .GetReadOnlyDevice(
                SegmentId,
                DiskSegmentConstants.DataCategory,
                diskOptions.EnableCompression,
                diskOptions.CompressionBlockSize,
                diskOptions.BlockCacheLimit,
                diskOptions.CompressionMethod,
                diskOptions.CompressionLevel,
                diskOptions.BlockCacheReplacementWarningDuration);
        InitDataLength();

        CircularCache = new CircularCache<TKey>(diskOptions.KeyCacheSize, diskOptions.KeyCacheRecordLifeTimeInMillisecond);
        LoadDefaultSparseArray();
    }

    public VariableSizeDiskSegment(long segmentId,
        ZoneTreeOptions<TKey, TValue> options,
        IRandomAccessDevice dataHeaderDevice,
        IRandomAccessDevice dataDevice) : base(segmentId, options, dataDevice)
    {
        DataHeaderDevice = dataHeaderDevice;
        Options = options;
        EnsureKeyAndValueTypesAreSupported();
        InitDataLength();
        var diskOptions = options.DiskSegmentOptions;
        CircularCache = new CircularCache<TKey>(diskOptions.KeyCacheSize, diskOptions.KeyCacheRecordLifeTimeInMillisecond);
        LoadDefaultSparseArray();
    }

    static void EnsureKeyAndValueTypesAreSupported()
    {
        var hasFixedSizeKey = !RuntimeHelpers.IsReferenceOrContainsReferences<TKey>();
        var hasFixedSizeValue = !RuntimeHelpers.IsReferenceOrContainsReferences<TValue>();
        if (hasFixedSizeKey || hasFixedSizeValue)
        {
            throw new Exception("The FixedSizeKeyDiskSegment requires variable size key and variable size value.");
        }
    }

    unsafe void InitDataLength()
    {
        Length = DataHeaderDevice.Length / sizeof(EntryHead);
    }

    void LoadDefaultSparseArray()
    {
        var options = Options;
        var diskOptions = options.DiskSegmentOptions;
        if (diskOptions.DefaultSparseArrayStepSize == 0) return;
        var deviceManager = options.RandomAccessDeviceManager;
        if (!deviceManager.DeviceExists(
            SegmentId,
            DiskSegmentConstants.SparseArrayCategory,
            diskOptions.EnableCompression))
            return;
        using var sparseArrayDevice = deviceManager.GetReadOnlyDevice(
            SegmentId,
            DiskSegmentConstants.SparseArrayCategory,
            diskOptions.EnableCompression,
            diskOptions.CompressionBlockSize,
            diskOptions.BlockCacheLimit,
            diskOptions.CompressionMethod,
            diskOptions.CompressionLevel,
            diskOptions.BlockCacheReplacementWarningDuration);
        var recordCount = BinarySerializerHelper.FromByteArray<int>(sparseArrayDevice.GetBytes(0, sizeof(int)));
        var offset = sizeof(int);
        var sparseArray = new SparseArrayEntry<TKey, TValue>[recordCount];
        for (var i = 0; i < recordCount; ++i)
        {
            var keySize = BinarySerializerHelper.FromByteArray<int>(sparseArrayDevice.GetBytes(offset, sizeof(int)));
            offset += sizeof(int);
            var key = KeySerializer.Deserialize(sparseArrayDevice.GetBytes(offset, keySize));
            offset += keySize;

            var valueSize = BinarySerializerHelper.FromByteArray<int>(sparseArrayDevice.GetBytes(offset, sizeof(int)));
            offset += sizeof(int);
            var value = ValueSerializer.Deserialize(sparseArrayDevice.GetBytes(offset, valueSize));
            offset += valueSize;

            var index = BinarySerializerHelper.FromByteArray<int>(sparseArrayDevice.GetBytes(offset, sizeof(long)));
            offset += sizeof(long);

            var entry = new SparseArrayEntry<TKey, TValue>(key, value, index);
            sparseArray[i] = entry;
        }
        sparseArrayDevice.Close();
    }

    public override void SetDefaultSparseArray(IReadOnlyList<SparseArrayEntry<TKey, TValue>> defaultSparseArray)
    {
        SparseArray = defaultSparseArray;
        var options = Options;
        var diskOptions = options.DiskSegmentOptions;
        var deviceManager = options.RandomAccessDeviceManager;
        using var sparseArrayDevice = deviceManager.CreateWritableDevice(
            SegmentId,
            DiskSegmentConstants.SparseArrayCategory,
            diskOptions.EnableCompression,
            diskOptions.CompressionBlockSize,
            diskOptions.BlockCacheLimit,
            true,
            false,
            diskOptions.CompressionMethod,
            diskOptions.CompressionLevel,
            diskOptions.BlockCacheReplacementWarningDuration);
        var sparseArray = SparseArray;
        var recordCount = sparseArray.Count;
        sparseArrayDevice.AppendBytesReturnPosition(BitConverter.GetBytes(recordCount));
        for (var i = 0; i < recordCount; ++i)
        {
            var entry = sparseArray[i];

            var keyBytes = KeySerializer.Serialize(entry.Key);
            sparseArrayDevice.AppendBytesReturnPosition(BitConverter.GetBytes(keyBytes.Length));
            sparseArrayDevice.AppendBytesReturnPosition(keyBytes);

            var valueBytes = ValueSerializer.Serialize(entry.Value);
            sparseArrayDevice.AppendBytesReturnPosition(BitConverter.GetBytes(valueBytes.Length));
            sparseArrayDevice.AppendBytesReturnPosition(valueBytes);

            sparseArrayDevice.AppendBytesReturnPosition(BitConverter.GetBytes(entry.Index));
        }
        sparseArrayDevice.SealDevice();
        sparseArrayDevice.Close();
    }

    protected unsafe override TKey ReadKey(long index)
    {
        try
        {
            if (CircularCache.TryGetFromCache(index, out var key)) return key;
            Interlocked.Increment(ref ReadCount);
            if (IsDroppping)
            {
                throw new DiskSegmentIsDroppingException();
            }
            var headBytes = DataHeaderDevice.GetBytes(index * sizeof(EntryHead), sizeof(KeyHead));
            var head = BinarySerializerHelper.FromByteArray<KeyHead>(headBytes);
            var keyBytes = DataDevice.GetBytes(head.KeyOffset, head.KeyLength);
            key = KeySerializer.Deserialize(keyBytes);
            CircularCache.TryAddToTheCache(index, ref key);
            return key;
        }
        finally
        {
            Interlocked.Decrement(ref ReadCount);
        }
    }

    protected unsafe override TValue ReadValue(long index)
    {
        try
        {
            Interlocked.Increment(ref ReadCount);
            if (IsDroppping)
            {
                throw new DiskSegmentIsDroppingException();
            }
            var headBytes = DataHeaderDevice.GetBytes(index * sizeof(EntryHead) + sizeof(KeyHead), sizeof(ValueHead));
            var head = BinarySerializerHelper.FromByteArray<ValueHead>(headBytes);
            var valueBytes = DataDevice.GetBytes(head.ValueOffset, head.ValueLength);
            return ValueSerializer.Deserialize(valueBytes);
        }
        finally
        {
            Interlocked.Decrement(ref ReadCount);
        }
    }

    protected override void DeleteDevices()
    {
        DataHeaderDevice?.Delete();
        DataDevice?.Delete();
    }

    public override void ReleaseResources()
    {
        DataHeaderDevice?.Dispose();
        DataDevice?.Dispose();
    }

    public override int ReleaseReadBuffers(long ticks)
    {
        var a = DataHeaderDevice?.ReleaseReadBuffers(ticks) ?? 0;
        var b = DataDevice?.ReleaseReadBuffers(ticks) ?? 0;
        return a + b;
    }
}
