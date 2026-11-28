namespace Gleanvolt.Cars;

/// <summary>The car plugin's shared texts, in English and Czech.</summary>
public static class CarText
{
    // The status line.
    public static readonly Words StatusWaiting = new("Waiting for the first reading.", "Čeká se na první údaje.");
    public static readonly Words StatusReading = new("Reading the car.", "Auto se čte.");
    public static readonly Words StatusNotReading = new("Not reading the car.", "Auto se nečte.");
    public static readonly Words Unreachable = new("The car's service could not be reached: {0}", "Služba auta není dostupná: {0}");

    // Gleanvolt.
    public static readonly Words GleanvoltCar = new("Gleanvolt's car", "Auto v Gleanvoltu");
    public static readonly Words SessionOpen = new("Gleanvolt is charging ({0}).", "Gleanvolt nabíjí ({0}).");
    public static readonly Words SessionClosed = new("Gleanvolt is not charging.", "Gleanvolt nenabíjí.");

    // The car.
    public static readonly Words Car = new("Car", "Auto");
    public static readonly Words Soc = new("State of charge", "Stav nabití");
    public static readonly Words ChargeState = new("Charging", "Nabíjení");
    public static readonly Words ChargeIdle = new("Not charging", "Nenabíjí");
    public static readonly Words ChargeCharging = new("Charging", "Nabíjí");
    public static readonly Words ChargeComplete = new("Finished", "Dokončeno");
    public static readonly Words Cable = new("Cable", "Kabel");
    public static readonly Words PlugConnected = new("Plugged in", "Připojen");
    public static readonly Words PlugDisconnected = new("Unplugged", "Odpojen");
    public static readonly Words Range = new("Range", "Dojezd");
    public static readonly Words TimeToFull = new("Time to full (the car's estimate)", "Doba do nabití (odhad auta)");
    public static readonly Words CapturedAt = new("Measured by the car", "Změřeno autem");
    public static readonly Words ReadNow = new("Read now", "Načíst teď");
}
