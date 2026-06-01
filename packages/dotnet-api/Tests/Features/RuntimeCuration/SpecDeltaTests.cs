using Source.Features.RuntimeBootstrap.Contracts;
using Source.Features.RuntimeCuration;

namespace Api.Tests.Features.RuntimeCuration;

/// <summary>
/// Pure-function coverage for <see cref="SpecDelta"/>. The handlers (Approve /
/// Edit) lean on this helper for the daemon push payload, so the helper's
/// contract is what guarantees the daemon-side delta is correct.
///
/// <para>V2 semantics: the proposed spec REPLACES the current spec on the
/// runtime row. The delta reports new-or-changed services (by name match),
/// removed services, and hash-based install / setup change flags so the
/// daemon only does the work that actually changed.</para>
/// </summary>
public class SpecDeltaTests
{
    [Fact]
    public void Compute_NullCurrent_ReturnsAllProposedAsNewOrChanged()
    {
        var proposed = """{"version":2,"services":[{"name":"postgres","command":"postgres -D /var/lib/postgresql/data"}]}""";

        var delta = SpecDelta.Compute(currentSpecJson: null, proposedSpecJson: proposed);

        delta.NewOrChangedServices.Should().HaveCount(1);
        delta.NewOrChangedServices[0].Name.Should().Be("postgres");
        delta.RemovedServices.Should().BeEmpty();
        delta.InstallChanged.Should().BeFalse("both sides have no install string");
        delta.SetupChanged.Should().BeFalse();
        delta.HasChanges.Should().BeTrue();
    }

    [Fact]
    public void Compute_EmptyStringCurrent_TreatedAsEmpty()
    {
        var proposed = """{"version":2,"services":[{"name":"redis","command":"redis-server"}]}""";

        var delta = SpecDelta.Compute(currentSpecJson: "", proposedSpecJson: proposed);

        delta.NewOrChangedServices.Should().HaveCount(1);
        delta.NewOrChangedServices[0].Name.Should().Be("redis");
        delta.RemovedServices.Should().BeEmpty();
    }

    [Fact]
    public void Compute_NewServiceAdded_ReportedInNewOrChanged()
    {
        var current = """{"version":2,"services":[{"name":"postgres","command":"postgres -D /data"}]}""";
        var proposed = """{"version":2,"services":[{"name":"postgres","command":"postgres -D /data"},{"name":"redis","command":"redis-server"}]}""";

        var delta = SpecDelta.Compute(current, proposed);

        delta.NewOrChangedServices.Should().HaveCount(1);
        delta.NewOrChangedServices[0].Name.Should().Be("redis");
        delta.RemovedServices.Should().BeEmpty();
    }

