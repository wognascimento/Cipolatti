using Cipolatti.API.Models;

namespace Cipolatti.API.Interfaces
{
    public interface ILookupRepository
    {
        Task<IEnumerable<QryLookup>> lookupBySigla(string sigla);
        Task<IEnumerable<QryLookup>> lookupBySiglaByCaminhao(string sigla, string caminhao);
    }
}
