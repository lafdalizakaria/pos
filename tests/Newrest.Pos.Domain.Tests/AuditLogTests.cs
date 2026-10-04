using Newrest.Pos.Domain.Audit;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Domain.Tests;

public class AuditLogTests
{
    [Fact]
    public void Audit_entry_captures_actor_action_and_snapshots()
    {
        var siteId = Guid.CreateVersion7();

        var entry = new AuditLog(TestData.Now, "admin@newrest.ma", "PriceChanged", "Article", "CSC-VND", "{\"price\":42}", "{\"price\":44}")
        {
            SiteId = siteId,
            IpAddress = "10.0.0.5",
            CorrelationId = "abc",
        };

        entry.Should().BeEquivalentTo(new
        {
            OccurredAt = TestData.Now,
            Actor = "admin@newrest.ma",
            Action = "PriceChanged",
            EntityType = "Article",
            EntityId = "CSC-VND",
            BeforeJson = "{\"price\":42}",
            AfterJson = "{\"price\":44}",
            SiteId = (Guid?)siteId,
        }, o => o.ExcludingMissingMembers());
        entry.Should().BeAssignableTo<IImmutableRecord>();
        FluentActions.Invoking(() => new AuditLog(TestData.Now, " ", "x", "y", null)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Domain_exception_codes_default_to_domain_error()
    {
        new DomainException().Code.Should().Be("domain_error");
        new DomainException("msg").Code.Should().Be("domain_error");
        new DomainException("msg", new InvalidOperationException()).InnerException.Should().BeOfType<InvalidOperationException>();
    }
}
