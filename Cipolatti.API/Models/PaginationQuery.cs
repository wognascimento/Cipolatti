using System.ComponentModel.DataAnnotations;
namespace Cipolatti.API.Models;
public sealed class PaginationQuery
{
    [Range(1, int.MaxValue)] public int Page { get; init; } = 1;
    [Range(1, 200)] public int PageSize { get; init; } = 50;
}
