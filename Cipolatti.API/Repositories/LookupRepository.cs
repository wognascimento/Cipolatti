using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Repositories
{
    public class LookupRepository : ILookupRepository
    {
        private readonly CipolattiContext _context;

        public LookupRepository(CipolattiContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<QryLookup>> lookupBySigla(string sigla)
        {
            return await _context.QryLookup.Where(x => x.SiglaServ == sigla).ToListAsync();
        }

        public async Task<IEnumerable<QryLookup>> lookupBySiglaByCaminhao(string sigla, string caminhao)
        {
            return await _context.QryLookup.Where(x => x.SiglaServ == sigla && x.BaiaCaminhao == caminhao).ToListAsync();
        }
    }
}
