using DocumentForge.Core;

namespace DocumentForge.Transactions;

public enum WalEntryType : byte
{
    BeginTransaction = 1,
    PageWrite = 2,
    CommitTransaction = 3,
    RollbackTransaction = 4,
    Checkpoint = 5
}

public sealed class WalEntry
{
    public ulong TransactionId { get; init; }
    public WalEntryType EntryType { get; init; }
    public PageId PageId { get; init; }
    public byte[]? BeforeImage { get; init; }
    public byte[]? AfterImage { get; init; }
}

public sealed class WalWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly object _lock = new();
    private bool _disposed;

    public long Length => _stream.Length;

    public WalWriter(string filePath)
    {
        _stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.SequentialScan);
        _stream.Seek(0, SeekOrigin.End);
    }

    public void WriteBegin(ulong transactionId)
    {
        WriteEntry(new WalEntry
        {
            TransactionId = transactionId,
            EntryType = WalEntryType.BeginTransaction,
            PageId = PageId.Invalid
        });
    }

    public void WritePageChange(ulong transactionId, PageId pageId, byte[] beforeImage, byte[] afterImage)
    {
        WriteEntry(new WalEntry
        {
            TransactionId = transactionId,
            EntryType = WalEntryType.PageWrite,
            PageId = pageId,
            BeforeImage = beforeImage,
            AfterImage = afterImage
        });
    }

    public void WriteCommit(ulong transactionId)
    {
        WriteEntry(new WalEntry
        {
            TransactionId = transactionId,
            EntryType = WalEntryType.CommitTransaction,
            PageId = PageId.Invalid
        });
        lock (_lock) _stream.Flush(true); // fsync on commit
    }

    public void WriteRollback(ulong transactionId)
    {
        WriteEntry(new WalEntry
        {
            TransactionId = transactionId,
            EntryType = WalEntryType.RollbackTransaction,
            PageId = PageId.Invalid
        });
    }

    private void WriteEntry(WalEntry entry)
    {
        lock (_lock)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // Placeholder for entry length
            writer.Write((int)0);
            writer.Write(entry.TransactionId);
            writer.Write((byte)entry.EntryType);
            writer.Write(entry.PageId.Value);

            if (entry.EntryType == WalEntryType.PageWrite)
            {
                writer.Write(entry.BeforeImage!);
                writer.Write(entry.AfterImage!);
            }

            // Calculate and write checksum
            var data = ms.ToArray();
            var checksum = CalculateChecksum(data, 4, data.Length - 4);
            writer.Write(checksum);

            // Write total length
            var totalLength = (int)ms.Position;
            ms.Position = 0;
            writer.Write(totalLength);

            _stream.Write(ms.ToArray());
        }
    }

    public void Truncate()
    {
        lock (_lock)
        {
            _stream.SetLength(0);
            _stream.Flush(true);
        }
    }

    private static uint CalculateChecksum(byte[] data, int offset, int length)
    {
        uint hash = 0;
        for (int i = offset; i < offset + length; i++)
        {
            hash = (hash << 5) + hash + data[i];
        }
        return hash;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _stream.Dispose();
            _disposed = true;
        }
    }
}
