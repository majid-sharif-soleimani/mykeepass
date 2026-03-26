namespace mykeepass.Models;

public sealed class AccountConfig
{
    public string Name  { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public List<DatabaseConfig> Databases { get; set; } = new();
}
