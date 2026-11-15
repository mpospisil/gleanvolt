# Tariff sample: an external tariff provider plugin

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
2. Under **External providers**, make a token and copy it, or copy the one already there: every
   plugin uses the same token.
3. Run the sample:

   ```sh
   dotnet run --project plugins/Gleanvolt.Plugin.TariffSample -- \
     --Gleanvolt:Endpoint=http://localhost:8090 \
     --Gleanvolt:Token=gvp_...
   ```

To keep the token out of the command line and the shell history, store it once as a user secret.
The sample reads user secrets however it is started, from `dotnet run`, VS Code's
*Gleanvolt.Plugin.TariffSample* launch entry or another debugger:

```sh
dotnet user-secrets set Gleanvolt:Token gvp_... --project plugins/Gleanvolt.Plugin.TariffSample
```

Environment variables and command-line arguments still win over it.

The panel on Settings shows "Tariff sample connected", and the Overview's tariff line says
"(from Tariff sample)".

The order doesn't matter: started before Gleanvolt, the sample keeps trying until Gleanvolt answers,
and it connects again by itself after Gleanvolt restarts.

To write a real provider, start from `SampleTariff.cs`: implement `ITariffSource.GetTariffAsync` and
register it with `AddGleanvoltTariffProvider<T>`. The rules the data must meet are in
[docs/PROVIDERS.md](../../docs/PROVIDERS.md).
