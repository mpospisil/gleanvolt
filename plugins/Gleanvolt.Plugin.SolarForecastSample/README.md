# Solar forecast sample: an external solar forecast provider plugin

A minimal provider built on `Gleanvolt.Providers.Client` (#388). It sends Gleanvolt the same fake
curve every time: **a bell from sunrise to sunset, peaking at solar noon at 70 % of the installed
kWp**, in 30-minute periods for seven days from local midnight. The kWp, the coordinates and the time
zone come from Gleanvolt's welcome, so the curve fits whatever installation it connects to. It
publishes after every accepted hello, answers every refresh request, and publishes again at the top
of every hour.

> **Not for a live installation.** With *External* selected under *Solar forecast*, Gleanvolt plans
> on this forecast: Forecasted and Targeted charge the car by a fake sunny week. Use a test
> installation, or switch back to Solcast afterwards.

It is not part of the container image or the release packages.

## Run it

1. In Gleanvolt, open **Settings → Solar forecast**, choose **External** and save.
2. Under **Token**, make a token and copy it.
3. Run the sample:

   ```sh
   dotnet run --project plugins/Gleanvolt.Plugin.SolarForecastSample -- \
     --Gleanvolt:Endpoint=http://localhost:8090 \
     --Gleanvolt:Token=gvp_...
   ```

To keep the token out of the command line and the shell history, store it once as a user secret.
The sample reads user secrets however it is started, from `dotnet run`, VS Code's
*Gleanvolt.Plugin.SolarForecastSample* launch entry or another debugger:

```sh
dotnet user-secrets set Gleanvolt:Token gvp_... --project plugins/Gleanvolt.Plugin.SolarForecastSample
```

Environment variables and command-line arguments still win over it.

The panel on Settings shows "Solar forecast sample connected", and the line under the Overview's
forecast table says "Forecast from Solar forecast sample".

The order doesn't matter: started before Gleanvolt, the sample keeps trying until Gleanvolt answers,
and it connects again by itself after Gleanvolt restarts.

To write a real provider, start from `SampleForecast.cs`: implement
`ISolarForecastSource.GetForecastAsync` and register it with `AddGleanvoltSolarForecastProvider<T>`.
The rules the forecast must meet are in [docs/PROVIDERS.md](../../docs/PROVIDERS.md).
