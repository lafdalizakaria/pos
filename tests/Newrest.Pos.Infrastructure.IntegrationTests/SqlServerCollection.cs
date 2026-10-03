using Newrest.Pos.Testing;

namespace Newrest.Pos.Infrastructure.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = SqlServerFixture.CollectionName;
}
