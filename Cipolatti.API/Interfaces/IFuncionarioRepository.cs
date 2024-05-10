using Cipolatti.API.Models;

namespace Cipolatti.API.Interfaces
{
    public interface IFuncionarioRepository
    {
        void Incluir(Funcionarios funcionaro);
        void Alterar(Funcionarios funcionaro);
        void Excluir(Funcionarios funcionaro);
        Task<Funcionarios> SelecionarByCodfun(int codfun);
        Task<Funcionarios> SelecionarByQrcode(string qrcode);
        Task<IEnumerable<Funcionarios>> SelecionarTodos();
        Task<bool> SaveAllAsync();
    }
}