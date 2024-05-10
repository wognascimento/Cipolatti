using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Repositories
{
    public class AtendentesAlmoxRepository : IAtendentesAlmoxRepository
    {
        private readonly CipolattiContext _context;

        public AtendentesAlmoxRepository(CipolattiContext context)
        {
            _context = context;
        }

        public void Alterar(TblAtendentesAlmox atendente)
        {
            throw new NotImplementedException();
        }

        public void Excluir(TblAtendentesAlmox atendente)
        {
            throw new NotImplementedException();
        }

        public void Incluir(TblAtendentesAlmox atendente)
        {
            throw new NotImplementedException();
        }

        public Task<bool> SaveAllAsync()
        {
            throw new NotImplementedException();
        }

        public async Task<IEnumerable<TblAtendentesAlmox>> SelecionarTodos()
        {
            return await _context.TblAtendentesAlmox.OrderBy(x => x.NomeFuncionario).ToListAsync();
        }
    }
}
