using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Source.Features.Waitlist.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Waitlist.Commands;

/// <summary>
/// Adds an email to the public waitlist. Anonymous + hostile input: the handler
/// validates the email, normalizes it, and is idempotent — a repeat address
/// returns success silently (we never reveal whether an email was already on the
/// list). The empty-email rejection here is the exact validation the landing-page
/// "movie" dramatizes the agent adding.
/// </summary>
public record CreateWaitlistSignupCommand : ICommand<Result<CreateWaitlistSignupResponse>>
{
    public string Email { get; init; } = string.Empty;
    public string? Source { get; init; }
    public string? Note { get; init; }
    public string? UserAgent { get; init; }
    public string? Referrer { get; init; }
}

public record CreateWaitlistSignupResponse
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required DateTime CreatedAt { get; init; }
}

public class CreateWaitlistSignupCommandHandler
    : ICommandHandler<CreateWaitlistSignupCommand, Result<CreateWaitlistSignupResponse>>
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<CreateWaitlistSignupCommandHandler> _logger;

    public CreateWaitlistSignupCommandHandler(
        ApplicationDbContext context,
        ILogger<CreateWaitlistSignupCommandHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Result<CreateWaitlistSignupResponse>> Handle(
        CreateWaitlistSignupCommand request,
        CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(email))
            return Result.Failure<CreateWaitlistSignupResponse>("Email is required");

        if (!IsValidEmail(email))
            return Result.Failure<CreateWaitlistSignupResponse>("Please enter a valid email address");

        var normalized = email.ToLowerInvariant();

        // Idempotent: if the address is already on the list, return its existing row
        // as a success. Silent dedupe — the response is identical to a fresh signup,
        // so a visitor never learns whether an email was previously registered.
        var existing = await _context.WaitlistSignups
            .FirstOrDefaultAsync(x => x.Email == normalized, cancellationToken);

        if (existing is not null)
        {
            return Result.Success(new CreateWaitlistSignupResponse
            {
                Id = existing.Id,
                Email = existing.Email,
                CreatedAt = existing.CreatedAt,
            });
        }

        var entity = new WaitlistSignup
        {
            Id = Guid.NewGuid(),
            Email = normalized,
            Source = Truncate(request.Source?.Trim(), 50),
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : Truncate(request.Note.Trim(), 500),
            UserAgent = Truncate(request.UserAgent?.Trim(), 500),
            Referrer = Truncate(request.Referrer?.Trim(), 500),
        };

        _context.WaitlistSignups.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Waitlist signup created: {Id} (source={Source})", entity.Id, entity.Source);

        return Result.Success(new CreateWaitlistSignupResponse
        {
            Id = entity.Id,
            Email = entity.Email,
            CreatedAt = entity.CreatedAt,
        });
    }

    private static bool IsValidEmail(string email)
    {
        // MailAddress is stricter and cheaper than a regex zoo, and rejects the
        // common garbage ("foo", "foo@", "@bar"). The Address round-trip guards
        // against MailAddress's lenient display-name parsing ("a <b@c>").
        try
        {
            var addr = new MailAddress(email);
            return addr.Address == email;
        }
        catch
        {
            return false;
        }
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
