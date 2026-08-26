using System.Collections.Frozen;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Gleanvolt.Api;

/// <summary>
/// The XML documentation files the build writes, read back at runtime so their contents can reach the
/// OpenAPI document (issue #126).
///
/// <para><b>Why this exists at all.</b> .NET's own OpenAPI generator already puts most of this
/// codebase's comments into the document — it reads the same files at compile time, and every schema
/// property's description comes from there. It does not do it for <b>enums</b>: an enum hoisted into
/// <c>components/schemas</c> arrives as a bare list of values, with the type's summary and every
/// member's summary dropped. That is the difference between <c>"mode": "targeted"</c> and a client
/// being told what targeted charging <em>is</em>, which for a generated MCP tool surface is the
/// difference between a usable tool and a guess.</para>
///
/// <para><b>Never authoritative.</b> This only fills in what the generator left empty — see
/// <see cref="XmlDocumentationTransformer"/>. If a future SDK starts describing enums itself,
/// this quietly stops having anything to do rather than fighting it.</para>
///
/// <para><b>Never fatal.</b> Every failure path here ends in "no documentation": a missing file, a
/// malformed one, a trimmed publish. The document is still valid without descriptions — it is only
/// poorer — and an API that refused to serve its own schema because a comment file was absent would
/// be a far worse trade.</para>
/// </summary>
internal sealed partial class XmlDocumentation
{
    private static readonly Lazy<XmlDocumentation> Instance = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Documentation ids ("T:Some.Type", "F:Some.Type.Member") to their summary text.</summary>
    private readonly FrozenDictionary<string, string> _summaries;

    private XmlDocumentation(FrozenDictionary<string, string> summaries) => _summaries = summaries;

    /// <summary>The documentation beside the running assemblies, read once.</summary>
    internal static XmlDocumentation Current => Instance.Value;

    /// <summary>The summary for a type, or null when it has none.</summary>
    internal string? ForType(Type type) => Summary($"T:{DocId(type)}");

    /// <summary>The summary for an enum member, or null when it has none.</summary>
    internal string? ForEnumMember(Type type, string memberName) => Summary($"F:{DocId(type)}.{memberName}");

    private string? Summary(string id) => _summaries.TryGetValue(id, out var summary) ? summary : null;

    // Nested types are documented as Outer.Inner rather than Outer+Inner, which is the one place the
    // CLR's own name and the documentation id disagree.
    private static string DocId(Type type) =>
        (type.FullName ?? type.Name).Replace('+', '.');

    private static XmlDocumentation Load()
    {
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            // Every .xml beside the assemblies, not a hard-coded list: the document describes types
            // from Gleanvolt.Api and Gleanvolt.Core today, and a type moving between assemblies should
            // not silently lose its documentation.
            foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.xml"))
            {
                Read(path, summaries);
            }
        }
        catch (Exception)
        {
            // Directory unreadable. No documentation, which is a poorer document and not a broken one.
        }

        return new XmlDocumentation(summaries.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static void Read(string path, Dictionary<string, string> summaries)
    {
        try
        {
            // Not every .xml beside an assembly is a documentation file; one that isn't simply yields
            // no members and is skipped.
            foreach (var member in XDocument.Load(path).Descendants("member"))
            {
                if (member.Attribute("name")?.Value is not { Length: > 0 } id)
                {
                    continue;
                }

                if (member.Element("summary") is not { } summary)
                {
                    continue;
                }

                var text = Flatten(summary);

                if (text.Length > 0)
                {
                    summaries.TryAdd(id, text);
                }
            }
        }
        catch (Exception)
        {
            // A malformed or unreadable file costs that file's documentation and nothing else.
        }
    }

    /// <summary>
    /// One summary as a line of prose. The comments here are written for a reader: hard-wrapped, full
    /// of <c>&lt;see cref&gt;</c> and <c>&lt;para&gt;</c>, and none of that survives usefully in a JSON
    /// string. A cref becomes the name it points at, the markup is dropped, and the wrapping is undone.
    /// </summary>
    private static string Flatten(XElement summary)
    {
        var builder = new System.Text.StringBuilder();

        foreach (var node in summary.DescendantNodes())
        {
            switch (node)
            {
                case XText text:
                    builder.Append(text.Value);
                    break;

                // A cref carries the only information in the element: "see ChargeControlMode.Solar"
                // reads correctly, where dropping it leaves a sentence with a hole in it.
                case XElement element when element.Name == "see" || element.Name == "seealso":
                    var reference = element.Attribute("cref")?.Value ?? element.Attribute("langword")?.Value;
                    if (reference is { Length: > 2 } && reference[1] == ':')
                    {
                        reference = reference[2..];
                    }

                    builder.Append(reference?.Split('.')[^1]);
                    break;

                // A paragraph is a paragraph. Everything else -- <b>, <em>, <c> -- contributes only
                // its text, which the XText case above already collects.
                case XElement element when element.Name == "para":
                    builder.Append("\n\n");
                    break;
            }
        }

        return Whitespace().Replace(builder.ToString(), " ").Trim();
    }

    /// <summary>Runs of whitespace, newlines included — the hard wrapping in the source.</summary>
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
