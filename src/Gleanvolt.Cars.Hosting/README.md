# Gleanvolt.Cars.Hosting

Write a car plugin for [Gleanvolt](https://github.com/mpospisil/gleanvolt) in your own repository. Gleanvolt plans the
charge; your plugin reads the car from its maker (its state of charge, whether it is plugged in and charging) so a
target can be set in percent. This package is everything else: the host on
[`Gleanvolt.Plugins.Hosting`](https://www.nuget.org/packages/Gleanvolt.Plugins.Hosting) (it starts before Gleanvolt,
finds it, and waits for the owner to accept it on Settings → Plugins), the page Gleanvolt frames, in English and Czech,
saved settings, the car's base with its clock, and the status Gleanvolt shows.

## A plugin in three files

`MyCar.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Gleanvolt.Cars.Hosting" Version="1.3.3" />
  </ItemGroup>
</Project>
```

`Program.cs`: what the plugin is.

```csharp
using Gleanvolt.Cars;

var plugin = new PluginInfo("my-car", "My car", "MyCar", Port: 8120);
var builder = CarPluginHost.CreateBuilder<MyCar>(args, plugin);
builder.Services.AddHttpClient();
CarPluginHost.Build(builder).Run();
```

`MyCar.cs`: the car, read from its maker.

```csharp
using Gleanvolt.Cars;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Vehicle;

public sealed class MyCar(CarPollOptions options, TimeProvider time, PluginSettings settings, IHttpClientFactory http)
    : PluginCar(options, time)
{
    protected override async Task<VehicleReading?> FetchAsync(VehicleInfo? car, CancellationToken cancellationToken)
    {
        if (settings.Get("apiKey").Current is not { } key)
        {
            Block(new Words("Enter the API key on this page.", "Zadejte na této stránce klíč API."), ProviderStatusCodes.NotSetUp);
            return null;
        }

        // Ask the maker; a thrown exception is reported as "unreachable" and the next read tries again.
        var soc = await ReadSocAsync(http.CreateClient(), key, cancellationToken);
        return new VehicleReading(DateTimeOffset.UtcNow, SocPercent: soc);
    }

    // ...
}
```

The page is a Razor component at `/` that renders `<CarFrame>` with your own `Settings` (the key, a sign-in) and,
once the owner has entered them, calls `Resume()`.

`using Gleanvolt.Cars;` is all a plugin needs: the package also puts the shared host's namespace, `Gleanvolt.Plugins`
(`PluginInfo`, `PluginSettings`, `Words`), in scope.

## The clock

A read at start, then every 5 minutes while Gleanvolt has a charging session open or the car says it is charging,
every 15 otherwise, and at once when a session opens or the owner presses Read now. The maker is never asked twice
within 30 seconds, and never while the car is blocked. Set `<Section>:PollInterval`, `<Section>:IdlePollInterval` and
`<Section>:MinimumTimeBetweenReads` to change them.

## Testing

[`Gleanvolt.Providers.Testing`](https://www.nuget.org/packages/Gleanvolt.Providers.Testing) has `VehicleTestHub` and
`CarPluginHarness`: your car, its clock and its status against a hub that checks every hello and reading as Gleanvolt
does, without Gleanvolt and without the maker.

MIT.
