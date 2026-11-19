using System.Text;
using System.Text.Json;
using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant.Services;

/// <summary>What the writer has to do about a refused request, which is the
/// only distinction worth making: the HTTP status alone does not say it, since
/// a 429 means "wait" for a rate limit and "pay" for an exhausted quota.</summary>
public enum AiProviderFailure
{
    Authentication,
    NotFound,
    RateLimited,
    QuotaExhausted,
    Unavailable,
    Rejected,
    Other,
}

/// <summary>
/// A provider answered with an error status. Carries what the response said,
/// shortened, so each feature can show the writer a localized explanation with
/// <see cref="Describe"/> instead of .NET's generic status text. The exception's
/// own message is an English summary for anything that does not localize it.
/// </summary>
public sealed class AiProviderException : InvalidOperationException
{
    internal AiProviderException(
        string provider, int statusCode, AiProviderFailure failure, string? providerMessage, TimeSpan? retryAfter)
        : base(Summary(provider, statusCode, failure, providerMessage))
    {
        Provider = provider;
        StatusCode = statusCode;
        Failure = failure;
        ProviderMessage = providerMessage;
        RetryAfter = retryAfter;
    }

    /// <summary>Provider id as stored in settings, for example "openai".</summary>
    public string Provider { get; }
    public int StatusCode { get; }
    public AiProviderFailure Failure { get; }

    /// <summary>The provider's own explanation, shortened and stripped of
    /// control characters. Null when the body carried none worth showing.</summary>
    public string? ProviderMessage { get; }
    public TimeSpan? RetryAfter { get; }

    /// <summary>The explanation and next step, in the writer's language.</summary>
    public string Describe(IExtensionLocalization loc)
    {
        var name = Provider == "custom" ? loc.T("settings.aiCustomProvider")
            : AiProviders.Labels.TryGetValue(Provider, out var label) ? label : Provider;
        var parts = new List<string> { Explanation(loc, name) };
        if (RetryAfter is { } wait && Failure is AiProviderFailure.RateLimited or AiProviderFailure.Unavailable)
        {
            var seconds = (int)Math.Ceiling(wait.TotalSeconds);
            parts.Add(seconds > 120
                ? loc.T("providerError.retryAfterMinutes", (int)Math.Ceiling(seconds / 60.0))
                : loc.T("providerError.retryAfter", Math.Max(1, seconds)));
        }
        if (!string.IsNullOrEmpty(ProviderMessage))
            parts.Add(loc.T("providerError.providerSays", ProviderMessage));
        return string.Join(" ", parts);
    }

    // Each key spelled out in full so the locale checks can see it is used.
    private string Explanation(IExtensionLocalization loc, string name) => Failure switch
    {
        AiProviderFailure.Authentication => loc.T("providerError.authentication", name, StatusCode),
        AiProviderFailure.NotFound => loc.T("providerError.notFound", name, StatusCode),
        AiProviderFailure.RateLimited => loc.T("providerError.rateLimited", name, StatusCode),
        AiProviderFailure.QuotaExhausted => loc.T("providerError.quotaExhausted", name, StatusCode),
        AiProviderFailure.Unavailable => loc.T("providerError.unavailable", name, StatusCode),
        AiProviderFailure.Rejected => loc.T("providerError.rejected", name, StatusCode),
        _ => loc.T("providerError.other", name, StatusCode),
    };

    private static string Summary(string provider, int status, AiProviderFailure failure, string? message)
    {
        var name = AiProviders.Labels.TryGetValue(provider, out var label) ? label : provider;
        var head = $"{name} returned HTTP {status} ({failure})";
        return string.IsNullOrEmpty(message) ? head + "." : $"{head}: {message}";
    }
}

/// <summary>Turns a failed provider response into an <see cref="AiProviderException"/>.
/// Shared by the OpenAI-compatible and Anthropic clients so the two explain the
/// same failure the same way.</summary>
public static class AiProviderErrors
{
    /// <summary>Longest provider explanation shown. Error bodies are the
    /// provider's text, not ours, and some quote parts of the request.</summary>
    internal const int MaxMessageLength = 240;

    /// <summary>Error bodies are a few hundred bytes; anything larger is not an
    /// API error and is not worth reading to the end.</summary>
    private const int MaxBodyChars = 16 * 1024;

