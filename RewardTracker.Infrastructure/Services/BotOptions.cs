namespace RewardTracker.Infrastructure.Services;

public class BotOptions
{
    public const string SectionName = "Bot";

    public bool Headless { get; set; }

    /// <summary>Relativna putanja se razresava u odnosu na direktorijum aplikacije.</summary>
    public string ArtifactsPath { get; set; } = "bot-artifacts";

    public int NavigationTimeoutMs { get; set; } = 45_000;

    public int ActionTimeoutMs { get; set; } = 15_000;

    /// <summary>Koliko najduze cekamo da se stanje poena pojavi na stranici.</summary>
    public int BalanceTimeoutMs { get; set; } = 30_000;

    /// <summary>Koliko najduze cekamo da korisnik rucno zavrsi logovanje.</summary>
    public int LoginWindowSeconds { get; set; } = 180;

    /// <summary>Koliko pretraga radimo kada Rewards status nije procitan (rezervna vrednost).</summary>
    public int BingSearchCount { get; set; } = 25;

    /// <summary>Gornja granica pretraga po pokretanju, i onda kada se broj racuna iz Rewards statusa.</summary>
    public int BingMaxSearches { get; set; } = 30;

    /// <summary>Koliko poena Bing daje po pretrazi - koristi se za racunanje potrebnog broja pretraga.</summary>
    public int BingPointsPerSearch { get; set; } = 3;

    /// <summary>Preskoci pretrage kada Rewards javi da je dnevni limit poena vec dostignut.</summary>
    public bool BingSkipCompletedSearches { get; set; } = true;

    /// <summary>Odradi Bing Daily Set zadatke (uobicajeno 3 x 10 poena).</summary>
    public bool BingDailySet { get; set; } = true;

    /// <summary>Pokusaj i kvizove iz Daily Set-a, ne samo obicne linkove.</summary>
    public bool BingDailySetQuizzes { get; set; } = true;

    /// <summary>Najduze vreme koje trosimo na jedan kviz.</summary>
    public int BingQuizTimeoutMs { get; set; } = 90_000;

    /// <summary>Pokusaj da preuzmes neuzete bonus poene ("Ready to claim").</summary>
    public bool BingClaimPoints { get; set; } = true;

    /// <summary>
    /// Odradi "Read to earn" (citanje MSN clanaka) preko Rewards API-ja mobilne aplikacije.
    /// Ta aktivnost ne postoji na webu.
    /// </summary>
    public bool BingReadToEarn { get; set; } = true;

    /// <summary>Gornja granica prijavljenih clanaka po pokretanju.</summary>
    public int BingMaxReadArticles { get; set; } = 10;

    /// <summary>Odradi dnevni check-in iz mobilne aplikacije.</summary>
    public bool BingDailyCheckIn { get; set; } = true;

    /// <summary>Drzava naloga, salje se Rewards API-ju.</summary>
    public string BingCountry { get; set; } = "rs";

    /// <summary>Jezik naloga, salje se Rewards API-ju.</summary>
    public string BingLanguage { get; set; } = "en";
}
