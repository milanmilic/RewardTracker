using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace RewardTracker.Infrastructure.Services;

/// <summary>Stanje dnevnog niza (Daily Streak) procitano iz Freecash API-ja.</summary>
public sealed record FreecashStreak(int Day, int Coins, bool Claimed);

/// <summary>Ishod pokusaja unosa bonus koda.</summary>
public enum BonusCodeOutcome
{
    /// <summary>Kod je prihvacen i nagrada je pripisana.</summary>
    Redeemed,

    /// <summary>Kod je odbijen (nevazeci, istekao ili vec iskoriscen).</summary>
    Rejected,

    /// <summary>Poziv nije uspeo - mrezna greska ili nepoznat odgovor.</summary>
    Failed
}

/// <summary>
/// Klijent za Freecash API. Freecash je jednostranicna aplikacija koja stanje i podatke
/// o dnevnom nizu povlaci sa svog API-ja, pa je citanje iz DOM-a nepouzdano - sadrzaj se
/// docrtava naknadno i selektori su generisani (nasumicne Tailwind klase).
///
/// Svi pozivi idu kroz <see cref="IPage.APIRequest"/>, koji deli kolacice sa stranicom,
/// pa nije potrebna nikakva dodatna prijava.
/// </summary>
public sealed class FreecashApiClient
{
    private const string BalanceUrl = "https://freecash.com/api/dynamic-state?path=widgets";
    private const string GraphqlUrl = "https://freecash.com/fc-api/graphql";

    private const string StreakQuery =
        "query getUserStreak { getUserStreak { coins day claimed } }";

    private const string StreakConfigQuery =
        "query getStreakConfiguration { getStreakConfiguration { minCoinsToEarn } }";

    private const string LotteryQuery =
        "query { offerLottery { lotteryId userTickets ticketCost } }";

    private const string ClaimBonusCodeMutation =
        "mutation ClaimBonusCode($code: String!) { claimBonusCode(code: $code) { reward rewardType id code } }";

    /// <summary>Freecash interno racuna u "coins"; 1000 coins je jedan dolar.</summary>
    public const int CoinsPerDollar = 1000;

    private readonly ILogger _logger;

    public FreecashApiClient(ILogger logger) => _logger = logger;

    /// <summary>
    /// Pretvara coins u cente, jer se u bazi stanje cuva u centima. Zaokruzuje se od nule,
    /// isto kao na sajtu - 25 coins je prikazano kao $0.03, a ne $0.02.
    /// </summary>
    public static int CoinsToCents(int coins) =>
        (int)Math.Round(coins / (CoinsPerDollar / 100.0), MidpointRounding.AwayFromZero);

    /// <summary>Stanje naloga u centima, ili null ako citanje nije uspelo.</summary>
    public async Task<int?> ReadBalanceCentsAsync(IPage page)
    {
        var root = await GetJsonAsync(page, BalanceUrl);
        if (root is null)
        {
            return null;
        }

        using (root)
        {
            if (root.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("balance", out var balance)
                && balance.TryGetProperty("lastSeen", out var seen)
                && seen.TryGetInt32(out var coins))
            {
                return CoinsToCents(coins);
            }
        }

        _logger.LogWarning("Freecash odgovor sa stanjem nema ocekivano polje balance.lastSeen.");
        return null;
    }

