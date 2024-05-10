using Cipolatti.API.Models;

namespace Cipolatti.API.Interfaces
{
    public interface IAtendentesAlmoxRepository
    {
        void Incluir(TblAtendentesAlmox atendente);
        void Alterar(TblAtendentesAlmox atendente);
        void Excluir(TblAtendentesAlmox atendente);
        Task<IEnumerable<TblAtendentesAlmox>> SelecionarTodos();
        Task<bool> SaveAllAsync();
    }
}
