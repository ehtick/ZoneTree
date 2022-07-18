﻿using ZoneTree.Core;
using ZoneTree.Serializers;

namespace ZoneTree.Transactional;

public sealed class OptimisticTransaction<TKey, TValue>
{
    public long TransactionId { get; }

    readonly ZoneTreeOptions<TKey, TValue> Options;
    
    readonly ITransactionLog<TKey, TValue> TransactionLog;

    public bool IsReadyToCommit { get; set; }

    public OptimisticTransaction(
        long transactionId,
        ZoneTreeOptions<TKey, TValue> options,
        ITransactionLog<TKey, TValue> transactionLog)
    {
        TransactionId = transactionId;
        Options = options;
        TransactionLog = transactionLog;
    }

    /// <summary>
    /// Marks read stamp, adds dependencies or aborts transaction.
    /// https://en.wikipedia.org/wiki/Timestamp-based_concurrency_control
    /// </summary>
    /// <param name="readWriteStamp"></param>
    /// <returns>Optimistic read action.</returns>
    public OptimisticReadAction HandleReadKey(
        ref ReadWriteStamp readWriteStamp)
    {
        if (readWriteStamp.WriteStamp > TransactionId)
        {
            return OptimisticReadAction.Abort;
        }

        if (readWriteStamp.WriteStamp != 0)
            TransactionLog.AddDependency(TransactionId, readWriteStamp.WriteStamp);
        readWriteStamp.ReadStamp = Math.Max(readWriteStamp.ReadStamp, TransactionId);
        return OptimisticReadAction.Read;
    }

    /// <summary>
    /// Marks write stamp, adds old values or aborts transaction.
    /// Returns SkipWrite for skipping writes. (Thomas Write Rule)
    /// https://en.wikipedia.org/wiki/Thomas_Write_Rule
    /// </summary>
    /// <param name="readWriteStamp"></param>
    /// <param name="key"></param>
    /// <param name="hasOldValue"></param>
    /// <param name="oldValue"></param>
    /// <returns>Optimistic write action</returns>
    public OptimisticWriteAction HandleWriteKey(
        ref ReadWriteStamp readWriteStamp,
        in TKey key,
        bool hasOldValue, 
        in TValue oldValue)
    {
        if (readWriteStamp.ReadStamp > TransactionId)
        {
            return OptimisticWriteAction.Abort;
        }

        if (readWriteStamp.WriteStamp > TransactionId)
            return OptimisticWriteAction.SkipWrite;

        var value = oldValue;
        if (!hasOldValue)
            Options.MarkValueDeleted(ref value);

        var combinedValue = 
            new CombinedValue<TValue, long>(value, readWriteStamp.WriteStamp);
        TransactionLog.AddHistory(TransactionId, key, combinedValue);
        readWriteStamp.WriteStamp = TransactionId;
        return OptimisticWriteAction.Write;
    }
}