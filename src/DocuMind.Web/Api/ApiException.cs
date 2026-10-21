using System.Net;
using System.Text.Json;

namespace DocuMind.Web.Api;

/// <summary>A failed API call, built from the API's RFC 9457 ProblemDetails body.</summary>
public sealed class ApiException(HttpStatusCode status, string message, IReadOnlyDictionary<string, string[]>? errors = null)
    : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();

    /// <summary>Validation messages if there are any, otherwise the problem's detail or title.</summary>
    public string UserMessage => Errors.Count > 0 ? string.Join(" ", Errors.Values.SelectMany(v => v)) : Message;

    public static async Task<ApiException> FromResponseAsync(HttpResponseMessage response)
    {
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your session has ended. Please sign in again.",
            HttpStatusCode.Forbidden => "You don't have permission to do that.",
            HttpStatusCode.NotFound => "Not found, or you don't have access to it.",
            HttpStatusCode.TooManyRequests => "Too many attempts. Wait a minute and try again.",
            _ => $"The request failed ({(int)response.StatusCode}).",
        };
        Dictionary<string, string[]>? errors = null;

        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                message = detail.GetString()!;
            else if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String && response.StatusCode != HttpStatusCode.Unauthorized)
                message = title.GetString()!;

            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Object)
                errors = errs.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray());
        }
        catch (JsonException)
        {
            // Empty or non-JSON body (e.g. a bare 401 from the bearer handler): keep the generic message.
        }

        return new ApiException(response.StatusCode, message, errors);
    }
}
