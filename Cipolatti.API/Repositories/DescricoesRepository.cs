using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Repositories
{
    public class DescricoesRepository : IDescricoesRepository
    {
        private readonly CipolattiContext _context;

        public DescricoesRepository(CipolattiContext context)
        {
            _context = context;
        }

        public async Task<Qry3descricoes> SelecionarByCodComplAdicional(int Codcompladicional)
        {
            return await _context.Qry3descricoes.Where(x => x.Codcompladicional == Codcompladicional && x.Planilha.Contains("ALMOX")).FirstOrDefaultAsync();
        }
    }
}
