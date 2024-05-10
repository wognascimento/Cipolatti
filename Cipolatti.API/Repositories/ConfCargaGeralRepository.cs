using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Repositories
{
    public class ConfCargaGeralRepository : IConfCargaGeral
    {
        private readonly CipolattiContext _context;

        public ConfCargaGeralRepository(CipolattiContext context)
        {
            _context = context;
        }

        public void Alterar(TConfCargaGeral confCarga)
        {
            _context.Entry(confCarga).State = EntityState.Modified;
        }

        public void Excluir(TConfCargaGeral confCarga)
        {
            _context.TConfCargaGeral.Remove(confCarga);
        }

        public void Incluir(TConfCargaGeral confCarga)
        {
            _context.TConfCargaGeral.Add(confCarga);
        }

        public async Task<bool> SaveAllAsync()
        {
            return await _context.SaveChangesAsync() > 0;
        }

        public async Task<TConfCargaGeral> SelecionarByBarcode(string barcode)
        {
            return await _context.TConfCargaGeral.Where(x => x.Barcode == barcode).FirstOrDefaultAsync();
        }

        public async Task<IEnumerable<TConfCargaGeral>> SelecionarTodos()
        {
            return await _context.TConfCargaGeral.ToListAsync();
        }
    }
}
