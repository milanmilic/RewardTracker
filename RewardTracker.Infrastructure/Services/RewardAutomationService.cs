using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using RewardTracker.Core.Entities;
using RewardTracker.Infrastructure.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RewardTracker.Infrastructure.Services;

public class RewardAutomationService
{
    private const int PollIntervalMs = 500;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private const string BingHomeUrl = "https://www.bing.com";

    private const string BingSearchUrl = "https://www.bing.com/search";

    /// <summary>
    /// Dashboard sa karticama Daily Set-a. Poeni za zadatak se priznaju tek kad se kartica
    /// klikne ovde - otvaranje istog destinationUrl-a direktno ne donosi nista.
    /// </summary>
    private const string BingDashboardUrl = "https://rewards.bing.com/";

    /// <summary>
    /// Jedina stranica koja jos uvek nosi kompletan Rewards JSON (userStatus, counters,
    /// dailySetPromotions). Novi rewards.bing.com/dashboard je Next.js i nema upotrebljiv HTML.
    /// </summary>
    private const string BingFlyoutUrl = "https://www.bing.com/rewards/panelflyout";

    /// <summary>Dugme unutar modala koje stvarno preuzima poene ("3 Pending Claim points").</summary>
    private const string ClaimModalButtonSelector = "button:has-text('Claim points')";

    /// <summary>Kartica na dashboard-u koja samo otvara modal ("Ready to claim 3 Claim").</summary>
    private const string ClaimCardSelector = "button:has-text('Ready to claim')";

    private const string YsenseSurveysUrl = "https://www.ysense.com/surveys";

    /// <summary>
    /// Tekst dugmeta je u skrivenom span-u, pa se mora ciljati preko title atributa.
    /// Postoje dve kopije (mobilna i desktop) - mobilna je nevidljiva i klik na nju puca.
    /// </summary>
    private const string YsenseChecklistButtonSelector =
        "#dailyChecklistContainerDesktop button[title='Daily Checklist Bonus']";

    private const string ChecklistHeading = "Daily Checklist Bonus";
    private const string ChecklistFooter = "View History";

    private const string FreecashRewardsUrl = "https://freecash.com/rewards";

    /// <summary>Dugme dnevnog niza; tekst nosi i iznos, npr. "Claim $ 0.03".</summary>
    private const string FreecashClaimSelector = "button:has-text('Claim $')";

    private enum LoginOutcome
    {
        Confirmed,
        Unknown,
        StillOnLoginPage
    }

