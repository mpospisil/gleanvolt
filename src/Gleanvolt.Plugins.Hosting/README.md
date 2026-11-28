# Gleanvolt.Plugins.Hosting

The host every [Gleanvolt](https://github.com/mpospisil/gleanvolt) plugin shares, whatever its kind: the configuration
(user secrets, the environment, the command line), the page Gleanvolt frames and admits only with its ticket, the
waiting view a plugin shows until the owner accepts it on Settings → Plugins, a frame policy that follows the pairing,
the data directory with the saved settings, and the viewer's language (`Words`, in English and Czech).

A plugin doesn't reference it directly: it references its kind's layer, which brings it along.

- A charger plugin: [`Gleanvolt.Chargers.Hosting`](https://www.nuget.org/packages/Gleanvolt.Chargers.Hosting).

MIT.
