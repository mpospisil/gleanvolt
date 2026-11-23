namespace Gleanvolt.Chargers;

/// <summary>The shared page sections' and statuses' texts, in English and Czech.</summary>
public static class SharedText
{
    // The status line.
    public static readonly Words StatusWaiting = new("Waiting for Gleanvolt to ask.", "Čeká se, až se Gleanvolt zeptá.");
    public static readonly Words StatusReading = new("Reading the charger.", "Nabíječka se čte.");
    public static readonly Words StatusNotReading = new("Not reading the charger.", "Nabíječka se nečte.");
    public static readonly Words NoAddress = new(
        "There is no address for the charger: enter it on this page.",
        "Chybí adresa nabíječky: zadejte ji na této stránce.");
    public static readonly Words Unreachable = new("The charger could not be reached: {0}", "Nabíječka není dostupná: {0}");

    // Gleanvolt.
    public static readonly Words Gleanvolt = new("Gleanvolt", "Gleanvolt");
    public static readonly Words NotConnected = new("Not connected to Gleanvolt yet.", "Zatím nepřipojeno ke Gleanvoltu.");
    public static readonly Words Range = new("Current range", "Rozsah proudu");
    public static readonly Words Phases = new("Phases", "Fáze");
    public static readonly Words DryRun = new(
        "Gleanvolt is in dry-run: it reads the charger and sends no command.",
        "Gleanvolt je ve zkušebním režimu (dry-run): nabíječku čte a neposílá žádné příkazy.");
    public static readonly Words ChargerCan = new("This charger", "Tato nabíječka");
    public static readonly Words CanDo = new("{0}–{1} A, {2} phase(s)", "{0}–{1} A, fáze: {2}");

    // The reading.
    public static readonly Words Charger = new("The charger", "Nabíječka");
    public static readonly Words State = new("State", "Stav");
    public static readonly Words Power = new("Power", "Výkon");
    public static readonly Words Setpoint = new("Current setpoint", "Nastavený proud");
    public static readonly Words Allowed = new("Charging allowed", "Nabíjení povoleno");
    public static readonly Words Read = new("Read at", "Přečteno");
    public static readonly Words NotSaid = new("not known", "neznámé");
    public static readonly Words Yes = new("yes", "ano");
    public static readonly Words No = new("no", "ne");
    public static readonly Words NoReadingYet = new("No reading yet.", "Zatím žádné čtení.");

    // The address.
    public static readonly Words AddressHeading = new("The charger's address", "Adresa nabíječky");
    public static readonly Words Address = new("Address", "Adresa");
    public static readonly Words AddressNotSet = new("not set", "nenastavena");
    public static readonly Words SourcePage = new("Saved on this page.", "Uloženo na této stránce.");
    public static readonly Words SourceConfiguration = new("From the plugin's configuration.", "Z konfigurace pluginu.");
    public static readonly Words SourceNone = new("No address yet.", "Zatím žádná adresa.");
    public static readonly Words SaveAndCheck = new("Save and check", "Uložit a ověřit");
    public static readonly Words Checking = new("Checking…", "Ověřuje se…");
    public static readonly Words Remove = new("Remove", "Odebrat");
    public static readonly Words NotAnAddress = new(
        "That is not an address: a host name or an IP address, with a port if it is not the usual one.",
        "To není adresa: název nebo IP adresa, s portem, pokud není obvyklý.");
    public static readonly Words AddressAccepted = new("Saved. The charger answers: {0}", "Uloženo. Nabíječka odpovídá: {0}");
    public static readonly Words AddressNotChecked = new(
        "Saved, but the charger did not answer: {0}",
        "Uloženo, ale nabíječka neodpověděla: {0}");
    public static readonly Words AddressNotKept = new(
        "In use, but it could not be written to the data directory: it is lost on a restart.",
        "Používá se, ale nepodařilo se ji zapsat do datového adresáře: po restartu se ztratí.");
    public static readonly Words AddressRemovedNone = new("Removed. There is no address now.", "Odebráno. Teď není žádná adresa.");
    public static readonly Words AddressRemovedConfiguration = new(
        "Removed. The address from the plugin's configuration is in use again.",
        "Odebráno. Znovu se používá adresa z konfigurace pluginu.");

    // The search.
    public static readonly Words SearchHeading = new("Find the charger", "Najít nabíječku");
    public static readonly Words SearchIntro = new(
        "Searches this machine's home network for the charger. A /24 takes up to a minute.",
        "Prohledá domácí síť tohoto počítače a najde nabíječku. Síť /24 trvá až minutu.");
    public static readonly Words Network = new("Network", "Síť");
    public static readonly Words Search = new("Search", "Hledat");
    public static readonly Words Searching = new("Searching {0}…", "Prohledává se {0}…");
    public static readonly Words Found = new("Found {0}.", "Nalezeno: {0}.");
    public static readonly Words FoundNothing = new(
        "Nothing answered as this charger. Is its local interface switched on?",
        "Nic neodpovědělo jako tato nabíječka. Je zapnuté její místní rozhraní?");
    public static readonly Words Use = new("Use", "Použít");
    public static readonly Words BadNetwork = new(
        "Type a private network such as 192.168.1.0/24, no larger than a /22.",
        "Zadejte soukromou síť, například 192.168.1.0/24, nejvýš /22.");

    // The recording.
    public static readonly Words RecordingHeading = new("Recording for testers", "Záznam pro testery");
    public static readonly Words RecordingIntro = new(
        "Records every exchange with the charger, as sent and as received, for {0} minutes, while Gleanvolt runs as usual. "
        + "Download it and attach it to a GitHub issue: it is how a charger nobody here has is verified. It holds the "
        + "charger's answers and no password.",
        "Zaznamená každou výměnu s nabíječkou, jak byla odeslána a přijata, po dobu {0} minut, zatímco Gleanvolt běží jako "
        + "obvykle. Stáhněte ho a přiložte k issue na GitHubu: tak se ověřuje nabíječka, kterou tu nikdo nemá. Obsahuje "
        + "odpovědi nabíječky a žádné heslo.");
    public static readonly Words StartRecording = new("Start recording", "Spustit záznam");
    public static readonly Words StopRecording = new("Stop", "Zastavit");
    public static readonly Words Recording = new("Recording: {0} exchanges so far, until {1}.", "Nahrává se: zatím {0} výměn, do {1}.");
    public static readonly Words Recorded = new("{0} exchanges recorded.", "Zaznamenáno výměn: {0}.");
    public static readonly Words Download = new("Download", "Stáhnout");
}
