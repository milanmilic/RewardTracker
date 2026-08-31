using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using RewardTracker.Infrastructure.Data;

var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
optionsBuilder.UseNpgsql("Host=localhost;Database=reward_tracker;Username=postgres;Password=admin");

using var dbContext = new AppDbContext(optionsBuilder.Options);
var accounts = dbContext.Accounts.Include(a => a.RewardSite).ToList();

Console.WriteLine("--- NALOZI U BAZI ---");
foreach (var a in accounts)
{
    Console.WriteLine($"ID: {a.Id}, Sajt: {a.RewardSite?.Name}, Username: {a.Username}, Ima Sesiju: {a.SessionData != null}");
}
