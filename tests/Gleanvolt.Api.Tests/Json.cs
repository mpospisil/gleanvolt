using System.Net.Http.Json;
using System.Text.Json;

namespace Gleanvolt.Api.Tests;

/// <summary>Reading responses the way a client would: as JSON, by the names on the wire.</summary>
internal static class Json
{
    internal static async Task<JsonElement> ReadAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    internal static Task<HttpResponseMessage> PostAsJsonAsync(this HttpClient client, string url, object body) =>
        HttpClientJsonExtensions.PostAsJsonAsync(client, url, body);

    internal static Task<HttpResponseMessage> PutAsJsonAsync(this HttpClient client, string url, object body) =>
        HttpClientJsonExtensions.PutAsJsonAsync(client, url, body);

    internal static double Number(this JsonElement element, string property) =>
        element.GetProperty(property).GetDouble();

    internal static string? Text(this JsonElement element, string property) =>
        element.GetProperty(property).ValueKind == JsonValueKind.Null
            ? null
            : element.GetProperty(property).GetString();
}
