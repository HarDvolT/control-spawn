using System.Text.Json;
using ControlSpawn.Core;
using Xunit;

namespace ControlSpawn.Lighting.Tests;

public sealed class LampHandoffTests
{
    private static readonly LampReading Orange = new(true, 100, LampReading.ColorMode, 4000, 0xFF4D1A);
    private static readonly LampReading PhoneWhite = new(true, 100, LampReading.WhiteMode, 5000, 0xFF4D1A);

    [Fact]
    public void ReadingsParseFromStringsOrNumbersAndRejectAnythingElse()
    {
        Assert.Equal(new LampReading(true, 80, 2, 2700, 16711680), LampReading.Parse(JsonSerializer.SerializeToElement(new[] { "on", "80", "16711680", "2700", "2" })));
        Assert.Equal(new LampReading(false, 5, 1, 4000, 255), LampReading.Parse(JsonSerializer.SerializeToElement(new object[] { "off", 5, 255, 4000, 1 })));
        Assert.Null(LampReading.Parse(JsonSerializer.SerializeToElement(new[] { "on", "80" })));
        Assert.Null(LampReading.Parse(JsonSerializer.SerializeToElement(new[] { "maybe", "80", "1", "2700", "2" })));
    }

    /// <summary>The owner set a white on the phone, then opened the app, which painted the saved colour over it (2026-11-03).</summary>
    [Fact]
    public void ABulbChangedWhileTheServiceWasAwayIsLeftAlone()
    {
        var handoff = new LampHandoff(Orange);
        Assert.True(handoff.Observe(PhoneWhite, moving: false));
        Assert.True(handoff.HandedOff);
        Assert.Equal(PhoneWhite, handoff.Current);
        Assert.False(handoff.Observe(PhoneWhite with { Brightness = 40 }, moving: false));   // stays handed off, tracking the bulb
        Assert.Equal(40, handoff.Current!.Brightness);
        Assert.Equal(Orange, handoff.Left);   // what Control Spawn left is kept, so a restart still sees the change
    }

    [Fact]
    public void AFirstEverContactAndAnUnchangedBulbAreTakenOver()
    {
        Assert.False(new LampHandoff().Observe(PhoneWhite, moving: false));
        var handoff = new LampHandoff(Orange);
        Assert.False(handoff.Observe(Orange, moving: false));
        Assert.False(handoff.HandedOff);
    }

    [Fact]
    public void StreamingColoursAreExpectedButAWhiteOrABrightnessFromThePhoneIsNot()
    {
        var handoff = new LampHandoff(Orange);
        Assert.False(handoff.Observe(Orange with { Rgb = 0x3355FF }, moving: true));
        Assert.False(handoff.Observe(Orange with { Rgb = 0x00FF00 }, moving: true));
        Assert.True(new LampHandoff(Orange).Observe(Orange with { Rgb = 0x00FF00 }, moving: false));
        Assert.True(new LampHandoff(Orange).Observe(PhoneWhite, moving: true));
        Assert.True(new LampHandoff(Orange).Observe(Orange with { Brightness = 30 }, moving: true));
        Assert.True(new LampHandoff(Orange with { On = false }).Observe(Orange, moving: false));   // switched on from the phone
    }

    [Fact]
    public void SmallDifferencesAndAnOffBulbsColourDontCount()
    {
        Assert.False(LampHandoff.ChangedElsewhere(PhoneWhite, PhoneWhite with { Kelvin = 5040, Brightness = 99 }, false));
        Assert.True(LampHandoff.ChangedElsewhere(PhoneWhite, PhoneWhite with { Kelvin = 3000 }, false));
        Assert.False(LampHandoff.ChangedElsewhere(Orange with { On = false }, PhoneWhite with { On = false }, false));
    }

    [Fact]
    public void WritingOrTakingOverDrivesTheBulbAgain()
    {
        var handoff = new LampHandoff(Orange);
        handoff.Observe(PhoneWhite, false);
        handoff.TakeOver();
        Assert.False(handoff.HandedOff);
        handoff.Wrote(Orange);
        Assert.Equal(Orange, handoff.Left);
        Assert.False(handoff.Observe(Orange, false));
    }

    [Theory]
    [InlineData(1700, "#FF7900")]
    [InlineData(6500, "#FFF9FD")]
    [InlineData(2850, "#FFAC62")]   // halfway between 2700 and 3000
    [InlineData(9000, "#FFF9FD")]
    public void ColourTemperaturesDrawAsTheirWhite(int kelvin, string hex) => Assert.Equal(hex, LampWhite.Hex(kelvin));

    [Fact]
    public void AWhiteReadingShowsItsTemperature() => Assert.Equal(LampWhite.Hex(5000), PhoneWhite.Hex);
}
