using Prism.App.Common;
using Prism.Core;

namespace Prism.App.Tests;

public class FormattingTests
{
    [Fact]
    public void Missing_values_show_an_em_dash()
    {
        Assert.Equal("—", Format.Number(null));
        Assert.Equal("—", Format.Percent(null));
        Assert.Equal("—", Format.Rpm(null));
        Assert.Equal("—", Format.Temp(null));
        Assert.Equal("—", Format.Gb(null));
        Assert.Equal("—", Format.Mbps(null));
        Assert.Equal("—", Format.Ms(null));
        Assert.Equal("—", Format.Dbm(null));
        Assert.Equal("—", Format.Text(null));
        Assert.Equal("—", Format.Text("  "));
        Assert.Equal("—", Format.GbPair(null, 32));
        Assert.Equal("—", Format.Ghz(double.NaN));
    }

    [Fact]
    public void Rpm_uses_grouping_and_a_unit()
    {
        Assert.Equal("1,268 rpm", Format.Rpm(1268.4));
        Assert.Equal("2,801 rpm", Format.Rpm(2801));
        Assert.Equal("0 rpm", Format.Rpm(0));
        Assert.Equal("1,268", Format.RpmShort(1268.4));
    }

    [Fact]
    public void Percentages_are_rounded_by_default()
    {
        Assert.Equal("44%", Format.Percent(44.2));
        Assert.Equal("44.2%", Format.Percent(44.2, 1));
        Assert.Equal("0%", Format.Percent(0));
        Assert.Equal("100%", Format.Percent(100));
    }

    [Fact]
    public void Temperatures_keep_one_decimal()
    {
        Assert.Equal("48.3°", Format.Temp(48.26));
        Assert.Equal("48°", Format.Temp(48.26, 0));
        Assert.Equal("48.3 °C", Format.TempUnit(48.26));
    }

    [Fact]
    public void Memory_and_network_have_human_units()
    {
        Assert.Equal("14.2 / 32 GB", Format.GbPair(14.2, 32));
        Assert.Equal("3.1 GB", Format.Gb(3.1));
        Assert.Equal("60 Mb/s", Format.Mbps(60.4));
        Assert.Equal("8.2 Mb/s", Format.Mbps(8.2, 1));
        Assert.Equal("21 ms", Format.Ms(21));
    }

    [Fact]
    public void Clocks_and_watts_are_formatted()
    {
        Assert.Equal("4.70 GHz", Format.Ghz(4700));
        Assert.Equal("4800 MHz", Format.Mhz(4800));
        Assert.Equal("65 W", Format.Watts(65.4));
        Assert.Equal("-97 dBm", Format.Dbm(-97));
        Assert.Equal("7.0 dB", Format.Db(7));
    }

    [Fact]
    public void Bar_values_are_clamped()
    {
        Assert.Equal(0, Format.Bar(null));
        Assert.Equal(0, Format.Bar(-20));
        Assert.Equal(50, Format.Bar(50));
        Assert.Equal(100, Format.Bar(150));
        Assert.Equal(50, Format.Bar(8, 16));
        Assert.Equal(0, Format.Bar(10, 0));
    }

    [Theory]
    [InlineData(FanMode.Silent, "Silent")]
    [InlineData(FanMode.Standard, "Standard")]
    [InlineData(FanMode.Turbo, "Turbo")]
    [InlineData(FanMode.Full, "Full speed")]
    [InlineData(FanMode.Custom, "Custom")]
    public void Fan_mode_names_match_the_mockups(FanMode mode, string expected) =>
        Assert.Equal(expected, Format.FanModeName(mode));

    [Theory]
    [InlineData(LightingEffect.RainbowWave, "Rainbow")]
    [InlineData(LightingEffect.ColorCycle, "Color cycle")]
    [InlineData(LightingEffect.StarryNight, "Starry night")]
    [InlineData(LightingEffect.Smart, "Smart")]
    public void Effect_names_match_the_mockups(LightingEffect effect, string expected) =>
        Assert.Equal(expected, Format.EffectName(effect));

    [Fact]
    public void Bytes_scale()
    {
        Assert.Equal("512 B", Format.Bytes(512));
        Assert.Equal("1.0 KB", Format.Bytes(1024));
        Assert.Equal("1.5 MB", Format.Bytes(1024 * 1024 * 3 / 2));
    }
}
