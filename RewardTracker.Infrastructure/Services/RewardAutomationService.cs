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
        string[] baseWords =
        [
            "Srbija", "Beograd", "Vesti", "Sport", "Filmovi", "Recepti", "Tehnologija", "Zanimljivosti",
            "Istorija", "Automobili", "Kompjuteri", "Muzika", "Klima", "Putovanja", "Fizika", "Astronomija",
            "Planete", "Ekonomija", "Zdravlje", "Trening", "Ishrana", "Programiranje", "Arhitektura"
        ];

        _logger.LogInformation("Pokrecem {Count} Bing pretraga.", _options.BingSearchCount);
        var completed = 0;

        for (var i = 0; i < _options.BingSearchCount; i++)
        {
            var term = $"{baseWords[Random.Shared.Next(baseWords.Length)]} " +
                       $"{baseWords[Random.Shared.Next(baseWords.Length)]} " +
                       $"{Random.Shared.Next(100, 9999)}";
            try
            {
                await NavigateAsync(page, "https://www.bing.com");

                var searchInput = page.Locator("[name='q']").First;
                await searchInput.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = _options.ActionTimeoutMs
                });

                await searchInput.FillAsync(term, new LocatorFillOptions { Force = true });
                await searchInput.PressAsync("Enter");
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
                {
                    Timeout = _options.NavigationTimeoutMs
                });

                completed++;
                await HumanPauseAsync(page, 4000, 10000);
            }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
            {
                _logger.LogWarning(ex, "Pretraga {Index}/{Total} nije uspela ({Term}).",
                    i + 1, _options.BingSearchCount, term);
            }
        }

        _logger.LogInformation("Zavrseno {Completed}/{Total} pretraga.", completed, _options.BingSearchCount);

        if (completed == 0)
        {
            failures.Add("Nijedna Bing pretraga nije uspela.");
            await CaptureScreenshotAsync(page, "bing_no_searches");
        }

        await NavigateAsync(page, "https://www.bing.com");
        var points = await PollForPointsAsync(page, async () =>
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

        await HandleBalanceAsync(page, dbContext, account, "bing", points, failures);
    }

    private async Task RunYsenseAsync(IPage page, AppDbContext dbContext, Account account, List<string> failures)
    {
        _logger.LogInformation("Pokusavam ySense Daily Poll.");
        try
        {
            await NavigateAsync(page, "https://www.ysense.com/surveys");

            var radios = page.Locator("input[type='radio']");
            await radios.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = _options.ActionTimeoutMs
            });

            await radios.First.ClickAsync();

            var voteBtn = page.Locator("button:has-text('Vote'), input[value='Vote'], button:has-text('Submit')").First;
            await voteBtn.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = _options.ActionTimeoutMs
            });

            await voteBtn.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions
            {
                Timeout = _options.ActionTimeoutMs
            });

            _logger.LogInformation("Daily Poll je odglasan.");
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            // Ocekivano kada je poll vec uradjen ili ga danas nema.
            _logger.LogInformation(ex, "Daily Poll nije odradjen (verovatno vec uradjen ili ga nema danas).");
        }

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

    private async Task RunFreecashAsync(IPage page, AppDbContext dbContext, Account account, List<string> failures)
    {
        _logger.LogInformation("Pokusavam Freecash Claim.");
        try
        {
            await NavigateAsync(page, "https://freecash.com/rewards");

            var claimBtn = page.Locator("button:has-text('Claim')").First;
            await claimBtn.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = _options.ActionTimeoutMs
            });

            await claimBtn.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions
            {
                Timeout = _options.ActionTimeoutMs
            });

            _logger.LogInformation("Claim dugme je kliknuto.");
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            _logger.LogInformation(ex, "Claim dugme nije pronadjeno (verovatno je vec kliknuto danas).");
        }

        await NavigateAsync(page, "https://freecash.com/rewards");
        var points = await PollForPointsAsync(
            page,
            async () => ParseFreecashCents(await page.ContentAsync()),
            "Freecash stanje");

        await HandleBalanceAsync(page, dbContext, account, "freecash", points, failures);
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

    private static int? ParseFreecashCents(string html)
    {
        var match = Regex.Match(html, @"secondaryLabel[^0-9]+([0-9]+[.,][0-9]+)", RegexOptions.None, RegexTimeout);
        if (match.Success &&
            decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture,
                out var amount) && amount > 0)
        {
            return (int)Math.Round(amount * 100);
        }

        var alt = Regex.Match(html, @"""coins""\s*:\s*(\d+)", RegexOptions.None, RegexTimeout);
        return alt.Success && int.TryParse(alt.Groups[1].Value, out var coins) && coins > 0 ? coins : null;
    }
}
