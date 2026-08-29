using Microsoft.Playwright;
using RewardTracker.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using System;

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
        catch (Exception ex)
        {
            Console.WriteLine("Greska: " + ex.Message);
        }
        finally
        {
            await browser.CloseAsync();
        }
    }

    public async Task RunDailyTasksAsync(int accountId)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var account = await dbContext.Accounts.FindAsync(accountId);
        if (account == null || string.IsNullOrEmpty(account.SessionData)) 
        {
            return;
        }

        using var playwright = await Playwright.CreateAsync();
        // Za sada je Headless = false da bi pratio šta se dešava
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });

        var random = new Random();
        // Neki osnovni pojmovi. Dodavaćemo brojeve na njih da uvek budu unikatni.
        var baseWords = new[] { "Srbija", "Beograd", "Vesti", "Sport", "Filmovi", "Recepti", "Tehnologija", "Zanimljivosti", "Istorija", "Automobili", "Kompjuteri", "Muzika" };

        Console.WriteLine("=== START: PC PRETRAGE ===");
        
        var desktopOptions = new BrowserNewContextOptions { StorageState = account.SessionData };
        var desktopContext = await browser.NewContextAsync(desktopOptions);
        var desktopPage = await desktopContext.NewPageAsync();

        try 
        {
            // Odradićemo 10 pretraga za test (pravi bot radi oko 30-35 za maksimum PC poena)
            for(int i = 0; i < 10; i++)
            {
                var term = baseWords[random.Next(baseWords.Length)] + " " + random.Next(1000, 99999);
                await desktopPage.GotoAsync("https://www.bing.com");
                await desktopPage.FillAsync("[name='q']", term);
                await desktopPage.PressAsync("[name='q']", "Enter");
                
                // Veoma važno: nasumična pauza između 5 i 12 sekundi (simulira ljudsko ponašanje)
                await Task.Delay(random.Next(5000, 12000)); 
            }

            // Opciono azuriranje kolacica u slucaju da ih je Microsoft osvezio
            account.SessionData = await desktopContext.StorageStateAsync();
            dbContext.Accounts.Update(account);
            await dbContext.SaveChangesAsync();
        }
        catch (Exception ex) { Console.WriteLine("Greska PC: " + ex.Message); }
        finally { await desktopContext.CloseAsync(); }

        Console.WriteLine("=== START: MOBILNE PRETRAGE ===");
        
        // Magija: Playwright sada glumi mobilni telefon (Pixel 5)
        var mobileOptions = playwright.Devices["Pixel 5"];
        mobileOptions.StorageState = account.SessionData; // Ucitava iste tvoje kolacice!
        
        var mobileContext = await browser.NewContextAsync(mobileOptions);
        var mobilePage = await mobileContext.NewPageAsync();

        try 
        {
            // Odradićemo 5 pretraga za test (pravi bot radi oko 20 za max mobilnih poena)
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
}
