# Your charger

> **Using Gleanvolt?** The user manual has this step by step: [Your charger](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/charger/). This page is the reference behind it.

Gleanvolt drives the EV charger through a **charger plugin**: a program for one charger family, from
[gleanvolt-chargers](https://github.com/mpospisil/gleanvolt-chargers) or anyone's repository. The plugin reads and
commands the charger; Gleanvolt decides when and how fast the car charges. Which chargers have one:
[Supported EV chargers](SUPPORTED_CHARGERS.md).

**Gleanvolt never opens a connection to the charger.** It has no charger settings, no charger address and no
built-in charger drivers since 1.4 (#445). Everything about the charger (its address, a password, the model) is on
the plugin's own page.

## Running a plugin

A plugin needs no token: it asks to be paired, and the owner accepts it on **Settings → Plugins** (#464). A token
set by hand (`Gleanvolt__Token`, `GLEANVOLT_PROVIDER_TOKEN` for the deploy scripts) still wins. Run one charger plugin
at a time. The manual has the steps by installation: [Your charger](https://mpospisil.github.io/gleanvolt/latest/en/setting-up/charger/).

| Where Gleanvolt runs | How the plugin runs |
|---|---|
| Raspberry Pi, with the deploy scripts | Add its name to `GLEANVOLT_PLUGINS` in `.env` (`GLEANVOLT_PLUGINS=solax`), deploy. `EV_CHARGER_HOST` is its address. [deploy/README.md](../deploy/README.md#shipped-plugins) |
| NAS or plain Docker Compose | Its profile (`COMPOSE_PROFILES=solax`), with `EV_CHARGER_HOST`. [NAS.md](NAS.md) |
| Windows, Linux package, or elsewhere | Its zip or image, as its README says |

Accept it on **Settings → Plugins**, then open its page under **Settings → Charger** and check the charger's address.
An address saved on the plugin's page wins over `EV_CHARGER_HOST`.

**Run in dry run first.** With `ChargeControl:DryRun` on, Gleanvolt sends the plugin nothing and logs what it would
have sent. Leave it on until the charger's status and power on the dashboard match the charger's own app.

## What Gleanvolt checks

- **The limits.** At its hello, a plugin says its charger's lowest and highest current and its phases. Gleanvolt
  refuses it while `ChargeControl:MinChargingCurrentAmps`, `MaxChargingCurrentAmps` or `Phases` ask for something
  outside that; Settings says which.
- **One plugin.** The first charger plugin accepted is remembered (`ExternalCharger:PluginId`). Another is refused
  until **Forget** on Settings → Plugins.
- **Availability.** With no plugin connected, a refused one, or one that can't read its charger, no mode starts.
  After `ChargeControl:ChargerUnavailableAfter` (2 minutes), a running mode is suspended, and it resumes by itself
  when the charger is back.

## Moving from a built-in driver (1.3 → 1.4)

Up to 1.3, Gleanvolt drove SolaX, Huawei SCharger and go-e chargers itself. 1.4 removes those drivers. The plugin
that replaced each one:

| `Pv:Chargers:0:Driver` (`PV_CHARGER_DRIVER`) | Plugin |
|---|---|
| empty, with a host, or `solax` | `solax` |
| `huawei-7ks`, `huawei-22kt` | `huawei` |
| `goe` | `goe` |

- **With the deploy scripts:** nothing to do. An `.env` with `EV_CHARGER_HOST` and no charger plugin in
  `GLEANVOLT_PLUGINS` gets the plugin above, and the deploy says so. Add it to `GLEANVOLT_PLUGINS` to silence that.
  Without `GLEANVOLT_PROVIDER_TOKEN` the plugin asks to be paired: accept it on Settings → Plugins.
- **Elsewhere:** start the plugin yourself, as above.
- **`Pv:Chargers` is ignored**, with a warning at every start that names the plugin. Remove `Pv__Chargers__0__*` from
  `.env` or the environment. A charger saved on Settings in 1.3 goes from `pv-system.json` with the next save of
  **Settings → Installation**. `ExternalCharger:UsePlugin` and `GLEANVOLT_CHARGER_PLUGIN` are gone too: the charger
  is always the plugin's.
- **The register snapshot of the charger** is gone from Settings. Each plugin's page has a recording for testers
  instead.
