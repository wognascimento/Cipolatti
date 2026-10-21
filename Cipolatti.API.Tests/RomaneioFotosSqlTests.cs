namespace Cipolatti.API.Tests;

public sealed class RomaneioFotosSqlTests
{
    [Fact]
    public void Schema_is_additive_and_contains_device_and_idempotency_controls()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var sql = File.ReadAllText(Path.Combine(root, "database", "013_romaneio_fotos.sql"));

        Assert.Contains("app_dispositivos_fotos", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("romaneio_foto_finalizacoes", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chave_idempotencia", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", sql, StringComparison.OrdinalIgnoreCase);
    }
}
