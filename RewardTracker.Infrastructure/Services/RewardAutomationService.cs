using Microsoft.Playwright;
using RewardTracker.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Hangfire;
using System.Threading.Tasks;
using System;
using System.Text.RegularExpressions;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace RewardTracker.Infrastructure.Services;

public class RewardAutomationService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IBackgroundJobClient _backgroundJobs;

    public RewardAutomationService(IServiceProvider serviceProvider, IBackgroundJobClient backgroundJobs)
    {
        _serviceProvider = serviceProvider;
        _backgroundJobs = backgroundJobs;
    }

    public void ScheduleRandomDailyRuns()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        
        var activeAccounts = dbContext.Accounts.Where(a => a.IsActive && a.SessionData != null).ToList();
        var random = new Random();

        foreach (var account in activeAccounts)
        {
            var randomDelayMinutes = random.Next(5, 120);
            _backgroundJobs.Schedule<RewardAutomationService>(
                s => s.RunDailyTasksAsync(account.Id), 
                TimeSpan.FromMinutes(randomDelayMinutes)
            );
        }
    }

        public async Task ScanSiteDOMAsync(int accountId)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await dbContext.Accounts.Include(a => a.RewardSite).FirstOrDefaultAsync(a => a.Id == accountId);
        if (account == null || string.IsNullOrEmpty(account.SessionData)) 
        {
            Console.WriteLine("Nalog nema sacuvanu sesiju!");
            return;
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
        
        var options = new BrowserNewContextOptions { StorageState = account.SessionData };
        var context = await browser.NewContextAsync(options);
        var page = await context.NewPageAsync();

        string url = account.RewardSite.Name.ToLower().Contains("ysense") ? "https://www.ysense.com/" : "https://freecash.com/";
        
        try 
        {
            Console.WriteLine($"Skeniram {url}...");
            await page.GotoAsync(url);
            await Task.Delay(15000); // Cekamo da prodju sve Cloudflare zastite i pop-upovi
            
            var html = await page.ContentAsync();
            var fileName = url.Contains("ysense") ? "ysense_source.txt" : "freecash_source.txt";
            var filePath = System.IO.Path.Combine("C:\\Users\\mimi2004\\Desktop", fileName);
            
            await System.IO.File.WriteAllTextAsync(filePath, html);
            Console.WriteLine($"Uspesno sacuvan kod na: {filePath}");
        }
        catch (Exception ex) 
        { 
            Console.WriteLine("Greska pri skeniranju: " + ex.Message); 
        }
        finally 
        { 
            await context.CloseAsync(); 
        }
    }

        public async Task StartLoginSessionAsync(int accountId)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await dbContext.Accounts.Include(a => a.RewardSite).FirstOrDefaultAsync(a => a.Id == accountId);
        if (account == null) return;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        string loginUrl = "https://login.live.com/";
        if (account.RewardSite != null)
        {
            var siteName = account.RewardSite.Name.ToLower();
            if (siteName.Contains("ysense")) loginUrl = "https://www.ysense.com/?action=login";
            else if (siteName.Contains("freecash")) loginUrl = "https://freecash.com/";
        }

        try 
        {
            Console.WriteLine($"Otvaram prozor za logovanje: {loginUrl}");
            await page.GotoAsync(loginUrl);
            await Task.Delay(90000); 

            account.SessionData = await context.StorageStateAsync();
            dbContext.Accounts.Update(account);
            await dbContext.SaveChangesAsync();
        }
        catch (Exception ex) { Console.WriteLine("Greska: " + ex.Message); }
        finally { await browser.CloseAsync(); }
    }

    public async Task RunDailyTasksAsync(int accountId)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await dbContext.Accounts.FindAsync(accountId);
        if (account == null || string.IsNullOrEmpty(account.SessionData)) return;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
        
        var random = new Random();
        var baseWords = new[] { "Srbija", "Beograd", "Vesti", "Sport", "Filmovi", "Recepti", "Tehnologija", "Zanimljivosti", "Istorija", "Automobili", "Kompjuteri", "Muzika", "Klima", "Putovanja", "Fizika", "Astronomija", "Planete", "Ekonomija", "Zdravlje", "Trening", "Ishrana", "Programiranje", "Arhitektura" };

        var desktopOptions = new BrowserNewContextOptions { StorageState = account.SessionData };
        var desktopContext = await browser.NewContextAsync(desktopOptions);
        var desktopPage = await desktopContext.NewPageAsync();

        try 
        {
            Console.WriteLine("=== START: BING PRETRAGE (25 iteracija) ===");
            for(int i = 0; i < 25; i++)
            {
                var term = baseWords[random.Next(baseWords.Length)] + " " + baseWords[random.Next(baseWords.Length)] + " " + random.Next(100, 9999);
                await desktopPage.GotoAsync("https://www.bing.com");
                await Task.Delay(2000);
                var searchInput = desktopPage.Locator("[name='q']").First;
                await searchInput.FillAsync(term, new() { Force = true });
                await searchInput.PressAsync("Enter");
                await Task.Delay(random.Next(4000, 10000)); 
            }

            Console.WriteLine("=== CITANJE UKUPNIH POENA SA BING EKRANA ===");
            await desktopPage.GotoAsync("https://www.bing.com");
            await Task.Delay(4000); 

            var pointsText = await desktopPage.EvaluateAsync<string>(@"
                () => {
                    var el = document.querySelector('span.points-container');
                    if (el) return el.innerText;
                    
                    var backup = document.querySelector('[data-tag=""RewardsHeader.Counter""]');
                    if (backup) return backup.innerText;

                    return '';
                }
            ");

            if (!string.IsNullOrWhiteSpace(pointsText))
            {
                var cleanNumber = Regex.Replace(pointsText, "[^0-9]", "");
                if (int.TryParse(cleanNumber, out int pts) && pts > 0)
                {
                    Console.WriteLine("Uspesno prepoznat broj: " + pts);
                    var log = new RewardTracker.Core.Entities.PointLog
                    {
                        AccountId = account.Id,
                        Date = DateTime.UtcNow,
                        TotalPointsAfter = pts,
                        PointsEarned = pts - account.CurrentPoints
                    };
                    dbContext.PointLogs.Add(log);
                    
                    account.CurrentPoints = pts;
                    dbContext.Accounts.Update(account);
                    await dbContext.SaveChangesAsync();
                }
            }
            
            account.SessionData = await desktopContext.StorageStateAsync();
            dbContext.Accounts.Update(account);
            await dbContext.SaveChangesAsync();
        }
        catch (Exception ex) { Console.WriteLine("Greska PC: " + ex.Message); }
        finally { await desktopContext.CloseAsync(); }
        
        Console.WriteLine("=== BOT JE ZAVRSIO SA RADOM ===");
    }
}


