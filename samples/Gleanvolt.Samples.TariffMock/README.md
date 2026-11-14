# Tariff mock: a sample external tariff provider

A minimal provider built on `Gleanvolt.Providers.Client` (#401). It sends Gleanvolt the same fake
pattern every time: **low tariff 22:00–06:00 and 13:00–15:00**, high otherwise, for today and
tomorrow in the installation's time zone. It publishes after every accepted hello, answers every
refresh request, and publishes again a minute after local midnight.

> **Not for a live installation.** With *External* selected under *Tariff*, Gleanvolt acts on these
> windows: *low tariff only* in Fast and SolarGrid, and Targeted's low-tariff option, follow a fake
> tariff. Use a test installation, or switch back to your real tariff afterwards.

It is not part of the container image or the release packages.

## Run it

1. In Gleanvolt, open **Settings → Tariff**, choose **External** and save.
2. Under **Token**, make a token and copy it.
3. Run the mock:

   ```sh
   dotnet run --project samples/Gleanvolt.Samples.TariffMock -- \
     --Gleanvolt:Endpoint=http://localhost:8090 \
     --Gleanvolt:Token=gvp_...
   ```

The panel on Settings shows "Tariff mock connected", and the Overview's tariff line says
"(from Tariff mock)".

To write a real provider, start from `MockTariff.cs`: implement `ITariffSource.GetTariffAsync` and
register it with `AddGleanvoltTariffProvider<T>`. The rules the data must meet are in
[docs/PROVIDERS.md](../../docs/PROVIDERS.md).
