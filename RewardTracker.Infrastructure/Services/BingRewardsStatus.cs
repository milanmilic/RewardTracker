using System.Globalization;
using System.Text.Json;

namespace RewardTracker.Infrastructure.Services;

/// <summary>Jedan zadatak iz Bing Daily Set-a.</summary>
public sealed record BingOffer(string OfferId, string Title, string DestinationUrl, int Points, bool Complete)
{
    /// <summary>
    /// Kvizove nije dovoljno samo posetiti - traze klikanje odgovora.
    /// Obican "urlreward" se zavrsava samim otvaranjem linka.
    /// </summary>
    public bool IsQuiz =>
        DestinationUrl.Contains("form=dsetqu", StringComparison.OrdinalIgnoreCase) ||
        DestinationUrl.Contains("WQOskey", StringComparison.OrdinalIgnoreCase) ||
        DestinationUrl.Contains("IsConversation", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Stanje Microsoft Rewards naloga, procitano iz JSON-a ugradjenog u
/// https://www.bing.com/rewards/panelflyout.
///
/// Novi rewards.bing.com/dashboard je Next.js (RSC) aplikacija bez upotrebljivog HTML-a,
/// ali flyout i dalje nosi kompletan stari model: userStatus.counters, dailySetPromotions,
/// pointClaimCardPromotion. Zato citamo odatle, a ne sa dashboard-a.
/// </summary>
public sealed class BingRewardsStatus
{
    public int AvailablePoints { get; init; }

    /// <summary>Danas zaradjeni poeni od pretraga.</summary>
    public int SearchProgress { get; init; }

    /// <summary>Dnevni limit poena od pretraga (uobicajeno 60).</summary>
    public int SearchMax { get; init; }

    public bool SearchComplete { get; init; }

    public IReadOnlyList<BingOffer> DailySet { get; init; } = [];

    public string? ClaimUrl { get; init; }

    /// <summary>Koliko pretraga jos ima smisla uraditi da bi se dostigao dnevni limit.</summary>
    public int RemainingSearches(int pointsPerSearch)
    {
        if (SearchComplete || SearchMax <= 0 || pointsPerSearch <= 0)
        {
            return 0;
        }

        var missing = SearchMax - SearchProgress;
        return missing <= 0 ? 0 : (int)Math.Ceiling(missing / (double)pointsPerSearch);
    }

    public static BingRewardsStatus? TryParse(string html)
    {
        var json = ExtractFlyoutJson(html);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var available = 0;
            var progress = 0;
            var max = 0;
            var complete = false;

            if (root.TryGetProperty("userStatus", out var userStatus) &&
                userStatus.ValueKind == JsonValueKind.Object)
            {
                available = ReadInt(userStatus, "availablePoints");

                if (userStatus.TryGetProperty("counters", out var counters) &&
                    counters.ValueKind == JsonValueKind.Object &&
                    counters.TryGetProperty("PCSearch", out var pcSearch) &&
                    pcSearch.ValueKind == JsonValueKind.Array &&
                    pcSearch.GetArrayLength() > 0)
                {
                    var counter = pcSearch[0];
                    progress = ReadInt(counter, "pointProgress");
                    max = ReadInt(counter, "pointProgressMax");
                    complete = ReadBool(counter, "complete");
                }
            }

            return new BingRewardsStatus
            {
                AvailablePoints = available,
                SearchProgress = progress,
                SearchMax = max,
                SearchComplete = complete,
                DailySet = ReadDailySet(root),
                ClaimUrl = ReadClaimUrl(root)
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Izvlaci uravnotezen JSON objekat koji stoji iza kljuca "flyoutResult".
    /// Regex ovde ne pomaze jer je objekat duboko ugnjezden.
    /// </summary>
    private static string? ExtractFlyoutJson(string html)
    {
        const string marker = "\"flyoutResult\":";

        var markerIndex = html.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var start = html.IndexOf('{', markerIndex + marker.Length);
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < html.Length; i++)
        {
            var current = html[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (current == '\\')
            {
                if (inString)
                {
                    escaped = true;
                }

                continue;
            }

            if (current == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (current == '{')
            {
                depth++;
            }
            else if (current == '}' && --depth == 0)
            {
                return html[start..(i + 1)];
            }
        }

        return null;
    }

    /// <summary>
    /// dailySetPromotions je mapa "MM/dd/yyyy" -> lista ponuda i sadrzi i naredne dane.
    /// Uzimamo iskljucivo danasnji kljuc; ponude za sutra jos nisu dostupne.
    /// </summary>
    private static List<BingOffer> ReadDailySet(JsonElement root)
    {
        var offers = new List<BingOffer>();

        if (!root.TryGetProperty("dailySetPromotions", out var promotions) ||
            promotions.ValueKind != JsonValueKind.Object)
        {
            return offers;
        }

        var today = DateTime.Now.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);

        foreach (var day in promotions.EnumerateObject())
        {
            if (!string.Equals(day.Name, today, StringComparison.Ordinal) ||
                day.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in day.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var destination = ReadString(item, "destinationUrl");
                if (string.IsNullOrWhiteSpace(destination))
                {
                    continue;
                }

                offers.Add(new BingOffer(
                    ReadString(item, "offerId"),
                    ReadString(item, "title"),
                    destination,
                    ReadInt(item, "pointProgressMax"),
                    ReadBool(item, "complete")));
            }

            break;
        }

        return offers;
    }

    private static string? ReadClaimUrl(JsonElement root)
    {
        if (!root.TryGetProperty("pointClaimCardPromotion", out var card) ||
            card.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var url = ReadString(card, "destinationUrl");
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
