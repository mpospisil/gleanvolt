# Gleanvolt.Providers.Contracts

The wire contract between [Gleanvolt](https://github.com/mpospisil/gleanvolt) and an external data
provider: the handshake (`ProviderHello`, `Welcome`), the hub and client interfaces for each provider
kind, and the data they carry. It has no dependencies.

Most providers reference `Gleanvolt.Providers.Client` instead, which brings this package with it and
handles the connection. The protocol, for a client in another language, is described in
[docs/PROVIDERS.md](https://github.com/mpospisil/gleanvolt/blob/main/docs/PROVIDERS.md).

MIT licence. A provider built on it can use any licence.
