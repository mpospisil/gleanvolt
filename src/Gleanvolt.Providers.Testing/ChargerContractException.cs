namespace Gleanvolt.Providers.Testing;

/// <summary>The plugin sent something Gleanvolt would refuse.</summary>
public sealed class ChargerContractException : Exception
{
    public ChargerContractException(string message, IReadOnlyList<string> problems)
        : base(problems.Count == 0 ? message : message + " " + string.Join(" ", problems))
    {
        Problems = problems;
    }

    /// <summary>Every rule that was broken, in English.</summary>
    public IReadOnlyList<string> Problems { get; }
}
