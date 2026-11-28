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
}
