using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Repositories
{
    public class FuncionarioRepository : IFuncionarioRepository
    {
        private readonly CipolattiContext _context;

        public FuncionarioRepository(CipolattiContext context)
        {
            _context = context;
        }

        public void Alterar(Funcionarios aprovado)
        {
            throw new NotImplementedException();
        }

        public void Excluir(Funcionarios aprovado)
        {
            throw new NotImplementedException();
        }

        public void Incluir(Funcionarios aprovado)
        {
            throw new NotImplementedException();
        }

        public Task<bool> SaveAllAsync()
        {
            throw new NotImplementedException();
        }

        public Task<Funcionarios> SelecionarByCodfun(int codfun)
        {
            throw new NotImplementedException();
        }

        public async Task<Funcionarios> SelecionarByQrcode(string qrcode)
        {
            return await _context.Funcionarios.Where(x => x.Qrcode == qrcode).FirstOrDefaultAsync();
        }

        public Task<IEnumerable<Funcionarios>> SelecionarTodos()
        {
            throw new NotImplementedException();
        }
    }
}
