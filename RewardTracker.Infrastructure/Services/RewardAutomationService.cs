using Microsoft.Playwright;
using RewardTracker.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using System;
using System.Text.Json.Nodes;

namespace RewardTracker.Infrastructure.Services;

public class RewardAutomationService
{
    private readonly IServiceProvider _serviceProvider;

    public RewardAutomationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task RunTestBrowserAsync()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
        var page = await browser.NewPageAsync();
        await page.GotoAsync("https://rewards.bing.com");
        await Task.Delay(15000);
    }

    public async Task StartLoginSessionAsync(int accountId)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await dbContext.Accounts.FindAsync(accountId);
        if (account == null) return;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        try 
        {
            await page.GotoAsync("https://login.live.com/");
            await Task.Delay(90000); 

            var sessionStateJson = await context.StorageStateAsync();
            account.SessionData = sessionStateJson;
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
        
        bool needsPcSearch = true;
        bool needsMobileSearch = true;

        Console.WriteLine("=== PROVERA STANJA POENA PREKO PRETRAŽIVAČA ===");
        
        var desktopOptions = new BrowserNewContextOptions { StorageState = account.SessionData };
        var desktopContext = await browser.NewContextAsync(desktopOptions);
        var desktopPage = await desktopContext.NewPageAsync();

        try
        {
            // Idemo direktno kroz pretraživač da zaobiđemo bot zaštitu API-ja
            var response = await desktopPage.GotoAsync("https://rewards.bing.com/api/getuserinfo?type=1");
            await Task.Delay(2000);
            
            // Izvlačimo sav tekst iz body taga gde browser renderuje JSON
            var jsonString = await desktopPage.EvaluateAsync<string>("() => document.body.innerText");
            Console.WriteLine("API Odgovor: " + (jsonString.Length > 200 ? jsonString.Substring(0, 200) : jsonString));

            var json = JsonNode.Parse(jsonString);
            var userStatus = json?["dashboard"]?["userStatus"];
            var counters = userStatus?["counters"];

            if (userStatus != null)
            {
                var ptsNode = userStatus["availablePoints"];
                if (ptsNode != null)
                {
                    int pts = (int)ptsNode;
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
                    Console.WriteLine("Uspesno azurirani poeni u bazi na: " + pts);
                }
            }

            if (counters != null)
            {
                var pcSearch = counters["pcSearch"]?[0];
                if (pcSearch != null)
                {
                    int pcProgress = (int)pcSearch["pointProgress"]!;
                    int pcMax = (int)pcSearch["pointProgressMax"]!;
                    needsPcSearch = pcProgress < pcMax;
                    Console.WriteLine($"PC Pretrage: {pcProgress}/{pcMax}");
                }

                var mobileSearch = counters["mobileSearch"]?[0];
                if (mobileSearch != null)
                {
                    int mobileProgress = (int)mobileSearch["pointProgress"]!;
                    int mobileMax = (int)mobileSearch["pointProgressMax"]!;
                    needsMobileSearch = mobileProgress < mobileMax;
                    Console.WriteLine($"Mobilne Pretrage: {mobileProgress}/{mobileMax}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Nisam uspeo da dekodiram stanje poena: " + ex.Message);
        }

        if (!needsPcSearch && !needsMobileSearch)
        {
            Console.WriteLine("Sve pretrage su završene za danas! Bot se gasi.");
            await desktopContext.CloseAsync();
            return;
        }

        var random = new Random();
        var baseWords = new[] { "Srbija", "Beograd", "Vesti", "Sport", "Filmovi", "Recepti", "Tehnologija", "Zanimljivosti", "Istorija", "Automobili", "Kompjuteri", "Muzika" };

        if (needsPcSearch)
        {
            Console.WriteLine("=== START: PC PRETRAGE ===");
            try 
            {
                for(int i = 0; i < 5; i++)
                {
                    var term = baseWords[random.Next(baseWords.Length)] + " " + random.Next(1000, 99999);
                    await desktopPage.GotoAsync("https://www.bing.com");
                    await desktopPage.FillAsync("[name='q']", term);
                    await desktopPage.PressAsync("[name='q']", "Enter");
                    await Task.Delay(random.Next(5000, 10000)); 
                }

                account.SessionData = await desktopContext.StorageStateAsync();
                dbContext.Accounts.Update(account);
                await dbContext.SaveChangesAsync();
            }
            catch (Exception ex) { Console.WriteLine("Greska PC: " + ex.Message); }
        }
        else
        {
            Console.WriteLine("Preskačem PC pretrage...");
        }
        await desktopContext.CloseAsync();

        if (needsMobileSearch)
        {
            Console.WriteLine("=== START: MOBILNE PRETRAGE ===");
            var mobileOptions = playwright.Devices["Pixel 5"];
            mobileOptions.StorageState = account.SessionData; 
            
            var mobileContext = await browser.NewContextAsync(mobileOptions);
            var mobilePage = await mobileContext.NewPageAsync();

            try 
            {
                for(int i = 0; i < 5; i++)
                {
                    var term = baseWords[random.Next(baseWords.Length)] + " " + random.Next(1000, 99999);
                    await mobilePage.GotoAsync("https://www.bing.com");
                    await mobilePage.FillAsync("[name='q']", term);
                    await mobilePage.PressAsync("[name='q']", "Enter");
                    await Task.Delay(random.Next(5000, 10000));
                }
            }
            catch (Exception ex) { Console.WriteLine("Greska Mobilni: " + ex.Message); }
            finally { await mobileContext.CloseAsync(); }
        }
        else
        {
            Console.WriteLine("Preskačem Mobilne pretrage...");
        }
    }
}
