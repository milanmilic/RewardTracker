using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RewardTracker.Core.Entities;
using RewardTracker.Infrastructure.Data;

namespace RewardTracker.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RewardSitesController : ControllerBase
{
    private readonly AppDbContext _context;

    public RewardSitesController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<RewardSite>>> GetSites()
    {
        return await _context.RewardSites.ToListAsync();
    }

    [HttpPost]
    public async Task<ActionResult<RewardSite>> CreateSite(RewardSite site)
    {
        _context.RewardSites.Add(site);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetSites), new { id = site.Id }, site);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteSite(int id)
    {
        var site = await _context.RewardSites.FindAsync(id);
        if (site == null) return NotFound();

        _context.RewardSites.Remove(site);
        await _context.SaveChangesAsync();

        return NoContent();
    }
}
