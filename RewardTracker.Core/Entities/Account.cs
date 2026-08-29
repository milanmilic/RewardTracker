namespace RewardTracker.Core.Entities;

public class Account
{
    public int Id { get; set; }
    public int RewardSiteId { get; set; }
    public RewardSite? RewardSite { get; set; }
    
    public string Username { get; set; } = string.Empty;
    
    // Ovde ćemo čuvati kolačiće (cookies) kako ne bismo čuvali lozinke
    public string? SessionData { get; set; } 
    
    public int CurrentPoints { get; set; }
    public bool IsActive { get; set; } = true;
    
    // Navigacioni property
    public ICollection<PointLog> PointLogs { get; set; } = new List<PointLog>();
}
