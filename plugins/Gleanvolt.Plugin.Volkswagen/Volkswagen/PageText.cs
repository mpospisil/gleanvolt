namespace Gleanvolt.Plugin.Volkswagen.Volkswagen;

/// <summary>
/// The plugin's texts in English and Czech (#415): its page, its sign-in and its status. Gleanvolt keeps
/// the brand and the screen names of Volkswagen's own site in English, so they stay so here too.
/// </summary>
internal static class PageText
{
    // The page.
    public static readonly Words StatusWaiting = new("Waiting for the first reading.", "Čeká se na první údaje.");
    public static readonly Words StatusReading = new("Reading the car.", "Údaje z auta se načítají.");
    public static readonly Words StatusNotReading = new("Not reading the car.", "Údaje z auta se nenačítají.");
    public static readonly Words LastReading = new("The last reading", "Poslední údaje");
    public static readonly Words Soc = new("State of charge", "Stav nabití");
    public static readonly Words Range = new("Range", "Dojezd");
    public static readonly Words Plug = new("Plug", "Kabel");
    public static readonly Words Charging = new("Charging", "Nabíjení");
    public static readonly Words CapturedByCar = new("Captured by the car", "Zaznamenáno autem");
    public static readonly Words Asked = new("Asked", "Dotázáno");
    public static readonly Words NotSaid = new("not said", "neuvedeno");
    public static readonly Words NotYet = new("not yet", "zatím ne");
    public static readonly Words NoReadingYet = new("No reading yet.", "Zatím žádné údaje.");
    public static readonly Words ToGo = new("{0} to go", "zbývá {0}");
    public static readonly Words PlugConnected = new("connected", "připojený");
    public static readonly Words PlugDisconnected = new("disconnected", "odpojený");
    public static readonly Words ChargeCharging = new("charging", "nabíjí se");
    public static readonly Words ChargeComplete = new("complete", "dokončeno");
    public static readonly Words ChargeIdle = new("not charging", "nenabíjí se");

    public static readonly Words SignInHeading = new("Sign in to volkswagen.de", "Přihlášení k volkswagen.de");
    public static readonly Words SignInIntro = new(
        "A cold sign-in always makes Volkswagen email a one-time code. Once you have entered it, this browser is "
        + "remembered, so a restart of the plugin should not need another. If Volkswagen asks for a code again, the "
        + "plugin stops reading the car until you sign in here; it never signs in by itself on a clock, which is how "
        + "an account gets locked.",
        "Při prvním přihlášení vám Volkswagen vždy pošle e-mailem jednorázový kód. Po jeho zadání si Volkswagen "
        + "tento prohlížeč pamatuje, takže restart pluginu by další kód potřebovat neměl. Pokud Volkswagen kód "
        + "vyžádá znovu, plugin přestane údaje z auta načítat, dokud se tady nepřihlásíte. Nikdy se nepřihlašuje "
        + "sám podle hodin, protože právě tak se účet zablokuje.");
    public static readonly Words CodeLabel = new("The code from the email", "Kód z e-mailu");
    public static readonly Words SubmitCode = new("Submit the code", "Odeslat kód");
    public static readonly Words SignIn = new("Sign in", "Přihlásit se");
    public static readonly Words Forget = new("Forget the session", "Zapomenout relaci");
    public static readonly Words SessionSaved = new("A signed-in session is saved.", "Přihlášená relace je uložená.");
    public static readonly Words NoSession = new("No session is saved.", "Žádná relace není uložená.");
    public static readonly Words KeptAs = new("Kept as {0}.", "Uloženo jako {0}.");

    public static readonly Words AccountHeading = new("The account", "Účet");
    public static readonly Words VwId = new("VW ID (email)", "VW ID (e-mail)");
    public static readonly Words Password = new("Password", "Heslo");
    public static readonly Words Vin = new("VIN", "VIN");
    public static readonly Words PasswordSaved = new("Saved. Leave empty to keep it.", "Uloženo. Nechte prázdné, pokud ho nechcete měnit.");
    public static readonly Words SaveAccount = new("Save the account", "Uložit účet");
    public static readonly Words AccountNotSaved = new(
        "The account could not be saved: the data directory is not writable.",
        "Účet se nepodařilo uložit: do datového adresáře nelze zapisovat.");
    public static readonly Words AccountSavedSignIn = new("Saved. Press Sign in.", "Uloženo. Klikněte na Přihlásit se.");
    public static readonly Words AccountIncomplete = new(
        "Saved. The VW ID, the password and the VIN are all needed.",
        "Uloženo. Je potřeba VW ID, heslo i VIN.");
    public static readonly Words AccountWarning = new(
        "The password and the session are passwords to your Volkswagen account, which also unlocks and locates the "
        + "car. They are kept in the plugin's data directory, never logged and never shown here again.",
        "Heslo i relace jsou přístupem k vašemu účtu Volkswagen, kterým lze auto také odemknout a zjistit, kde je. "
        + "Ukládají se do datového adresáře pluginu, nikdy se nezapisují do logu a už se tady znovu nezobrazí.");

