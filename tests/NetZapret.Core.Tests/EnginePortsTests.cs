using System.Net.Sockets;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Подбор и чтение служебных портов sing-box (Issue #85: резерв портов Hyper-V / WinNAT).
/// </summary>
public sealed class EnginePortsTests
{
    private static readonly ProxyServer Vless = new()
    {
        Protocol = ProxyProtocol.Vless,
        Tag = "NL",
        Host = "nl.example.com",
        Port = 443,
        Credential = "PLACEHOLDER",
        Transport = "tcp",
        Security = "reality",
        Sni = "nl.example.com",
        RealityPublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0",
        RealityShortId = "abcd",
    };

    [Fact]
    public void AccessDenied_from_HyperV_picks_another_port_even_if_old_config_had_preferred()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-ports-{Guid.NewGuid():N}.json");
        try
        {
            var engine = RuleSetLoader.Load("mode: selective\nrules: []");
            var oldJson = new SingBoxConfigCompiler().Compile(engine.RuleSet, [Vless], new SingBoxOptions
            {
                HealthInbound = true,
                Keys = EngineKeys.Generate(),
            }).Json;
            File.WriteAllText(path, oldJson);

            int picked = EnginePorts.Pick(
                SingBoxOptions.DefaultHealthPort,
                configPath: path,
                probe: p => p == SingBoxOptions.DefaultHealthPort ? SocketError.AccessDenied : null,
                allocate: () => 45123);

            Assert.Equal(45123, picked);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void AddressAlreadyInUse_by_own_running_engine_keeps_preferred_port()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-ports-{Guid.NewGuid():N}.json");
        try
        {
            var engine = RuleSetLoader.Load("mode: selective\nrules: []");
            var oldJson = new SingBoxConfigCompiler().Compile(engine.RuleSet, [Vless], new SingBoxOptions
            {
                HealthInbound = true,
                Keys = EngineKeys.Generate(),
            }).Json;
            File.WriteAllText(path, oldJson);

            int picked = EnginePorts.Pick(
                SingBoxOptions.DefaultHealthPort,
                configPath: path,
                probe: p => p == SingBoxOptions.DefaultHealthPort ? SocketError.AddressAlreadyInUse : null,
                allocate: () => 45124);

            Assert.Equal(SingBoxOptions.DefaultHealthPort, picked);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void AddressAlreadyInUse_by_another_program_picks_another_port()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"nz-ports-missing-{Guid.NewGuid():N}.json");

        int picked = EnginePorts.Pick(
            TunnelStatus.DefaultClashPort,
            configPath: missing,
            probe: p => p == TunnelStatus.DefaultClashPort ? SocketError.AddressAlreadyInUse : null,
            allocate: () => 45125);

        Assert.Equal(45125, picked);
    }

    [Fact]
    public void Picked_ports_round_trip_through_compiled_singbox_json_and_pass_singbox_check()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-ports-{Guid.NewGuid():N}.json");
        try
        {
            var engine = RuleSetLoader.Load("mode: selective\nrules: []");
            var json = new SingBoxConfigCompiler().Compile(engine.RuleSet, [Vless], new SingBoxOptions
            {
                HealthInbound = true,
                ClashApiListen = "127.0.0.1:45909",
                HealthPort = 45910,
                Keys = EngineKeys.Generate(),
            }).Json;

            File.WriteAllText(path, json);

            var ports = EnginePorts.Current(path);
            Assert.Equal(45909, ports.ClashPort);
            Assert.Equal(45910, ports.HealthPort);
            Assert.Equal("127.0.0.1:45909", EnginePorts.CurrentClashListen(path));

            var (ok, said) = SingBoxCheck.Run(json);
            Assert.True(ok, $"sing-box check не пройден: {said}");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
