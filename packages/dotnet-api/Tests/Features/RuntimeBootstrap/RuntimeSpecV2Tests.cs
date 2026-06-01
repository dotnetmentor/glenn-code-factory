using System.Text.Json;
using Source.Features.RuntimeBootstrap.Contracts;

namespace Api.Tests.Features.RuntimeBootstrap;

/// <summary>
/// Roundtrip + validation coverage for <see cref="RuntimeSpecV2"/>. The contract
/// is the source of truth for the jsonb body stored on <c>ProjectRuntime.Spec</c>
/// and the wire shape the daemon will consume, so its serialisation invariants
/// (camelCase, optional fields omitted when null, required service fields)
/// have to be locked down at the unit level — the rest of the slice trusts
/// these without re-checking.
/// </summary>
public class RuntimeSpecV2Tests
{
    [Fact]
    public void Roundtrip_MinimalSpec_PreservesVersion()
    {
        var spec = new RuntimeSpecV2();

        var json = spec.ToJson();
        var parsed = RuntimeSpecV2.TryParse(json);

        parsed.IsSuccess.Should().BeTrue(parsed.Error);
        parsed.Value.Version.Should().Be(2);
        parsed.Value.Install.Should().BeNull();
        parsed.Value.Services.Should().BeNull();
        parsed.Value.Setup.Should().BeNull();
    }

    [Fact]
    public void Roundtrip_FullSpec_PreservesAllFields()
    {
        var spec = new RuntimeSpecV2
        {
            Install = "apt-get install -y mongodb-org",
            Setup = "npm install && npm run migrate",
            Services = new List<ServiceSpec>
            {
                new()
                {
                    Name = "mongodb",
                    Command = "mongod --dbpath /data/db",
                    User = "agent",
                    Autorestart = true,
                    Env = new Dictionary<string, string>
                    {
                        ["MONGO_INITDB_DATABASE"] = "app",
                    },
                    Healthcheck = new HealthcheckSpec
                    {
                        Command = "mongosh --eval 'db.adminCommand({ping:1})'",
                        IntervalSeconds = 10,
                    },
                    Install = "mkdir -p /data/db",
                },
                new()
                {
                    Name = "postgres",
                    Command = "postgres -D /var/lib/postgresql/data",
                },
            },
        };

        var json = spec.ToJson();
        var parsed = RuntimeSpecV2.TryParse(json);

        parsed.IsSuccess.Should().BeTrue(parsed.Error);
        var roundtripped = parsed.Value;
        roundtripped.Version.Should().Be(2);
        roundtripped.Install.Should().Be(spec.Install);
        roundtripped.Setup.Should().Be(spec.Setup);
        roundtripped.Services.Should().HaveCount(2);

        var mongo = roundtripped.Services!.Single(s => s.Name == "mongodb");
        mongo.Command.Should().Be("mongod --dbpath /data/db");
        mongo.User.Should().Be("agent");
        mongo.Autorestart.Should().Be(true);
        mongo.Env.Should().ContainKey("MONGO_INITDB_DATABASE").WhoseValue.Should().Be("app");
        mongo.Healthcheck.Should().NotBeNull();
        mongo.Healthcheck!.Command.Should().Be("mongosh --eval 'db.adminCommand({ping:1})'");
        mongo.Healthcheck.IntervalSeconds.Should().Be(10);
        mongo.Install.Should().Be("mkdir -p /data/db");

        var pg = roundtripped.Services.Single(s => s.Name == "postgres");
        pg.Command.Should().Be("postgres -D /var/lib/postgresql/data");
        pg.User.Should().BeNull();
        pg.Autorestart.Should().BeNull();
        pg.Env.Should().BeNull();
        pg.Healthcheck.Should().BeNull();
        pg.Install.Should().BeNull();
    }

    [Fact]
    public void Serialise_UsesCamelCase()
    {
        var spec = new RuntimeSpecV2
        {
            Install = "x",
            Services = new List<ServiceSpec>
            {
                new()
                {
                    Name = "svc",
                    Command = "cmd",
                    Healthcheck = new HealthcheckSpec { Command = "ping", IntervalSeconds = 1 },
                },
            },
        };

        var json = spec.ToJson();

        json.Should().Contain("\"version\":2");
        json.Should().Contain("\"install\":");
        json.Should().Contain("\"services\":");
        json.Should().Contain("\"intervalSeconds\":1");
        // Pascal-cased property names would break the daemon's TypeScript shape.
        json.Should().NotContain("\"Version\":");
        json.Should().NotContain("\"IntervalSeconds\":");
    }

