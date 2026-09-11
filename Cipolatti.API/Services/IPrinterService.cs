using Cipolatti.API.Models;
namespace Cipolatti.API.Services;
public interface IPrinterService { Task PrintProductLabelAsync(Qry3descricoes product, CancellationToken cancellationToken); }
