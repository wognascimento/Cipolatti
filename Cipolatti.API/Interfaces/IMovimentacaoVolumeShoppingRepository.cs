using Cipolatti.API.Models;

namespace Cipolatti.API.Interfaces
{
    public interface IMovimentacaoVolumeShoppingRepository
    {
        void Incluir(TblMovimentacaoVolumeShopping confCarga);
        void Alterar(TblMovimentacaoVolumeShopping confCarga);
        void Excluir(TblMovimentacaoVolumeShopping confCarga);
        Task<TblMovimentacaoVolumeShopping> SelecionarByBarcode(string barcode);
        Task<IEnumerable<TblMovimentacaoVolumeShopping>> SelecionarTodos();
        Task<bool> SaveAllAsync();
    }
}
