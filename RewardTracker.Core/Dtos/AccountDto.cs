namespace RewardTracker.Core.Dtos;

/// <summary>
/// Prikaz naloga bezbedan za slanje klijentu. Namerno ne sadrzi SessionData
/// (kolacici = pun pristup nalogu), vec samo informaciju da li sesija postoji.
/// </summary>
public class AccountDto
{
    public int Id { get; set; }
    public int RewardSiteId { get; set; }
    public string Username { get; set; } = string.Empty;
    public int CurrentPoints { get; set; }
    public bool IsActive { get; set; }
    public bool HasSession { get; set; }
}

public class CreateAccountRequest
{
    public int RewardSiteId { get; set; }
    public string Username { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
