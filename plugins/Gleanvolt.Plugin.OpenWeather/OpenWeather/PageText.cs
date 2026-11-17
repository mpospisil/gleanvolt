namespace Gleanvolt.Plugin.OpenWeather.OpenWeather;

/// <summary>
/// The plugin's texts in English and Czech (#424): its page and its status. Gleanvolt keeps the names
/// OpenWeather and OpenWeatherMap, and the screen names of its site, in English, so they stay so here.
/// The weather's own words (the condition) come from OpenWeatherMap in English.
/// </summary>
internal static class PageText
{
    // The status.
    public static readonly Words StatusWaiting = new("Waiting for Gleanvolt to ask.", "Čeká se, až se Gleanvolt zeptá.");
    public static readonly Words StatusReading = new("Reading the weather.", "Počasí se načítá.");
    public static readonly Words StatusNotReading = new("Not reading the weather.", "Počasí se nenačítá.");
    public static readonly Words NoKey = new(
        "There is no OpenWeatherMap key: enter one on this page.",
        "Chybí klíč OpenWeatherMap: zadejte ho na této stránce.");
    public static readonly Words NoLocation = new(
        "Gleanvolt has no location for the site: enter the latitude and longitude on Gleanvolt's Settings.",
        "Gleanvolt nemá polohu instalace: zadejte zeměpisnou šířku a délku v Nastavení Gleanvoltu.");
    public static readonly Words KeyRefused = new(
        "OpenWeatherMap refused the key. A new key can take a couple of hours to start working; otherwise enter the right one on this page.",
        "OpenWeatherMap klíč odmítl. Než nový klíč začne fungovat, může to trvat několik hodin; jinak na této stránce zadejte správný.");
    public static readonly Words RateLimited = new(
        "OpenWeatherMap is limiting calls; the plugin asks again at the next request.",
        "OpenWeatherMap omezuje počet dotazů; plugin se zeptá znovu při dalším požadavku.");
    public static readonly Words Unreachable = new("OpenWeatherMap could not be reached: {0}", "OpenWeatherMap není dostupný: {0}");
    public static readonly Words ServiceChanged = new(
        "OpenWeatherMap answered in a way the plugin doesn't understand: it may need an update.",
        "OpenWeatherMap odpověděl způsobem, kterému plugin nerozumí: možná potřebuje aktualizaci.");

    // The page.
    public static readonly Words Site = new("The site", "Instalace");
    public static readonly Words SiteFromGleanvolt = new(
        "Gleanvolt tells the plugin where the site is when it connects.",
        "Gleanvolt pluginu sdělí polohu instalace, když se připojí.");
    public static readonly Words Location = new("Location", "Poloha");
    public static readonly Words NotConnected = new("not connected to Gleanvolt yet", "zatím nepřipojeno ke Gleanvoltu");
    public static readonly Words NoLocationShort = new("not set in Gleanvolt", "v Gleanvoltu nezadaná");

    public static readonly Words LastReading = new("The last reading", "Poslední údaje");
    public static readonly Words NoReadingYet = new(
        "No reading yet. Gleanvolt asks when a charging session opens and when it closes.",
        "Zatím žádné údaje. Gleanvolt se ptá, když nabíjení začne a když skončí.");
    public static readonly Words Conditions = new("Conditions", "Počasí");
    public static readonly Words Temperature = new("Temperature", "Teplota");
    public static readonly Words Clouds = new("Cloud cover", "Oblačnost");
    public static readonly Words Humidity = new("Humidity", "Vlhkost");
    public static readonly Words Pressure = new("Pressure", "Tlak");
    public static readonly Words Visibility = new("Visibility", "Viditelnost");
    public static readonly Words Daylight = new("Sunrise – sunset", "Východ – západ slunce");
    public static readonly Words Observed = new("Observed by OpenWeatherMap", "Pozorováno OpenWeatherMap");
    public static readonly Words Asked = new("Asked", "Dotázáno");
    public static readonly Words NotSaid = new("not said", "neuvedeno");

    public static readonly Words KeyHeading = new("The OpenWeatherMap key", "Klíč OpenWeatherMap");
    public static readonly Words KeyIntro = new(
        "The weather comes from a free OpenWeatherMap account: create a key under API keys on openweathermap.org. "
        + "Saving checks the key with one call; the free plan allows a thousand a day.",
        "Počasí pochází z bezplatného účtu OpenWeatherMap: klíč vytvoříte v sekci API keys na openweathermap.org. "
        + "Uložení ověří klíč jedním dotazem; bezplatný tarif jich dovoluje tisíc denně.");
    public static readonly Words ApiKey = new("API key", "Klíč API");
    public static readonly Words KeySaved = new("Saved. Leave empty to keep it.", "Uloženo. Nechte prázdné, pokud ho nechcete měnit.");
    public static readonly Words KeyNotSet = new("not set", "nezadán");
    public static readonly Words SaveAndCheck = new("Save and check", "Uložit a ověřit");
    public static readonly Words Checking = new("Checking…", "Ověřuje se…");
    public static readonly Words Remove = new("Remove", "Odstranit");
    public static readonly Words SourcePage = new("Saved here.", "Uloženo tady.");
    public static readonly Words SourceEnvironment = new(
        "From the plugin's configuration (OpenWeather:ApiKey). A key saved here overrides it.",
        "Z konfigurace pluginu (OpenWeather:ApiKey). Klíč uložený tady má přednost.");
    public static readonly Words SourceNone = new("No key.", "Žádný klíč.");
    public static readonly Words KeptAs = new("Kept as {0}.", "Uloženo jako {0}.");
    public static readonly Words KeptEncrypted = new(
        "a file encrypted for this user (DPAPI)",
        "soubor zašifrovaný pro tohoto uživatele (DPAPI)");
    public static readonly Words KeptOwnerOnly = new(
        "a file only the plugin's user can read (0600), not encrypted",
        "soubor, který může číst jen uživatel pluginu (0600), nešifrovaný");
    public static readonly Words KeyAccepted = new("Saved. OpenWeatherMap accepted the key.", "Uloženo. OpenWeatherMap klíč přijal.");
    public static readonly Words KeyRefusedOnSave = new(
        "Saved, but OpenWeatherMap refused the key. A new key can take a couple of hours to start working.",
        "Uloženo, ale OpenWeatherMap klíč odmítl. Než nový klíč začne fungovat, může to trvat několik hodin.");
    public static readonly Words KeyCheckFailed = new(
        "Saved, but OpenWeatherMap could not be reached to check it.",
        "Uloženo, ale OpenWeatherMap není dostupný, takže klíč nešlo ověřit.");
    public static readonly Words KeyNotKept = new(
        "In use, but it could not be saved: the data directory is not writable, and a restart will forget it.",
        "Používá se, ale nepodařilo se ho uložit: do datového adresáře nelze zapisovat a po restartu se zapomene.");
    public static readonly Words KeyRemovedEnvironment = new(
        "Removed. The key from the plugin's configuration is in use again.",
        "Odstraněno. Znovu se používá klíč z konfigurace pluginu.");
    public static readonly Words KeyRemovedNone = new("Removed. There is no key now.", "Odstraněno. Teď není zadaný žádný klíč.");
}