    /// <summary>Danasnji korak dnevnog niza, ili null ako nije procitan.</summary>
    public async Task<FreecashStreak?> ReadStreakAsync(IPage page)
    {
        var root = await PostGraphqlAsync(page, StreakQuery);
        if (root is null)
        {
            return null;
        }

        using (root)
        {
            if (!TryGetData(root, "getUserStreak", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in list.EnumerateArray())
            {
                var day = item.TryGetProperty("day", out var d) && d.TryGetInt32(out var dv) ? dv : 0;
                var coins = item.TryGetProperty("coins", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
                var claimed = item.TryGetProperty("claimed", out var cl) && cl.ValueKind == JsonValueKind.True;
                return new FreecashStreak(day, coins, claimed);
            }
        }

        return null;
    }

    /// <summary>Koliko coins-a treba zaraditi u toku dana da bi se niz odrzao.</summary>
    public async Task<int?> ReadMinCoinsToEarnAsync(IPage page)
    {
        var root = await PostGraphqlAsync(page, StreakConfigQuery);
        if (root is null)
        {
            return null;
        }

        using (root)
        {
            return TryGetData(root, "getStreakConfiguration", out var cfg)
                && cfg.TryGetProperty("minCoinsToEarn", out var min)
                && min.TryGetInt32(out var value)
                ? value
                : null;
        }
    }

    /// <summary>Broj tiketa za nedeljnu lutriju.</summary>
    public async Task<int?> ReadLotteryTicketsAsync(IPage page)
    {
        var root = await PostGraphqlAsync(page, LotteryQuery);
        if (root is null)
        {
            return null;
        }

        using (root)
        {
            return TryGetData(root, "offerLottery", out var lottery)
                && lottery.TryGetProperty("userTickets", out var tickets)
                && tickets.TryGetInt32(out var value)
                ? value
                : null;
        }
    }

    /// <summary>
    /// Unosi bonus kod. Nevazeci kod nije greska - Freecash na njega odgovara sa HTTP 200
    /// i porukom u polju errors, pa se to razlikuje od stvarnog pada poziva.
    /// </summary>
    public async Task<(BonusCodeOutcome Outcome, string Message)> ClaimBonusCodeAsync(IPage page, string code)
    {
        var root = await PostGraphqlAsync(page, ClaimBonusCodeMutation, new { code });
        if (root is null)
        {
            return (BonusCodeOutcome.Failed, "poziv nije uspeo");
        }

        using (root)
        {
            if (root.RootElement.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0)
            {
                var first = errors[0];
                var message = first.TryGetProperty("message", out var m) ? m.GetString() : null;
                return (BonusCodeOutcome.Rejected, message ?? "kod je odbijen");
            }

            if (TryGetData(root, "claimBonusCode", out var claim))
            {
                var reward = claim.TryGetProperty("reward", out var r) && r.TryGetInt32(out var rv) ? rv : 0;
                var type = claim.TryGetProperty("rewardType", out var t) ? t.GetString() : null;
                return (BonusCodeOutcome.Redeemed, $"{reward} {type ?? "coins"}");
            }
        }

        return (BonusCodeOutcome.Failed, "nepoznat odgovor");
    }

    private static bool TryGetData(JsonDocument document, string field, out JsonElement value)
    {
        value = default;
        return document.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty(field, out value)
            && value.ValueKind is not JsonValueKind.Null;
    }

    private Task<JsonDocument?> PostGraphqlAsync(IPage page, string query, object? variables = null) =>
        SendAsync(page, "GraphQL", () => page.APIRequest.PostAsync(GraphqlUrl, new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string>
            {
                ["content-type"] = "application/json",
                ["origin"] = "https://freecash.com",
                ["referer"] = "https://freecash.com/rewards"
            },
            DataObject = variables is null ? new { query } : new { query, variables }
        }));

    private Task<JsonDocument?> GetJsonAsync(IPage page, string url) =>
        SendAsync(page, url, () => page.APIRequest.GetAsync(url));

    private async Task<JsonDocument?> SendAsync(IPage page, string what, Func<Task<IAPIResponse>> send)
    {
        try
        {
            var response = await send();
            var text = await response.TextAsync();

            // GraphQL vraca 200 i kada je upit odbijen, pa 4xx ovde znaci stvarni problem.
            if (!response.Ok && response.Status != 400)
            {
                _logger.LogWarning("Freecash poziv ({What}) je vratio HTTP {Status}.", what, response.Status);
                return null;
            }

            return JsonDocument.Parse(text);
        }
        catch (Exception ex) when (ex is PlaywrightException or JsonException or TimeoutException)
        {
            _logger.LogWarning(ex, "Freecash poziv ({What}) nije uspeo.", what);
            return null;
        }
    }
}
