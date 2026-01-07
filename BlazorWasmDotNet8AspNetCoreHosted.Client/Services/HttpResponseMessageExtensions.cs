using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BlazorWasmDotNet8AspNetCoreHosted.Client.Services;

// Виняток для обробки детальних помилок від API
public sealed class ApiErrorException : Exception
{
    public ApiErrorException(HttpStatusCode statusCode, string message, IReadOnlyList<string>? errors = null, IReadOnlyList<string>? warnings = null, Exception? innerException = null)
        : base(string.IsNullOrWhiteSpace(message) ? $"{(int)statusCode} {statusCode}" : message, innerException)
    {
        StatusCode = statusCode;
        Errors = errors ?? Array.Empty<string>();
        Warnings = warnings ?? Array.Empty<string>();
    }
    public HttpStatusCode StatusCode { get; }
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<string> Warnings { get; }
}

public static class HttpResponseMessageExtensions
{
    // Перевіряє відповідь HTTP і кидає виняток з деталями помилки.
    public static async Task EnsureSuccessWithDetailsAsync(this HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        string? payload = null;
        if (response.Content is not null)
        {
            try
            {
                payload = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch
            {
            }
        }
        var (message, errors, warnings) = ParseErrorPayload(payload);
        throw new ApiErrorException(response.StatusCode, message ?? response.ReasonPhrase ?? string.Empty, errors, warnings);
    }
    // Розбирає JSON-помилку в узгоджений формат повідомлення.
    private static (string? Message, IReadOnlyList<string>? Errors, IReadOnlyList<string>? Warnings) ParseErrorPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return (null, null, null);
        }
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            string? message = TryGetString(root, "message")
                               ?? TryGetString(root, "title")
                               ?? TryGetString(root, "detail");
            var errors = root.TryGetProperty("errors", out var errorsElement) ? ExtractStrings(errorsElement) : null;
            var warnings = root.TryGetProperty("warnings", out var warningsElement) ? ExtractStrings(warningsElement) : null;
            return (message ?? payload, errors, warnings);
        }
        catch (JsonException)
        {
            return (payload, null, null);
        }
    }
    // Безпечно читає рядкове поле з JSON.
    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }
        return null;
    }
    // Витягує список рядків з масиву/об'єкта JSON.
    private static IReadOnlyList<string>? ExtractStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                var list = new List<string>();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is string value)
                    {
                        list.Add(value);
                    }
                    else if (item.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                    {
                        list.Add(item.ToString());
                    }
                }
                return list.Count > 0 ? list : null;
            case JsonValueKind.Object:
                var aggregated = new List<string>();
                foreach (var property in element.EnumerateObject())
                {
                    var nested = ExtractStrings(property.Value);
                    if (nested is { Count: > 0 })
                    {
                        aggregated.AddRange(nested);
                    }
                }
                return aggregated.Count > 0 ? aggregated : null;
            case JsonValueKind.String:
                return new[] { element.GetString() ?? string.Empty };
            default:
                return null;
        }
    }
}
