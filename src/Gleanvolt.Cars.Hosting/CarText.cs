namespace Gleanvolt.Cars;

/// <summary>The car plugin's shared texts, in English and Czech.</summary>
public static class CarText
{
    // The status line.
    public static readonly Words StatusWaiting = new("Waiting for the first reading.", "Čeká se na první údaje.");
    public static readonly Words StatusReading = new("Reading the car.", "Auto se čte.");
    public static readonly Words StatusNotReading = new("Not reading the car.", "Auto se nečte.");
    public static readonly Words Unreachable = new("The car's service could not be reached: {0}", "Služba auta není dostupná: {0}");
}
