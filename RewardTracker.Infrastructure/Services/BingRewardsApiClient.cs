using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace RewardTracker.Infrastructure.Services;

/// <summary>Jedna Rewards aktivnost, procitana iz mobilnog API-ja.</summary>
public sealed record RewardsActivity(
    string OfferId,
    string Type,
    string Title,
    bool Complete,
    int? ActivityProgress,
    int? ActivityMax,
    int PointProgress,
    int PointMax)
{
    /// <summary>Koliko puta jos treba prijaviti aktivnost. Ima smisla samo kada postoji activitymax.</summary>
    public int RemainingActivities =>
        Complete || ActivityMax is null ? 0 : Math.Max(0, ActivityMax.Value - (ActivityProgress ?? 0));
}

/// <summary>
/// Klijent za prod.rewardsplatform.microsoft.com - isti API koji koristi Bing mobilna aplikacija.
/// Kroz njega se vide i prijavljuju aktivnosti kojih nema na webu ("Read to earn", dnevni check-in).
///
/// Token se dobija OAuth razmenom nad vec sacuvanim kolacicima naloga, bez rucne prijave.
/// </summary>
public sealed class BingRewardsApiClient
{
    private const string ClientId = "0000000040170455";
    private const string Scope = "service::prod.rewardsplatform.microsoft.com::MBI_SSL";
    private const string RedirectUri = "https://login.live.com/oauth20_desktop.srf";
    private const string TokenUrl = "https://login.live.com/oauth20_token.srf";
    private const string MeUrl = "https://prod.rewardsplatform.microsoft.com/dapi/me?channel=SAAndroid&options=613";
    private const string ActivitiesUrl = "https://prod.rewardsplatform.microsoft.com/dapi/me/activities";

    /// <summary>Prijava procitanog clanka; isti tip salje i sama aplikacija.</summary>
    private const int ActivityReportType = 101;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly ILogger _logger;
    private readonly BotOptions _options;
    private string? _token;

    public BingRewardsApiClient(ILogger logger, BotOptions options)
    {
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// Razmenjuje postojece kolacice naloga za pristupni token. Otvara zaseban tab da ne bi
    /// pomerio stranicu koju pozivalac koristi.
    /// </summary>
    public async Task<bool> AuthenticateAsync(IBrowserContext context)
    {
        var authorizeUrl =
            $"https://login.live.com/oauth20_authorize.srf?client_id={ClientId}" +
            $"&scope={Uri.EscapeDataString(Scope)}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}";

        string? code;
        var tab = await context.NewPageAsync();
        try
        {
            await tab.GotoAsync(authorizeUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _options.NavigationTimeoutMs
            });
            await tab.WaitForTimeoutAsync(2000);
            code = ReadQueryValue(tab.Url, "code");
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            _logger.LogWarning(ex, "OAuth razmena za Rewards API nije uspela.");
            return false;
        }
        finally
        {
            if (!tab.IsClosed)
            {
                await tab.CloseAsync();
            }
        }

        if (string.IsNullOrEmpty(code))
        {
            _logger.LogWarning("Rewards API: nije dobijen autorizacioni kod - sesija je verovatno istekla.");
            return false;
        }

        try
        {
            using var response = await Http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["redirect_uri"] = RedirectUri,
                ["grant_type"] = "authorization_code",
                ["code"] = code
            }));

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            _token = document.RootElement.TryGetProperty("access_token", out var value) ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Rewards API: razmena koda za token nije uspela.");
            return false;
        }

        if (string.IsNullOrEmpty(_token))
        {
            _logger.LogWarning("Rewards API: odgovor ne sadrzi access_token.");
            return false;
        }

        return true;
    }

    /// <summary>Trazi aktivnost po tipu iz atributa (npr. "msnreadearn" ili "checkin").</summary>
    public async Task<RewardsActivity?> TryGetActivityAsync(string type)
    {
        var body = await SendAsync(new HttpRequestMessage(HttpMethod.Get, MeUrl));
        if (body is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("response", out var response) ||
                !response.TryGetProperty("promotions", out var promotions) ||
                promotions.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var promotion in promotions.EnumerateArray())
            {
                if (!promotion.TryGetProperty("attributes", out var attributes) ||
                    attributes.ValueKind != JsonValueKind.Object ||
                    !string.Equals(ReadAttribute(attributes, "type"), type, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var offerId = ReadAttribute(attributes, "offerid");
                if (string.IsNullOrWhiteSpace(offerId))
                {
                    continue;
                }

                return new RewardsActivity(
                    offerId,
                    type,
                    ReadAttribute(attributes, "title") ?? type,
                    string.Equals(ReadAttribute(attributes, "complete"), "True", StringComparison.OrdinalIgnoreCase),
                    ReadAttributeInt(attributes, "activityprogress"),
                    ReadAttributeInt(attributes, "activitymax"),
                    ReadAttributeInt(attributes, "pointprogress") ?? 0,
                    ReadAttributeInt(attributes, "pointmax") ?? 0);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Rewards API: odgovor nije ocekivanog oblika.");
        }

        return null;
    }

    /// <summary>Prijavljuje jednu aktivnost. Vraca broj dodeljenih poena, ili null ako poziv nije uspeo.</summary>
    public async Task<int?> ReportActivityAsync(RewardsActivity activity)
    {
        var payload = JsonSerializer.Serialize(new
        {
            amount = 1,
            country = _options.BingCountry,
            id = Guid.NewGuid().ToString(),
            type = ActivityReportType,
            attributes = new
            {
                offerid = activity.OfferId,
                show_ux = "true",
                type = activity.Type
            }
        });

        var request = new HttpRequestMessage(HttpMethod.Post, ActivitiesUrl)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        var body = await SendAsync(request);
        if (body is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("response", out var response) &&
                response.TryGetProperty("activity", out var reported) &&
                reported.TryGetProperty("p", out var points) &&
                points.ValueKind == JsonValueKind.Number)
            {
                return points.GetInt32();
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Rewards API: odgovor na prijavu aktivnosti nije ocekivanog oblika.");
        }

        return 0;
    }

    private async Task<string?> SendAsync(HttpRequestMessage request)
    {
        if (string.IsNullOrEmpty(_token))
        {
            return null;
        }

        using (request)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);
            request.Headers.TryAddWithoutValidation("X-Rewards-Country", _options.BingCountry);
            request.Headers.TryAddWithoutValidation("X-Rewards-Language", _options.BingLanguage);

            try
            {
                using var response = await Http.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Rewards API {Method} {Path} -> {Status}.",
                        request.Method, request.RequestUri?.AbsolutePath, (int)response.StatusCode);
                    return null;
                }

                return body;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Rewards API poziv nije uspeo.");
                return null;
            }
        }
    }

    private static string? ReadQueryValue(string url, string name)
    {
        var separator = url.IndexOf('?');
        if (separator < 0)
        {
            return null;
        }

        foreach (var pair in url[(separator + 1)..].Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == name)
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return null;
    }

    private static string? ReadAttribute(JsonElement attributes, string name) =>
        attributes.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadAttributeInt(JsonElement attributes, string name) =>
        int.TryParse(ReadAttribute(attributes, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
