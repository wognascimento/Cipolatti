using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Cipolatti.API.Infrastructure;

public static class RomaneioDeviceDefaults
{
    public const string AuthenticationScheme = "RomaneioDevice";
    public const string Scope = "romaneio.fotos";
}

public sealed class RomaneioDeviceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IDbConnectionFactory db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = authorization[7..].Trim();
        if (token.Length is < 32 or > 512)
            return AuthenticateResult.Fail("Token de dispositivo inválido.");

        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        await using var connection = await db.OpenConnectionAsync(Context.RequestAborted);
        var device = await connection.QuerySingleOrDefaultAsync<DeviceRow>(new CommandDefinition("""
            SELECT id, nome
              FROM expedicao.app_dispositivos_fotos
             WHERE token_hash = @tokenHash
               AND ativo = true
               AND ativado_em IS NOT NULL
               AND revogado_em IS NULL
            """, new { tokenHash }, cancellationToken: Context.RequestAborted));
        if (device is null) return AuthenticateResult.Fail("Token de dispositivo inválido ou revogado.");

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE expedicao.app_dispositivos_fotos
               SET ultimo_acesso_em = now()
             WHERE id = @deviceId
            """, new { deviceId = device.Id }, cancellationToken: Context.RequestAborted));

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, device.Id.ToString()),
            new Claim(ClaimTypes.Name, device.Nome),
            new Claim("scope", RomaneioDeviceDefaults.Scope)
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private sealed record DeviceRow(long Id, string Nome);
}

