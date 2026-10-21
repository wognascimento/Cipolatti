using System.Net;
using Cipolatti.API.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Cipolatti.API.Infrastructure;
namespace Cipolatti.API.Tests;

public sealed class SecurityTests
{
    [Fact]
    public async Task Business_endpoint_requires_authentication()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=localhost;Database=test;Username=test;Password=test").UseSetting("Jwt:SigningKey", "a-test-signing-key-with-at-least-32-bytes"));
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/Funcionario/SelecionarByQrcode?qrcode=teste", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(typeof(AprovadoController), nameof(AprovadoController.Get))]
    [InlineData(typeof(LookupController), nameof(LookupController.BySigla))]
    [InlineData(typeof(LookupController), nameof(LookupController.ByCaminhao))]
    [InlineData(typeof(RomaneioController), nameof(RomaneioController.List))]
    [InlineData(typeof(RomaneioController), nameof(RomaneioController.Insert))]
    [InlineData(typeof(RomaneioController), nameof(RomaneioController.Update))]
    [InlineData(typeof(RomaneioController), nameof(RomaneioController.Get))]
    [InlineData(typeof(MovimentacaoVolumeShoppingController), nameof(MovimentacaoVolumeShoppingController.Insert))]
    [InlineData(typeof(MovimentacaoVolumeShoppingController), nameof(MovimentacaoVolumeShoppingController.Endereco))]
    [InlineData(typeof(MovimentacaoVolumeShoppingController), nameof(MovimentacaoVolumeShoppingController.Volume))]
    [InlineData(typeof(ConfCargaGeralController), nameof(ConfCargaGeralController.Volume))]
    [InlineData(typeof(ConfCargaGeralController), nameof(ConfCargaGeralController.PreConferencia))]
    public void Selected_endpoint_allows_anonymous_access(Type controller, string action)
    {
        var method = Assert.Single(controller.GetMethods(), candidate => candidate.Name == action);
        Assert.NotNull(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).SingleOrDefault());
    }

    [Fact]
    public void Romaneio_photo_upload_requires_device_authentication()
    {
        var method = Assert.Single(typeof(RomaneioController).GetMethods(),
            candidate => candidate.Name == nameof(RomaneioController.FinalizePhotos));
        var attribute = Assert.IsType<AuthorizeAttribute>(
            method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Single());
        Assert.Equal("RomaneioFotos", attribute.Policy);
        Assert.Equal(RomaneioDeviceDefaults.AuthenticationScheme, attribute.AuthenticationSchemes);
        Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
    }

    [Fact]
    public async Task Public_catalog_endpoint_allows_anonymous_and_returns_validation_problem()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:DefaultConnection", "Host=localhost;Database=test;Username=test;Password=test")
            .UseSetting("Jwt:SigningKey", "a-test-signing-key-with-at-least-32-bytes"));
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/catalogo/temas?conceitos=invalido", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Catalog_admin_endpoint_requires_authentication()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:DefaultConnection", "Host=localhost;Database=test;Username=test;Password=test")
            .UseSetting("Jwt:SigningKey", "a-test-signing-key-with-at-least-32-bytes"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/catalogo/admin/temas", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
