namespace Source.Features.CiPublish.Models;

public record CiRegistryCredentialsDto
{
    public required string RegistryHost { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
}
