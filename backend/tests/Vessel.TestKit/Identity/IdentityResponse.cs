using System.Net.Http.Json;
using System.Text.Json;

namespace Vessel.Tests.Identity;

internal static class IdentityResponse
{
    /// <summary>An identity error response: its status, its stable code and no caching.</summary>
    public static async Task Code(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
}
