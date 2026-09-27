using Home.Client;
using Home.Protocol;
using Home.Server.Devices;
using Home.Server.Ota;
using Xunit;

namespace Home.Server.Tests;

public class UnitTests
{
    [Fact]
    public void MaskAndPrefix()
    {
        Assert.Equal(24, ProtoMap.PrefixFromMask(ProtoMap.MaskFromPrefix(24)));
        Assert.Equal("255.255.255.0", ProtoMap.Ip(ProtoMap.MaskFromPrefix(24)));
        Assert.Equal("192.168.1.50", ProtoMap.Ip(ProtoMap.IpToU32("192.168.1.50")));
        var cfg = ProtoMap.FromDto(new NetworkDto { Mode = "static", Ip = "192.168.1.50", Prefix = 24, Gateway = "192.168.1.1" });
        Assert.Equal(IpMode.Static, cfg.IpMode);
        Assert.Equal("192.168.1.1", ProtoMap.Ip(cfg.Dns1));
        Assert.Throws<ArgumentException>(() => ProtoMap.FromDto(new NetworkDto { Mode = "static", Ip = "192.168.1.50" }));
    }

    [Fact]
    public void ValueConversion()
    {
        var sw = new PointDto { Key = "power", Kind = "actuator", Type = "bool" };
        Assert.True(ProtoMap.FromJson(sw, System.Text.Json.Nodes.JsonValue.Create("on")).B);
        var sel = new PointDto { Key = "mode", Kind = "setting", Type = "enum", Options = { "a", "b" } };
        Assert.Equal(1, ProtoMap.FromJson(sel, System.Text.Json.Nodes.JsonValue.Create("b")).I);
        Assert.Throws<ArgumentException>(() => ProtoMap.FromJson(sel, System.Text.Json.Nodes.JsonValue.Create("c")));
        Assert.Equal("b", PointFormat.Format(sel, System.Text.Json.Nodes.JsonValue.Create(1)));
    }

    [Fact]
    public void FirmwareImageParse()
    {
        var img = FirmwareImage.BuildFake((int)Model.Co2Egg, "2.0.1", 100_000, "co2-egg");
        var info = FirmwareImage.Parse(img);
        Assert.Equal("2.0.1", info.Version);
        Assert.Equal("co2-egg", info.Project);
        Assert.Equal((int)Model.Co2Egg, info.Model);
        img[0] = 0;
        Assert.Throws<InvalidDataException>(() => FirmwareImage.Parse(img));
    }
}
