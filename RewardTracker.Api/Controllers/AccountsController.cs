using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RewardTracker.Core.Dtos;
using RewardTracker.Core.Entities;
using RewardTracker.Infrastructure.Data;
using RewardTracker.Infrastructure.Services;
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
    public async Task<ActionResult<IEnumerable<AccountDto>>> GetAccountsBySite(int siteId)
    {
        return await _context.Accounts
            .Where(a => a.RewardSiteId == siteId)
            .Select(a => new AccountDto
            {
                Id = a.Id,
                RewardSiteId = a.RewardSiteId,
                Username = a.Username,
                CurrentPoints = a.CurrentPoints,
                IsActive = a.IsActive,
                HasSession = a.SessionData != null && a.SessionData != ""
            })
            .ToListAsync();
    }

    [HttpPost]
    public async Task<ActionResult<AccountDto>> CreateAccount(CreateAccountRequest request)
    {
        var account = new Account
        {
            RewardSiteId = request.RewardSiteId,
            Username = request.Username,
            IsActive = request.IsActive
        };

        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        var dto = new AccountDto
        {
            Id = account.Id,
            RewardSiteId = account.RewardSiteId,
            Username = account.Username,
            CurrentPoints = account.CurrentPoints,
            IsActive = account.IsActive,
            HasSession = false
        };

        return CreatedAtAction(nameof(GetAccountsBySite), new { siteId = account.RewardSiteId }, dto);
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

    [HttpPost("{id}/login")]
    public IActionResult StartLogin(int id, [FromServices] IBackgroundJobClient backgroundJobs)
    {
        backgroundJobs.Enqueue<RewardAutomationService>(s => s.StartLoginSessionAsync(id));
        return Ok();
    }

    [HttpPost("{id}/scan")]
    public IActionResult ScanSite(int id, [FromServices] IBackgroundJobClient backgroundJobs)
    {
        backgroundJobs.Enqueue<RewardAutomationService>(s => s.ScanSiteDOMAsync(id));
        return Ok();
    }

    [HttpPost("{id}/run")]
    public IActionResult RunTasks(int id, [FromServices] IBackgroundJobClient backgroundJobs)
    {
        backgroundJobs.Enqueue<RewardAutomationService>(s => s.RunDailyTasksAsync(id));
        return Ok();
    }
}
