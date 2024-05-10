using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Repositories
{
    public class VolumeControladoRepository : IVolumeControladoRepository
    {
        private readonly CipolattiContext _context;
        public VolumeControladoRepository(CipolattiContext context)
        {
            _context = context;
        }

        public void Alterar(TblVolumeControlado volumeControlado)
        {
            _context.Entry(volumeControlado).State = EntityState.Modified;
        }

        public void Excluir(TblVolumeControlado volumeControlado)
        {
            _context.TblVolumeControlado.Remove(volumeControlado);
        }

        public void Incluir(TblVolumeControlado volumeControlado)
        {
            _context.TblVolumeControlado.Add(volumeControlado);
        }

        public async Task<bool> SaveAllAsync()
        {
            return await _context.SaveChangesAsync() > 0;
        }

        public async Task<TblVolumeControlado> SelecionarBySiglaByVolume(string sigla, int volume)
        {
            return await _context.TblVolumeControlado.Where(x => x.Sigla == sigla && x.Volume == volume).FirstOrDefaultAsync();
        }

        public async Task<IEnumerable<TblVolumeControlado>> SelecionarTodos()
        {
            return await _context.TblVolumeControlado.ToListAsync();
        }
    }
}
