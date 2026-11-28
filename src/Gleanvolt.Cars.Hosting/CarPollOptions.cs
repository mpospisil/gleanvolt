namespace Gleanvolt.Cars;

/// <summary>
/// How often a car plugin reads the car, from the plugin's section: <c>&lt;Section&gt;:PollInterval</c>,
/// <c>&lt;Section&gt;:IdlePollInterval</c> and <c>&lt;Section&gt;:MinimumTimeBetweenReads</c>. The defaults are the
/// ones Gleanvolt's own car feeds used.
/// </summary>
public sealed class CarPollOptions
{
    /// <summary>How often while Gleanvolt has a charging session open, or the car says it is charging.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often otherwise.</summary>
    public TimeSpan IdlePollInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The shortest time between two requests to the car's maker: a read sooner than this is answered with the last
    /// reading, however many reasons to read arrive (Gleanvolt's refresh, a session opening, Read now on the page).
    /// </summary>
    public TimeSpan MinimumTimeBetweenReads { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Refuses intervals the clock can't run on: a poll interval of zero would publish to Gleanvolt in a loop, and a
    /// negative wait would stop the plugin. <paramref name="section"/> names the keys in the message.
    /// </summary>
    public void Validate(string section)
    {
        if (PollInterval <= TimeSpan.Zero)
        {
            throw Refused(section, nameof(PollInterval), PollInterval, "longer than zero");
        }

        if (IdlePollInterval <= TimeSpan.Zero)
        {
            throw Refused(section, nameof(IdlePollInterval), IdlePollInterval, "longer than zero");
        }

        if (MinimumTimeBetweenReads < TimeSpan.Zero)
        {
            throw Refused(section, nameof(MinimumTimeBetweenReads), MinimumTimeBetweenReads, "zero or longer");
        }
    }

    private static InvalidOperationException Refused(string section, string key, TimeSpan value, string must) =>
        new($"{section}:{key} is {value}; it must be {must}, e.g. 00:05:00 for five minutes.");
}
