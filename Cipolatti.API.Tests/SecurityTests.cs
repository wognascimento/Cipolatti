using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
namespace Cipolatti.API.Tests;

public sealed class SecurityTests
{
    [Fact]
    public async Task Business_endpoint_requires_authentication()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=localhost;Database=test;Username=test;Password=test").UseSetting("Jwt:SigningKey", "a-test-signing-key-with-at-least-32-bytes"));
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/Romaneio/romaneios", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