    [Fact]
    public void Compute_UnchangedServices_NotReported()
    {
        var current = """{"version":2,"services":[{"name":"postgres","command":"postgres -D /data"}]}""";
        var proposed = """{"version":2,"services":[{"name":"postgres","command":"postgres -D /data"}]}""";

        var delta = SpecDelta.Compute(current, proposed);

        delta.NewOrChangedServices.Should().BeEmpty();
        delta.RemovedServices.Should().BeEmpty();
        delta.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void Compute_ServiceCommandChanged_ReportedInNewOrChanged()
    {
        var current = """{"version":2,"services":[{"name":"postgres","command":"postgres -D /old"}]}""";
        var proposed = """{"version":2,"services":[{"name":"postgres","command":"postgres -D /new"}]}""";

        var delta = SpecDelta.Compute(current, proposed);

        delta.NewOrChangedServices.Should().HaveCount(1);
        delta.NewOrChangedServices[0].Name.Should().Be("postgres");
        delta.NewOrChangedServices[0].Command.Should().Be("postgres -D /new");
    }

    [Fact]
    public void Compute_ServiceRemoved_ReportedInRemovedServices()
    {
        var current = """{"version":2,"services":[{"name":"postgres","command":"postgres -D /data"},{"name":"redis","command":"redis-server"}]}""";
        var proposed = """{"version":2,"services":[{"name":"postgres","command":"postgres -D /data"}]}""";

        var delta = SpecDelta.Compute(current, proposed);

        delta.NewOrChangedServices.Should().BeEmpty();
        delta.RemovedServices.Should().ContainInOrder("redis");
        delta.HasChanges.Should().BeTrue();
    }

    [Fact]
    public void Compute_EmptyProposed_AllCurrentServicesRemoved()
    {
        var current = """{"version":2,"services":[{"name":"postgres","command":"postgres -D /data"}]}""";
        var proposed = """{"version":2,"services":[]}""";

        var delta = SpecDelta.Compute(current, proposed);

        delta.NewOrChangedServices.Should().BeEmpty();
        delta.RemovedServices.Should().ContainInOrder("postgres");
    }

    [Fact]
    public void Compute_InstallStringAdded_InstallChangedTrue()
    {
        var current = """{"version":2}""";
        var proposed = """{"version":2,"install":"apt-get install -y mongodb-org"}""";

        var delta = SpecDelta.Compute(current, proposed);

        delta.InstallChanged.Should().BeTrue();
        delta.InstallNew.Should().Be("apt-get install -y mongodb-org");
    }

    [Fact]
    public void Compute_InstallStringChanged_InstallChangedTrueWithNewValue()
    {
        var current = """{"version":2,"install":"apt-get install -y old-package"}""";
        var proposed = """{"version":2,"install":"apt-get install -y new-package"}""";

        var delta = SpecDelta.Compute(current, proposed);

        delta.InstallChanged.Should().BeTrue();
        delta.InstallNew.Should().Be("apt-get install -y new-package");
    }

    [Fact]
    public void Compute_InstallUnchanged_InstallChangedFalseAndNewNull()
    {
        var current = """{"version":2,"install":"apt-get install -y mongodb-org"}""";
        var proposed = """{"version":2,"install":"apt-get install -y mongodb-org"}""";

        var delta = SpecDelta.Compute(current, proposed);

        delta.InstallChanged.Should().BeFalse();
        delta.InstallNew.Should().BeNull("unchanged install strings save wire bytes by omitting the new value");
    }

    [Fact]
    public void Compute_WhitespaceOnlyInstallDifferences_NotConsideredChanged()
    {
        // Hash-based comparison normalises null/empty/whitespace to the same
        // bucket so cosmetic edits don't trigger needless re-runs.
        var current = """{"version":2,"install":""}""";
        var proposed = """{"version":2,"install":"   "}""";

        var delta = SpecDelta.Compute(current, proposed);

        delta.InstallChanged.Should().BeFalse();
    }

    [Fact]
    public void Compute_SetupStringChanged_SetupChangedTrueWithNewValue()
    {
        var current = """{"version":2,"setup":"npm install"}""";
        var proposed = """{"version":2,"setup":"npm ci"}""";

        var delta = SpecDelta.Compute(current, proposed);

        delta.SetupChanged.Should().BeTrue();
        delta.SetupNew.Should().Be("npm ci");
    }

    [Fact]
    public void Compute_NoChanges_HasChangesFalse()
    {
        var current = """{"version":2,"install":"apt-get install x","setup":"npm install","services":[{"name":"postgres","command":"postgres"}]}""";
        var proposed = """{"version":2,"install":"apt-get install x","setup":"npm install","services":[{"name":"postgres","command":"postgres"}]}""";

        var delta = SpecDelta.Compute(current, proposed);

        delta.HasChanges.Should().BeFalse(
            "identical specs produce an empty delta — the daemon push can be skipped entirely");
    }

    [Fact]
    public void Compute_TypedOverload_NullsCollapseToEmptySpec()
    {
        var delta = SpecDelta.Compute(current: null, proposed: null);

        delta.NewOrChangedServices.Should().BeEmpty();
        delta.RemovedServices.Should().BeEmpty();
        delta.InstallChanged.Should().BeFalse();
        delta.SetupChanged.Should().BeFalse();
        delta.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void Compute_ServiceInstallVerifyChange_TreatedAsServiceChange()
    {
        // installVerify is part of the service contract — a verify-only edit
        // (no other field changed) must still surface via newOrChangedServices
        // so the daemon learns the new predicate on next boot. Without this,
        // a user adding "command -v mongod" to a previously-naked service
        // would silently fail to propagate until something else changed.
        var current = """{"version":2,"services":[{"name":"mongo","command":"mongod"}]}""";
        var proposed = """{"version":2,"services":[{"name":"mongo","command":"mongod","installVerify":"command -v mongod"}]}""";

        var delta = SpecDelta.Compute(current, proposed);

        delta.NewOrChangedServices.Should().HaveCount(1,
            "adding installVerify counts as a service-shape change");
        delta.NewOrChangedServices[0].InstallVerify.Should().Be("command -v mongod");
        delta.RemovedServices.Should().BeEmpty();
    }

    [Fact]
    public void Compute_ServiceInstallVerifyIdentical_NotChanged()
    {
        // Same service with identical installVerify on both sides — no delta.
        var current = """{"version":2,"services":[{"name":"mongo","command":"mongod","installVerify":"command -v mongod"}]}""";
        var proposed = """{"version":2,"services":[{"name":"mongo","command":"mongod","installVerify":"command -v mongod"}]}""";

        var delta = SpecDelta.Compute(current, proposed);

        delta.NewOrChangedServices.Should().BeEmpty();
        delta.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void Compute_ServiceNameMatchIsCaseSensitive()
    {
        // Supervisord program names are case-sensitive identifiers, so the
        // delta layer follows suit — "Postgres" and "postgres" are different
        // services.
        var current = """{"version":2,"services":[{"name":"Postgres","command":"postgres"}]}""";
        var proposed = """{"version":2,"services":[{"name":"postgres","command":"postgres"}]}""";

        var delta = SpecDelta.Compute(current, proposed);

        delta.NewOrChangedServices.Should().HaveCount(1, "lowercase name treated as new service");
        delta.NewOrChangedServices[0].Name.Should().Be("postgres");
        delta.RemovedServices.Should().ContainInOrder("Postgres");
    }
}
