# Gleanvolt.Chargers.Hosting

Write an EV charger plugin for [Gleanvolt](https://github.com/mpospisil/gleanvolt) in your own repository. Gleanvolt
decides when and how fast the car charges; your plugin reads the charger and carries out two commands. This package is
everything else: the host, the connection, the page Gleanvolt frames (guarded by Gleanvolt's ticket, in English and
Czech), the charger's address and your own settings, a LAN search, and a recording for testers. The plugins in
[gleanvolt-chargers](https://github.com/mpospisil/gleanvolt-chargers) (SolaX, Huawei, go-e, Shelly TopAC) are built
on it; read one of them first.

## A plugin in four files

`MyCharger.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Gleanvolt.Chargers.Hosting" Version="1.3.1" />
  </ItemGroup>
</Project>
```

`Program.cs`: what the plugin is, and its transport.

```csharp
using Gleanvolt.Chargers;

var plugin = new PluginInfo("my-charger", "My charger", "MyCharger", Port: 8110, DefaultDevicePort: 502);
var builder = PluginHost.CreateBuilder<MyCharger>(args, plugin);

// A Modbus charger: a client at the address the page saves, 250 ms between requests.
builder.Services.AddSingleton(provider =>
{
    var settings = provider.GetRequiredService<PluginSettings>();
    return new ModbusDevice(settings.Address, settings.Get("unitId"), 502, 1, TimeSpan.FromMilliseconds(250),
        provider.GetRequiredService<DeviceRecorder>(), provider.GetRequiredService<ILogger<ModbusDevice>>());
});

PluginHost.Build(builder).Run();
```

`MyCharger.cs`: the device.

```csharp
public sealed class MyCharger(ModbusDevice device, TimeProvider time) : PluginCharger
{
    public override ChargerCapabilities Capabilities => new(6, 32, Phases: 3);

    public override async Task<ChargerReading> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reading = await device.UseAsync(async client => { /* read the registers */ }, cancellationToken);
            LastReading = reading;
            Healthy();
            return reading;
        }
        catch (NoAddressException)
        {
            Problem(SharedText.NoAddress, ProviderStatusCodes.NotSetUp, needsOwner: true);
            throw;
        }
    }

    public override Task SetCurrentAsync(int amps, CancellationToken cancellationToken) => /* below 6 A pauses */;

    public override Task SetChargingAllowedAsync(bool allowed, CancellationToken cancellationToken) => /* start, stop */;
}
```

`Pages/Home.razor`: the page, in the shared frame, with what you show of the charger and how an address is checked.

```razor
@page "/"
@using Gleanvolt.Chargers.Components
@inject MyCharger Charger

<PluginFrame Check="CheckAsync" UnitId="true">
    <p>Anything your charger says beyond the reading.</p>
</PluginFrame>

@code {
    private Task<string> CheckAsync(DeviceAddress address, byte? unitId) => /* read it once, say what answered */;
}
```

Run it with `--Gleanvolt:Token=<the token from Gleanvolt's Settings → External providers>`.

## What you get

- **`PluginHost`**: configuration (user secrets, the environment, the command line), the page on `Urls` (default
  `http://localhost:<Port>`), the data directory (`<Section>:DataDirectory` or `DataDirectory`, owner-only), the
  connection to Gleanvolt with your capabilities in every hello, and a status reporter.
- **`PluginCharger`**: the base of your charger. Call `Healthy()` or `Problem(words, code, needsOwner)` as a read goes;
  the page and Gleanvolt show it. `Reset()` runs when the owner changes the address.
- **`PluginSettings`**: the address (`<Section>:Host`, or what the page saved), and `Get(name)` for your own settings
  (a model, a password), saved owner-only. **`DeviceAddress`** parses an address with an optional port.
- **`ModbusDevice`**: a Gleanvolt.Modbus client at the current address, made anew when it changes.
- **`IChargerSearch`**: register one, and the page offers a search of the LAN that asks your probe about each address
  that accepts a connection on your port.
- **`DeviceRecorder`**: the recording for testers. Every exchange through `ModbusDevice`, or through an HTTP client
  with `RecordingHandler`, is recorded while the owner records, and downloaded from the page. Never a password.
- **`PluginFrame`**: the page. Your content goes under the reading, your settings in `Settings`.

Test it without Gleanvolt with `Gleanvolt.Providers.Testing`: `PluginHarness`, and `FakeModbusServer` for a Modbus
charger. The contract (what a reading holds, what a pause is) is in Gleanvolt's
[docs/PROVIDERS.md](https://github.com/mpospisil/gleanvolt/blob/main/docs/PROVIDERS.md#the-charger-kind).

MIT licence. A plugin built on it can use any licence.
