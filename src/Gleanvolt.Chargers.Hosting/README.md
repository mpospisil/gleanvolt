# Gleanvolt.Chargers.Hosting

Write an EV charger plugin for [Gleanvolt](https://github.com/mpospisil/gleanvolt) in your own repository. Gleanvolt
decides when and how fast the car charges; your plugin reads the charger and carries out two commands. This package is
everything else: the host, the connection, the page Gleanvolt frames (guarded by Gleanvolt's ticket, in English and
Czech), saved settings, helpers for the charger's address and a LAN search, and a recording for testers. The
charger's address and port, and finding the charger, are your plugin's: they are on its page, as the charger needs. The plugins in
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

`Pages/Home.razor`: the page, in the shared frame, with what you show of the charger and your own sections: the
charger's address and port (saved to `PluginSettings.Address` as `host:port`, checked by a read), a search when your
charger can be found, and any other setting. The plugins in gleanvolt-chargers each have an `AddressSection` and a
`SearchSection` to copy.

```razor
@page "/"
@using Gleanvolt.Chargers.Components
@inject MyCharger Charger

<PluginFrame>
    <ChildContent>
        <p>Anything your charger says beyond the reading.</p>
    </ChildContent>
    <Settings>
        <section id="address-section"><!-- the host, the port, Save and check --></section>
    </Settings>
</PluginFrame>
```

Run it, then accept it in Gleanvolt.

## Gleanvolt:Token

Optional. Start the plugin, then accept it in Gleanvolt: Settings → Plugins. Until then the page, open to
the whole network, shows only that it waits, the pairing code to compare with the one Gleanvolt shows, and the
plugin's addresses; it moves on by itself once accepted, with no restart. The token and the address that accepted it
are kept in the data directory.

To set it by hand instead, run it with `--Gleanvolt:Token=<the token from Gleanvolt's Settings → Plugins>`
(and `--Gleanvolt:Endpoint=` when Gleanvolt is not at `http://localhost:8090`). A token set this way always wins over
a paired one. The page says which one it connects with.

## What you get

- **`PluginHost`**: configuration (user secrets, the environment, the command line), the page on `Urls` (default
  `http://localhost:<Port>`), the data directory (`<Section>:DataDirectory` or `DataDirectory`, owner-only), the
  connection to Gleanvolt with your capabilities in every hello, and a status reporter.
- **`PluginCharger`**: the base of your charger. Call `Healthy()` or `Problem(words, code, needsOwner)` as a read goes;
  the page and Gleanvolt show it. `Reset()` runs when the owner changes the address.
- **`PluginSettings`**: the address (`<Section>:Host`, or what the page saved), and `Get(name)` for your own settings
  (a model, a password), saved owner-only. **`DeviceAddress`** parses an address with an optional port. The address
section is yours to write; `SharedText` has its words in English and Czech.
- **Plugin texts in resx**: keep them in `Resources/PageText.resx` (English) and `PageText.cs.resx` (Czech); the page
  reads the generated class, and a status passes `Words.From(PageText.ResourceManager, nameof(PageText.X))`. A missing
  translation is English. The SDK does not generate `PageText` for you; put this in your csproj or a
  `Directory.Build.targets` (a `.targets` file, because an `Update` in the props runs before the SDK's default
  `EmbeddedResource` glob and matches nothing):

  ```xml
  <PropertyGroup>
    <NeutralLanguage>en</NeutralLanguage>
  </PropertyGroup>
  <ItemGroup>
    <NeutralResx Include="Resources\*.resx" Exclude="Resources\*.*.resx" />
    <EmbeddedResource Update="@(NeutralResx)">
      <StronglyTypedLanguage>CSharp</StronglyTypedLanguage>
      <StronglyTypedNamespace>$(RootNamespace).Resources</StronglyTypedNamespace>
      <StronglyTypedClassName>%(Filename)</StronglyTypedClassName>
      <StronglyTypedFileName>$(IntermediateOutputPath)%(Filename).Resources.g.cs</StronglyTypedFileName>
    </EmbeddedResource>
  </ItemGroup>
  ```

- **`ViewerCulture`**: a page that re-renders when the charger changes calls `InvokeAsync` from the poll's thread,
  which runs in the service's culture, so the text turns English after the first reading. Keep
  `private readonly ViewerCulture _viewer = new();` in the component and re-render with
  `InvokeAsync(_viewer.Wrap(StateHasChanged))`.
- **`ModbusDevice`**: a Gleanvolt.Modbus client at the current address, made anew when it changes.
- **`LanScan`**: for a search of your own, this machine's networks, and every host of one that accepts a connection on
  a port, asked by your probe whether it is your charger. Find your charger any other way (mDNS, the vendor's
  discovery) as suits it.
- **`DeviceRecorder`**: the recording for testers. Every exchange through `ModbusDevice`, or through an HTTP client
  with `RecordingHandler`, is recorded while the owner records, and downloaded from the page. Never a password.
- **`PluginFrame`**: the page. Your content goes under the reading, and your sections (the address, the search, your
  settings) in `Settings`, before the recording.

Test it without Gleanvolt with `Gleanvolt.Providers.Testing`: `PluginHarness`, and `FakeModbusServer` for a Modbus
charger. The contract (what a reading holds, what a pause is) is in Gleanvolt's
[docs/PROVIDERS.md](https://github.com/mpospisil/gleanvolt/blob/main/docs/PROVIDERS.md#the-charger-kind).

MIT licence. A plugin built on it can use any licence.
