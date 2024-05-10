using Cipolatti.API.Models;

namespace Cipolatti.API.Interfaces
{
    public interface IVolumeControladoRepository
    {
        void Incluir(TblVolumeControlado volumeControlado);
        void Alterar(TblVolumeControlado volumeControlado);
        void Excluir(TblVolumeControlado volumeControlado);
        Task<TblVolumeControlado> SelecionarBySiglaByVolume(string sigla, int volume);
        Task<IEnumerable<TblVolumeControlado>> SelecionarTodos();
        Task<bool> SaveAllAsync();
    }
}
