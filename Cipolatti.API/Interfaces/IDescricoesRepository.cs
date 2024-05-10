using Cipolatti.API.Models;

namespace Cipolatti.API.Interfaces
{
    public interface IDescricoesRepository
    {
        Task<Qry3descricoes> SelecionarByCodComplAdicional(int Codcompladicional);
    }
}
