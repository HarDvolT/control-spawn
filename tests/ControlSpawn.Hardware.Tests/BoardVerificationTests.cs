using Xunit;

namespace ControlSpawn.Hardware.Tests;

public sealed class BoardVerificationTests
{
    private sealed class ManualTime : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
    }

    [Theory]
    [InlineData("ROG STRIX B850-F GAMING WIFI", true)]
    [InlineData("Unknown board", false)]
    public void FailedLookupKeepsTheBoardUnverifiedAndRetriesAtThirtySeconds(string product, bool expected)
    {
        var time = new ManualTime();
        var check = new BoardVerification(time);
        int attempts = 0;
        string ReadProduct() => ++attempts <= 2 ? throw new IOException("SMBIOS unavailable") : product;

        check.Check(ReadProduct);
        Assert.False(check.Verified);
        time.Elapsed = TimeSpan.FromSeconds(29);
        check.Check(ReadProduct);
        Assert.Equal(1, attempts);
        Assert.False(check.Verified);

        time.Elapsed = TimeSpan.FromSeconds(30);
        check.Check(ReadProduct);
        Assert.Equal(2, attempts);
        Assert.False(check.Verified);
        time.Elapsed = TimeSpan.FromSeconds(59);
        check.Check(ReadProduct);
        Assert.Equal(2, attempts);
        Assert.False(check.Verified);

        time.Elapsed = TimeSpan.FromSeconds(60);
        check.Check(ReadProduct);
        Assert.Equal(3, attempts);
        Assert.Equal(expected, check.Verified);
        time.Elapsed = TimeSpan.FromSeconds(90);
        check.Check(ReadProduct);
        Assert.Equal(3, attempts);
        Assert.Equal(expected, check.Verified);
    }
}
