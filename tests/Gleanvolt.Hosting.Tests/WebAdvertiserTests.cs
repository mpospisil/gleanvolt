using System.Net;
using System.Net.Sockets;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Hosting.Advertising;
using Gleanvolt.Infrastructure.Mdns;
using Gleanvolt.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Issue #363: the web UI announces itself on the LAN as <c>gleanvolt.local</c>. What it says about
/// itself, when it runs, and that it never takes the host down with it.
/// </summary>
public class WebAdvertiserTests
{
    [Theory]
    [InlineData(true, "setup=pending")]
    [InlineData(false, "setup=done")]
    public void The_text_record_says_whether_a_setup_is_pending_and_nothing_more(bool setupMode, string expected)
    {
        var advertisement = WebAdvertiser.Describe("gleanvolt", 8090, setupMode, "1.2.3");

        Assert.Equal(["path=/", "version=1.2.3", expected], advertisement.Text);
        Assert.Equal([WebAdvertiser.HttpService, WebAdvertiser.GleanvoltService], advertisement.ServiceTypes);
        Assert.Equal(8090, advertisement.Port);
        Assert.Equal("Gleanvolt", advertisement.InstanceName);
    }

    [Fact]
    public void Another_name_is_shown_beside_the_product_name()
    {
        Assert.Equal("Gleanvolt garage", WebAdvertiser.Describe("garage", 8090, false, "1.2.3").InstanceName);
    }

    [Theory]
    [InlineData("gleanvolt", "gleanvolt")]
    [InlineData(" Garage-Pi ", "garage-pi")]
    [InlineData("roof2", "roof2")]
    [InlineData("-leading", null)]
    [InlineData("trailing-", null)]
    [InlineData("has space", null)]
    [InlineData("tečka", null)]
    [InlineData("gleanvolt.local", null)]
    [InlineData("", null)]
    public void The_name_must_be_one_dns_label(string name, string? expected)
    {
        Assert.Equal(expected, WebAdvertiser.HostLabel(name));
    }

    [Fact]
    public void A_label_of_64_characters_is_one_too_many()
    {
        Assert.NotNull(WebAdvertiser.HostLabel(new string('a', 63)));
        Assert.Null(WebAdvertiser.HostLabel(new string('a', 64)));
    }

    [Theory]
    [InlineData(null, null, true)]            // on by default
    [InlineData("false", null, false)]        // Web__Advertise=false
    [InlineData(null, "false", false)]        // no UI, nothing to point at
    public void Registered_on_the_host_whenever_the_ui_is_on(string? advertise, string? webEnabled, bool expected)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(UsableSite.Settings
            .Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value))
            .Concat([new("Web:Advertise", advertise), new("Web:Enabled", webEnabled)])
            .Where(setting => setting.Value is not null));

        builder.AddGleanvolt();

        var registered = builder.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(WebAdvertiser));
        Assert.Equal(expected, registered);
    }

    [Fact]
    public void Runs_in_setup_mode_too()
    {
        // No device addresses: setup mode, which takes every worker out -- but not this one.
        var builder = WebApplication.CreateSlimBuilder();

        builder.AddGleanvolt();

        Assert.Contains(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(WebAdvertiser));
    }

    [Fact]
    public async Task A_port_it_cannot_open_is_a_warning_not_a_failure()
    {
        var advertiser = new WebAdvertiser(
            new WebOptions(),
            new NoSetupState(),
            NullLogger<WebAdvertiser>.Instance,
            () => throw new SocketException((int)SocketError.AddressAlreadyInUse));

        await advertiser.StartAsync(CancellationToken.None);
        await advertiser.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(advertiser.ExecuteTask.IsCompletedSuccessfully);
        await advertiser.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_unusable_name_falls_back_to_the_default()
    {
        var transport = new SilentTransport();
        var advertiser = new WebAdvertiser(
            new WebOptions { AdvertiseName = "not a label" },
            new NoSetupState(),
            NullLogger<WebAdvertiser>.Instance,
            () => transport);

        await advertiser.StartAsync(CancellationToken.None);
        var probe = await transport.FirstProbe.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await advertiser.StopAsync(CancellationToken.None);

        Assert.Contains(probe.Questions, question => question.Name == "gleanvolt.local");
    }

    // A LAN with nobody else on it: records what is sent, receives nothing.
    private sealed class SilentTransport : IMdnsTransport
    {
        public TaskCompletionSource<DnsMessage> FirstProbe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<MdnsInterface> Interfaces() => [new MdnsInterface(1, "lan0", [IPAddress.Parse("192.168.1.20")])];

        public IReadOnlyList<IPAddress> AddressesOf(int interfaceIndex) => [IPAddress.Parse("192.168.1.20")];

        public ValueTask SendMulticastAsync(ReadOnlyMemory<byte> data, int interfaceIndex, CancellationToken cancellationToken)
        {
            if (DnsMessage.TryParse(data.Span) is { IsResponse: false } probe)
            {
                FirstProbe.TrySetResult(probe);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask SendUnicastAsync(ReadOnlyMemory<byte> data, IPEndPoint destination, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask<MdnsDatagram> ReceiveAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
