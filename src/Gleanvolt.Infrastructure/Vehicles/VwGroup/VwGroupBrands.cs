namespace Gleanvolt.Infrastructure.Vehicles.VwGroup;

/// <summary>
/// The OIDC client id each brand's portal signs in with, so an owner names <b>their brand</b> rather
/// than fetching a GUID out of a redirect URL with developer tools open.
///
/// <para>The ids belong to the portal rather than to us — there is no "register your own OAuth app"
/// route on this interface — so they are looked up, not owned. Every Home Assistant integration for
/// this portal ships the same table and asks only which brand you drive; asking an owner to read
/// <c>client_id=</c> out of an address bar was a worse answer to the same question, and this is the
/// correction.</para>
///
/// <para><b>Reverse-engineered, not published.</b> VW documents none of this, so a value here can go
/// stale without warning. That is survivable because it is not the only route:
/// <see cref="VwGroupPortalOptions.ClientId"/> still overrides the table outright, which is both the
/// escape hatch for a brand that has changed its id and the way a brand missing from this list is
/// used at all.</para>
///
/// <para>Two pairs share an id, which is a fact about the portal rather than a shortcut here: VW
/// passenger cars and commercial vehicles are one client, and SEAT and Cupra are another.</para>
/// </summary>
public static class VwGroupBrands
{
    private const string Volkswagen = "9b58543e-1c15-4193-91d5-8a14145bebb0@apps_vw-dilab_com";
    private const string SeatCupra = "f85e5b69-e3b2-43aa-9c0d-1b7d0e0b576f@apps_vw-dilab_com";

    private static readonly Dictionary<string, string> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vw"] = Volkswagen,
        ["volkswagen"] = Volkswagen,
        ["vwn"] = Volkswagen,
        ["vw-commercial"] = Volkswagen,
        ["audi"] = "cc29b87a-5e9a-4362-aecf-5adea6b01bbb@apps_vw-dilab_com",
        ["skoda"] = "3ea88bf9-1d4e-4a68-b3ad-4098c1f1d246@apps_vw-dilab_com",
        ["seat"] = SeatCupra,
        ["cupra"] = SeatCupra,
        ["bentley"] = "d38aac0f-3d89-4a63-8538-b75b31322c7b@apps_vw-dilab_com",
    };

    /// <summary>
    /// The portal's own name for each brand. It travels in the OIDC <c>state</c> parameter, which the
    /// portal decodes on the way back to know which brand and locale the callback belongs to — so it
    /// is not an opaque nonce and a random one is simply not understood.
    /// </summary>
    private static readonly Dictionary<string, string> PortalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vw"] = "VOLKSWAGEN_PASSENGER_CARS",
        ["volkswagen"] = "VOLKSWAGEN_PASSENGER_CARS",
        ["vwn"] = "VOLKSWAGEN_COMMERCIAL_VEHICLES",
        ["vw-commercial"] = "VOLKSWAGEN_COMMERCIAL_VEHICLES",
        ["audi"] = "AUDI",
        ["skoda"] = "SKODA",
        ["seat"] = "SEAT",
        ["cupra"] = "CUPRA",
        ["bentley"] = "BENTLEY",
    };

    /// <summary>The portal's brand key for a brand name, or null when the name is unrecognised.</summary>
    public static string? PortalKey(string? brand) =>
        string.IsNullOrWhiteSpace(brand) ? null
            : PortalKeys.TryGetValue(brand.Trim(), out var key) ? key : null;

    /// <summary>The brand names that resolve, for a message that has to list them.</summary>
    public static string Known => "vw, audi, skoda, seat, cupra, bentley";

    /// <summary>
    /// The client id for a brand name, or <c>null</c> when the name is empty or unrecognised. An
    /// unrecognised name is deliberately <b>not</b> silently treated as "none": a typo would then
    /// fail as a missing client id rather than as a misspelt brand, and those want different fixes.
    /// </summary>
    public static string? Resolve(string? brand) =>
        string.IsNullOrWhiteSpace(brand) ? null
            : Ids.TryGetValue(brand.Trim(), out var id) ? id
            : null;

    /// <summary>Whether a non-empty brand name is one this table knows.</summary>
    public static bool IsKnown(string? brand) =>
        !string.IsNullOrWhiteSpace(brand) && Ids.ContainsKey(brand.Trim());
}
