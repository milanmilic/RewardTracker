using System;

namespace RewardTracker.Core.Entities;

public class PointLog
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    
    public DateTime Date { get; set; }
    public int PointsEarned { get; set; }
    public int TotalPointsAfter { get; set; }
}
