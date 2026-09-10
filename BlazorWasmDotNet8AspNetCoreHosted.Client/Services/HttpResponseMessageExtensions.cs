using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BlazorWasmDotNet8AspNetCoreHosted.Client.Services;

// Виняток для обробки детальних помилок від API
public sealed class ApiErrorException : Exception
{
    public ApiErrorException(
        HttpStatusCode statusCode,
        string message,
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<string>? warnings = null,
        string? traceId = null,
        string? code = null,
        Exception? innerException = null)
        : base(string.IsNullOrWhiteSpace(message) ? $"{(int)statusCode} {statusCode}" : message, innerException)
    {
        StatusCode = statusCode;
        Errors = errors ?? Array.Empty<string>();
        Warnings = warnings ?? Array.Empty<string>();
        TraceId = traceId;
        Code = code;
    }
    public HttpStatusCode StatusCode { get; }
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<string> Warnings { get; }
    public string? TraceId { get; }
    public string? Code { get; }
}

public static class HttpResponseMessageExtensions
{
    // Отримує JSON лише після перевірки статусу, щоб клієнт не втрачав деталі помилки сервера.
    public static async Task<T?> GetFromJsonWithDetailsAsync<T>(
        this HttpClient client,
        string requestUri,
        CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(requestUri, cancellationToken);
        await response.EnsureSuccessWithDetailsAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return default;
        }
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

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
        var (message, errors, warnings, traceId, code) = ParseErrorPayload(payload);
        throw new ApiErrorException(
            response.StatusCode,
            message ?? BuildFallbackMessage(response.StatusCode, response.ReasonPhrase),
            errors,
            warnings,
            traceId,
            code);
    }
    // Розбирає JSON-помилку в узгоджений формат повідомлення.
    private static (string? Message, IReadOnlyList<string>? Errors, IReadOnlyList<string>? Warnings, string? TraceId, string? Code) ParseErrorPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return (null, null, null, null, null);
        }
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            string? message = TryGetString(root, "message")
                               ?? TryGetString(root, "detail")
                               ?? TryGetString(root, "title");
            var errors = TryGetProperty(root, "errors", out var errorsElement) ? ExtractStrings(errorsElement) : null;
            var warnings = TryGetProperty(root, "warnings", out var warningsElement) ? ExtractStrings(warningsElement) : null;
            var traceId = TryGetString(root, "traceId");
            var code = TryGetString(root, "code");
            return (message ?? payload, errors, warnings, traceId, code);
        }
        catch (JsonException)
        {
            return (payload, null, null, null, null);
        }
    }
    // Безпечно читає рядкове поле з JSON.
    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (TryGetProperty(root, propertyName, out var element) && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }
        return null;
    }
    // Шукає поле JSON без залежності від регістру імені.
    private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
    // Повертає зрозуміле повідомлення, якщо сервер не надав ProblemDetails.
    private static string BuildFallbackMessage(HttpStatusCode statusCode, string? reasonPhrase)
        => statusCode switch
        {
            HttpStatusCode.BadRequest => "Запит має некоректні параметри.",
            HttpStatusCode.Conflict => "Операцію не виконано через конфлікт даних.",
            HttpStatusCode.TooManyRequests => "Забагато одночасних операцій. Дочекайтеся завершення поточної задачі та повторіть спробу.",
            HttpStatusCode.ServiceUnavailable => "Сервіс тимчасово недоступний. Повторіть спробу пізніше.",
            _ => string.IsNullOrWhiteSpace(reasonPhrase)
                ? $"HTTP {(int)statusCode}."
                : reasonPhrase
        };
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