    /// <summary>
    /// Delovi URL-a koji znace da smo jos uvek na login ekranu. Kada URL vise ne sadrzi
    /// nijedan od njih, logovanje je zavrseno i ne moramo da cekamo ceo prozor.
    /// Sajtovi koji se loguju kroz modal (Freecash) namerno nisu ovde.
    /// </summary>
    private static readonly Dictionary<string, string[]> LoginUrlFragments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bing"] = ["login.live.com", "login.microsoftonline.com"],
        ["ysense"] = ["action=login"]
    };

    /// <summary>
    /// Selektori koji se vide samo kada nalog NIJE ulogovan. Koriste se kao dijagnostika
    /// kada citanje stanja ne uspe, da bismo razlikovali isteklu sesiju od promene DOM-a.
    /// </summary>
    private static readonly Dictionary<string, string[]> LoggedOutMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bing"] = ["a#id_l", "a[href*='login.live.com']"],
        ["ysense"] = ["input[name='password']", "a[href*='action=login']", "form#loginForm"],
        ["freecash"] = ["button:has-text('Sign in')", "button:has-text('Log in')", "a[href*='/login']"]
    };

    private readonly IServiceProvider _serviceProvider;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<RewardAutomationService> _logger;
    private readonly BotOptions _options;
    private readonly FreecashApiClient _freecash;

    public RewardAutomationService(
        IServiceProvider serviceProvider,
        IBackgroundJobClient backgroundJobs,
        ILogger<RewardAutomationService> logger,
        IOptions<BotOptions> options)
    {
        _serviceProvider = serviceProvider;
        _backgroundJobs = backgroundJobs;
        _logger = logger;
        _options = options.Value;
        _freecash = new FreecashApiClient(logger);
    }

    public void ScheduleRandomDailyRuns()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var activeAccounts = dbContext.Accounts.Where(a => a.IsActive && a.SessionData != null).ToList();

        foreach (var account in activeAccounts)
        {
            var randomDelayMinutes = Random.Shared.Next(5, 120);
            _backgroundJobs.Schedule<RewardAutomationService>(
                s => s.RunDailyTasksAsync(account.Id),
                TimeSpan.FromMinutes(randomDelayMinutes));

            _logger.LogInformation(
                "Zakazan dnevni posao za nalog {AccountId} za {DelayMinutes} minuta.",
                account.Id, randomDelayMinutes);
        }

        _logger.LogInformation("Ukupno zakazano dnevnih poslova: {Count}", activeAccounts.Count);
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task ScanSiteDOMAsync(int accountId)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await dbContext.Accounts.Include(a => a.RewardSite).FirstOrDefaultAsync(a => a.Id == accountId);

        if (account == null || string.IsNullOrEmpty(account.SessionData))
        {
            _logger.LogWarning("Nalog {AccountId} ne postoji ili nema sacuvanu sesiju.", accountId);
            return;
        }

        var siteKey = ResolveSiteKey(account);
        var url = siteKey == "ysense" ? "https://www.ysense.com/" : "https://freecash.com/rewards";

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchBrowserAsync(playwright);
        var context = await NewContextAsync(browser, account.SessionData);
        var page = await context.NewPageAsync();

        try
        {
            _logger.LogInformation("Skeniram {Url} za nalog {AccountId}.", url, accountId);
            await NavigateAsync(page, url);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions
            {
                Timeout = _options.BalanceTimeoutMs
            });

            var html = await page.ContentAsync();
            var filePath = Path.Combine(EnsureArtifactsDirectory(), $"{siteKey}_source_{Timestamp()}.html");
            await File.WriteAllTextAsync(filePath, html);

            _logger.LogInformation("Kod stranice sacuvan na: {FilePath}", filePath);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Stranica {Url} se nije smirila na vreme, snimam sta imamo.", url);
            var html = await page.ContentAsync();
            var filePath = Path.Combine(EnsureArtifactsDirectory(), $"{siteKey}_source_{Timestamp()}.html");
            await File.WriteAllTextAsync(filePath, html);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Greska pri skeniranju {Url} za nalog {AccountId}.", url, accountId);
            await CaptureScreenshotAsync(page, $"{siteKey}_scan_error");
            throw;
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task StartLoginSessionAsync(int accountId)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await dbContext.Accounts.Include(a => a.RewardSite).FirstOrDefaultAsync(a => a.Id == accountId);

        if (account == null)
        {
            _logger.LogWarning("Nalog {AccountId} ne postoji.", accountId);
            return;
        }

        var siteKey = ResolveSiteKey(account);
        var loginUrl = siteKey switch
        {
            "ysense" => "https://www.ysense.com/?action=login",
            "freecash" => "https://freecash.com/rewards",
            _ => "https://login.live.com/"
        };

        using var playwright = await Playwright.CreateAsync();
        // Logovanje uvek zahteva vidljiv prozor - korisnik rucno unosi podatke.
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        try
        {
            _logger.LogInformation(
                "Otvaram prozor za logovanje ({Url}). Imate do {Seconds} sekundi.",
                loginUrl, _options.LoginWindowSeconds);

            await NavigateAsync(page, loginUrl);

            var outcome = await WaitForLoginAsync(page, siteKey);
            var storageState = await context.StorageStateAsync();

            // Ne pregazimo postojecu (mozda ispravnu) sesiju praznim stanjem.
            if (!ContainsCookies(storageState))
            {
                _logger.LogWarning(
                    "Logovanje za nalog {AccountId} nije sacuvano - browser nije vratio nijedan kolacic.",
                    accountId);
                return;
            }

            account.SessionData = storageState;
            dbContext.Accounts.Update(account);
            await dbContext.SaveChangesAsync();

            switch (outcome)
            {
                case LoginOutcome.Confirmed:
                    _logger.LogInformation("Sesija za nalog {AccountId} je uspesno sacuvana.", accountId);
                    break;
                case LoginOutcome.Unknown:
                    _logger.LogInformation(
                        "Sesija za nalog {AccountId} je sacuvana, ali nisam mogao automatski da potvrdim logovanje. Pokrenite bota da proverite.",
                        accountId);
                    break;
                case LoginOutcome.StillOnLoginPage:
                    _logger.LogWarning(
                        "Sesija za nalog {AccountId} je sacuvana, ali je prozor istekao dok ste jos bili na login stranici. Verovatno nije ispravna.",
                        accountId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Greska pri logovanju naloga {AccountId}.", accountId);
            await CaptureScreenshotAsync(page, $"{siteKey}_login_error");
            throw;
        }
        finally
        {
            await browser.CloseAsync();
        }
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task RunDailyTasksAsync(int accountId)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await dbContext.Accounts.Include(a => a.RewardSite).FirstOrDefaultAsync(a => a.Id == accountId);

        if (account == null || string.IsNullOrEmpty(account.SessionData))
        {
            _logger.LogWarning("Nalog {AccountId} ne postoji ili nema sacuvanu sesiju - preskacem.", accountId);
            return;
        }

        var siteKey = ResolveSiteKey(account);
        var failures = new List<string>();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchBrowserAsync(playwright);
        var desktopContext = await NewContextAsync(browser, account.SessionData);
        var desktopPage = await desktopContext.NewPageAsync();

        try
        {
            _logger.LogInformation("=== START: {SiteKey} / nalog {AccountId} ===", siteKey, accountId);

            switch (siteKey)
            {
                case "bing":
                    await RunBingAsync(desktopPage, dbContext, account, failures);
                    break;
                case "ysense":
                    await RunYsenseAsync(desktopPage, dbContext, account, failures);
                    break;
                case "freecash":
                    await RunFreecashAsync(desktopPage, dbContext, account, failures);
                    break;
                default:
                    failures.Add($"Nepoznat sajt '{account.RewardSite?.Name}' - nema definisanih zadataka.");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Neocekivana greska za nalog {AccountId} ({SiteKey}).", accountId, siteKey);
            await CaptureScreenshotAsync(desktopPage, $"{siteKey}_run_error");
            failures.Add(ex.Message);
        }
        finally
        {
            await PersistSessionAsync(dbContext, desktopContext, account);
            await desktopContext.CloseAsync();
        }

        if (failures.Count > 0)
        {
            // Bacamo izuzetak da bi posao bio crven u Hangfire tabli umesto da tiho "uspe".
            throw new InvalidOperationException(
                $"Bot nije zavrsio sve zadatke za nalog {accountId} ({siteKey}): {string.Join(" | ", failures)}");
        }

        _logger.LogInformation("=== BOT JE ZAVRSIO SA RADOM: {SiteKey} / nalog {AccountId} ===", siteKey, accountId);
    }

    // ---------------------------------------------------------------- sajtovi

    private async Task RunBingAsync(IPage page, AppDbContext dbContext, Account account, List<string> failures)
    {
        // Zagrevanje sesije - flyout ne vraca podatke ako prethodno nismo bili na bing.com.
        await NavigateAsync(page, BingHomeUrl);
        var status = await ReadBingStatusAsync(page);

        await RunBingSearchesAsync(page, status, failures);

        if (_options.BingDailySet)
        {
            await RunBingDailySetAsync(page, status);
        }

        if (_options.BingClaimPoints)
        {
            await ClaimBingPointsAsync(page, status);
        }

        await RunBingAppActivitiesAsync(page);

        var points = await ReadBingPointsAsync(page);
        await HandleBalanceAsync(page, dbContext, account, "bing", points, failures);
    }

    private async Task RunBingSearchesAsync(IPage page, BingRewardsStatus? status, List<string> failures)
    {
        var target = ResolveSearchCount(status);
        if (target <= 0)
        {
            return;
        }

        string[] baseWords =
        [
            "Srbija", "Beograd", "Vesti", "Sport", "Filmovi", "Recepti", "Tehnologija", "Zanimljivosti",
            "Istorija", "Automobili", "Kompjuteri", "Muzika", "Klima", "Putovanja", "Fizika", "Astronomija",
            "Planete", "Ekonomija", "Zdravlje", "Trening", "Ishrana", "Programiranje", "Arhitektura"
        ];

        _logger.LogInformation("Pokrecem {Count} Bing pretraga.", target);
        var completed = 0;

        for (var i = 0; i < target; i++)
        {
            var term = $"{baseWords[Random.Shared.Next(baseWords.Length)]} " +
                       $"{baseWords[Random.Shared.Next(baseWords.Length)]} " +
                       $"{Random.Shared.Next(100, 9999)}";
            try
            {
                // Direktna navigacija na stranicu rezultata. Raniji nacin - otvoriti pocetnu
                // stranu, upisati pojam i pritisnuti Enter - u praksi najcesce nije poslao
                // formu: stranica bi ostala na bing.com/, a posao se racunao kao uspesan jer
                // se cekalo samo DOMContentLoaded, koji nad neizmenjenom stranicom odmah prodje.
                // Provereno uzivo: od 21 takve "pretrage" priznate su 3.
                var url = $"{BingSearchUrl}?q={Uri.EscapeDataString(term)}&form=QBLH";
                await NavigateAsync(page, url);

                if (!page.Url.Contains("/search?", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Pretraga {Index}/{Total} nije otvorila rezultate ({Url}).",
                        i + 1, target, page.Url);
                    continue;
                }

                completed++;
                await HumanPauseAsync(page, 4000, 10000);
            }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
            {
                _logger.LogWarning(ex, "Pretraga {Index}/{Total} nije uspela ({Term}).", i + 1, target, term);
            }
        }

        _logger.LogInformation("Zavrseno {Completed}/{Total} pretraga.", completed, target);

        if (completed == 0)
        {
            failures.Add("Nijedna Bing pretraga nije uspela.");
            await CaptureScreenshotAsync(page, "bing_no_searches");
        }
    }

    /// <summary>
    /// Racuna koliko pretraga stvarno treba. Ranije je uvek radio fiksnih 25, iako je
    /// dnevni limit 60 poena (20 pretraga po 3 poena) - visak je bio bacen posao.
    /// </summary>
    private int ResolveSearchCount(BingRewardsStatus? status)
    {
        var fallback = Math.Min(_options.BingSearchCount, _options.BingMaxSearches);

        if (status is null)
        {
            _logger.LogWarning("Rewards status nije procitan - radim rezervnih {Count} pretraga.", fallback);
            return fallback;
        }

        if (!_options.BingSkipCompletedSearches)
        {
            return fallback;
        }

        var needed = status.RemainingSearches(_options.BingPointsPerSearch);
        if (needed <= 0)
        {
            _logger.LogInformation(
                "Dnevni limit poena od pretraga je vec dostignut ({Progress}/{Max}) - preskacem pretrage.",
                status.SearchProgress, status.SearchMax);
            return 0;
        }

        // Jedna pretraga vise od izracunatog, jer poneka ne bude priznata.
        var target = Math.Min(needed + 1, _options.BingMaxSearches);
        _logger.LogInformation(
            "Nedostaje {Missing} poena do limita ({Progress}/{Max}) - radim {Target} pretraga.",
            status.SearchMax - status.SearchProgress, status.SearchProgress, status.SearchMax, target);

        return target;
    }

    /// <summary>
    /// Cita Rewards status iz JSON-a ugradjenog u panelflyout. Dashboard je Next.js aplikacija
    /// bez upotrebljivog HTML-a, pa je flyout jedini pouzdan izvor ovih podataka.
    /// </summary>
    private async Task<BingRewardsStatus?> ReadBingStatusAsync(IPage page)
    {
        try
        {
            await NavigateAsync(page, BingFlyoutUrl);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions
            {
                Timeout = _options.ActionTimeoutMs
            });
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            _logger.LogDebug(ex, "Rewards flyout se nije ucitao do kraja - citam sadrzaj svejedno.");
        }

        try
        {
            var status = BingRewardsStatus.TryParse(await page.ContentAsync());
            if (status is null)
            {
                _logger.LogWarning(
                    "Rewards flyout je otvoren, ali JSON sa statusom nije pronadjen (moguca promena na Microsoft strani).");
                return null;
            }

            _logger.LogInformation(
                "Rewards status: {Points} poena, pretrage {Progress}/{Max}, Daily Set {Done}/{Total}.",
                status.AvailablePoints, status.SearchProgress, status.SearchMax,
                status.DailySet.Count(offer => offer.Complete), status.DailySet.Count);

            return status;
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            _logger.LogWarning(ex, "Citanje Rewards flyout-a nije uspelo.");
            return null;
        }
    }

    /// <summary>Stanje iz Rewards JSON-a je pouzdanije od zaglavlja bing.com, koje ostaje kao rezerva.</summary>
    private async Task<int?> ReadBingPointsAsync(IPage page)
    {
        var status = await ReadBingStatusAsync(page);
        if (status is { AvailablePoints: > 0 })
        {
            return status.AvailablePoints;
        }

        _logger.LogInformation("Padam na citanje poena iz zaglavlja bing.com.");
        await NavigateAsync(page, BingHomeUrl);

        return await PollForPointsAsync(page, async () =>
        {
            var text = await page.EvaluateAsync<string>(@"
                () => {
                    var el = document.querySelector('span.points-container');
                    if (el) return el.innerText;
                    var backup = document.querySelector('[data-tag=""RewardsHeader.Counter""]');
                    if (backup) return backup.innerText;
                    return '';
                }
            ");
            return ParseWholeNumber(text);
        }, "Bing stanje poena");
    }

    /// <summary>
    /// Odradjuje Daily Set (uobicajeno 3 x 10 poena). Neuspeh ovde se ne racuna kao pad posla -
    /// ponude se menjaju svakog dana i kvizovi su po prirodi nepouzdani.
    /// </summary>
    private async Task RunBingDailySetAsync(IPage page, BingRewardsStatus? status)
    {
        if (status is null)
        {
            return;
        }

        if (status.DailySet.Count == 0)
        {
            _logger.LogInformation("Daily Set za danas nije pronadjen u Rewards statusu.");
            return;
        }

        var pending = status.DailySet.Where(offer => !offer.Complete).ToList();
        if (pending.Count == 0)
        {
            _logger.LogInformation("Daily Set je vec kompletan ({Total} zadataka).", status.DailySet.Count);
            return;
        }

        _logger.LogInformation("Daily Set: preostalo {Count} od {Total} zadataka.", pending.Count, status.DailySet.Count);

        var dashboard = await page.Context.NewPageAsync();
        try
        {
            await NavigateAsync(dashboard, BingDashboardUrl);
            await WaitForAppReadyAsync(dashboard);

            foreach (var offer in pending)
            {
                try
                {
                    await RunBingOfferAsync(dashboard, offer);
                }
                catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
                {
                    _logger.LogWarning(ex, "Daily Set zadatak \"{Title}\" ({OfferId}) nije zavrsen.", offer.Title, offer.OfferId);
                }
            }
        }
        finally
        {
            if (!dashboard.IsClosed)
            {
                await dashboard.CloseAsync();
            }
        }
    }

    /// <summary>
    /// Klikce karticu zadatka na Rewards dashboard-u i saceka da se otvoreni tab ucita.
    /// </summary>
    /// <remarks>
    /// Provereno uzivo: navigacija pravo na offer.DestinationUrl ne donosi nijedan poen, ma
    /// koliko se dugo cekalo, jer se zasluga vezuje za klik na dashboard-u. Klik na karticu
    /// donosi punih 10 poena - i za kvizove, bez odgovaranja na ijedno pitanje.
    /// </remarks>
    private async Task RunBingOfferAsync(IPage dashboard, BingOffer offer)
    {
        _logger.LogInformation("Daily Set zadatak \"{Title}\" ({Points} poena, kviz: {IsQuiz}).",
            offer.Title, offer.Points, offer.IsQuiz);

        var card = await FindOfferCardAsync(dashboard, offer);
        if (card is null)
        {
            _logger.LogWarning("Kartica za \"{Title}\" nije pronadjena na dashboard-u.", offer.Title);
            await CaptureScreenshotAsync(dashboard, "bing_dailyset_bez_kartice");
            return;
        }

        IPage? tab = null;
        try
        {
            var pending = dashboard.Context.WaitForPageAsync();
            await card.ClickAsync(new LocatorClickOptions { Timeout = _options.ActionTimeoutMs });

            tab = await pending;
            await tab.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
            {
                Timeout = _options.NavigationTimeoutMs
            });

            // Zasluga se knjizi tek kad otvorena stranica malo "odstoji".
            await HumanPauseAsync(tab, _options.BingOfferDwellMinMs, _options.BingOfferDwellMaxMs);
        }
        finally
        {
            if (tab is not null && !tab.IsClosed)
            {
                await tab.CloseAsync();
            }

            if (!dashboard.IsClosed)
            {
                await dashboard.BringToFrontAsync();
            }
        }
    }

    /// <summary>
    /// Trazi karticu zadatka. Naslov zadatka stoji kao alt teksta slike na kartici, a kao
    /// rezerva se koristi poklapanje po odredisnom URL-u.
    /// </summary>
    private static async Task<ILocator?> FindOfferCardAsync(IPage dashboard, BingOffer offer)
    {
        if (!string.IsNullOrWhiteSpace(offer.Title))
        {
            var byAlt = dashboard.Locator($"a:has(img[alt={QuoteSelector(offer.Title)}])");
            if (await byAlt.CountAsync() > 0)
            {
                return byAlt.First;
            }
        }

        if (!string.IsNullOrWhiteSpace(offer.DestinationUrl))
        {
            // Dovoljno je prvih par parametara - dashboard ih ispisuje HTML-escapovane.
            var key = offer.DestinationUrl.Split('&')[0];
            var byHref = dashboard.Locator($"a[href^={QuoteSelector(key)}]");
            if (await byHref.CountAsync() > 0)
            {
                return byHref.First;
            }
        }

        return null;
    }

    private static string QuoteSelector(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Preuzima bonus poene koji stoje neuzeti ("Ready to claim") i inace bi istekli.</summary>
    private async Task ClaimBingPointsAsync(IPage page, BingRewardsStatus? status)
    {
        var url = status?.ClaimUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var tab = await page.Context.NewPageAsync();
        try
        {
            await NavigateAsync(tab, url);
            await WaitForAppReadyAsync(tab);

            // Prvo trazimo dugme u modalu; kartica na dashboard-u samo otvara modal.
            var claimed = await TryClaimAsync(tab);

            if (!claimed && await TryClickAsync(tab, tab.Locator(ClaimCardSelector)))
            {
                await WaitForAppReadyAsync(tab);
                claimed = await TryClaimAsync(tab);
            }

            if (!claimed)
            {
                _logger.LogInformation("Nema bonus poena za preuzimanje.");
            }
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            _logger.LogWarning(ex, "Preuzimanje bonus poena nije uspelo.");
        }
        finally
        {
            if (!tab.IsClosed)
            {
                await tab.CloseAsync();
            }
        }
    }

    /// <summary>
    /// Dugme za preuzimanje ostaje na stranici i kada nema sta da se uzme (tekst "0 Claim points"),
    /// pa broj citamo iz samog teksta da ne bismo prijavili preuzimanje koje se nije desilo.
    /// </summary>
    private async Task<bool> TryClaimAsync(IPage tab)
    {
        var button = tab.Locator(ClaimModalButtonSelector);
        int? pending;

        try
        {
            if (await button.CountAsync() == 0)
            {
                return false;
            }

            pending = ParseWholeNumber(await button.First.InnerTextAsync(new LocatorInnerTextOptions
            {
                Timeout = _options.ActionTimeoutMs
            }));
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            _logger.LogDebug(ex, "Nisam mogao da procitam dugme za preuzimanje bonus poena.");
            return false;
        }

        if (pending is null || !await TryClickAsync(tab, button))
        {
            return false;
        }

        _logger.LogInformation("Preuzeto {Points} bonus poena.", pending);
        await HumanPauseAsync(tab, 4000, 7000);
        return true;
    }

    /// <summary>
    /// Aktivnosti koje postoje samo u Bing mobilnoj aplikaciji ("Read to earn", dnevni check-in).
    /// Idu preko Rewards API-ja jer ih na webu nema.
    /// </summary>
    private async Task RunBingAppActivitiesAsync(IPage page)
    {
        if (!_options.BingReadToEarn && !_options.BingDailyCheckIn)
        {
            return;
        }

        var client = new BingRewardsApiClient(_logger, _options);
        if (!await client.AuthenticateAsync(page.Context))
        {
            _logger.LogWarning("Preskacem aktivnosti iz mobilne aplikacije - prijava na Rewards API nije uspela.");
            return;
        }

        if (_options.BingReadToEarn)
        {
            await RunReadToEarnAsync(client, page);
        }

        if (_options.BingDailyCheckIn)
        {
            await RunDailyCheckInAsync(client);
        }
    }

    private async Task RunReadToEarnAsync(BingRewardsApiClient client, IPage page)
    {
        var activity = await client.TryGetActivityAsync("msnreadearn");
        if (activity is null)
        {
            _logger.LogInformation("Read to earn nije ponudjen ovom nalogu.");
            return;
        }

        var remaining = activity.RemainingActivities;
        if (remaining <= 0)
        {
            _logger.LogInformation("Read to earn je vec odradjen ({Progress}/{Max} poena).",
                activity.PointProgress, activity.PointMax);
            return;
        }

        var target = Math.Min(remaining, _options.BingMaxReadArticles);
        _logger.LogInformation("Read to earn: prijavljujem {Target} od {Remaining} preostalih clanaka.",
            target, remaining);

        var earned = 0;
        for (var i = 0; i < target; i++)
        {
            var points = await client.ReportActivityAsync(activity);
            if (points is null)
            {
                _logger.LogWarning("Read to earn: prijava {Index}/{Target} nije uspela - prekidam.", i + 1, target);
                break;
            }

            earned += points.Value;

            // Citanje clanka traje - bez pauze bi obrazac bio ocigledno masinski.
            await HumanPauseAsync(page, 8000, 20000);
        }

        _logger.LogInformation("Read to earn: zaradjeno {Earned} poena.", earned);
    }

    private async Task RunDailyCheckInAsync(BingRewardsApiClient client)
    {
        var activity = await client.TryGetActivityAsync("checkin");
        if (activity is null)
        {
            _logger.LogInformation("Dnevni check-in nije ponudjen ovom nalogu.");
            return;
        }

        if (activity.Complete)
        {
            _logger.LogInformation("Dnevni check-in je vec odradjen.");
            return;
        }

        var points = await client.ReportActivityAsync(activity);
        if (points is null)
        {
            _logger.LogWarning("Dnevni check-in nije uspeo.");
            return;
        }

        _logger.LogInformation("Dnevni check-in: zaradjeno {Points} poena.", points.Value);
    }

    /// <summary>
    /// Rewards dashboard je React aplikacija - DOMContentLoaded nije dovoljan da bi
    /// dugmad postojala u DOM-u. Zato cekamo mirovanje mreze pa jos malo iscrtavanje.
    /// </summary>
    private async Task WaitForAppReadyAsync(IPage page)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions
            {
                Timeout = _options.NavigationTimeoutMs
            });
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            _logger.LogDebug(ex, "Cekanje na mirovanje mreze je isteklo - nastavljam.");
        }

        await page.WaitForTimeoutAsync(_options.AppRenderDelayMs);
    }

    /// <summary>Klikce samo ono sto stvarno postoji, vidi se i omoguceno je. Vraca da li je klik izvrsen.</summary>
    private async Task<bool> TryClickAsync(IPage page, ILocator locator)    {
        try
        {
            if (page.IsClosed || await locator.CountAsync() == 0)
            {
                return false;
            }

            var element = locator.First;
            if (!await element.IsVisibleAsync() || !await element.IsEnabledAsync())
            {
                return false;
            }

            await element.ClickAsync(new LocatorClickOptions { Timeout = _options.ActionTimeoutMs });
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            return false;
        }
    }

    private async Task RunYsenseAsync(IPage page, AppDbContext dbContext, Account account, List<string> failures)
    {
        await NavigateAsync(page, YsenseSurveysUrl);
        await ReadYsenseChecklistAsync(page);

        await NavigateAsync(page, "https://www.ysense.com/");
        var points = await PollForPointsAsync(page, async () =>
        {
            var text = await page.EvaluateAsync<string>(@"
                () => {
                    var b = document.querySelector('.dropdown-menu-right li a strong');
                    if (b && b.innerText.includes('$')) return b.innerText;
                    return '';
                }
            ");
            return ParseDecimalAsCents(text);
        }, "ySense stanje");

        await HandleBalanceAsync(page, dbContext, account, "ysense", points, failures);
    }

    /// <summary>
    /// Cita "Daily Checklist Bonus" panel i ispisuje dokle se stiglo. Sam bonus se ne moze
    /// automatizovati - trazi stvarno odradjene ankete ili ponude - ali je korisno videti
    /// sta jos fali za njega.
    /// </summary>
    private async Task ReadYsenseChecklistAsync(IPage page)
    {
        if (!_options.YsenseChecklist)
        {
            return;
        }

        try
        {
            var button = page.Locator(YsenseChecklistButtonSelector);
            await button.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = _options.ActionTimeoutMs
            });

            // ySense povremeno drzi otvoren modal sa preporukom ankete cija providna
            // podloga presretne obican klik. DispatchEvent ne zavisi od pointer dogadjaja.
            await button.DispatchEventAsync("click", null, new LocatorDispatchEventOptions
            {
                Timeout = _options.ActionTimeoutMs
            });

            await page.WaitForTimeoutAsync(_options.AppRenderDelayMs);

            var summary = ExtractChecklistSummary(await page.Locator("body").InnerTextAsync());
            if (summary is null)
            {
                _logger.LogInformation("Daily Checklist panel je otvoren, ali sadrzaj nije prepoznat.");
                return;
            }

            _logger.LogInformation("Daily Checklist: {Summary}", summary);
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            _logger.LogInformation(ex, "Daily Checklist panel nije procitan.");
        }
    }

    /// <summary>
    /// Izvlaci deo teksta panela izmedju naslova i dugmeta "View History" i spaja ga u jedan red.
    /// Namerno se oslanja na tekst, a ne na klase - ySense generise nasumicne sufikse u nazivima klasa.
    /// </summary>
    private static string? ExtractChecklistSummary(string pageText)
    {
        var start = pageText.IndexOf(ChecklistHeading, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += ChecklistHeading.Length;
        var end = pageText.IndexOf(ChecklistFooter, start, StringComparison.OrdinalIgnoreCase);
        var slice = end > start ? pageText[start..end] : pageText[start..];

        var condensed = Regex.Replace(slice, @"\s*\n\s*", " · ", RegexOptions.None, RegexTimeout).Trim(' ', '·');
        return condensed.Length == 0 ? null : condensed;
    }

    private async Task RunFreecashAsync(IPage page, AppDbContext dbContext, Account account, List<string> failures)
    {
        await NavigateAsync(page, FreecashRewardsUrl);
        await page.WaitForTimeoutAsync(_options.AppRenderDelayMs);

        await RunFreecashStreakAsync(page);
        await RunFreecashBonusCodesAsync(page);
        await LogFreecashLotteryAsync(page);

        var points = await PollForPointsAsync(page, () => _freecash.ReadBalanceCentsAsync(page), "Freecash stanje");
        await HandleBalanceAsync(page, dbContext, account, "freecash", points, failures);
    }

    /// <summary>
    /// Preuzima dnevni niz samo kada je to stvarno moguce. Nagrada je zakljucana dok se
    /// u toku dana ne zaradi propisani iznos, pa dugme tada stoji onemoguceno.
    /// </summary>
    private async Task RunFreecashStreakAsync(IPage page)
    {
        if (!_options.FreecashStreak)
        {
            return;
        }

        var streak = await _freecash.ReadStreakAsync(page);
        if (streak is null)
        {
            _logger.LogWarning("Status dnevnog niza nije procitan.");
            return;
        }

        var required = await _freecash.ReadMinCoinsToEarnAsync(page);
        var requiredText = required is null
            ? "nepoznato"
            : $"${required.Value / (double)FreecashApiClient.CoinsPerDollar:0.00}";

        _logger.LogInformation(
            "Dnevni niz: dan {Day}, nagrada {Reward} centi, preuzeto: {Claimed}. Uslov: zarada od {Required} u toku dana.",
            streak.Day, FreecashApiClient.CoinsToCents(streak.Coins), streak.Claimed ? "da" : "ne", requiredText);

        if (streak.Claimed)
        {
            return;
        }

        var button = page.Locator(FreecashClaimSelector).First;

        if (await button.CountAsync() == 0)
        {
            _logger.LogInformation("Dugme za preuzimanje dnevnog niza nije prikazano.");
            return;
        }

        if (!await button.IsEnabledAsync())
        {
            _logger.LogInformation(
                "Nagrada za dnevni niz je zakljucana - dnevna zarada od {Required} jos nije ostvarena.", requiredText);
            return;
        }

        await button.ClickAsync(new LocatorClickOptions { Timeout = _options.ActionTimeoutMs });
        await HumanPauseAsync(page, 4000, 7000);

        var after = await _freecash.ReadStreakAsync(page);
        if (after?.Claimed == true)
        {
            _logger.LogInformation("Preuzeta nagrada dnevnog niza: {Cents} centi.", FreecashApiClient.CoinsToCents(streak.Coins));
        }
        else
        {
            _logger.LogWarning("Klik na dugme dnevnog niza nije potvrdjen kao preuzimanje.");
        }
    }

    /// <summary>
    /// Unosi bonus kodove iz konfiguracije. Kodovi se objavljuju na drustvenim mrezama
    /// Freecash-a, a one se ne mogu citati automatski (x.com bez prijave vraca HTTP 403),
    /// pa se lista popunjava rucno u appsettings.json.
    /// </summary>
    private async Task RunFreecashBonusCodesAsync(IPage page)
    {
        var codes = _options.FreecashBonusCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (codes.Count == 0)
        {
            return;
        }

        foreach (var code in codes)
        {
            var (outcome, message) = await _freecash.ClaimBonusCodeAsync(page, code);

            switch (outcome)
            {
                case BonusCodeOutcome.Redeemed:
                    _logger.LogInformation("Bonus kod {Code}: iskoriscen ({Message}).", code, message);
                    break;
                case BonusCodeOutcome.Rejected:
                    _logger.LogInformation("Bonus kod {Code}: odbijen ({Message}).", code, message);
                    break;
                default:
                    _logger.LogWarning("Bonus kod {Code}: {Message}.", code, message);
                    break;
            }

            await HumanPauseAsync(page, 2000, 4000);
        }
    }

    private async Task LogFreecashLotteryAsync(IPage page)
    {
        if (!_options.FreecashLottery)
        {
            return;
        }

        var tickets = await _freecash.ReadLotteryTicketsAsync(page);
        if (tickets is not null)
        {
            _logger.LogInformation("Nedeljna lutrija: {Tickets} tiketa.", tickets);
        }
    }

    // ---------------------------------------------------------------- pomocne

    /// <summary>
    /// Ceka da citanje vrati vrednost, umesto fiksne pauze. Vraca cim vrednost postoji
    /// (brze nego ranije), a pokusava sve do isteka tajmauta (pouzdanije nego ranije).
    /// </summary>
    private async Task<int?> PollForPointsAsync(IPage page, Func<Task<int?>> read, string what)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(_options.BalanceTimeoutMs);
        Exception? lastError = null;

        do
        {
            try
            {
                var value = await read();
                if (value is > 0)
                {
                    _logger.LogInformation("Procitano {What}: {Value}", what, value);
                    return value;
                }
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                lastError = ex;
            }

            await page.WaitForTimeoutAsync(PollIntervalMs);
        }
        while (DateTime.UtcNow < deadline);

        _logger.LogWarning(lastError, "Isteklo vreme ({TimeoutMs} ms) pri cekanju na: {What}",
            _options.BalanceTimeoutMs, what);
        return null;
    }

    /// <summary>Cuva procitano stanje, ili dijagnostikuje zasto citanje nije uspelo.</summary>
    private async Task HandleBalanceAsync(
        IPage page, AppDbContext dbContext, Account account, string siteKey, int? points, List<string> failures)
    {
        if (points is > 0)
        {
            TrySavePoints(dbContext, account, points.Value);
            return;
        }

        var screenshot = await CaptureScreenshotAsync(page, $"{siteKey}_balance_unreadable");

        if (await LooksLoggedOutAsync(page, siteKey))
        {
            _logger.LogError(
                "SESIJA JE ISTEKLA za nalog {AccountId} ({SiteKey}). Potrebno je ponovno logovanje. Snimak: {Screenshot}",
                account.Id, siteKey, screenshot ?? "nije snimljen");
            failures.Add("Sesija je istekla - potrebno je ponovno logovanje.");
            return;
        }

        _logger.LogError(
            "Ne mogu da procitam stanje za nalog {AccountId} ({SiteKey}) iako izgleda da smo ulogovani - verovatno je sajt promenio DOM. Snimak: {Screenshot}",
            account.Id, siteKey, screenshot ?? "nije snimljen");
        failures.Add("Stanje poena nije procitano (moguca promena DOM-a na sajtu).");
    }

    private void TrySavePoints(AppDbContext dbContext, Account account, int pts)
    {
        if (pts <= 0)
        {
            _logger.LogWarning("Odbacena vrednost {Points} za nalog {AccountId}.", pts, account.Id);
            return;
        }

        var isFirstReading = !dbContext.PointLogs.Any(l => l.AccountId == account.Id);
        var earned = isFirstReading ? 0 : pts - account.CurrentPoints;

        if (isFirstReading)
        {
            _logger.LogInformation(
                "Prvo citanje za nalog {AccountId} - upisujem {Points} kao pocetno stanje (zarada 0).",
                account.Id, pts);
        }
        else if (earned < 0)
        {
            // Legitimno kod isplate/konverzije, ali i simptom pogresnog citanja.
            // Ne upisujemo negativnu zaradu da ne bismo pokvarili grafikon.
            _logger.LogWarning(
                "Stanje naloga {AccountId} je palo sa {Previous} na {Current}. Upisujem zaradu 0 (isplata ili pogresno citanje).",
                account.Id, account.CurrentPoints, pts);
            earned = 0;
        }

        dbContext.PointLogs.Add(new PointLog
        {
            AccountId = account.Id,
            Date = DateTime.UtcNow,
            TotalPointsAfter = pts,
            PointsEarned = earned
        });

        account.CurrentPoints = pts;
        dbContext.Accounts.Update(account);
        dbContext.SaveChanges();

        _logger.LogInformation("Sacuvano stanje {Points} (zarada {Earned}) za nalog {AccountId}.",
            pts, earned, account.Id);
    }

    private async Task PersistSessionAsync(AppDbContext dbContext, IBrowserContext context, Account account)
    {
        try
        {
            var storageState = await context.StorageStateAsync();
            if (!ContainsCookies(storageState))
            {
                _logger.LogWarning("Sesija naloga {AccountId} nije osvezena - stanje je prazno.", account.Id);
                return;
            }

            account.SessionData = storageState;
            dbContext.Accounts.Update(account);
            await dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nije uspelo cuvanje sesije za nalog {AccountId}.", account.Id);
        }
    }

    private async Task<LoginOutcome> WaitForLoginAsync(IPage page, string siteKey)
    {
        if (!LoginUrlFragments.TryGetValue(siteKey, out var fragments))
        {
            _logger.LogInformation(
                "Za ovaj sajt nemam pouzdan signal o zavrsenom logovanju - cekam ceo prozor od {Seconds} s.",
                _options.LoginWindowSeconds);
            await WaitUnlessClosedAsync(page, _options.LoginWindowSeconds * 1000);
            return LoginOutcome.Unknown;
        }

        var deadline = DateTime.UtcNow.AddSeconds(_options.LoginWindowSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (page.IsClosed)
            {
                return LoginOutcome.Unknown;
            }

            var url = page.Url ?? string.Empty;
            var onRealPage = url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            var stillOnLogin = fragments.Any(f => url.Contains(f, StringComparison.OrdinalIgnoreCase));

            if (onRealPage && !stillOnLogin)
            {
                // Napustili smo login stranicu - dajemo sajtu trenutak da postavi kolacice.
                await page.WaitForTimeoutAsync(3000);
                _logger.LogInformation("Logovanje je zavrseno, ne cekam ceo prozor.");
                return LoginOutcome.Confirmed;
            }

            await page.WaitForTimeoutAsync(2000);
        }

        return LoginOutcome.StillOnLoginPage;
    }

    private static async Task WaitUnlessClosedAsync(IPage page, int totalMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(totalMs);
        while (DateTime.UtcNow < deadline && !page.IsClosed)
        {
            await page.WaitForTimeoutAsync(2000);
        }
    }

    private async Task<bool> LooksLoggedOutAsync(IPage page, string siteKey)
    {
        if (!LoggedOutMarkers.TryGetValue(siteKey, out var selectors))
        {
            return false;
        }

        foreach (var selector in selectors)
        {
            try
            {
                if (await page.Locator(selector).First.IsVisibleAsync())
                {
                    _logger.LogDebug("Pronadjen znak da nismo ulogovani: {Selector}", selector);
                    return true;
                }
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // Selektor ne postoji na stranici - to je dobar znak.
            }
        }

        return false;
    }

    private async Task<string?> CaptureScreenshotAsync(IPage page, string label)
    {
        try
        {
            if (page.IsClosed)
            {
                return null;
            }

            var filePath = Path.Combine(EnsureArtifactsDirectory(), $"{Timestamp()}_{Sanitize(label)}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = filePath, FullPage = true });
            _logger.LogInformation("Snimak ekrana sacuvan: {FilePath}", filePath);
            return filePath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nije uspelo snimanje ekrana ({Label}).", label);
            return null;
        }
    }

    private Task<IBrowser> LaunchBrowserAsync(IPlaywright playwright) =>
        playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = _options.Headless });

    private static Task<IBrowserContext> NewContextAsync(IBrowser browser, string? storageState) =>
        browser.NewContextAsync(new BrowserNewContextOptions { StorageState = storageState });

    private Task NavigateAsync(IPage page, string url) =>
        page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = _options.NavigationTimeoutMs
        });

    /// <summary>Namerna pauza koja oponasa coveka - nije cekanje na uslov.</summary>
    private static Task HumanPauseAsync(IPage page, int minMs, int maxMs) =>
        page.WaitForTimeoutAsync(Random.Shared.Next(minMs, maxMs));

    private static string ResolveSiteKey(Account account)
    {
        var name = account.RewardSite?.Name?.ToLowerInvariant() ?? string.Empty;
        if (name.Contains("ysense")) return "ysense";
        if (name.Contains("freecash")) return "freecash";
        if (name.Contains("bing")) return "bing";
        return name;
    }

    private string EnsureArtifactsDirectory()
    {
        var configured = string.IsNullOrWhiteSpace(_options.ArtifactsPath) ? "bot-artifacts" : _options.ArtifactsPath;
        var path = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);

        Directory.CreateDirectory(path);
        return path;
    }

    private static bool ContainsCookies(string? storageState) =>
        !string.IsNullOrWhiteSpace(storageState) &&
        Regex.IsMatch(storageState, @"""cookies""\s*:\s*\[\s*\{", RegexOptions.None, RegexTimeout);

    private static string Timestamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static int? ParseWholeNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var digits = Regex.Replace(text, "[^0-9]", string.Empty, RegexOptions.None, RegexTimeout);
        return int.TryParse(digits, out var value) && value > 0 ? value : null;
    }

    private static int? ParseDecimalAsCents(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = Regex.Replace(text, @"[^0-9\.]", string.Empty, RegexOptions.None, RegexTimeout);
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) && amount > 0
            ? (int)Math.Round(amount * 100)
            : null;
    }
}
