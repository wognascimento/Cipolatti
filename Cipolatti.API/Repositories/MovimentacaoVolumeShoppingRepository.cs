using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Repositories
{
    public class MovimentacaoVolumeShoppingRepository : IMovimentacaoVolumeShoppingRepository
    {
        private readonly CipolattiContext _context;

        public MovimentacaoVolumeShoppingRepository(CipolattiContext context)
        {
            _context = context;
        }

        public void Alterar(TblMovimentacaoVolumeShopping confCarga)
        {
            _context.Entry(confCarga).State = EntityState.Modified;
        }

        public void Excluir(TblMovimentacaoVolumeShopping confCarga)
        {
            _context.TblMovimentacaoVolumeShopping.Remove(confCarga);
        }

        public void Incluir(TblMovimentacaoVolumeShopping confCarga)
        {
            _context.TblMovimentacaoVolumeShopping.Add(confCarga);
        }

        public async Task<bool> SaveAllAsync()
        {
            return await _context.SaveChangesAsync() > 0;
        }

        public async Task<TblMovimentacaoVolumeShopping> SelecionarByBarcode(string barcode)
        {
            return await _context.TblMovimentacaoVolumeShopping.Where(x => x.BarcodeVolume == barcode).FirstOrDefaultAsync();
        }

        public async Task<IEnumerable<TblMovimentacaoVolumeShopping>> SelecionarTodos()
        {
            return await _context.TblMovimentacaoVolumeShopping.ToListAsync();
        }
    }
}
