using Cipolatti.API.Models;

namespace Cipolatti.API.Interfaces
{
    public interface IConfCargaGeral
    {
        void Incluir(TConfCargaGeral confCarga);
        void Alterar(TConfCargaGeral confCarga);
        void Excluir(TConfCargaGeral confCarga);
        Task<TConfCargaGeral> SelecionarByBarcode(string barcode);
        Task<IEnumerable<TConfCargaGeral>> SelecionarTodos();
        Task<bool> SaveAllAsync();
    }
}
