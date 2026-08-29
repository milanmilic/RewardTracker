using Microsoft.Playwright;
using RewardTracker.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using System;
using System.Text.RegularExpressions;

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
        var baseWords = new[] { "Srbija", "Beograd", "Vesti", "Sport", "Filmovi", "Recepti", "Tehnologija", "Zanimljivosti", "Istorija", "Automobili", "Kompjuteri", "Muzika" };

        Console.WriteLine("=== START: PC PRETRAGE ===");
        var desktopOptions = new BrowserNewContextOptions { StorageState = account.SessionData };
        var desktopContext = await browser.NewContextAsync(desktopOptions);
        var desktopPage = await desktopContext.NewPageAsync();

        try 
        {
            for(int i = 0; i < 3; i++) // Test
            {
                var term = baseWords[random.Next(baseWords.Length)] + " " + random.Next(1000, 99999);
                await desktopPage.GotoAsync("https://www.bing.com");
                await desktopPage.FillAsync("[name='q']", term);
                await desktopPage.PressAsync("[name='q']", "Enter");
                await Task.Delay(random.Next(3000, 6000)); 
            }
            account.SessionData = await desktopContext.StorageStateAsync();
            dbContext.Accounts.Update(account);
            await dbContext.SaveChangesAsync();
        }
        catch (Exception ex) { Console.WriteLine("Greska PC: " + ex.Message); }
        finally { await desktopContext.CloseAsync(); } // OVO JE BILO ZABORAVLJENO!

        Console.WriteLine("=== START: MOBILNE PRETRAGE ===");
        var mobileOptions = playwright.Devices["Pixel 5"];
        mobileOptions.StorageState = account.SessionData; 
        
        var mobileContext = await browser.NewContextAsync(mobileOptions);
        var mobilePage = await mobileContext.NewPageAsync();

        try 
        {
            for(int i = 0; i < 3; i++)
            {
                var term = baseWords[random.Next(baseWords.Length)] + " " + random.Next(1000, 99999);
                await mobilePage.GotoAsync("https://www.bing.com");
                await mobilePage.FillAsync("[name='q']", term);
                await mobilePage.PressAsync("[name='q']", "Enter");
                await Task.Delay(random.Next(3000, 6000));
            }
        }
        catch (Exception ex) { Console.WriteLine("Greska Mobilni: " + ex.Message); }
        finally { await mobileContext.CloseAsync(); }

        Console.WriteLine("=== CITANJE UKUPNIH POENA ===");
        // Otvaramo novi prozor cisto za proveru poena na kraju
        var finalContext = await browser.NewContextAsync(new BrowserNewContextOptions { StorageState = account.SessionData });
        var finalPage = await finalContext.NewPageAsync();
        try
        {
            // Idemo direktno na Rewards Dashboard!
            await finalPage.GotoAsync("https://rewards.bing.com");
            await Task.Delay(5000); 

            // Koristimo JavaScript injekciju da procitamo poene direktno iz memorije browsera (najpouzdaniji metod ikada!)
            int pts = await finalPage.EvaluateAsync<int>(@"
                () => {
                    try {
                        return window.dashboard.userStatus.availablePoints;
                    } catch(e) {
                        return -1;
                    }
                }
            ");

            if (pts > 0)
            {
                Console.WriteLine("Pronadjen tacan broj poena preko JS-a: " + pts);
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
            else
            {
                Console.WriteLine("Nisam uspeo da ucitam window.dashboard varijablu.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Greska pri citanju poena: " + ex.Message);
        }
        finally
        {
            await finalContext.CloseAsync();
        }
    }
}
