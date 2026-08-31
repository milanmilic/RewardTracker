using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RewardTracker.Core.Entities;
using RewardTracker.Infrastructure.Data;
using Hangfire;

namespace RewardTracker.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AccountsController : ControllerBase
{
    private readonly AppDbContext _context;

    public AccountsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet("site/{siteId}")]
    public async Task<ActionResult<IEnumerable<Account>>> GetAccountsBySite(int siteId)
    {
        return await _context.Accounts.Where(a => a.RewardSiteId == siteId).ToListAsync();
    }

    [HttpPost]
    public async Task<ActionResult<Account>> CreateAccount(Account account)
    {
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetAccountsBySite), new { siteId = account.RewardSiteId }, account);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteAccount(int id)
    {
        var account = await _context.Accounts.FindAsync(id);
        if (account == null) return NotFound();

        _context.Accounts.Remove(account);
        await _context.SaveChangesAsync();

        return NoContent();
    }

        [HttpGet("{id}/logs")]
    public async Task<ActionResult<IEnumerable<PointLog>>> GetPointLogs(int id)
    {
        return await _context.PointLogs
            .Where(l => l.AccountId == id)
            .OrderBy(l => l.Date)
            .ToListAsync();
    }

    // NOVA RUTA ZA LOGOVANJE
    [HttpPost("{id}/login")]
    public IActionResult StartLogin(int id, [FromServices] IBackgroundJobClient backgroundJobs)
    {
        backgroundJobs.Enqueue<RewardTracker.Infrastructure.Services.RewardAutomationService>(s => s.StartLoginSessionAsync(id));
        return Ok();
    }
    [HttpPost("{id}/run")]
    public IActionResult RunTasks(int id, [FromServices] IBackgroundJobClient backgroundJobs)
    {
        backgroundJobs.Enqueue<RewardTracker.Infrastructure.Services.RewardAutomationService>(s => s.RunDailyTasksAsync(id));
        return Ok();
    }
}


