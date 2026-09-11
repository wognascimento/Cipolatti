using System.Reflection;
using Cipolatti.API.Controllers;
using Cipolatti.API.Services;
using Microsoft.AspNetCore.Authorization;

namespace Cipolatti.API.Tests;

public sealed class CatalogoAdminControllerTests
{
    [Fact]
    public void Controller_requires_catalog_admin_policy()
    {
        var attribute = Assert.IsType<AuthorizeAttribute>(
            Attribute.GetCustomAttribute(typeof(CatalogoAdminController), typeof(AuthorizeAttribute)));

        Assert.Equal("CatalogoAdmin", attribute.Policy);
    }

    [Fact]
    public void Youtube_callback_is_anonymous_but_controller_remains_protected()
    {
        var callback=typeof(CatalogoAdminController).GetMethod("YoutubeCallback");
        Assert.NotNull(callback);
        Assert.NotNull(callback.GetCustomAttribute<AllowAnonymousAttribute>());
        var controller=Assert.IsType<AuthorizeAttribute>(Attribute.GetCustomAttribute(typeof(CatalogoAdminController),typeof(AuthorizeAttribute)));
        Assert.Equal("CatalogoAdmin",controller.Policy);
    }

    [Theory]
    [InlineData("Marcenaria do Noel", "marcenaria-do-noel")]
    [InlineData("  Natal: Luz & Emoção!  ", "natal-luz-emocao")]
    [InlineData("Árvore  2026", "arvore-2026")]
    public void Slugify_normalizes_public_names(string value, string expected)
    {
        var method = typeof(CatalogoAdminService).GetMethod("Slugify", BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.Equal(expected, method.Invoke(null, [value]));
    }
}
