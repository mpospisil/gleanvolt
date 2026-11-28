namespace Gleanvolt.Plugins;

/// <summary>
/// The texts the shared host and every kind's page use, in English and Czech: the waiting view while the plugin is
/// paired, where the token came from, and the words for a reading's values.
/// </summary>
public static class HostText
{
    public static readonly Words Gleanvolt = new("Gleanvolt", "Gleanvolt");
    public static readonly Words NotConnected = new("Not connected to Gleanvolt yet.", "Zatím nepřipojeno ke Gleanvoltu.");
    public static readonly Words TokenFromConfiguration = new(
        "Connected with the token from the configuration.",
        "Připojeno tokenem z konfigurace.");
    public static readonly Words TokenFromPairing = new("Connected with the token from pairing.", "Připojeno tokenem ze spárování.");
    public static readonly Words PairingSearching = new("Looking for Gleanvolt on this network.", "Hledá se Gleanvolt v této síti.");
    public static readonly Words PairingWaiting = new(
        "Waiting for the owner to accept this plugin in Gleanvolt: Settings → Plugins.",
        "Čeká se, až vlastník tento plugin přijme v Gleanvoltu: Nastavení → Pluginy.");
    public static readonly Words PairingRejected = new(
        "Gleanvolt rejected this plugin. It asks again in an hour, or when restarted.",
        "Gleanvolt tento plugin odmítl. Znovu se zeptá za hodinu nebo po restartu.");
    public static readonly Words PairingCode = new("Pairing code", "Kód pro spárování");
    public static readonly Words PairingAddresses = new("This plugin's addresses", "Adresy tohoto pluginu");
    public static readonly Words Read = new("Read at", "Přečteno");
    public static readonly Words NotSaid = new("not known", "neznámé");
    public static readonly Words Yes = new("yes", "ano");
    public static readonly Words No = new("no", "ne");
    public static readonly Words NoReadingYet = new("No reading yet.", "Zatím žádné čtení.");
}
