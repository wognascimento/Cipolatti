using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Repositories
{
    public class AprovadoRepository : IAprovadoRepository
    {
        private readonly CipolattiContext _context;

        public AprovadoRepository(CipolattiContext context)
        {
            _context = context;
        }

        public void Alterar(QryAprovados aprovado)
        {
            _context.Entry(aprovado).State = EntityState.Modified;
        }

        public void Excluir(QryAprovados aprovado)
        {
            _context.QryAprovados.Remove(aprovado);
        }

        public void Incluir(QryAprovados aprovado)
        {
            _context.QryAprovados.Add(aprovado);
        }

        public async Task<bool> SaveAllAsync()
        {
            return await _context.SaveChangesAsync() > 0;
        }

        public async Task<QryAprovados> SelecionarById(int id)
        {
            return await _context.QryAprovados.Where(x => x.IdAprovado == id).FirstOrDefaultAsync();  
        }

        public async Task<IEnumerable<QryAprovados>> SelecionarTodos()
        {
            return await _context.QryAprovados.ToListAsync();
        }
    }
}