    [Fact]
    public void Serialise_OmitsNullOptionalFields()
    {
        var spec = new RuntimeSpecV2();

        var json = spec.ToJson();

        // Only `version` should be emitted; everything else is null and the
        // DefaultIgnoreCondition=WhenWritingNull config drops it.
        json.Should().NotContain("\"install\"");
        json.Should().NotContain("\"services\"");
        json.Should().NotContain("\"setup\"");
    }

    [Fact]
    public void Parse_AcceptsCamelCaseFromExternalSource()
    {
        const string json = """
            {
              "version": 2,
              "install": "echo install",
              "services": [
                { "name": "redis", "command": "redis-server" }
              ],
              "setup": "echo setup"
            }
            """;

        var parsed = RuntimeSpecV2.TryParse(json);

        parsed.IsSuccess.Should().BeTrue(parsed.Error);
        parsed.Value.Install.Should().Be("echo install");
        parsed.Value.Setup.Should().Be("echo setup");
        parsed.Value.Services.Should().ContainSingle()
            .Which.Name.Should().Be("redis");
    }

    [Fact]
    public void Parse_NullOrWhitespace_ReturnsFailure()
    {
        RuntimeSpecV2.TryParse(null).IsFailure.Should().BeTrue();
        RuntimeSpecV2.TryParse("").IsFailure.Should().BeTrue();
        RuntimeSpecV2.TryParse("   ").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsFailure()
    {
        var result = RuntimeSpecV2.TryParse("{ not valid json");
        result.IsFailure.Should().BeTrue();
        result.Error.Should().StartWith("spec_malformed");
    }

    [Fact]
    public void Validate_NoServices_Succeeds()
    {
        var spec = new RuntimeSpecV2 { Install = "echo hi", Setup = "echo bye" };
        spec.Validate().IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyServiceList_Succeeds()
    {
        var spec = new RuntimeSpecV2 { Services = new List<ServiceSpec>() };
        spec.Validate().IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_ValidServices_Succeeds()
    {
        var spec = new RuntimeSpecV2
        {
            Services = new List<ServiceSpec>
            {
                new() { Name = "a", Command = "cmd-a" },
                new() { Name = "b", Command = "cmd-b" },
            },
        };
        spec.Validate().IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyServiceName_Fails()
    {
        var spec = new RuntimeSpecV2
        {
            Services = new List<ServiceSpec>
            {
                new() { Name = "", Command = "cmd" },
            },
        };
        var result = spec.Validate();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("service_name_required");
    }

    [Fact]
    public void Validate_WhitespaceServiceName_Fails()
    {
        var spec = new RuntimeSpecV2
        {
            Services = new List<ServiceSpec>
            {
                new() { Name = "   ", Command = "cmd" },
            },
        };
        var result = spec.Validate();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("service_name_required");
    }

    [Fact]
    public void Validate_EmptyCommand_Fails()
    {
        var spec = new RuntimeSpecV2
        {
            Services = new List<ServiceSpec>
            {
                new() { Name = "svc", Command = "" },
            },
        };
        var result = spec.Validate();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("service_command_required: svc");
    }

    [Fact]
    public void Validate_DuplicateServiceNames_Fails()
    {
        var spec = new RuntimeSpecV2
        {
            Services = new List<ServiceSpec>
            {
                new() { Name = "redis", Command = "redis-server --port 6379" },
                new() { Name = "redis", Command = "redis-server --port 6380" },
            },
        };
        var result = spec.Validate();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("service_name_duplicate: redis");
    }

    [Fact]
    public void Validate_NamesCaseSensitive_AllowsDifferentCasing()
    {
        // Supervisord program names are case-sensitive identifiers. Two
        // services that only differ by case are technically allowed by
        // supervisord; we mirror that. (Bad practice but not invalid.)
        var spec = new RuntimeSpecV2
        {
            Services = new List<ServiceSpec>
            {
                new() { Name = "redis", Command = "redis-server" },
                new() { Name = "Redis", Command = "redis-server --port 6380" },
            },
        };
        spec.Validate().IsSuccess.Should().BeTrue();
    }
}
