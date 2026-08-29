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
    public async Task RunDailyTasksAsync(int accountId)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var account = await dbContext.Accounts.FindAsync(accountId);
        if (account == null || string.IsNullOrEmpty(account.SessionData)) 
        {
            Console.WriteLine("Nalog ne postoji ili nema sacuvanu sesiju.");
            return;
        }

        using var playwright = await Playwright.CreateAsync();
        
        // Za probu ostavljamo Headless = false da bi ti video kako bot radi sam!
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });

        // Najbitniji deo: Ubacujemo TVOJE kolačiće iz baze pre nego što otvorimo prozor!
        var contextOptions = new BrowserNewContextOptions
        {
            StorageState = account.SessionData
        };
        var context = await browser.NewContextAsync(contextOptions);
        var page = await context.NewPageAsync();

        try 
        {
            await page.GotoAsync("https://www.bing.com");
            
            // Cekamo par sekundi da se ucita
            await Task.Delay(3000);

            // Radimo 3 random pretrage da bi dobili poene (simuliramo rad)
            var pretrage = new[] { "Vremenska prognoza", "Najbolji restorani", "Kako radi Playwright" };

            foreach(var termin in pretrage)
            {
                // Nalazimo polje za pretragu (name='q') i kucamo tekst
                await page.FillAsync("[name='q']", termin);
                await page.PressAsync("[name='q']", "Enter");
                
                // Čekamo da mreža prestane da učitava elemente
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                
                // Pravimo se da čitamo rezultate 5 sekundi (zbog anti-bot zaštite)
                await Task.Delay(5000); 
                
                // Vraćamo se na početnu
                await page.GotoAsync("https://www.bing.com");
                await Task.Delay(2000);
            }

            // Opciono: Azuriramo SessionData jer Microsoft nekad osveži kolačiće
            var newSessionState = await context.StorageStateAsync();
            account.SessionData = newSessionState;
            dbContext.Accounts.Update(account);
            
            await dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Greska pri botovanju: " + ex.Message);
        }
        finally
        {
            await browser.CloseAsync();
        }
    }
}
