using Cipolatti.API.Models;

namespace Cipolatti.API.Interfaces
{
    public interface IEnderecamentoGalpaoRepository
    {
        Task<IEnumerable<QryEnderecamentoGalpao>> SelecionarTodos();
    }
}
