namespace RewardTracker.Core.Entities;

public class RewardSite
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Url { get; set; }
    public bool IsActive { get; set; } = true;
    
    // Navigacioni property
    public ICollection<Account> Accounts { get; set; } = new List<Account>();
}
