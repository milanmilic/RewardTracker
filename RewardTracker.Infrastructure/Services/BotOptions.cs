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

    public int BingSearchCount { get; set; } = 25;
}