    private static readonly string[] RateLimitCodes =
        ["rate_limit_exceeded", "rate_limit_error", "rate_limit", "requests", "tokens", "too_many_requests"];

    private static readonly string[] QuotaCodes =
        ["insufficient_quota", "quota_exceeded", "billing_hard_limit_reached", "billing_not_active", "insufficient_credits", "payment_required"];

    /// <summary>The text a feature shows for a failure: the localized
    /// explanation for a provider refusal, the exception's message otherwise.</summary>
    public static string Describe(Exception error, IExtensionLocalization loc)
    {
        if (error is AggregateException aggregate) error = aggregate.GetBaseException();
        return error is AiProviderException provider ? provider.Describe(loc) : error.Message;
    }

    /// <summary>Returns when the response succeeded; otherwise reads the body
    /// and throws. Cancellation while reading the body propagates unchanged.</summary>
    public static async Task ThrowIfFailedAsync(
        HttpResponseMessage response, string provider, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        throw Create(provider, (int)response.StatusCode, body, RetryAfter(response));
    }

    internal static AiProviderException Create(string provider, int status, string? body, TimeSpan? retryAfter)
    {
        var (message, code) = ReadError(body);
        return new AiProviderException(provider, status, Classify(status, code, message), Shorten(message), retryAfter);
    }

    internal static AiProviderFailure Classify(int status, string? code, string? message)
    {
        if (status is 400 or 402 or 403 or 429 && IsQuota(code, message))
            return AiProviderFailure.QuotaExhausted;
        return status switch
        {
            401 or 403 => AiProviderFailure.Authentication,
            402 => AiProviderFailure.QuotaExhausted,
            404 => AiProviderFailure.NotFound,
            429 => AiProviderFailure.RateLimited,
            // 529 is Anthropic's "overloaded".
            408 or >= 500 and <= 599 => AiProviderFailure.Unavailable,
            400 or 409 or 413 or 422 => AiProviderFailure.Rejected,
            _ => AiProviderFailure.Other,
        };
    }

    /// <summary>A recognised error code decides; only without one does the
    /// wording decide. OpenAI's rate-limit text links to the billing page, so
    /// "billing" in a message is no evidence of an empty account.</summary>
    private static bool IsQuota(string? code, string? message)
    {
        if (Matches(code, QuotaCodes)) return true;
        if (Matches(code, RateLimitCodes)) return false;
        return message != null
            && (message.Contains("quota", StringComparison.OrdinalIgnoreCase)
                || message.Contains("credit", StringComparison.OrdinalIgnoreCase));
    }

    private static bool Matches(string? code, string[] known)
        => !string.IsNullOrEmpty(code) && known.Contains(code, StringComparer.OrdinalIgnoreCase);

    /// <summary>The message and code from the error shapes in use: OpenAI and
    /// its imitators ({"error":{"message","code","type"}}), Anthropic
    /// ({"type":"error","error":{"type","message"}}), and the flat
    /// {"error":"..."} / {"message":"..."} / {"detail":"..."} variants.</summary>
    internal static (string? Message, string? Code) ReadError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        var trimmed = body.TrimStart();
        // An HTML page is a proxy or gateway talking, not the API; its markup
        // means nothing to the writer.
        if (trimmed.StartsWith('<')) return (null, null);
        if (!trimmed.StartsWith('{')) return (trimmed, null);
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return (error.GetString(), null);
                if (error.ValueKind == JsonValueKind.Object)
                    return (Text(error, "message"), Text(error, "code") ?? Text(error, "type"));
            }
            return (Text(root, "message") ?? Text(root, "detail"), Text(root, "code") ?? Text(root, "type"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    /// <summary>One line, no control characters, at most
    /// <see cref="MaxMessageLength"/> characters.</summary>
    internal static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var builder = new StringBuilder(Math.Min(text.Length, MaxMessageLength + 1));
        var space = false;
        foreach (var character in text.Trim())
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space) builder.Append(' ');
            space = false;
            builder.Append(character);
            if (builder.Length > MaxMessageLength) break;
        }
        if (builder.Length <= MaxMessageLength) return builder.ToString();
        return builder.ToString(0, MaxMessageLength - 3).TrimEnd() + "...";
    }

    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }
        return null;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[MaxBodyChars];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            return new string(buffer, 0, read);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or DecoderFallbackException)
        {
            // The status still says what went wrong; the body only adds detail.
            return string.Empty;
        }
    }
}
