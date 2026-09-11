using System.DirectoryServices.Protocols;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Cipolatti.API.Models.Catalogo;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Cipolatti.API.Services;

public sealed class AdOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 636;
    public bool UseSsl { get; set; } = true;
    public string Domain { get; set; } = "";
    public string BaseDn { get; set; } = "";
    public string RequiredGroup { get; set; } = "";
    public int TokenLifetimeMinutes { get; set; } = 480;
}

public interface IAdAuthenticationService
{
    AdLoginResponse Authenticate(string usuario, string senha);
}

public sealed class AdAuthenticationService(
    IOptions<AdOptions> adOptions,
    IConfiguration configuration,
    ILogger<AdAuthenticationService> logger) : IAdAuthenticationService
{
    public AdLoginResponse Authenticate(string usuario, string senha)
    {
        var options = adOptions.Value;
        ValidateConfiguration(options);

        var account = NormalizeAccount(usuario, options.Domain);
        if (string.IsNullOrWhiteSpace(senha)) throw new UnauthorizedAccessException("Credenciais inválidas.");

        try
        {
            using var connection = new LdapConnection(new LdapDirectoryIdentifier(options.Host, options.Port, true, false))
            {
                AuthType = AuthType.Basic,
                Timeout = TimeSpan.FromSeconds(15),
                Credential = new NetworkCredential(account.UserPrincipalName, senha)
            };
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
            connection.SessionOptions.SecureSocketLayer = options.UseSsl;
            connection.Bind();

            var groupDn = FindGroupDn(connection, options.BaseDn, options.RequiredGroup);
            var user = FindAuthorizedUser(connection, options.BaseDn, account.UserPrincipalName, groupDn);
            if (user is null) throw new UnauthorizedAccessException("Usuário não pertence ao grupo autorizado.");

            var login = GetString(user, "sAMAccountName") ?? account.SamAccountName;
            var displayName = GetString(user, "displayName") ?? login;
            var token = CreateToken(login, displayName, options.TokenLifetimeMinutes, out var expiresAt);
            logger.LogInformation("Login administrativo realizado pelo usuário {Usuario} via Active Directory.", login);
            return new AdLoginResponse(token, expiresAt, login, displayName);
        }
        catch (LdapException ex) when (ex.ErrorCode == 49)
        {
            logger.LogWarning("Tentativa de login administrativo recusada para {Usuario}.", account.SamAccountName);
            throw new UnauthorizedAccessException("Credenciais inválidas.");
        }
    }

    private string CreateToken(string login, string displayName, int lifetimeMinutes, out DateTimeOffset expiresAt)
    {
        var issuer = configuration["Jwt:Issuer"]!;
        var audience = configuration["Jwt:Audience"]!;
        var signingKey = configuration["Jwt:SigningKey"]!;
        expiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(lifetimeMinutes, 5, 720));
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, login),
            new Claim(ClaimTypes.NameIdentifier, login),
            new Claim(ClaimTypes.Name, login),
            new Claim("display_name", displayName),
            new Claim(ClaimTypes.Role, "CatalogoAdmin")
        };
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer, audience, claims, DateTime.UtcNow, expiresAt.UtcDateTime, credentials));
    }

    private static SearchResultEntry? FindAuthorizedUser(LdapConnection connection, string baseDn, string upn, string groupDn)
    {
        var filter = $"(&(objectClass=user)(userPrincipalName={EscapeFilter(upn)})(memberOf:1.2.840.113556.1.4.1941:={EscapeFilter(groupDn)}))";
        var response = (SearchResponse)connection.SendRequest(
            new SearchRequest(baseDn, filter, SearchScope.Subtree, "sAMAccountName", "displayName"));
        return response.Entries.Count == 1 ? response.Entries[0] : null;
    }

    private static string FindGroupDn(LdapConnection connection, string baseDn, string group)
    {
        var filter = $"(&(objectClass=group)(sAMAccountName={EscapeFilter(group)}))";
        var response = (SearchResponse)connection.SendRequest(
            new SearchRequest(baseDn, filter, SearchScope.Subtree, "distinguishedName"));
        if (response.Entries.Count != 1) throw new InvalidOperationException("Grupo administrativo não encontrado ou ambíguo no AD.");
        return response.Entries[0].DistinguishedName;
    }

    private static (string SamAccountName, string UserPrincipalName) NormalizeAccount(string value, string domain)
    {
        var trimmed = value.Trim();
        if (trimmed.Contains('\\')) trimmed = trimmed[(trimmed.LastIndexOf('\\') + 1)..];
        var sam = trimmed.Contains('@') ? trimmed[..trimmed.IndexOf('@')] : trimmed;
        if (string.IsNullOrWhiteSpace(sam) || sam.Length > 120) throw new UnauthorizedAccessException("Credenciais inválidas.");
        return (sam, trimmed.Contains('@') ? trimmed : $"{sam}@{domain}");
    }

    private static string? GetString(SearchResultEntry entry, string attribute) =>
        entry.Attributes[attribute] is { Count: > 0 } values ? values[0]?.ToString() : null;

    private static string EscapeFilter(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var ch in value)
            result.Append(ch switch { '\\' => "\\5c", '*' => "\\2a", '(' => "\\28", ')' => "\\29", '\0' => "\\00", _ => ch });
        return result.ToString();
    }

    private static void ValidateConfiguration(AdOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Host) || string.IsNullOrWhiteSpace(options.Domain) ||
            string.IsNullOrWhiteSpace(options.BaseDn) || string.IsNullOrWhiteSpace(options.RequiredGroup))
            throw new InvalidOperationException("Configuração do Active Directory incompleta.");
        if (!options.UseSsl) throw new InvalidOperationException("Autenticação LDAP sem TLS não é permitida.");
    }
}
