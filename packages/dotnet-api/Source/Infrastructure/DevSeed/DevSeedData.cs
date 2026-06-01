namespace Source.Infrastructure.DevSeed;

/// <summary>
/// Static seed data definitions for development environment.
/// Provides deterministic test data for E2E testing.
/// </summary>
public static class DevSeedData
{
    /// <summary>
    /// Test users with hardcoded OTPs and passwords for E2E testing
    /// </summary>
    public static readonly TestUserDefinition[] TestUsers =
    [
        new TestUserDefinition
        {
            Email = "admin@test.com",
            FirstName = "Admin",
            LastName = "User",
            OtpCode = "111111",
            Password = "Test123!",
            IsSuperAdmin = true
        },
        new TestUserDefinition
        {
            Email = "user@test.com",
            FirstName = "Test",
            LastName = "User",
            OtpCode = "222222",
            Password = "Test123!",
            IsSuperAdmin = false
        },
        new TestUserDefinition
        {
            Email = "test@test.com",
            FirstName = "Test",
            LastName = "User",
            OtpCode = "123456",
            Password = "Test123!",
            IsSuperAdmin = true
        }
    ];

    /// <summary>
    /// Curated V1 starters seeded into the global <c>ProjectTemplates</c> catalogue.
    /// Each row is identified by its <see cref="StarterDefinition.Slug"/> — the
    /// seeder is idempotent against that column, so re-runs only insert missing
    /// rows and never touch existing data (admins can rename / re-icon / archive
    /// without their changes being clobbered on next boot).
    ///
    /// <para>Placeholder GitHub repos under <c>glenncode-starters</c> are
    /// intentional for V1: real template repos do not yet exist. Create-project
    /// against these will surface a generic GitHub error, which is acceptable
    /// per the starters spec (Scene 4 — broken-template flagging is the admin's
    /// responsibility post-launch).</para>
    ///
    /// <para>The React + Vite + TS starter ships with an inline V3 runtime spec
    /// matching <c>RuntimeSpecV3</c>'s schema (validated at seed time by
    /// <c>DevSeedService</c>). Empty and Rails 8 carry <see cref="StarterDefinition.RuntimeSpec"/>
    /// of <c>null</c> — the runtime then boots with the default/empty spec,
    /// which is correct for both (Empty has nothing to install, Rails 8 V1 has
    /// the user run <c>rails s</c> manually).</para>
    /// </summary>
    public static readonly StarterDefinition[] Starters =
    [
        new StarterDefinition
        {
            Slug = "empty",
            Name = "Empty project",
            Description = "Start from scratch \u2014 bring your own code",
            IconKey = "package",
            SourceRepoOwner = "glenncode-starters",
            SourceRepoName = "empty",
            RuntimeSpec = null,
            IsActive = true,
            IsDefault = true,
            SortOrder = 0,
        },
        new StarterDefinition
        {
            Slug = "react-vite-ts",
            Name = "React + Vite + TypeScript",
            Description = "Modern React app with Vite dev server",
            IconKey = "code",
            SourceRepoOwner = "glenncode-starters",
            SourceRepoName = "react-vite-ts",
            // Minimal valid V3 spec — references the built-in `node-vite`
            // preset, which encapsulates install (npm install via mise) +
            // setup + command + healthcheck. Project path defaults to the
            // repo root; Vite's host/port keep their preset defaults so the
            // runtime preview proxy can reach 5173.
            RuntimeSpec = """
                {
                  "version": 3,
                  "services": [
                    {
                      "kind": "node-vite",
                      "name": "dev",
                      "values": {
                        "project": "."
                      }
                    }
                  ]
                }
                """,
            IsActive = true,
            IsDefault = false,
            SortOrder = 10,
        },
        new StarterDefinition
        {
            Slug = "rails-8",
            Name = "Rails 8",
            Description = "Ruby on Rails 8 starter (run rails s manually)",
            IconKey = "code",
            SourceRepoOwner = "glenncode-starters",
            SourceRepoName = "rails-8",
            // No auto-boot in V1 per spec — the user runs `rails s` manually
            // until a curated Rails runtime spec lands.
            RuntimeSpec = null,
            IsActive = true,
            IsDefault = false,
            SortOrder = 20,
        },
    ];
}

public record TestUserDefinition
{
    public required string Email { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public required string OtpCode { get; init; }
    public required string Password { get; init; }
    public required bool IsSuperAdmin { get; init; }
}

/// <summary>
/// Definition of a single seeded <c>ProjectTemplate</c> (Starter) row. Mirrors
/// the entity's column shape; the seeder maps each definition into an entity
/// instance on first run.
/// </summary>
public record StarterDefinition
{
    /// <summary>URL-safe globally-unique identifier. Used as the seeder's idempotency key.</summary>
    public required string Slug { get; init; }

    /// <summary>Human-friendly display name shown in the picker.</summary>
    public required string Name { get; init; }

    /// <summary>Optional one-line description shown beneath the name.</summary>
    public string? Description { get; init; }

    /// <summary>Optional icon key the frontend maps to a concrete component.</summary>
    public string? IconKey { get; init; }

    /// <summary>GitHub owner login (user or org) of the source template repo.</summary>
    public required string SourceRepoOwner { get; init; }

    /// <summary>GitHub repo name of the source template repo.</summary>
    public required string SourceRepoName { get; init; }

    /// <summary>
    /// Optional inline V3 runtime-spec JSON. <c>null</c> = "no runtime recipe";
    /// the runtime boots with the default/empty spec.
    /// </summary>
    public string? RuntimeSpec { get; init; }

    /// <summary>Whether the starter shows up in the user-facing picker.</summary>
    public bool IsActive { get; init; } = true;

    /// <summary>Whether this is the pre-selected starter on the new-project screen.</summary>
    public bool IsDefault { get; init; } = false;

    /// <summary>Display order in the picker — lower sorts first.</summary>
    public int SortOrder { get; init; } = 0;
}
