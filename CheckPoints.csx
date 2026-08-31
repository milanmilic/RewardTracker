using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using RewardTracker.Infrastructure.Data;

var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
optionsBuilder.UseNpgsql("Host=localhost;Database=reward_tracker;Username=postgres;Password=admin");

using var dbContext = new AppDbContext(optionsBuilder.Options);
var accounts = dbContext.Accounts.Include(a => a.RewardSite).ToList();

Console.WriteLine("--- STANJE RACUNA ---");
foreach (var a in accounts)
{
    Console.WriteLine($"Sajt: {a.RewardSite?.Name}, User: {a.Username}, Poeni: {a.CurrentPoints}");
}
var logs = dbContext.PointLogs.OrderByDescending(l => l.Date).Take(5).ToList();
Console.WriteLine("--- ZADNJI LOGOVI ---");
foreach (var l in logs)
{
    Console.WriteLine($"Acc: {l.AccountId}, Total: {l.TotalPointsAfter}, Earned: {l.PointsEarned}");
}
