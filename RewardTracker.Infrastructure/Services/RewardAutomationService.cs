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
        // Koristimo ServiceProvider da bezbedno izvučemo bazu u Hangfire pozadinskom poslu
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var account = await dbContext.Accounts.FindAsync(accountId);
        if (account == null) return;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions 
        { 
            Headless = false // Korisnik mora da vidi ekran
        });

        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        try 
        {
            // Idemo na stranicu za prijavu
            await page.GotoAsync("https://login.live.com/");

            // Dajemo korisniku 90 sekundi da se polako uloguje, unese kod iz SMS-a itd.
            // Čekamo fiksno kako bi bili 100% sigurni da je Microsoft upisao sve kolačiće nakon logovanja
            await Task.Delay(90000); 

            // Izvlačimo sve kolačiće i localStorage
            var sessionStateJson = await context.StorageStateAsync();
            
            // Čuvamo u bazu
            account.SessionData = sessionStateJson;
            dbContext.Accounts.Update(account);
            await dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Greska pri cuvanju sesije: " + ex.Message);
        }
        finally
        {
            await browser.CloseAsync();
        }
    }
}
