using Cipolatti.API.Models;

namespace Cipolatti.API.Interfaces
{
    public interface IAprovadoRepository
    {
        void Incluir(QryAprovados aprovado);
        void Alterar(QryAprovados aprovado);
        void Excluir(QryAprovados aprovado);
        Task<QryAprovados> SelecionarById(int id);
        Task<IEnumerable<QryAprovados>> SelecionarTodos();
        Task<bool> SaveAllAsync();
    }
}