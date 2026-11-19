using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Novalist.Extensions.AiAssistant.Services;
using Novalist.Extensions.AiAssistant.ViewModels;
using Novalist.Sdk.Models;
using Novalist.Sdk.Services;
using Xunit;

namespace Novalist.Extensions.AiAssistant.Tests;

/// <summary>
/// A provider that refuses a request has to tell the writer why and what to do,
/// in their language. The bare .NET status text ("Response status code does not
/// indicate success: 429") said neither, and was all every AI feature showed.
/// </summary>
public sealed class ProviderErrorTests
{
    internal const string RateLimitBody = """
        {"error":{"message":"Rate limit reached for gpt-4o-mini in organization org-test on requests per min (RPM): Limit 3, Used 3, Requested 1. Please try again in 20s.","type":"requests","param":null,"code":"rate_limit_exceeded"}}
        """;

    internal const string QuotaBody = """
        {"error":{"message":"You exceeded your current quota, please check your plan and billing details.","type":"insufficient_quota","param":null,"code":"insufficient_quota"}}
        """;

    private const string GenericStatusText = "does not indicate success";

    [Fact]
    public async Task RateLimitedSynopsisTellsTheWriterToWaitAndQuotesTheProvider()
    {
        var shown = await SynopsisNotificationAsync(TooManyRequests(RateLimitBody, retryAfterSeconds: 20));

        Assert.DoesNotContain(GenericStatusText, shown);
        Assert.Contains("rate limit", shown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rate limit reached for gpt-4o-mini", shown);
        Assert.Contains("20 seconds", shown);
        Assert.DoesNotContain("billing", shown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QuotaExhaustedSynopsisPointsToBillingRatherThanWaiting()
    {
        var shown = await SynopsisNotificationAsync(TooManyRequests(QuotaBody, retryAfterSeconds: 20));

        Assert.DoesNotContain(GenericStatusText, shown);
        Assert.Contains("billing", shown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("You exceeded your current quota", shown);
        Assert.DoesNotContain("Wait a moment", shown);
    }

    [Theory]
    [InlineData(RateLimitBody, "rate limit")]
    [InlineData(QuotaBody, "billing")]
    public async Task AiChatShowsTheExplainedProviderError(string body, string expected)
    {
        using var fixture = new HostFixture(_ => TooManyRequests(body, retryAfterSeconds: 20));
        using var chat = new AiChatViewModel(fixture.Host, fixture.Extension);

        await chat.SendPreparedAsync("Synthetic question");

        var error = Assert.Single(chat.Messages, message => message.Role == "error");
        Assert.DoesNotContain(GenericStatusText, error.Content);
        Assert.Contains(expected, error.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RateLimitBody, "rate limit")]
    [InlineData(QuotaBody, "billing")]
    public async Task CharacterChatShowsTheExplainedProviderError(string body, string expected)
    {
        using var fixture = new HostFixture(_ => TooManyRequests(body, retryAfterSeconds: 20));
        using var chat = new CharacterChatViewModel(fixture.Host, fixture.Extension, () => null)
        {
            SelectedCharacter = new CharacterInfo { Id = "c1", DisplayName = "Synthetic" },
            UserInput = "Synthetic question"
        };

        await chat.SendCommand.ExecuteAsync(null);

        var error = chat.Turns.Last();
        Assert.DoesNotContain(GenericStatusText, error.Content);
        Assert.Contains(expected, error.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnthropicRateLimitReadsLikeTheCompatibleOne()
    {
        const string body = """
            {"type":"error","error":{"type":"rate_limit_error","message":"This request would exceed the rate limit for your organization of 50 requests per minute."}}
            """;
        var shown = await SynopsisNotificationAsync(TooManyRequests(body, retryAfterSeconds: 30), provider: "anthropic");

        Assert.DoesNotContain("Anthropic API returned 429", shown);
        Assert.Contains("Wait a moment", shown);
        Assert.Contains("30 seconds", shown);
        Assert.Contains("50 requests per minute", shown);
    }

    // ── classification ──────────────────────────────────────────────

    [Theory]
    [InlineData(401, """{"error":{"message":"Incorrect API key provided.","type":"invalid_request_error","code":"invalid_api_key"}}""", AiProviderFailure.Authentication)]
    [InlineData(403, """{"error":{"message":"Project does not have access to model.","code":"model_not_allowed"}}""", AiProviderFailure.Authentication)]
    [InlineData(404, """{"error":{"message":"The model `gpt-9` does not exist or you do not have access to it.","code":"model_not_found"}}""", AiProviderFailure.NotFound)]
    [InlineData(429, RateLimitBody, AiProviderFailure.RateLimited)]
    [InlineData(429, QuotaBody, AiProviderFailure.QuotaExhausted)]
    // OpenAI's rate-limit text links to the billing page; the code decides.
    [InlineData(429, """{"error":{"message":"Rate limit reached. You can increase your rate limit by adding a payment method at https://platform.openai.com/account/billing.","code":"rate_limit_exceeded"}}""", AiProviderFailure.RateLimited)]
    // OpenRouter's free daily allowance: a numeric code and an offer of credits.
    [InlineData(429, """{"error":{"message":"Rate limit exceeded: free-models-per-day. Add 10 credits to unlock 1000 free model requests per day","code":429}}""", AiProviderFailure.QuotaExhausted)]
    [InlineData(429, "", AiProviderFailure.RateLimited)]
    [InlineData(402, """{"error":{"message":"Insufficient credits","code":402}}""", AiProviderFailure.QuotaExhausted)]
    // Anthropic reports an empty balance as a 400.
    [InlineData(400, """{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits."}}""", AiProviderFailure.QuotaExhausted)]
    [InlineData(400, """{"error":{"message":"max_tokens is too large","type":"invalid_request_error"}}""", AiProviderFailure.Rejected)]
    [InlineData(413, "", AiProviderFailure.Rejected)]
    [InlineData(500, """{"error":{"message":"The server had an error while processing your request."}}""", AiProviderFailure.Unavailable)]
    [InlineData(503, "", AiProviderFailure.Unavailable)]
    [InlineData(529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", AiProviderFailure.Unavailable)]
    [InlineData(418, "", AiProviderFailure.Other)]
    public async Task CompatibleRefusalsAreClassifiedByWhatTheWriterMustDo(int status, string body, AiProviderFailure expected)
    {
        var error = await CompatibleFailureAsync(Failure((HttpStatusCode)status, body));

        Assert.Equal(expected, error.Failure);
        Assert.Equal(status, error.StatusCode);
        Assert.Equal("openai", error.Provider);
    }

    [Theory]
    [InlineData(429, """{"type":"error","error":{"type":"rate_limit_error","message":"Too many requests."}}""", AiProviderFailure.RateLimited)]
    [InlineData(401, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", AiProviderFailure.Authentication)]
    [InlineData(404, """{"type":"error","error":{"type":"not_found_error","message":"model: claude-nonexistent"}}""", AiProviderFailure.NotFound)]
    [InlineData(529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", AiProviderFailure.Unavailable)]
    public async Task AnthropicRefusalsUseTheSameClassification(int status, string body, AiProviderFailure expected)
    {
        using var http = new HttpClient(new AuditAiRequestTests.Handler((_, _) =>
            Task.FromResult(Failure((HttpStatusCode)status, body))));
        var client = new AnthropicClient(http) { ApiKey = "fake" };

        var error = await Assert.ThrowsAsync<AiProviderException>(() => client.GenerateAsync("", [("user", "Synthetic")]));

        Assert.Equal(expected, error.Failure);
        Assert.Equal("anthropic", error.Provider);
    }

    [Theory]
    [InlineData("data: {\"error\":{\"code\":429,\"message\":\"Rate limit exceeded upstream\"}}\n", AiProviderFailure.RateLimited)]
    [InlineData("data: {\"error\":{\"code\":502,\"message\":\"Upstream failed\"}}\n", AiProviderFailure.Unavailable)]
    public async Task ErrorsInsideACompatibleStreamAreExplainedToo(string stream, AiProviderFailure expected)
    {
        var error = await CompatibleFailureAsync(AuditAiRequestTests.Stream(stream));
        Assert.Equal(expected, error.Failure);
    }

    [Fact]
    public async Task UnnumberedStreamErrorsNameTheProviderRatherThanLmStudio()
    {
        using var http = new HttpClient(new AuditAiRequestTests.Handler((_, _) =>
            Task.FromResult(AuditAiRequestTests.Stream("data: {\"error\":{\"message\":\"Something\\nbroke\"}}\n"))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AuditAiRequestTests.Compatible(http).GenerateChatAsync([new AiChatMessage { Content = "x" }]));

        Assert.Equal("OpenAI: Something broke", error.Message);
    }

    [Theory]
    [InlineData("overloaded_error", AiProviderFailure.Unavailable)]
    [InlineData("rate_limit_error", AiProviderFailure.RateLimited)]
    public void AnthropicStreamErrorsAreExplainedToo(string type, AiProviderFailure expected)
    {
        var payload = JsonSerializer.Serialize(new { type = "error", error = new { type, message = "Synthetic" } });
        var error = Assert.Throws<AiProviderException>(() => AnthropicClient.ReadEvent(payload));
        Assert.Equal(expected, error.Failure);
    }

    // ── wording ─────────────────────────────────────────────────────

    [Fact]
    public void EveryFailureKindHasAnEnglishGermanAndArabicExplanation()
    {
        foreach (var language in new[] { "en", "de", "ar" })
        {
            var loc = LocaleFile.Localization(language);
            foreach (var failure in Enum.GetValues<AiProviderFailure>())
            {
                var text = new AiProviderException("openai", 499, failure, "Synthetic", TimeSpan.FromSeconds(5)).Describe(loc);
                Assert.DoesNotContain("providerError.", text);
                Assert.Contains("OpenAI", text);
                Assert.Contains("Synthetic", text);
            }
        }
    }

    [Fact]
    public void GermanExplanationsAreWrittenWithUmlauts()
    {
        var loc = LocaleFile.Localization("de");
        var rate = new AiProviderException("groq", 429, AiProviderFailure.RateLimited, null, TimeSpan.FromSeconds(12)).Describe(loc);
        var quota = new AiProviderException("custom", 429, AiProviderFailure.QuotaExhausted, null, null).Describe(loc);

        Assert.StartsWith("Groq erhält gerade zu viele Anfragen von deinem Konto (HTTP 429, Ratenlimit).", rate);
        Assert.EndsWith("Der Anbieter bittet dich, 12 Sekunden zu warten, bevor du es erneut versuchst.", rate);
        Assert.StartsWith("Benutzerdefiniert (OpenAI-kompatibel) meldet, dass dein Konto kein API-Guthaben mehr hat", quota);
        Assert.Contains("Prüfe Tarif und Abrechnung", quota);
    }

    [Fact]
    public void LongWaitsAreGivenInMinutesAndQuotaNeverSuggestsWaiting()
    {
        var loc = LocaleFile.Localization("en");
        var daily = new AiProviderException("openrouter", 429, AiProviderFailure.RateLimited, null, TimeSpan.FromHours(2)).Describe(loc);
        var quota = new AiProviderException("openai", 429, AiProviderFailure.QuotaExhausted, null, TimeSpan.FromSeconds(20)).Describe(loc);

        Assert.Contains("about 120 minutes", daily);
        Assert.DoesNotContain("seconds", quota);
        Assert.Contains("does not include API credit", quota);
    }

    [Fact]
    public async Task RetryAfterAsAnHttpDateIsHonoured()
    {
        var response = Failure((HttpStatusCode)429, RateLimitBody);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(90));

        var error = await CompatibleFailureAsync(response);

        Assert.InRange(error.RetryAfter!.Value.TotalSeconds, 60, 91);
    }

    // ── what is safe to show ────────────────────────────────────────

    [Fact]
    public async Task ProviderMessagesAreShortenedToOneLine()
    {
        var longMessage = "First line\r\nsecond\tline " + new string('x', 2000);
        var error = await CompatibleFailureAsync(Failure(HttpStatusCode.BadRequest,
            JsonSerializer.Serialize(new { error = new { message = longMessage } })));

        Assert.NotNull(error.ProviderMessage);
        Assert.True(error.ProviderMessage!.Length <= AiProviderErrors.MaxMessageLength);
        Assert.StartsWith("First line second line x", error.ProviderMessage);
        Assert.EndsWith("...", error.ProviderMessage);
        Assert.DoesNotContain(error.ProviderMessage, character => char.IsControl(character));
    }

    [Fact]
    public async Task GatewayHtmlPagesAreNotShownAsTheProviderMessage()
    {
        var error = await CompatibleFailureAsync(Failure(HttpStatusCode.BadGateway,
            "<html><body><h1>502 Bad Gateway</h1></body></html>", mediaType: "text/html"));

        Assert.Equal(AiProviderFailure.Unavailable, error.Failure);
        Assert.Null(error.ProviderMessage);
        Assert.DoesNotContain("<", error.Describe(LocaleFile.Localization("en")));
    }

    [Fact]
    public async Task ThePromptNeverAppearsInTheError()
    {
        const string manuscript = "SYNTHETIC-MANUSCRIPT-SENTENCE";
        using var http = new HttpClient(new AuditAiRequestTests.Handler((_, _) =>
            Task.FromResult(TooManyRequests(RateLimitBody, 20))));
        var error = await Assert.ThrowsAsync<AiProviderException>(() => AuditAiRequestTests.Compatible(http)
            .GenerateChatAsync([new AiChatMessage { Role = "user", Content = manuscript }]));

        Assert.DoesNotContain(manuscript, error.Message);
        Assert.DoesNotContain(manuscript, error.Describe(LocaleFile.Localization("en")));
    }

    // ── cancellation and best-effort probes ─────────────────────────

    [Fact]
    public async Task CancellingWhileTheErrorBodyArrivesStillCancels()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new AuditAiRequestTests.Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)429) { Content = new StreamContent(new StallingStream(cancellation)) })));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AuditAiRequestTests.Compatible(http)
            .GenerateChatAsync([new AiChatMessage { Content = "x" }], cancellationToken: cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task LmStudioLoadProbeFailuresStayQuietAndTheCompletionExplains()
    {
        var paths = new List<string>();
        using var http = new HttpClient(new AuditAiRequestTests.Handler((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(request.Method == HttpMethod.Get
                ? Failure(HttpStatusCode.InternalServerError, "")
                : Failure(HttpStatusCode.NotFound, """{"error":{"message":"Model not loaded"}}"""));
        }));
        var service = new AiService(http);
        service.Configure(new AiSettings { Provider = "lmstudio", LmStudioModel = "local" });

        var error = await Assert.ThrowsAsync<AiProviderException>(() =>
            service.GenerateChatAsync([new AiChatMessage { Content = "x" }]));

        Assert.Equal(AiProviderFailure.NotFound, error.Failure);
        Assert.Equal(new[] { "/api/v1/models", "/v1/chat/completions" }, paths);
        Assert.Contains("LM Studio does not offer the requested model", error.Describe(LocaleFile.Localization("en")));
    }

    [Fact]
    public async Task ModelListingStillReturnsEmptyWhenRefused()
    {
        using var http = new HttpClient(new AuditAiRequestTests.Handler((_, _) =>
            Task.FromResult(TooManyRequests(RateLimitBody, 20))));
        Assert.Empty(await AuditAiRequestTests.Compatible(http).ListModelsAsync());
    }

    [Fact]
    public void OtherFailuresKeepTheirOwnMessage()
    {
        var loc = LocaleFile.Localization("en");
        Assert.Equal("disk full", AiProviderErrors.Describe(new IOException("disk full"), loc));
        var wrapped = new AggregateException(new AiProviderException("openai", 401, AiProviderFailure.Authentication, null, null));
        Assert.StartsWith("OpenAI did not accept the API key (HTTP 401).", AiProviderErrors.Describe(wrapped, loc));
    }

    // ── helpers ─────────────────────────────────────────────────────

    private static async Task<AiProviderException> CompatibleFailureAsync(HttpResponseMessage response)
    {
        using var http = new HttpClient(new AuditAiRequestTests.Handler((_, _) => Task.FromResult(response)));
        return await Assert.ThrowsAsync<AiProviderException>(() =>
            AuditAiRequestTests.Compatible(http).GenerateChatAsync([new AiChatMessage { Content = "x" }]));
    }

    /// <summary>A body that never finishes until the request is cancelled.</summary>
    private sealed class StallingStream(CancellationTokenSource cancellation) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    internal static HttpResponseMessage TooManyRequests(string body, int? retryAfterSeconds)
        => Failure((HttpStatusCode)429, body, retryAfterSeconds);

    internal static HttpResponseMessage Failure(HttpStatusCode status, string body, int? retryAfterSeconds = null, string mediaType = "application/json")
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        if (retryAfterSeconds is { } seconds)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }

    private static async Task<string> SynopsisNotificationAsync(HttpResponseMessage failure, string provider = "openai")
    {
        using var fixture = new HostFixture(_ => failure, provider);
        var synopsis = new SceneSynopsisService(fixture.Extension.AiService, fixture.Host, fixture.Localization);

        Assert.Null(await synopsis.GenerateAndSaveAsync("chapter", "scene"));

        return Assert.Single(fixture.Notifications);
    }

    /// <summary>A host that answers from the real English locale file, so the
    /// assertions read the text a writer would see.</summary>
    internal sealed class HostFixture : IDisposable
    {
        private readonly HttpClient _http;

        public HostFixture(Func<HttpRequestMessage, HttpResponseMessage> respond, string provider = "openai")
        {
            _http = new HttpClient(new AuditAiRequestTests.Handler((request, _) => Task.FromResult(respond(request))));
            var service = new AiService(_http);
            service.Configure(new AiSettings
            {
                Provider = provider, LmStudioModel = "synthetic", LmStudioApiToken = "fake",
                AnthropicApiKey = "fake", AnthropicModel = "synthetic"
            });
            Extension = new AiAssistantExtension(service);
            Localization = LocaleFile.Localization("en");
            var progress = ServiceProxy.Create<IBusyProgress>((method, _) => method.Name switch
            {
                "get_CancellationToken" => CancellationToken.None,
                _ => null
            });
            var project = ServiceProxy.Create<IExtensionProjectService>((method, _) => method.Name switch
            {
                "ReadSceneContentAsync" => Task.FromResult("<p>Synthetic scene text.</p>"),
                "get_CurrentScene" => null,
                _ => throw new InvalidOperationException(method.Name)
            });
            var entities = ServiceProxy.Create<IExtensionEntityService>((method, _) => method.Name switch
            {
                "GetCharacterDetailedAsync" => Task.FromResult<CharacterDetailedInfo?>(null),
                _ => throw new InvalidOperationException(method.Name)
            });
            Host = ServiceProxy.Create<IHostServices>((method, args) =>
            {
                switch (method.Name)
                {
                    case "GetLocalization": return Localization;
                    case "get_ProjectService": return project;
                    case "get_EntityService": return entities;
                    case "ShowBusyProgress": return progress;
                    case "ShowNotification": Notifications.Add((string)args[0]!); return null;
                    case "GetAiHooks": return Array.Empty<Novalist.Sdk.Hooks.IAiHook>();
                    case "get_CurrentLanguage": return "en";
                    case "get_CurrentLanguageDisplayName": return "English";
                    case "PostToUI": ((Action)args[0]!)(); return null;
                    case "add_ProjectLoaded" or "remove_ProjectLoaded" or "add_SceneOpened" or "remove_SceneOpened":
                        return null;
                    default: throw new InvalidOperationException(method.Name);
                }
            });
        }

        public IHostServices Host { get; }
        public AiAssistantExtension Extension { get; }
        public IExtensionLocalization Localization { get; }
        public List<string> Notifications { get; } = [];

        public void Dispose() => _http.Dispose();
    }
}

/// <summary>Reads a shipped locale file the way the host does: flattened
/// dotted keys, English text, composite-format arguments.</summary>
internal static class LocaleFile
{
    public static Dictionary<string, string> Load(string language)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Novalist.Extensions.AiAssistant.csproj")))
            directory = directory.Parent;
        var path = Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("Extension root"), "Locales", language + ".json");
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        Flatten(document.RootElement, string.Empty, flat);
        return flat;
    }

    public static IExtensionLocalization Localization(string language)
    {
        var strings = Load(language);
        string Resolve(string key) => strings.TryGetValue(key, out var value) ? value : key;
        return ServiceProxy.Create<IExtensionLocalization>((method, args) => method.Name switch
        {
            "get_Item" => Resolve((string)args[0]!),
            "T" when args.Length == 1 => Resolve((string)args[0]!),
            "T" => string.Format(CultureInfo.InvariantCulture, Resolve((string)args[0]!), (object[])args[1]!),
            "add_PropertyChanged" or "remove_PropertyChanged" => null,
            _ => throw new InvalidOperationException(method.Name)
        });
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> into)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            into[prefix] = element.GetString() ?? string.Empty;
            return;
        }
        foreach (var property in element.EnumerateObject())
            Flatten(property.Value, prefix.Length == 0 ? property.Name : prefix + "." + property.Name, into);
    }
}
