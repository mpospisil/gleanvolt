namespace Gleanvolt.Plugin.Shelly.TopAc;

/// <summary>
/// The plugin's texts in English and Czech (#429): its page and its status. Shelly, TopAC and the names of the
/// charger's own settings and states stay as Shelly writes them.
/// </summary>
internal static class PageText
{
    // The status.
    public static readonly Words StatusWaiting = new("Waiting for Gleanvolt to ask.", "Čeká se, až se Gleanvolt zeptá.");
    public static readonly Words StatusReading = new("Reading the charger.", "Nabíječka se čte.");
    public static readonly Words StatusNotReading = new("Not reading the charger.", "Nabíječka se nečte.");
    public static readonly Words NoAddress = new(
        "There is no address for the charger: enter it on this page.",
        "Chybí adresa nabíječky: zadejte ji na této stránce.");
    public static readonly Words Unauthorized = new(
        "The charger asks for a password. Turn authentication off in the Shelly app: the plugin does not send one yet.",
        "Nabíječka vyžaduje heslo. Vypněte ověřování v aplikaci Shelly: plugin zatím heslo neposílá.");
    public static readonly Words NotTopAc = new(
        "The device at the address does not answer as a Shelly TopAC: {0}",
        "Zařízení na adrese neodpovídá jako Shelly TopAC: {0}");
    public static readonly Words Unreachable = new("The charger could not be reached: {0}", "Nabíječka není dostupná: {0}");
    public static readonly Words AutoBalanceOn = new(
        "Auto balance is on in the Shelly app. It lowers the current on its own and works against Gleanvolt: turn it off.",
        "V aplikaci Shelly je zapnuté Auto balance. Samo snižuje proud a jde proti Gleanvoltu: vypněte ho.");

    // Gleanvolt.
    public static readonly Words Gleanvolt = new("Gleanvolt", "Gleanvolt");
    public static readonly Words NotConnected = new("Not connected to Gleanvolt yet.", "Zatím nepřipojeno ke Gleanvoltu.");
    public static readonly Words Range = new("Current range", "Rozsah proudu");
    public static readonly Words Phases = new("Phases", "Fáze");
    public static readonly Words DryRun = new(
        "Gleanvolt is in dry-run: it reads the charger and sends no command.",
        "Gleanvolt je ve zkušebním režimu (dry-run): nabíječku čte a neposílá žádné příkazy.");
    public static readonly Words AboveCharger = new(
        "Gleanvolt's maximum is {0} A; the charger takes at most {1} A, so it never gets more.",
        "Maximum v Gleanvoltu je {0} A; nabíječka bere nejvýš {1} A, takže víc nikdy nedostane.");

    // The charger.
    public static readonly Words Charger = new("The charger", "Nabíječka");
    public static readonly Words NoReadingYet = new(
        "No reading yet. Gleanvolt asks every few seconds while it runs.",
        "Zatím žádné údaje. Gleanvolt se ptá každých pár sekund, dokud běží.");
    public static readonly Words State = new("State", "Stav");
    public static readonly Words Power = new("Power", "Výkon");
    public static readonly Words Setpoint = new("Current setpoint", "Nastavený proud");
    public static readonly Words Switch = new("Charging", "Nabíjení");
    public static readonly Words Allowed = new("allowed", "povoleno");
    public static readonly Words Stopped = new("stopped", "zastaveno");
    public static readonly Words PausedByGleanvolt = new("paused by Gleanvolt", "pozastaveno Gleanvoltem");
    public static readonly Words PerPhase = new("Per phase", "Po fázích");
    public static readonly Words UnitFound = new("total_power is in", "total_power je v");
    public static readonly Words UnitUnknown = new("not settled until the car draws", "neurčeno, dokud auto neodebírá");
    public static readonly Words Read = new("Read", "Přečteno");
    public static readonly Words OwnerSettings = new("In the Shelly app", "V aplikaci Shelly");
    public static readonly Words On = new("on", "zapnuto");
    public static readonly Words Off = new("off", "vypnuto");
    public static readonly Words NotSaid = new("not said", "neuvedeno");

    // The address.
    public static readonly Words AddressHeading = new("The charger's address", "Adresa nabíječky");
    public static readonly Words AddressIntro = new(
        "Gleanvolt has no address for a charger behind a plugin: the plugin reaches it over its local API. "
        + "Give the charger a fixed address in your router, and leave authentication off in the Shelly app.",
        "Gleanvolt pro nabíječku za pluginem adresu nemá: plugin ji obsluhuje přes její místní API. "
        + "Přidělte nabíječce v routeru pevnou adresu a v aplikaci Shelly nechte ověřování vypnuté.");
    public static readonly Words Address = new("Host name or IP address", "Název nebo IP adresa");
    public static readonly Words AddressNotSet = new("e.g. 192.168.1.50", "např. 192.168.1.50");
    public static readonly Words SaveAndCheck = new("Save and check", "Uložit a ověřit");
    public static readonly Words Checking = new("Checking…", "Ověřuje se…");
    public static readonly Words Remove = new("Remove", "Odstranit");
    public static readonly Words SourcePage = new("Saved here.", "Uloženo tady.");
    public static readonly Words SourceConfiguration = new(
        "From the plugin's configuration (Shelly:Host). An address saved here overrides it.",
        "Z konfigurace pluginu (Shelly:Host). Adresa uložená tady má přednost.");
    public static readonly Words SourceNone = new("No address.", "Žádná adresa.");
    public static readonly Words NotAnAddress = new(
        "That is not an address: a host name or an IP address, with :port when it is not 80.",
        "To není adresa: název nebo IP adresa, s :port, pokud to není 80.");
    public static readonly Words AddressAccepted = new("Saved. The charger answered: {0}.", "Uloženo. Nabíječka odpověděla: {0}.");
    public static readonly Words AddressNotChecked = new("Saved, but {0}", "Uloženo, ale {0}");
    public static readonly Words AddressNotKept = new(
        "In use, but it could not be saved: the data directory is not writable, and a restart will forget it.",
        "Používá se, ale nepodařilo se ji uložit: do datového adresáře nelze zapisovat a po restartu se zapomene.");
    public static readonly Words AddressRemovedConfiguration = new(
        "Removed. The address from the plugin's configuration is in use again.",
        "Odstraněno. Znovu se používá adresa z konfigurace pluginu.");
    public static readonly Words AddressRemovedNone = new("Removed. There is no address now.", "Odstraněno. Teď není zadaná žádná adresa.");
}
