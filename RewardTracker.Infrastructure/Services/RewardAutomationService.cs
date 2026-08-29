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
        
        bool needsPcSearch = true;
        bool needsMobileSearch = true;

        Console.WriteLine("=== PROVERA STANJA POENA ===");
        
        // Gađamo Bing Rewards API da proverimo trenutno stanje
        var apiContext = await playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions { StorageState = account.SessionData });
        var response = await apiContext.GetAsync("https://rewards.bing.com/api/getuserinfo?type=1");
        
        if (response.Ok)
        {
            try 
            {
                var jsonString = await response.TextAsync();
                var json = JsonNode.Parse(jsonString);
                
                var counters = json?["dashboard"]?["userStatus"]?["counters"];
                if (counters != null)
                {
                    // PC Pretrage
                    var pcSearch = counters["pcSearch"]?[0];
                    if (pcSearch != null)
                    {
                        int pcProgress = (int)pcSearch["pointProgress"]!;
                        int pcMax = (int)pcSearch["pointProgressMax"]!;
                        needsPcSearch = pcProgress < pcMax;
                        Console.WriteLine($"PC Pretrage: {pcProgress}/{pcMax}");
                    }

                    // Mobilne pretrage
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
                Console.WriteLine("Nisam uspeo da dekodiram stanje poena, pokrećem za svaki slučaj: " + ex.Message);
            }
        }
        else
        {
            Console.WriteLine("Nisam uspeo da dohvatim API podatke. Krećem sa pretragama naslepo.");
        }

        // Ako su obe stvari završene, možemo odmah da izađemo
        if (!needsPcSearch && !needsMobileSearch)
        {
            Console.WriteLine("Sve pretrage su završene za danas! Bot se gasi bez otvaranja pretraživača.");
            return;
        }

        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
        var random = new Random();
        var baseWords = new[] { "Srbija", "Beograd", "Vesti", "Sport", "Filmovi", "Recepti", "Tehnologija", "Zanimljivosti", "Istorija", "Automobili", "Kompjuteri", "Muzika" };

        if (needsPcSearch)
        {
            Console.WriteLine("=== START: PC PRETRAGE ===");
            var desktopOptions = new BrowserNewContextOptions { StorageState = account.SessionData };
            var desktopContext = await browser.NewContextAsync(desktopOptions);
            var desktopPage = await desktopContext.NewPageAsync();

            try 
            {
                for(int i = 0; i < 5; i++) // Test
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
            finally { await desktopContext.CloseAsync(); }
        }
        else
        {
            Console.WriteLine("Preskačem PC pretrage...");
        }

        if (needsMobileSearch)
        {
            Console.WriteLine("=== START: MOBILNE PRETRAGE ===");
            var mobileOptions = playwright.Devices["Pixel 5"];
            mobileOptions.StorageState = account.SessionData; 
            
            var mobileContext = await browser.NewContextAsync(mobileOptions);
            var mobilePage = await mobileContext.NewPageAsync();

            try 
            {
                for(int i = 0; i < 5; i++) // Test
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
