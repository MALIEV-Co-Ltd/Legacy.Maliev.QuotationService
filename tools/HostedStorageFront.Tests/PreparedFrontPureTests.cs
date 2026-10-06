using System.Net;
using System.Text;
using Xunit;

namespace InvoiceCompletionProducerAcceptance.Companion;

/// <summary>Authored future controls only; currently uncompiled and outside the publication candidate.</summary>
public sealed class PreparedFrontPureTests
{
    private const string Header = " sl local_address rem_address st tx_queue rx_queue tr tm retr uid inode\n";

    [Fact]
    public void ActualClientTupleMatchesIpv4KernelInode()
    {
        string table = Header + "0: 0100007F:C350 0100007F:AFC9 01 0 0 0 0 0 456\n";
        Assert.Equal("456", FileSdkCallerGuard.FindClientInode([table], IPAddress.Loopback, 50000, IPAddress.Loopback, 45001));
    }

    [Theory]
    [InlineData("02")]
    [InlineData("0A")]
    [InlineData("08")]
    public void NonEstablishedSocketCannotGrantSdkAuthority(string state)
    {
        string table = Header + $"0: 0100007F:C350 0100007F:AFC9 {state} 0 0 0 0 0 456\n";
        Assert.Throws<InvalidDataException>(() => FileSdkCallerGuard.FindClientInode([table], IPAddress.Loopback, 50000, IPAddress.Loopback, 45001));
    }

    [Fact]
    public void DuplicateTupleOwnersCannotGrantSdkAuthority()
    {
        string row = "0: 0100007F:C350 0100007F:AFC9 01 0 0 0 0 0 456\n";
        Assert.Throws<InvalidDataException>(() => FileSdkCallerGuard.FindClientInode([Header + row + row],
            IPAddress.Loopback, 50000, IPAddress.Loopback, 45001));
    }

    [Fact]
    public async Task AsyncInputByteLimitIsCheckedBeforeUnlimitedRead()
    {
        await using var source = new MemoryStream(Encoding.ASCII.GetBytes("12345"));
        await using var bounded = new BoundedLeaseReadStream(source, 4, DateTimeOffset.UtcNow.AddMinutes(1));
        byte[] buffer = new byte[1024];
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            int read = await bounded.ReadAsync(buffer);
            Assert.InRange(read, 0, buffer.Length);
        });
        Assert.Equal(5, source.Position);
    }

    [Fact]
    public async Task ExpiredLeaseFailsBeforeSourceRead()
    {
        await using var source = new MemoryStream(Encoding.ASCII.GetBytes("12345"));
        await using var bounded = new BoundedLeaseReadStream(source, 4, DateTimeOffset.UtcNow.AddSeconds(-1));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            byte[] buffer = new byte[1024];
            int read = await bounded.ReadAsync(buffer);
            Assert.InRange(read, 0, buffer.Length);
        });
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public void WrapperDisposalRetainsApplicationOwnedBody()
    {
        using var source = new MemoryStream(Encoding.ASCII.GetBytes("1234"));
        var bounded = new BoundedLeaseReadStream(source, 4, DateTimeOffset.UtcNow.AddMinutes(1));
        bounded.Dispose();
        Assert.True(source.CanRead);
    }
}
