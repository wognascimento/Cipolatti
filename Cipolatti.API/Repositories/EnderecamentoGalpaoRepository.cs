using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Repositories
{
    public class EnderecamentoGalpaoRepository : IEnderecamentoGalpaoRepository
    {
        private readonly CipolattiContext _context;

        public EnderecamentoGalpaoRepository(CipolattiContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<QryEnderecamentoGalpao>> SelecionarTodos()
        {
            return await _context.QryEnderecamentoGalpao.ToListAsync();
        }
    }
}