    // The sign-in.
    public static readonly Words SignInNotConfigured = new(
        "Enter the VW ID, the password and the VIN, and save.",
        "Zadejte VW ID, heslo a VIN a uložte je.");
    public static readonly Words SignInEnterCode = new("Enter the code from the email.", "Zadejte kód z e-mailu.");
    public static readonly Words SignInSignedIn = new(
        "Signed in. This browser is remembered, so a restart should not need another code.",
        "Přihlášeno. Volkswagen si tento prohlížeč pamatuje, takže restart by další kód potřebovat neměl.");
    public static readonly Words SignInCodeEmailed = new(
        "Volkswagen has emailed a one-time code. Enter it below.",
        "Volkswagen vám poslal e-mailem jednorázový kód. Zadejte ho níže.");
    public static readonly Words SignInCodeRefused = new(
        "That code was not accepted. Check the newest email (an earlier one will not work) and try again. The "
        + "challenge is still open, so this does not need starting over.",
        "Kód nebyl přijat. Zkontrolujte nejnovější e-mail (starší kód nefunguje) a zkuste to znovu. Výzva je pořád "
        + "otevřená, takže není potřeba začínat znovu.");
    public static readonly Words SignInConsent = new(
        "Volkswagen is showing a consent or terms screen. Open volkswagen.de in a browser, clear it there, then sign "
        + "in here again.",
        "Volkswagen zobrazuje obrazovku se souhlasem nebo podmínkami. Otevřete volkswagen.de v prohlížeči, potvrďte "
        + "ji tam a pak se tady přihlaste znovu.");
    public static readonly Words SignInCredentials = new(
        "The VW ID or the password was not accepted. Check them above.",
        "VW ID nebo heslo nebylo přijato. Zkontrolujte je výše.");
    public static readonly Words SignInNoChallenge = new(
        "There is no code challenge open, so that code was not sent anywhere. Press Sign in to start one. If "
        + "Volkswagen does not email a new code, press Forget the session first and then Sign in: that discards a "
        + "challenge left over from an earlier attempt.",
        "Žádná výzva ke kódu není otevřená, takže kód nebyl nikam odeslán. Klikněte na Přihlásit se a začněte novou. "
        + "Pokud Volkswagen nový kód nepošle, klikněte nejdřív na Zapomenout relaci a pak na Přihlásit se: tím se "
        + "zahodí výzva, která zbyla z dřívějšího pokusu.");
    public static readonly Words SignInFailed = new(
        "The sign-in did not complete. If this repeats, wait a few minutes rather than retrying: a replayed login at a "
        + "real identity provider risks locking the account.",
        "Přihlášení se nedokončilo. Pokud se to opakuje, počkejte pár minut a nezkoušejte to hned znovu: opakované "
        + "přihlašování u skutečného poskytovatele identity může účet zablokovat.");

    // The status Gleanvolt shows, beside its own words for the code (#415).
    public static readonly Words AccountChanged = new("The account changed.", "Účet se změnil.");
    public static readonly Words Unreachable = new("volkswagen.de was unreachable ({0}).", "volkswagen.de nebyl dostupný ({0}).");
    public static readonly Words WantsCode = new("volkswagen.de wants a one-time code.", "volkswagen.de vyžaduje jednorázový kód.");
    public static readonly Words NoReading = new("volkswagen.de did not answer with a reading.", "volkswagen.de neodpověděl s údaji o autě.");
    public static readonly Words Forgotten = new("You forgot the session.", "Relaci jste zapomněli.");
    public static readonly Words ResumesAfterSignIn = new(
        "Reading resumes once you have signed in on the plugin's page. The charge is unaffected.",
        "Načítání se obnoví, jakmile se přihlásíte na stránce pluginu. Nabíjení to neovlivní.");
    public static readonly Words NotSetUp = new(
        "The Volkswagen account is not entered yet. Enter it on the plugin's page, on Gleanvolt's Car page.",
        "Účet Volkswagen ještě není zadaný. Zadejte ho na stránce pluginu, na stránce Auto v Gleanvoltu.");
}
