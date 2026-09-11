using System.Data.Common;
namespace Cipolatti.API.Infrastructure;
public interface IDbConnectionFactory
{
    ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
