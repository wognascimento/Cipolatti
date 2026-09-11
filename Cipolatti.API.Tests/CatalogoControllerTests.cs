using Cipolatti.API.Controllers;
using Cipolatti.API.Models;
using Cipolatti.API.Models.Catalogo;
using Cipolatti.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cipolatti.API.Tests;

public sealed class CatalogoControllerTests
{
    [Fact]
    public void Public_controller_is_explicitly_anonymous()
    {
        Assert.NotNull(Attribute.GetCustomAttribute(typeof(CatalogoController), typeof(AllowAnonymousAttribute)));
    }

    [Fact]
    public async Task List_rejects_invalid_concept_csv_without_calling_service()
    {
        var service = new FakeCatalogoService();
        var controller = new CatalogoController(service);

        var response = await controller.ListarTemas(
            new CatalogoTemasQuery { Conceitos = "12,invalido" },
            TestContext.Current.CancellationToken);

        var result = Assert.IsType<ObjectResult>(response.Result);
        Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.Null(service.LastFilter);
    }

    [Fact]
    public async Task List_parses_distinct_concepts_match_and_escapes_like_wildcards()
    {
        var service = new FakeCatalogoService();
        var controller = new CatalogoController(service);

        var response = await controller.ListarTemas(new CatalogoTemasQuery
        {
            Busca = " Noel_100% ",
            Conceitos = "20, 12,20",
            Match = "all",
            Page = 2,
            PageSize = 10
        }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(response.Result);
        Assert.NotNull(service.LastFilter);
        Assert.Equal([12, 20], service.LastFilter.ConceptIds);
        Assert.True(service.LastFilter.MatchAll);
        Assert.Equal("%Noel\\_100\\%%", service.LastFilter.SearchPattern);
        Assert.Equal(2, service.LastFilter.Page);
        Assert.Equal(10, service.LastFilter.PageSize);
    }

    [Fact]
    public async Task Missing_theme_returns_not_found()
    {
        var controller = new CatalogoController(new FakeCatalogoService());
        var response = await controller.ObterTema("inexistente", TestContext.Current.CancellationToken);
        Assert.IsType<NotFoundResult>(response.Result);
    }

    [Fact]
    public async Task Non_positive_pdf_id_returns_validation_problem()
    {
        var controller = new CatalogoController(new FakeCatalogoService());
        var response = await controller.ListarPaginas(0, TestContext.Current.CancellationToken);
        var result = Assert.IsType<ObjectResult>(response.Result);
        Assert.IsType<ValidationProblemDetails>(result.Value);
    }

    private sealed class FakeCatalogoService : ICatalogoService
    {
        public CatalogoTemasFilter? LastFilter { get; private set; }

        public Task<PagedResult<TemaResumoDto>> ListarTemasAsync(CatalogoTemasFilter filter, CancellationToken ct)
        {
            LastFilter = filter;
            return Task.FromResult(new PagedResult<TemaResumoDto>([], filter.Page, filter.PageSize, 0));
        }

        public Task<TemaDetalheDto?> ObterTemaAsync(string slug, CancellationToken ct) => Task.FromResult<TemaDetalheDto?>(null);
        public Task<IReadOnlyCollection<ConceitoDto>> ListarConceitosAsync(CancellationToken ct) => Task.FromResult<IReadOnlyCollection<ConceitoDto>>([]);
        public Task<IReadOnlyCollection<TemaPdfDto>?> ListarPdfsAsync(string slug, CancellationToken ct) => Task.FromResult<IReadOnlyCollection<TemaPdfDto>?>(null);
        public Task<IReadOnlyCollection<TemaPdfPaginaDto>?> ListarPaginasAsync(long idPdf, CancellationToken ct) => Task.FromResult<IReadOnlyCollection<TemaPdfPaginaDto>?>(null);
        public Task<IReadOnlyCollection<TemaVideoDto>?> ListarVideosAsync(string slug, CancellationToken ct) => Task.FromResult<IReadOnlyCollection<TemaVideoDto>?>(null);
        public Task<IReadOnlyCollection<TemaResumoDto>?> ListarRelacionadosAsync(string slug, CancellationToken ct) => Task.FromResult<IReadOnlyCollection<TemaResumoDto>?>(null);
    }
}
