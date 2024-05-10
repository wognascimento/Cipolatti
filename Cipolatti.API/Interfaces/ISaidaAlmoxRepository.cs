using Cipolatti.API.Models;

namespace Cipolatti.API.Interfaces
{
    public interface ISaidaAlmoxRepository
    {
        void Incluir(TSaidaAlmox saida);
        Task<bool> SaveAllAsync();
    }
}
