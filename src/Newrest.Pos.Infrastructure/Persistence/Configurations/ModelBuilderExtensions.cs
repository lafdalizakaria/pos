using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Newrest.Pos.Infrastructure.Persistence.Configurations;

internal static class ModelBuilderExtensions
{
    /// <summary>Marks a parent/child relationship whose children may be deleted with their parent (mutable aggregates only).</summary>
    public static ReferenceCollectionBuilder<TPrincipal, TDependent> CascadeChildren<TPrincipal, TDependent>(
        this ReferenceCollectionBuilder<TPrincipal, TDependent> builder)
        where TPrincipal : class
        where TDependent : class
    {
        builder.OnDelete(Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade);
        builder.Metadata.SetAnnotation(PosDbContext.CascadeAllowed, true);
        return builder;
    }

    public static PropertyBuilder<decimal> IsRate(this PropertyBuilder<decimal> builder) => builder.HasPrecision(9, 4);
}
