namespace InvoiceCompletionProducerAcceptance.Companion;

/// <summary>Read-only transport wrapper; keeps request bytes bounded and the owner lease current.</summary>
public sealed class BoundedLeaseReadStream(Stream source, long maximum, DateTimeOffset expiresUtc) : Stream
{
    private long consumed;
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => consumed; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        Check();
        int read = source.Read(buffer, offset, (int)Math.Min(count, maximum - consumed + 1));
        Account(read); return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Check(); cancellationToken.ThrowIfCancellationRequested();
        int read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, maximum - consumed + 1)], cancellationToken);
        Account(read); return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Account(int count)
    {
        consumed += count;
        if (consumed > maximum) throw new InvalidDataException("Actual SDK request body exceeded bounded copy.");
        Check();
    }

    private void Check()
    {
        if (maximum <= 0 || DateTimeOffset.UtcNow >= expiresUtc) throw new InvalidDataException("Finite request stream lease required.");
    }

    // Source is the application-owned request body; disposing this wrapper must not close that foreign handle.
}
