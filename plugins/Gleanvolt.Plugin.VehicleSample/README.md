# Vehicle sample: an external car provider plugin

A minimal car provider built on `Gleanvolt.Providers.Client` (#408). It reports one pretend car,
**plugged in and charging: 40 % when the sample starts, one point every 3 minutes, capped at 80 %,
then complete.** The range follows the SOC and the pack size from Gleanvolt's welcome (60 kWh when
Gleanvolt doesn't know it), at 18 kWh per 100 km. It publishes after every accepted hello, on every
step of the SOC, and in answer to *Ask the car*.

Started with `--simulate-login-expiry`, every fifth step reports that the car account's login has
expired instead of publishing, and the next step that it is back. Gleanvolt shows the sign-in band
on every page and the Home Assistant **Car feed** entity reads `NeedsOwner` for those 3 minutes.

> **Not for a live installation.** With *An external provider* chosen as the car's feed on the Car
> page, a target set as a percentage converts from this fake SOC, and the Overview shows the fake car.
> Use a test installation, or choose your real feed again afterwards.

It is not part of the container image or the release packages.

## Run it

1. In Gleanvolt, open the **Car** page, choose **An external provider** under *What reads this car*,
   save and restart.
2. Under **Settings → External providers**, make a token and copy it, or copy the one already
   there: every plugin uses the same token.
3. Run the sample:

   ```sh
   dotnet run --project plugins/Gleanvolt.Plugin.VehicleSample -- \
     --Gleanvolt:Endpoint=http://localhost:8090 \
     --Gleanvolt:Token=gvp_...
   ```

To keep the token out of the command line and the shell history, store it once as a user secret.
The sample reads user secrets however it is started, from `dotnet run`, VS Code's
*Gleanvolt.Plugin.VehicleSample* launch entry or another debugger:

```sh
dotnet user-secrets set Gleanvolt:Token gvp_... --project plugins/Gleanvolt.Plugin.VehicleSample
```

Environment variables and command-line arguments still win over it.

The Car page shows "Vehicle sample connected" under *External provider*, and the Overview's vehicle card
says "via Vehicle sample".

The order doesn't matter: started before Gleanvolt, the sample keeps trying until Gleanvolt answers,
and it connects again by itself after Gleanvolt restarts.

To write a real provider, start from `SimulatedCar.cs`: implement `IVehicleSource.GetReadingAsync`,
register it with `AddGleanvoltVehicleProvider<T>`, and call `VehicleProviderConnection.PublishNowAsync`
whenever the car reports something new. Report an expired login with
`ReportStatusAsync(new ProviderStatus(false, "...", NeedsOwner: true))`. The rules a reading must
meet are in [docs/PROVIDERS.md](../../docs/PROVIDERS.md).
