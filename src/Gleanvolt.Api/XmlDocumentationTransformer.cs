using System.Reflection;
using System.Text;
using Microsoft.OpenApi;

namespace Gleanvolt.Api;

/// <summary>
/// Puts the source's own comments on the schemas .NET's generator leaves bare (issue #126) — in
/// practice, the enums.
///
/// <para>An enum reaches <c>components/schemas</c> as nothing but its list of values. Both halves of
/// what a reader needs are in the source and neither survives the trip: what the type <em>is</em>, and
/// what each value <em>means</em>. For a document whose whole purpose is to be the entire interface a
/// generated client — an MCP tool surface especially — has to work from, <c>one of: off, solar,
/// forecasted, fastNoBattery, targeted</c> is a list of words to guess between.</para>
///
/// <para><b>A document transformer rather than a schema one, and that is not a preference.</b> An
/// <c>IOpenApiSchemaTransformer</c> is never invoked for an enum that has been hoisted into
/// <c>components</c> — the obvious implementation compiles, registers, runs, and changes nothing at
/// all. By the time the document is finished the component is an ordinary object in a dictionary, and
/// editing it is unambiguous.</para>
///
/// <para><b>It only ever fills in blanks.</b> A description the generator produced is left exactly as
/// it is, so this cannot silently disagree with the compile-time reading of the same comments — and if
/// a later SDK starts describing enums itself, this stops finding anything to do rather than fighting
/// it.</para>
/// </summary>
internal static class XmlDocumentationTransformer
{
    /// <summary>
    /// Describes every enum component that has no description, from the XML documentation beside the
    /// running assemblies.
    /// </summary>
    /// <param name="document">The finished document, modified in place.</param>
    /// <param name="assemblies">
    /// Where to look for the CLR type behind a schema name. The document names a component by the
    /// type's short name, which is all there is to match on; a name that matches two types in these
    /// assemblies is left alone rather than guessed at.
    /// </param>
    internal static void DescribeEnums(OpenApiDocument document, params Assembly[] assemblies)
    {
        if (document.Components?.Schemas is not { Count: > 0 } schemas)
        {
            return;
        }

        var enums = EnumsByName(assemblies);
        var documentation = XmlDocumentation.Current;

        foreach (var (name, schema) in schemas)
        {
            if (schema is not OpenApiSchema concrete
                || concrete.Enum is not { Count: > 0 }
                || !enums.TryGetValue(name, out var type)
                || type is null)
            {
                continue;
            }

            if (Describe(documentation, type, concrete) is { Length: > 0 } description)
            {
                concrete.Description = description;
            }
        }
    }

    private static string? Describe(XmlDocumentation documentation, Type type, OpenApiSchema schema)
    {
        // Whatever the built-in generator already made of the type's summary is kept exactly as it is;
        // this only ever adds the legend it does not produce. Falling back to reading the summary here
        // covers the enums it describes not at all.
        var description = new StringBuilder(
            string.IsNullOrWhiteSpace(schema.Description) ? documentation.ForType(type) ?? string.Empty : schema.Description);

        var names = Enum.GetNames(type);

        // One line per value, in the order the document lists them, naming each as it appears **on the
        // wire** rather than as it is spelled in C#. A reader of this document never sees
        // `FastNoBattery`; they see `fastNoBattery`, and a legend in the other spelling would be a
        // legend for a different document.
        foreach (var value in schema.Enum!)
        {
            if (value?.ToString() is not { Length: > 0 } serialised)
            {
                continue;
            }

            // Matched case-insensitively rather than by re-deriving the naming policy: the policy is
            // the host's to choose and could change, while "the same word in a different case" holds
            // whichever one is in force.
            var member = Array.Find(
                names, candidate => string.Equals(candidate, serialised, StringComparison.OrdinalIgnoreCase));

            if (member is null || documentation.ForEnumMember(type, member) is not { Length: > 0 } text)
            {
                continue;
            }

            if (description.Length > 0)
            {
                description.Append("\n\n");
            }

            description.Append("- `").Append(serialised).Append("`: ").Append(text);
        }

        return description.Length > 0 ? description.ToString() : null;
    }

    /// <summary>
    /// Public enums by short name. A name claimed by two types maps to null — ambiguous, so the
    /// document keeps whatever it had rather than being given one of them at random.
    /// </summary>
    private static Dictionary<string, Type?> EnumsByName(Assembly[] assemblies)
    {
        var found = new Dictionary<string, Type?>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        {
            IEnumerable<Type> types;

            try
            {
                types = assembly.GetExportedTypes();
            }
            catch (Exception)
            {
                // An assembly that cannot be reflected over costs its own types and nothing else.
                continue;
            }

            foreach (var type in types.Where(candidate => candidate.IsEnum))
            {
                found[type.Name] = found.ContainsKey(type.Name) && found[type.Name] != type ? null : type;
            }
        }

        return found;
    }
}
