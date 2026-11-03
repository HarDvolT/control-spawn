using Prism.Core.Ipc;
using Xunit;

namespace Prism.Lighting.Tests;

public sealed class MiHomeSupportTests
{
    [Theory]
    [InlineData("yeelink.light.color5", DeviceKind.Lamp, "Yeelight bulb")]
    [InlineData("careli.fryer.maf10a", DeviceKind.Appliance, "Careli air fryer")]
    [InlineData("chuangmi.plug.m3", null, "chuangmi.plug.m3")]
    [InlineData("acme.fryer.unknown", null, "Acme air fryer")]   // a fryer without a spec is listed, never guessed at
    public void ModelsSayWhatControlSpawnCanDoWithThem(string model, DeviceKind? kind, string name)
    {
        Assert.Equal(kind, MiHomeSupport.Kind(model));
        Assert.Equal(name, MiHomeSupport.Describe(model));
    }
}
