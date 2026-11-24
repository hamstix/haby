using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;

namespace Haby.Persistence.PostgreSql;

/// <summary>Bulk LINQ writes bypass SaveChanges; these append-only entity types have no supported bulk mutation.</summary>
internal sealed class SharedCredentialBulkWriteGuard : IQueryExpressionInterceptor
{
    internal static readonly SharedCredentialBulkWriteGuard Instance = new();
    private SharedCredentialBulkWriteGuard() { }
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        if (queryExpression is MethodCallExpression { Arguments.Count: > 0 } call &&
            call.Method.Name is "ExecuteUpdate" or "ExecuteDelete" &&
            HasHistoryRoot(call.Arguments[0]))
            throw new InvalidOperationException("Shared credential history does not support bulk mutation.");
        return queryExpression;
    }
    private static bool HasHistoryRoot(Expression expression)
    {
        var visitor = new HistoryRootVisitor();
        visitor.Visit(expression);
        return visitor.Found;
    }
    private sealed class HistoryRootVisitor : ExpressionVisitor
    {
        internal bool Found { get; private set; }
        protected override Expression VisitExtension(Expression node)
        {
            if (node is EntityQueryRootExpression root)
            {
                if (root.EntityType.ClrType == typeof(SharedCredentialBindingRow) ||
                    root.EntityType.ClrType == typeof(SharedCredentialRevocationRow) ||
                    root.EntityType.ClrType == typeof(ConfigurationSharedUseRow)) Found = true;
                return node;
            }
            return base.VisitExtension(node);
        }
    }
}
public sealed partial class ManifestCatalogDbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        // IQueryExpressionInterceptor is singleton: reuse one stateless instance across contexts.
        optionsBuilder.AddInterceptors(SharedCredentialBulkWriteGuard.Instance);
    }
}
