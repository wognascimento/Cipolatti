using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;

namespace Cipolatti.API.Repositories
{
    public class SaidaAlmoxRepository : ISaidaAlmoxRepository
    {
        private readonly CipolattiContext _context;

        public SaidaAlmoxRepository(CipolattiContext context)
        {
            _context = context;
        }

        public void Incluir(TSaidaAlmox saida)
        {
            _context.TSaidaAlmox.Add(saida);
        }

        public async Task<bool> SaveAllAsync()
        {
            return await _context.SaveChangesAsync() > 0;
        }
    }
}
