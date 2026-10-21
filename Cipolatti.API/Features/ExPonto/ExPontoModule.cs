using System.IdentityModel.Tokens.Jwt;
using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Microsoft.AspNetCore.Authorization;
using Cipolatti.API.Services;

namespace Cipolatti.API.ExPonto;

public sealed class ExPontoDataSource : IAsyncDisposable
{
    private readonly NpgsqlDataSource _inner;
    public ExPontoDataSource(string connectionString) => _inner = NpgsqlDataSource.Create(connectionString);
    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) => _inner.OpenConnectionAsync(cancellationToken);
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

internal static class ExPontoSql
{
    internal static DateTime DateParameter(DateOnly value) => value.ToDateTime(TimeOnly.MinValue);
    internal static bool IsFuncaoExtra(int? limite, int usados) => limite.HasValue && usados >= limite.Value;
}

public static class ExPontoModule
{
    public const string AuthenticationScheme = "ExPontoBearer";

    public static IServiceCollection AddExPonto(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);
        var connectionString = configuration.GetConnectionString("ExPonto");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Configure ConnectionStrings__ExPonto para habilitar o modulo exPonto.");
        var jwtSecret = configuration["ExPonto:Jwt:Secret"];
        if (string.IsNullOrWhiteSpace(jwtSecret) || Encoding.UTF8.GetByteCount(jwtSecret) < 32)
            throw new InvalidOperationException("Configure ExPonto__Jwt__Secret com pelo menos 32 bytes fora do repositorio.");

        services.Configure<EmailOptions>(configuration.GetSection("ExPonto:Email"));
        services.Configure<AppLinksOptions>(configuration.GetSection("ExPonto:AppLinks"));
        services.Configure<LocationAdjustmentOptions>(configuration.GetSection("ExPonto:LocationAdjustment"));
        services.Configure<MaintenanceApprovalOptions>(configuration.GetSection("ExPonto:MaintenanceApproval"));
        services.Configure<MissingPointingAlertOptions>(configuration.GetSection("ExPonto:MissingPointingAlert"));
        services.AddTransient<UserNotificationEmailService>();
        services.AddTransient<LocationAdjustmentEmailService>();
        services.AddTransient<ApontamentoSyncEmailService>();
        services.AddTransient<BeatAdjustmentEmailService>();
        services.AddTransient<CronogramaExceptionEmailService>();
        services.AddTransient<MaintenanceAdAuthenticator>();
        services.AddTransient<MaintenanceExceptionEmailService>();
        services.AddTransient<IntegranteLoanEmailService>();
        services.AddHostedService<MissingPointingAlertHostedService>();
        services.AddSingleton(new ExPontoDataSource(connectionString));
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
        services.AddAuthentication().AddJwtBearer(AuthenticationScheme, options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,
                ClockSkew = TimeSpan.FromMinutes(2)
            };
        });
        services.AddAuthorizationBuilder().AddPolicy("ExPontoUser", policy =>
            policy.AddAuthenticationSchemes(AuthenticationScheme).RequireAuthenticatedUser());
        return services;
    }

    public static void MapExPonto(this WebApplication app)
    {
        var jwtSecret = app.Configuration["ExPonto:Jwt:Secret"]!;
        var jwtExpirationHours = int.TryParse(app.Configuration["ExPonto:Jwt:ExpirationHours"], out var hours) ? hours : 24;
        var api = app.MapGroup("/api");
api.MapGet("/", () => new { message = "Sistema de Controle de Ponto - API ASP.NET Core + PostgreSQL" }).AllowAnonymous();
api.MapGet("/health", async (ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var ok = await db.ExecuteScalarAsync<int>("select 1");
    return Results.Ok(new { status = ok == 1 ? "healthy" : "degraded", timestamp = DateTime.UtcNow });
}).AllowAnonymous();
api.MapGet("/configuracoes-sistema", async (HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();

    var batidaImparLimiteHora = await GetBatidaImparLimiteHora(db);
    return Results.Ok(new
    {
        batida_impar_limite_hora = batidaImparLimiteHora
    });
}).RequireAuthorization("ExPontoUser");

api.MapPost("/auth/register", async (
    UserCreate request,
    ExPontoDataSource dataSource,
    UserNotificationEmailService emailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var normalizedEmail = request.Email.Trim();
    var existingUser = await QueryUserByEmail(db, normalizedEmail);
    if (existingUser is not null)
    {
        if (!existingUser.MustChangePassword)
        {
            await emailService.SendExistingUserAlreadyAccessedAdminNotificationAsync(existingUser, cancellationToken);
            return Results.BadRequest(new { detail = "Email ja cadastrado. O administrador foi notificado que o usuario ja acessou e pode redefinir a senha." });
        }

        await using var resendTransaction = await db.BeginTransactionAsync(cancellationToken);
        var newTemporaryPassword = GenerateTemporaryPassword();
        existingUser.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newTemporaryPassword);
        existingUser.UpdatedAt = DateTime.UtcNow;

        await db.ExecuteAsync("""
            update users
            set password_hash = @PasswordHash,
                must_change_password = true,
                updated_at = @UpdatedAt
            where id = @Id
            """, existingUser, resendTransaction);

        await emailService.SendExistingProvisionalPasswordWillBeResentAdminNotificationAsync(existingUser, cancellationToken);
        var resendEmailSent = await emailService.SendUserRegisteredAsync(existingUser, newTemporaryPassword, request.UrlAndroid, request.UrlIos, cancellationToken);
        if (!resendEmailSent)
        {
            await resendTransaction.RollbackAsync(cancellationToken);
            return Results.Problem(
                detail: "Nova senha provisoria nao reenviada porque o email de acesso nao foi enviado.",
                statusCode: StatusCodes.Status502BadGateway,
                title: "Falha ao enviar email");
        }

        await resendTransaction.CommitAsync(cancellationToken);
        await emailService.SendUserRegisteredAdminNotificationAsync(existingUser, cancellationToken);

        var existingFuncoes = await QueryFuncoes(db, existingUser.Id);
        var existingClientes = await QueryClientes(db, existingUser.Id);
        return Results.Ok(ToUserResponse(existingUser, existingFuncoes, existingClientes));
    }

    await using var transaction = await db.BeginTransactionAsync(cancellationToken);

    var temporaryPassword = GenerateTemporaryPassword();
    var user = new UserRow
    {
        Id = NewId(),
        Nome = request.Nome,
        Email = normalizedEmail,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(temporaryPassword),
        MustChangePassword = true,
        Ativo = true,
        CreatedAt = DateTime.UtcNow
    };

    await db.ExecuteAsync("""
        insert into users (id, nome, email, password_hash, must_change_password, ativo, created_at)
        values (@Id, @Nome, @Email, @PasswordHash, @MustChangePassword, @Ativo, @CreatedAt)
        """, user, transaction);

    var funcoes = await EnsureDefaultFuncoes(db, user.Id, transaction);
    if (request.Funcoes is { Count: > 0 })
    {
        await ReplaceFuncoes(db, user.Id, request.Funcoes, transaction);
        funcoes = await QueryFuncoes(db, user.Id, transaction);
    }

    if (request.ClienteIds is { Count: > 0 })
    {
        await ReplaceUserClientes(db, user.Id, request.ClienteIds, transaction);
    }

    var emailSent = await emailService.SendUserRegisteredAsync(user, temporaryPassword, request.UrlAndroid, request.UrlIos, cancellationToken);
    if (!emailSent)
    {
        await transaction.RollbackAsync(cancellationToken);
        return Results.Problem(
            detail: "Usuario nao cadastrado porque o email de boas-vindas nao foi enviado. Verifique Email:FromAddress e as configuracoes SMTP.",
            statusCode: StatusCodes.Status502BadGateway,
            title: "Falha ao enviar email");
    }

    await transaction.CommitAsync(cancellationToken);
    await emailService.SendUserRegisteredAdminNotificationAsync(user, cancellationToken);

    var clientes = await QueryClientes(db, user.Id);

    return Results.Ok(ToUserResponse(user, funcoes, clientes));
}).AllowAnonymous();
api.MapPost("/auth/login", async (LoginRequest request, ExPontoDataSource dataSource, ILogger<ExPontoLog> logger) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var email = request.Email.Trim();
    var password = request.Password.Trim();
    var user = await db.QuerySingleOrDefaultAsync<UserRow>("""
        select id, nome, email, password_hash as PasswordHash, must_change_password as MustChangePassword,
               ativo, master_user_id as MasterUserId, integrante_vinculado_id as IntegranteVinculadoId,
               created_at as CreatedAt, updated_at as UpdatedAt
        from users
        where lower(email) = lower(@Email)
        """, new { Email = email });

    if (user is null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
    {
        logger.LogWarning("Falha de login para {Email}: credenciais invalidas", email);
        return Results.Json(new { detail = "Email ou senha invalidos" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    if (!user.Ativo)
    {
        return Results.BadRequest(new { detail = "Usuario inativo" });
    }

    var token = CreateToken(user.Id, request.DeviceId, jwtSecret, jwtExpirationHours);
    var expiresAt = DateTime.UtcNow.AddHours(jwtExpirationHours);
    var deviceLogin = await RegisterDeviceLogin(db, user.Id, request.DeviceId);

    await db.ExecuteAsync("delete from sessions where user_id = @UserId", new { UserId = user.Id });
    await db.ExecuteAsync("""
        insert into sessions (id, user_id, device_id, token, created_at, expires_at)
        values (@Id, @UserId, @DeviceId, @Token, @CreatedAt, @ExpiresAt)
        """, new
    {
        Id = NewId(),
        UserId = user.Id,
        request.DeviceId,
        Token = token,
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = expiresAt
    });

    var ownerId = DataOwnerId(user);
    var funcoes = await EnsureDefaultFuncoes(db, ownerId);
    var clientes = await QueryClientes(db, ownerId);

    return Results.Ok(new
    {
        access_token = token,
        token_type = "bearer",
        user = ToUserResponse(user, funcoes, clientes),
        must_change_password = user.MustChangePassword,
        device_id = request.DeviceId,
        previous_device_blocked = deviceLogin.PreviousDeviceBlocked,
        current_device_was_blocked = deviceLogin.CurrentDeviceWasBlocked
    });
}).AllowAnonymous();
api.MapPost("/auth/logout", async (HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var token = BearerToken(http);
    if (!string.IsNullOrWhiteSpace(token))
    {
        await db.ExecuteAsync("delete from sessions where token = @Token", new { Token = token });
    }
    return Results.Ok(new { message = "Logout realizado com sucesso" });
}).RequireAuthorization("ExPontoUser");

api.MapGet("/auth/me", async (HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();

    var ownerId = DataOwnerId(user);
    var funcoes = await EnsureDefaultFuncoes(db, ownerId);
    var clientes = await QueryClientes(db, ownerId);
    return Results.Ok(ToUserResponse(user, funcoes, clientes));
}).RequireAuthorization("ExPontoUser");

api.MapPost("/auth/change-password", async (ChangePasswordRequest request, HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
    {
        return Results.BadRequest(new { detail = "Senha atual incorreta" });
    }

    await db.ExecuteAsync("""
        update users
        set password_hash = @PasswordHash, must_change_password = false, updated_at = @UpdatedAt
        where id = @Id
        """, new
    {
        user.Id,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword),
        UpdatedAt = DateTime.UtcNow
    });

    return Results.Ok(new { message = "Senha alterada com sucesso" });
}).RequireAuthorization("ExPontoUser");

api.MapPost("/auth/forgot-password", async (
    ForgotPasswordRequest request,
    ExPontoDataSource dataSource,
    UserNotificationEmailService emailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var normalizedEmail = request.Email.Trim();
    if (string.IsNullOrWhiteSpace(normalizedEmail))
    {
        return Results.BadRequest(new { detail = "Informe o email." });
    }

    var user = await db.QuerySingleOrDefaultAsync<UserRow>("""
        select id, nome, email, password_hash as PasswordHash, must_change_password as MustChangePassword,
               ativo, created_at as CreatedAt, updated_at as UpdatedAt
        from users
        where lower(email) = lower(@Email)
        """, new { Email = normalizedEmail });

    if (user is null || !user.Ativo)
    {
        return Results.Ok(new { message = "Se o email estiver cadastrado, voce recebera um codigo de recuperacao." });
    }

    var code = GeneratePasswordResetCode();
    var createdAt = DateTime.UtcNow;
    var expiresAt = createdAt.AddMinutes(15);

    await db.ExecuteAsync("delete from password_reset_codes where user_id = @UserId or expires_at < now() or consumed_at is not null", new { UserId = user.Id });
    await db.ExecuteAsync("""
        insert into password_reset_codes (id, user_id, code, created_at, expires_at)
        values (@Id, @UserId, @Code, @CreatedAt, @ExpiresAt)
        """, new
    {
        Id = NewId(),
        UserId = user.Id,
        Code = code,
        CreatedAt = createdAt,
        ExpiresAt = expiresAt
    });

    var emailSent = await emailService.SendPasswordResetCodeAsync(user, code, expiresAt, cancellationToken);
    if (!emailSent)
    {
        await db.ExecuteAsync("delete from password_reset_codes where user_id = @UserId and code = @Code", new { UserId = user.Id, Code = code });
        return Results.Problem(
            detail: "Nao foi possivel enviar o codigo de recuperacao por email.",
            statusCode: StatusCodes.Status502BadGateway,
            title: "Falha ao enviar email");
    }

    return Results.Ok(new { message = "Se o email estiver cadastrado, voce recebera um codigo de recuperacao." });
}).AllowAnonymous();
api.MapPost("/auth/reset-password", async (
    ResetPasswordRequest request,
    ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var code = request.Token.Trim();
    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(request.NewPassword))
    {
        return Results.BadRequest(new { detail = "Informe o codigo e a nova senha." });
    }

    var row = await db.QuerySingleOrDefaultAsync<PasswordResetCodeRow>("""
        select pr.id as Id, pr.user_id as UserId, pr.code as Code, pr.created_at as CreatedAt,
               pr.expires_at as ExpiresAt, pr.consumed_at as ConsumedAt,
               u.nome, u.email, u.password_hash as PasswordHash,
               u.must_change_password as MustChangePassword, u.ativo,
               u.created_at as UserCreatedAt, u.updated_at as UserUpdatedAt
        from password_reset_codes pr
        join users u on u.id = pr.user_id
        where pr.code = @Code
          and pr.consumed_at is null
          and pr.expires_at > now()
        order by pr.created_at desc
        limit 1
        """, new { Code = code });

    if (row is null || !row.Ativo)
    {
        return Results.BadRequest(new { detail = "Codigo invalido ou expirado." });
    }

    var now = DateTime.UtcNow;
    await db.ExecuteAsync("""
        update users
        set password_hash = @PasswordHash, must_change_password = false, updated_at = @UpdatedAt
        where id = @Id
        """, new
    {
        Id = row.UserId,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword),
        UpdatedAt = now
    });

    await db.ExecuteAsync("""
        update password_reset_codes
        set consumed_at = @ConsumedAt
        where id = @Id
        """, new
    {
        Id = row.Id,
        ConsumedAt = now
    });

    await db.ExecuteAsync("delete from sessions where user_id = @UserId", new { UserId = row.UserId });

    return Results.Ok(new { message = "Senha redefinida com sucesso" });
}).AllowAnonymous();
api.MapPut("/users/{id}/authorizations", async (string id, UserAuthorizationUpdate request, HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var currentUser = await CurrentUser(http, db);
    if (currentUser is null) return Results.Unauthorized();

    var targetUser = await QueryUser(db, id, onlyActive: true);

    if (targetUser is null) return Results.NotFound(new { detail = "Usuario nao encontrado" });

    if (request.Funcoes is not null)
    {
        await ReplaceFuncoes(db, targetUser.Id, request.Funcoes);
    }

    if (request.ClienteIds is not null)
    {
        await ReplaceUserClientes(db, targetUser.Id, request.ClienteIds);
    }

    var funcoes = await EnsureDefaultFuncoes(db, targetUser.Id);
    var clientes = await QueryClientes(db, targetUser.Id);
    return Results.Ok(ToUserResponse(targetUser, funcoes, clientes));
}).RequireAuthorization("ExPontoUser");

api.MapPost("/usuarios-vinculados", async (
    LinkedUserCreate request,
    HttpContext http,
    ExPontoDataSource dataSource,
    UserNotificationEmailService emailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var master = await CurrentUser(http, db);
    if (master is null) return Results.Unauthorized();
    if (IsLinkedUser(master)) return Results.Forbid();

    var normalizedEmail = request.Email.Trim();
    var existingUser = await QueryUserByEmail(db, normalizedEmail);
    if (existingUser is not null)
    {
        if (!existingUser.MustChangePassword)
        {
            await emailService.SendExistingUserAlreadyAccessedAdminNotificationAsync(existingUser, cancellationToken);
            return Results.BadRequest(new { detail = "Email ja cadastrado. O administrador foi notificado que o usuario ja acessou e pode redefinir a senha." });
        }

        await using var resendTransaction = await db.BeginTransactionAsync(cancellationToken);
        var newTemporaryPassword = GenerateTemporaryPassword();
        existingUser.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newTemporaryPassword);
        existingUser.UpdatedAt = DateTime.UtcNow;

        await db.ExecuteAsync("""
            update users
            set password_hash = @PasswordHash,
                must_change_password = true,
                updated_at = @UpdatedAt
            where id = @Id
            """, existingUser, resendTransaction);

        await emailService.SendExistingProvisionalPasswordWillBeResentAdminNotificationAsync(existingUser, cancellationToken);
        var resendEmailSent = await emailService.SendUserRegisteredAsync(existingUser, newTemporaryPassword, request.UrlAndroid, request.UrlIos, cancellationToken);
        if (!resendEmailSent)
        {
            await resendTransaction.RollbackAsync(cancellationToken);
            return Results.Problem(
                detail: "Nova senha provisoria nao reenviada porque o email de acesso nao foi enviado.",
                statusCode: StatusCodes.Status502BadGateway,
                title: "Falha ao enviar email");
        }

        await resendTransaction.CommitAsync(cancellationToken);
        await emailService.SendUserRegisteredAdminNotificationAsync(existingUser, cancellationToken);

        var existingFuncoes = await EnsureDefaultFuncoes(db, master.Id);
        var existingClientes = await QueryClientes(db, master.Id);
        return Results.Ok(ToUserResponse(existingUser, existingFuncoes, existingClientes));
    }

    var integrante = await QueryIntegrante(db, request.IntegranteId, master.Id);
    if (integrante is null || !integrante.Ativo)
    {
        return Results.NotFound(new { detail = "Integrante nao encontrado" });
    }

    await using var transaction = await db.BeginTransactionAsync(cancellationToken);

    var temporaryPassword = GenerateTemporaryPassword();
    var linkedUser = new UserRow
    {
        Id = NewId(),
        Nome = string.IsNullOrWhiteSpace(request.Nome) ? integrante.Nome : request.Nome.Trim(),
        Email = normalizedEmail,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(temporaryPassword),
        MustChangePassword = true,
        Ativo = true,
        MasterUserId = master.Id,
        IntegranteVinculadoId = integrante.Id,
        CreatedAt = DateTime.UtcNow
    };

    await db.ExecuteAsync("""
        insert into users
            (id, nome, email, password_hash, must_change_password, ativo, master_user_id, integrante_vinculado_id, created_at)
        values
            (@Id, @Nome, @Email, @PasswordHash, @MustChangePassword, @Ativo, @MasterUserId, @IntegranteVinculadoId, @CreatedAt)
        """, linkedUser, transaction);

    var emailSent = await emailService.SendUserRegisteredAsync(linkedUser, temporaryPassword, request.UrlAndroid, request.UrlIos, cancellationToken);
    if (!emailSent)
    {
        await transaction.RollbackAsync(cancellationToken);
        return Results.Problem(
            detail: "Usuario vinculado nao cadastrado porque o email de acesso nao foi enviado.",
            statusCode: StatusCodes.Status502BadGateway,
            title: "Falha ao enviar email");
    }

    await transaction.CommitAsync(cancellationToken);
    await emailService.SendUserRegisteredAdminNotificationAsync(linkedUser, cancellationToken);

    var funcoes = await EnsureDefaultFuncoes(db, master.Id);
    var clientes = await QueryClientes(db, master.Id);
    return Results.Ok(ToUserResponse(linkedUser, funcoes, clientes));
}).RequireAuthorization("ExPontoUser");

api.MapGet("/funcoes", async (HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();

    var rows = await EnsureDefaultFuncoes(db, DataOwnerId(user));
    return Results.Ok(rows.Select(ToFuncaoResponse));
}).RequireAuthorization("ExPontoUser");

api.MapPost("/funcoes", async (FuncaoCreate request, HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    if (IsLinkedUser(user)) return Results.Forbid();
    var funcaoBaseId = await EnsureFuncaoBase(db, request.Funcao);

    var row = new FuncaoRow
    {
        Id = NewId(),
        IdUsuario = user.Id,
        FuncaoBaseId = funcaoBaseId,
        Funcao = request.Funcao,
        Valor = request.Valor,
        CreatedAt = DateTime.UtcNow
    };

    await db.ExecuteAsync("""
        insert into funcoes (id, id_usuario, funcao_base_id, funcao, valor, created_at)
        values (@Id, @IdUsuario, @FuncaoBaseId, @Funcao, @Valor, @CreatedAt)
        """, row);

    return Results.Ok(ToFuncaoResponse(row));
}).RequireAuthorization("ExPontoUser");

api.MapPut("/funcoes/{id}", async (string id, FuncaoCreate request, HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    if (IsLinkedUser(user)) return Results.Forbid();
    var funcaoBaseId = await EnsureFuncaoBase(db, request.Funcao);

    var updated = await db.QuerySingleOrDefaultAsync<FuncaoRow>("""
        update funcoes
        set funcao_base_id = @FuncaoBaseId, funcao = @Funcao, valor = @Valor, updated_at = @UpdatedAt
        where id = @Id and id_usuario = @UserId
        returning id, id_usuario as IdUsuario, funcao_base_id as FuncaoBaseId, funcao, valor, created_at as CreatedAt, updated_at as UpdatedAt
        """, new { Id = id, UserId = user.Id, FuncaoBaseId = funcaoBaseId, request.Funcao, request.Valor, UpdatedAt = DateTime.UtcNow });

    return updated is null ? Results.NotFound(new { detail = "Funcao nao encontrada" }) : Results.Ok(ToFuncaoResponse(updated));
}).RequireAuthorization("ExPontoUser");

api.MapDelete("/funcoes/{id}", async (string id, HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    if (IsLinkedUser(user)) return Results.Forbid();

    await db.ExecuteAsync("delete from funcoes where id = @Id and id_usuario = @UserId", new { Id = id, UserId = user.Id });
    return Results.Ok(new { message = "Funcao excluida com sucesso" });
}).RequireAuthorization("ExPontoUser");

api.MapGet("/integrantes", async (HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();

    var rows = await QueryIntegrantes(db, DataOwnerId(user));
    return Results.Ok(rows.Select(i => ToIntegranteResponse(i)));
}).RequireAuthorization("ExPontoUser");

api.MapGet("/integrantes/all", async (HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();

    var rows = await QueryIntegrantes(db, DataOwnerId(user));
    return Results.Ok(rows.Select(i => ToIntegranteResponse(i)));
}).RequireAuthorization("ExPontoUser");

api.MapPost("/integrantes", async (IntegranteCreate request, HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    if (IsLinkedUser(user)) return Results.Forbid();

    var existing = await QueryIntegranteByIdOrCpf(db, request.Id, request.Cpf, user.Id);
    if (existing is not null)
    {
        var derivedFaceEncoding = request.FaceEncoding?.ToArray()
            ?? (request.BiometriaFacial is null ? existing.FaceEncoding : ImageSignature(request.BiometriaFacial).ToArray());
        var updated = existing with
        {
            Cpf = request.Cpf,
            Nome = request.Nome,
            FuncaoId = request.FuncaoId,
            BiometriaFacial = null,
            FaceEncoding = derivedFaceEncoding,
            Ativo = true,
            UpdatedAt = DateTime.UtcNow
        };

        await db.ExecuteAsync("""
            update integrantes
            set cpf = @Cpf, nome = @Nome, funcao_id = @FuncaoId, biometria_facial = @BiometriaFacial,
                face_encoding = @FaceEncoding, ativo = @Ativo, updated_at = @UpdatedAt
            where id = @Id and id_usuario = @IdUsuario
            """, updated);

        var savedExisting = await QueryIntegrante(db, updated.Id, user.Id);
        return Results.Ok(ToIntegranteResponse(savedExisting!));
    }

    var derivedFaceEncodingOnCreate = request.FaceEncoding?.ToArray()
        ?? (request.BiometriaFacial is null ? null : ImageSignature(request.BiometriaFacial).ToArray());
    var row = new IntegranteRow
    {
        Id = string.IsNullOrWhiteSpace(request.Id) ? NewId() : request.Id,
        IdUsuario = user.Id,
        Cpf = request.Cpf,
        Nome = request.Nome,
        FuncaoId = request.FuncaoId,
        BiometriaFacial = null,
        FaceEncoding = derivedFaceEncodingOnCreate,
        Ativo = true,
        CreatedAt = DateTime.UtcNow
    };

    await db.ExecuteAsync("""
        insert into integrantes (id, id_usuario, cpf, nome, funcao_id, biometria_facial, face_encoding, ativo, created_at)
        values (@Id, @IdUsuario, @Cpf, @Nome, @FuncaoId, @BiometriaFacial, @FaceEncoding, @Ativo, @CreatedAt)
        """, row);

    var saved = await QueryIntegrante(db, row.Id, user.Id);
    return Results.Ok(ToIntegranteResponse(saved!));
}).RequireAuthorization("ExPontoUser");

api.MapPut("/integrantes/{id}", async (string id, IntegranteUpdate request, HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    if (IsLinkedUser(user)) return Results.Forbid();

    var current = await QueryIntegrante(db, id, user.Id);
    if (current is null) return Results.NotFound(new { detail = "Integrante nao encontrado" });

    var derivedFaceEncoding = request.RemoverBiometria
        ? null
        : request.FaceEncoding?.ToArray()
            ?? (request.BiometriaFacial is null ? current.FaceEncoding : ImageSignature(request.BiometriaFacial).ToArray());
    var next = current with
    {
        Nome = request.Nome ?? current.Nome,
        FuncaoId = request.FuncaoId ?? current.FuncaoId,
        BiometriaFacial = null,
        FaceEncoding = derivedFaceEncoding,
        Ativo = request.Ativo ?? current.Ativo,
        UpdatedAt = DateTime.UtcNow
    };

    await db.ExecuteAsync("""
        update integrantes
        set nome = @Nome, funcao_id = @FuncaoId, biometria_facial = @BiometriaFacial,
            face_encoding = @FaceEncoding, ativo = @Ativo, updated_at = @UpdatedAt
        where id = @Id and id_usuario = @IdUsuario
        """, next);

    var saved = await QueryIntegrante(db, id, user.Id);
    return Results.Ok(ToIntegranteResponse(saved!));
}).RequireAuthorization("ExPontoUser");

api.MapDelete("/integrantes/{id}", async (string id, HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    if (IsLinkedUser(user)) return Results.Forbid();

    var hasActiveLoan = await db.ExecuteScalarAsync<bool>("""
        select exists(
            select 1 from integrante_emprestimos
            where integrante_id=@Id and equipe_origem_id=@UserId and status='ativo'
        )
        """, new { Id = id, UserId = user.Id });
    if (hasActiveLoan)
        return Results.Conflict(new { detail = "Encerre o emprestimo ativo antes de excluir o integrante." });

    await db.ExecuteAsync("""
        update integrantes
        set ativo = false, updated_at = @UpdatedAt
        where id = @Id and id_usuario = @UserId
        """, new { Id = id, UserId = user.Id, UpdatedAt = DateTime.UtcNow });

    return Results.Ok(new { message = "Integrante excluido com sucesso" });
}).RequireAuthorization("ExPontoUser");

api.MapGet("/integrantes/face-encodings", async (HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();

    var rows = await QueryIntegrantes(db, DataOwnerId(user), onlyActiveWithFaceEncoding: true);
    var integrantes = rows.Select(i => ToIntegranteResponse(i, includeEncoding: true)).ToArray();
    return Results.Ok(new { integrantes, total = integrantes.Length });
}).RequireAuthorization("ExPontoUser");

api.MapGet("/emprestimos", async (HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    var ownerId = DataOwnerId(user);
    await EncerrarEmprestimosVencidos(db);
    var rows = await QueryEmprestimos(db, ownerId);
    return Results.Ok(rows);
}).RequireAuthorization("ExPontoUser");

api.MapPost("/integrantes/{id}/emprestimos", async (
    string id,
    EmprestimoCreate request,
    HttpContext http,
    ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    if (IsLinkedUser(user)) return Results.Forbid();
    var integrante = await QueryIntegrante(db, id, user.Id);
    if (integrante is null || integrante.IdUsuario != user.Id)
        return Results.NotFound(new { detail = "Integrante nao encontrado na equipe de origem." });
    var target = await QueryUserByEmail(db, request.EquipeDestinoEmail.Trim());
    if (target is null || !target.Ativo) return Results.NotFound(new { detail = "Equipe receptora nao encontrada." });
    var destinationId = DataOwnerId(target);
    if (destinationId == user.Id) return Results.BadRequest(new { detail = "A equipe receptora deve ser diferente da origem." });
    if (request.DataFim < request.DataInicio) return Results.BadRequest(new { detail = "Data final menor que a inicial." });

    var overlaps = await db.ExecuteScalarAsync<bool>("""
        select exists(
            select 1 from integrante_emprestimos
            where integrante_id = @IntegranteId
              and status = 'ativo'
              and data_inicio <= cast(@DataFim as date)
              and data_fim >= cast(@DataInicio as date)
        )
        """, new
    {
        IntegranteId = id,
        DataInicio = ExPontoSql.DateParameter(request.DataInicio),
        DataFim = ExPontoSql.DateParameter(request.DataFim)
    });
    if (overlaps) return Results.Conflict(new { detail = "O integrante ja possui emprestimo ativo nesse periodo." });

    var loanId = NewId();
    await db.ExecuteAsync("""
        insert into integrante_emprestimos
            (id, integrante_id, equipe_origem_id, equipe_destino_id, data_inicio, data_fim, status, created_at)
        values
            (@Id, @IntegranteId, @OrigemId, @DestinoId, cast(@DataInicio as date), cast(@DataFim as date), 'ativo', @Now)
        """, new
    {
        Id = loanId,
        IntegranteId = id,
        OrigemId = user.Id,
        DestinoId = destinationId,
        DataInicio = ExPontoSql.DateParameter(request.DataInicio),
        DataFim = ExPontoSql.DateParameter(request.DataFim),
        Now = DateTime.UtcNow
    });
    await CriarNotificacao(db, destinationId, "emprestimo_recebido", "Integrante recebido por emprestimo",
        $"{integrante.Nome} foi disponibilizado de {request.DataInicio:dd/MM/yyyy} ate {request.DataFim:dd/MM/yyyy}.", loanId, $"loan:{loanId}:received");
    return Results.Ok(new { id = loanId, detail = "Emprestimo criado." });
}).RequireAuthorization("ExPontoUser");

api.MapPost("/emprestimos/{id}/renovacoes", async (
    string id,
    EmprestimoRenovacaoCreate request,
    HttpContext http,
    ExPontoDataSource dataSource,
    IntegranteLoanEmailService emailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    var ownerId = DataOwnerId(user);
    var loan = await QueryEmprestimo(db, id);
    if (loan is null || loan.EquipeDestinoId != ownerId) return Results.NotFound(new { detail = "Emprestimo nao encontrado." });
    if (loan.Status != "ativo") return Results.BadRequest(new { detail = "Emprestimo nao esta ativo." });
    if (request.NovaDataFim <= loan.DataFim) return Results.BadRequest(new { detail = "A nova data deve ser posterior ao vencimento atual." });
    var alreadyPending = await db.ExecuteScalarAsync<bool>(
        "select exists(select 1 from emprestimo_renovacoes where emprestimo_id=@Id and status='pendente')",
        new { Id = id });
    if (alreadyPending) return Results.Conflict(new { detail = "Ja existe uma renovacao aguardando decisao." });

    var renewalId = NewId();
    var token = NewToken();
    await db.ExecuteAsync("""
        insert into emprestimo_renovacoes
            (id, emprestimo_id, data_fim_atual, nova_data_fim, status, approval_token, solicitado_em)
        values (@Id, @EmprestimoId, cast(@Atual as date), cast(@Nova as date), 'pendente', @Token, @Now)
        """, new
    {
        Id = renewalId,
        EmprestimoId = id,
        Atual = ExPontoSql.DateParameter(loan.DataFim),
        Nova = ExPontoSql.DateParameter(request.NovaDataFim),
        Token = token,
        Now = DateTime.UtcNow
    });
    await emailService.SendRenewalRequestedAsync(loan, renewalId, token, request.NovaDataFim, cancellationToken);
    await CriarNotificacao(db, ownerId, "renovacao_solicitada", "Renovacao solicitada",
        $"A renovacao de {loan.IntegranteNome} foi enviada para a equipe de origem.", id, $"renewal:{renewalId}:requested");
    return Results.Ok(new { id = renewalId, detail = "Renovacao solicitada." });
}).RequireAuthorization("ExPontoUser");

api.MapPost("/emprestimos/{id}/devolver", async (string id, HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    var ownerId = DataOwnerId(user);
    var loan = await QueryEmprestimo(db, id);
    if (loan is null || (loan.EquipeOrigemId != ownerId && loan.EquipeDestinoId != ownerId)) return Results.NotFound();
    await db.ExecuteAsync("""
        update integrante_emprestimos
        set status='devolvido', devolvido_em=@Now, updated_at=@Now
        where id=@Id and status='ativo'
        """, new { Id = id, Now = DateTime.UtcNow });
    await CriarNotificacao(db, loan.EquipeOrigemId, "emprestimo_devolvido", "Integrante devolvido",
        $"O emprestimo de {loan.IntegranteNome} foi encerrado.", id, $"loan:{id}:returned:origin");
    await CriarNotificacao(db, loan.EquipeDestinoId, "emprestimo_devolvido", "Emprestimo encerrado",
        $"{loan.IntegranteNome} nao esta mais disponivel para novos apontamentos.", id, $"loan:{id}:returned:destination");
    return Results.Ok(new { detail = "Emprestimo encerrado." });
}).RequireAuthorization("ExPontoUser");

api.MapGet("/notificacoes", async (HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    var ownerId = DataOwnerId(user);
    await GerarNotificacoesVencimentoEmprestimo(db, ownerId);
    var rows = await db.QueryAsync("""
        select id, tipo, titulo, mensagem, referencia_id, lida_em, created_at
        from notificacoes_usuario
        where usuario_id=@UserId
        order by created_at desc
        limit 100
        """, new { UserId = ownerId });
    return Results.Ok(rows);
}).RequireAuthorization("ExPontoUser");

api.MapPut("/notificacoes/{id}/lida", async (string id, HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    await db.ExecuteAsync("update notificacoes_usuario set lida_em=coalesce(lida_em,@Now) where id=@Id and usuario_id=@UserId",
        new { Id = id, UserId = DataOwnerId(user), Now = DateTime.UtcNow });
    return Results.Ok();
}).RequireAuthorization("ExPontoUser");

api.MapGet("/emprestimos/renovacoes/{id}/{decisao}", async (
    string id,
    string decisao,
    string token,
    ExPontoDataSource dataSource) =>
{
    if (decisao is not ("aprovar" or "recusar")) return Results.NotFound();
    await using var db = await dataSource.OpenConnectionAsync();
    var result = await DecideEmprestimoRenovacao(db, id, token, decisao == "aprovar" ? "aprovada" : "recusada");
    if (result is null) return Results.NotFound("Solicitacao nao encontrada ou token invalido.");
    return Results.Content($"<h2>Renovacao {result.Status}</h2><p>{WebUtility.HtmlEncode(result.IntegranteNome)}: {result.DataFim:dd/MM/yyyy}</p>", "text/html; charset=utf-8");
}).AllowAnonymous();

api.MapGet("/clientes", async (ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var rows = await QueryClientes(db, userId: "");
    return Results.Ok(rows.Select(c => ToClienteResponse(c)));
}).AllowAnonymous();
api.MapPost("/clientes", async ([FromBody] JsonElement body, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var requests = ParseClientePayload(body);
    if (requests.Count == 0)
    {
        return Results.BadRequest(new { detail = "Envie um cliente ou uma lista de clientes no corpo da requisicao." });
    }

    var created = new List<ClienteResponse>();
    var errors = new List<object>();

    foreach (var request in requests)
    {
        var validationError = ValidateCliente(request);
        if (validationError is not null)
        {
            errors.Add(new { id_aprovado = request.IdAprovado, nome = request.Nome, detail = validationError });
            continue;
        }

        var row = await UpsertCliente(db, request);
        created.Add(ToClienteResponse(row));
    }

    if (errors.Count > 0)
    {
        Console.WriteLine($"Importacao de clientes recusada: {JsonSerializer.Serialize(errors)}");
        return Results.BadRequest(new { detail = "Alguns clientes possuem dados invalidos.", imported = created.Count, errors });
    }

    return body.ValueKind == JsonValueKind.Array
        ? Results.Ok(new { imported = created.Count, clientes = created })
        : Results.Ok(created[0]);
}).AllowAnonymous();
api.MapGet("/clientes/nearby", async (double latitude, double longitude, HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();

    var rows = await QueryClientes(db, DataOwnerId(user));
    var result = rows.Select(c =>
    {
        var distance = DistanceMeters(latitude, longitude, c.Latitude, c.Longitude);
        return ToClienteResponse(c) with { Distancia = distance, DentroDoRaio = distance <= c.Raio };
    }).OrderBy(c => c.Distancia);

    return Results.Ok(result);
}).RequireAuthorization("ExPontoUser");

api.MapPost("/apontamentos/identificar", async (IdentificacaoRequest request, HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();

    var signature = ImageSignature(request.BiometriaFacial);
    var rows = await QueryIntegrantes(db, DataOwnerId(user), onlyActiveWithFaceEncoding: true);
    var best = rows
        .Select(i => new { Integrante = i, Score = Similarity(i.FaceEncoding ?? Array.Empty<double>(), signature) })
        .OrderByDescending(i => i.Score)
        .FirstOrDefault();

    if (best is null || best.Score < 0.72)
    {
        return Results.Ok(new { integrante = (object?)null, confianca = 0.0, message = "Nenhuma correspondencia encontrada." });
    }

    return Results.Ok(new { integrante = ToIntegranteResponse(best.Integrante), confianca = Math.Round(best.Score, 3) });
}).RequireAuthorization("ExPontoUser");

api.MapPost("/apontamentos", async (
    ApontamentoCreate request,
    HttpContext http,
    ExPontoDataSource dataSource,
    CronogramaExceptionEmailService cronogramaEmailService,
    MaintenanceExceptionEmailService maintenanceEmailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    var ownerId = DataOwnerId(user);

    var response = await CreateApontamento(
        db,
        ownerId,
        request.ClienteId,
        request.IntegranteId,
        request.Tipo,
        request.Geolocalizacao,
        request.BiometriaFacial,
        null,
        false,
        false,
        false,
        false,
        request.LocalizacaoStatus,
        request.SolicitacaoAjusteId,
        request.CronogramaJustificativa,
        cronogramaEmailService,
        maintenanceEmailService,
        user,
        cancellationToken);
    return response is null ? Results.NotFound(new { detail = "Cliente ou integrante nao encontrado" }) : Results.Ok(response);
}).RequireAuthorization("ExPontoUser");

api.MapGet("/apontamentos", async (
    DateTime? data_inicio,
    DateTime? data_fim,
    string? cliente_id,
    HttpContext http,
    ExPontoDataSource dataSource,
    ILogger<ExPontoLog> logger) =>
{
    try
    {
        await using var db = await dataSource.OpenConnectionAsync();
        var user = await CurrentUser(http, db);
        if (user is null) return Results.Unauthorized();
        var ownerId = DataOwnerId(user);

        var rows = await db.QueryAsync<ApontamentoListRow>("""
            select a.id, a.id_usuario as IdUsuario, a.cliente_id as ClienteId, c.nome as ClienteNome,
                   a.integrante_id as IntegranteId, i.nome as IntegranteNome,
                   a.tipo, a.latitude, a.longitude, a.timestamp, a.dentro_do_raio as DentroDoRaio,
                   a.biometria_verificada as BiometriaVerificada,
                   a.localizacao_status as LocalizacaoStatus,
                   a.solicitacao_ajuste_id as SolicitacaoAjusteId,
                   a.cronograma_fase_id as CronogramaFaseId,
                   a.cronograma_fase_sigla as CronogramaFaseSigla,
                   a.dia_extra as DiaExtra,
                   a.funcao_extra as FuncaoExtra,
                   a.cronograma_status as CronogramaStatus,
                   a.cronograma_excecao_id as CronogramaExcecaoId,
                   a.cronograma_justificativa as CronogramaJustificativa,
                   a.manutencao_status as ManutencaoStatus,
                   a.manutencao_excecao_id as ManutencaoExcecaoId,
                   a.created_at as CreatedAt
            from apontamentos a
            left join clientes c on c.id = a.cliente_id
            left join integrantes i on i.id = a.integrante_id
            where a.id_usuario = @UserId
              and (cast(@ClienteId as text) is null or a.cliente_id = cast(@ClienteId as text))
              and (cast(@DataInicio as timestamptz) is null or a.timestamp >= cast(@DataInicio as timestamptz))
              and (cast(@DataFim as timestamptz) is null or a.timestamp <= cast(@DataFim as timestamptz))
            order by a.timestamp desc
            limit 1000
            """, new { UserId = ownerId, ClienteId = cliente_id, DataInicio = data_inicio, DataFim = data_fim });

        return Results.Ok(rows.Select(ToApontamentoResponse));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Falha ao consultar apontamentos. cliente_id={ClienteId}, data_inicio={DataInicio}, data_fim={DataFim}", cliente_id, data_inicio, data_fim);
        return Results.Problem(
            title: "Falha ao consultar apontamentos",
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireAuthorization("ExPontoUser");

api.MapGet("/apontamentos/sync-cache", async (
    HttpContext http,
    ExPontoDataSource dataSource,
    ILogger<ExPontoLog> logger) =>
{
    try
    {
        await using var db = await dataSource.OpenConnectionAsync();
        var user = await CurrentUser(http, db);
        if (user is null) return Results.Unauthorized();
        var ownerId = DataOwnerId(user);

        var totalUsuario = await db.ExecuteScalarAsync<int>(
            "select count(*) from apontamentos where id_usuario = @UserId",
            new { UserId = ownerId });

        var rows = await db.QueryAsync<ApontamentoListRow>("""
            select a.id, a.id_usuario as IdUsuario, a.cliente_id as ClienteId, c.nome as ClienteNome,
                   a.integrante_id as IntegranteId, i.nome as IntegranteNome,
                   a.tipo, a.latitude, a.longitude, a.timestamp, a.dentro_do_raio as DentroDoRaio,
                   a.biometria_verificada as BiometriaVerificada,
                   a.localizacao_status as LocalizacaoStatus,
                   a.solicitacao_ajuste_id as SolicitacaoAjusteId,
                   a.cronograma_fase_id as CronogramaFaseId,
                   a.cronograma_fase_sigla as CronogramaFaseSigla,
                   a.dia_extra as DiaExtra,
                   a.funcao_extra as FuncaoExtra,
                   a.cronograma_status as CronogramaStatus,
                   a.cronograma_excecao_id as CronogramaExcecaoId,
                   a.cronograma_justificativa as CronogramaJustificativa,
                   a.manutencao_status as ManutencaoStatus,
                   a.manutencao_excecao_id as ManutencaoExcecaoId,
                   a.created_at as CreatedAt
            from apontamentos a
            left join clientes c on c.id = a.cliente_id
            left join integrantes i on i.id = a.integrante_id
            where a.id_usuario = @UserId
            order by a.timestamp desc
            limit 5000
            """, new { UserId = ownerId });

        var apontamentos = rows.Select(ToApontamentoResponse).ToList();
        return Results.Ok(new
        {
            user_id = ownerId,
            user_email = user.Email,
            total_usuario = totalUsuario,
            total_retorno = apontamentos.Count,
            apontamentos
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Falha ao consultar apontamentos para sync-cache.");
        return Results.Problem(
            title: "Falha ao consultar apontamentos para sincronizacao",
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireAuthorization("ExPontoUser");

api.MapGet("/apontamentos/presentes/{clienteId}", async (string clienteId, HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    var ownerId = DataOwnerId(user);

    var start = DateTime.UtcNow.Hour < 6
        ? DateTime.UtcNow.Date.AddDays(-1).AddHours(22)
        : DateTime.UtcNow.Date;

    var rows = await db.QueryAsync<PresencaRow>("""
        select a.integrante_id as IntegranteId, a.tipo, a.timestamp, a.dentro_do_raio as DentroDoRaio,
               a.biometria_verificada as BiometriaVerificada,
               i.id, i.id_usuario as IdUsuario, i.cpf, i.nome, i.funcao_id as FuncaoId,
               f.funcao as FuncaoNome, i.ativo,
               i.created_at as CreatedAt, i.updated_at as UpdatedAt
        from apontamentos a
        join integrantes i on i.id = a.integrante_id
        left join funcoes f on f.id = i.funcao_id and f.id_usuario = @UserId
        where a.id_usuario = @UserId and a.cliente_id = @ClienteId and a.timestamp >= @Start
        order by a.timestamp
        """, new { UserId = ownerId, ClienteId = clienteId, Start = start });

    var presence = new Dictionary<string, PresencaRow>();
    foreach (var row in rows)
    {
        if (row.Tipo == "entrada") presence[row.IntegranteId] = row;
        if (row.Tipo == "saida") presence.Remove(row.IntegranteId);
    }

    var result = presence.Values.Select(row => new
    {
        integrante = ToIntegranteResponse(row.ToIntegrante()),
        entrada = row.Timestamp,
        dentro_do_raio = row.DentroDoRaio,
        biometria_verificada = row.BiometriaVerificada
    });

    return Results.Ok(result);
}).RequireAuthorization("ExPontoUser");

api.MapPost("/sync/apontamentos", async (
    SyncRequest request,
    HttpContext http,
    ExPontoDataSource dataSource,
    CronogramaExceptionEmailService cronogramaEmailService,
    MaintenanceExceptionEmailService maintenanceEmailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    var ownerId = DataOwnerId(user);

    var results = new List<SyncItemResult>();
    foreach (var item in request.Apontamentos)
    {
        var created = await CreateApontamento(
            db,
            ownerId,
            item.IdCliente,
            item.IdIntegrante,
            item.Tipo,
            item.Geolocalizacao,
            item.BiometriaFacial,
            item.Timestamp,
            item.LivenessVerified,
            item.OfflineFaceVerified,
            item.OfflineDeviceBiometricVerified,
            syncedFromOffline: true,
            item.LocalizacaoStatus,
            item.SolicitacaoAjusteId,
            item.CronogramaJustificativa,
            cronogramaEmailService,
            maintenanceEmailService,
            user,
            cancellationToken);

        results.Add(created is null
            ? new SyncItemResult(item.OfflineId, null, "error", "Cliente ou integrante nao encontrado", null, null, null, null, null, null, null, null, null)
            : new SyncItemResult(
                item.OfflineId,
                created.Id,
                "success",
                CronogramaMessage(created),
                created.BiometriaVerificada,
                created.DentroDoRaio,
                created.CronogramaFaseSigla,
                created.DiaExtra,
                created.FuncaoExtra,
                created.CronogramaStatus,
                created.CronogramaExcecaoId,
                created.ManutencaoStatus,
                created.ManutencaoExcecaoId));
    }

    return Results.Ok(new
    {
        total = results.Count,
        synced = results.Count(r => r.Status == "success"),
        errors = results.Count(r => r.Status == "error"),
        results
    });
}).RequireAuthorization("ExPontoUser");

api.MapPost("/apontamentos/enviar-email", async (
    HttpContext http,
    ExPontoDataSource dataSource,
    ApontamentoSyncEmailService emailService,
    CronogramaExceptionEmailService cronogramaEmailService,
    MaintenanceExceptionEmailService maintenanceEmailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();

    var emailUser = IsLinkedUser(user)
        ? await QueryUser(db, DataOwnerId(user)) ?? user
        : user;
    var reclassified = await ReclassificarApontamentosSemCronograma(
        db,
        emailUser,
        cronogramaEmailService,
        maintenanceEmailService,
        cancellationToken);
    var internalNotificationsSent = await EnviarNotificacoesExcecoesPendentes(
        db,
        emailUser,
        cronogramaEmailService,
        maintenanceEmailService,
        cancellationToken);
    var sent = await emailService.SendApontamentosResumoAsync(db, emailUser, cancellationToken);
    return sent
        ? Results.Ok(new
        {
            detail = "Resumo enviado por email.",
            apontamentos_reclassificados = reclassified,
            notificacoes_internas_enviadas = internalNotificationsSent
        })
        : Results.BadRequest(new { detail = "Nao ha apontamentos sincronizados para enviar por email." });
}).RequireAuthorization("ExPontoUser");

api.MapGet("/cronograma/fases", async (string cliente_id, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();

    var rows = await db.QueryAsync("""
        select f.id, f.cliente_id, f.fase, f.data_inicio, f.data_fim,
               coalesce(json_agg(json_build_object(
                   'funcao', coalesce(fb.funcao, ff.funcao_nome),
                   'limite_integrantes', ff.limite_integrantes
               )) filter (where ff.id is not null), '[]'::json) as funcoes
        from cronograma_fases f
        left join cronograma_fase_funcoes ff on ff.fase_id = f.id
        left join funcoes_base fb on fb.id = ff.funcao_base_id
        where f.cliente_id = @ClienteId
        group by f.id
        order by f.data_inicio
        """, new { ClienteId = cliente_id });

    return Results.Ok(rows);
}).AllowAnonymous();
api.MapPost("/cronograma/fases", async (CronogramaFaseCreate request, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();

    var cliente = await QueryCliente(db, request.ClienteId, "");
    if (cliente is null) return Results.NotFound(new { detail = "Cliente nao encontrado" });
    if (string.IsNullOrWhiteSpace(request.Fase)) return Results.BadRequest(new { detail = "Informe a fase." });
    if (request.DataFim < request.DataInicio)
    {
        return Results.BadRequest(new { detail = "Data final nao pode ser menor que a data inicial." });
    }

    await using var transaction = await db.BeginTransactionAsync();
    var faseId = await UpsertCronogramaFase(
        db,
        request.ClienteId,
        request.Fase,
        request.DataInicio,
        request.DataFim,
        request.Funcoes,
        transaction,
        request.Id);

    await transaction.CommitAsync();
    return Results.Ok(new { id = faseId, detail = "Fase cadastrada." });
}).AllowAnonymous();
api.MapPost("/cronograma/importar", async (CronogramaImportRequest request, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    await using var transaction = await db.BeginTransactionAsync();

    var clientesImportados = new List<ClienteResponse>();
    var fasesImportadas = new List<object>();
    var errors = new List<object>();
    var clientesPorIdAprovado = new Dictionary<string, ClienteRow>(StringComparer.OrdinalIgnoreCase);

    foreach (var clienteRequest in request.Clientes ?? Array.Empty<ClienteCreate>())
    {
        var validationError = ValidateCliente(clienteRequest);
        if (validationError is not null)
        {
            errors.Add(new { tipo = "cliente", id_aprovado = clienteRequest.IdAprovado, detail = validationError });
            continue;
        }

        var cliente = await UpsertCliente(db, clienteRequest, transaction);
        clientesPorIdAprovado[cliente.IdAprovado] = cliente;
        clientesImportados.Add(ToClienteResponse(cliente));
    }

    foreach (var faseRequest in request.CronogramaFases ?? Array.Empty<CronogramaImportFase>())
    {
        if (string.IsNullOrWhiteSpace(faseRequest.Fase))
        {
            errors.Add(new { tipo = "cronograma_fase", cliente_id = faseRequest.ClienteId, id_aprovado = faseRequest.IdAprovado, detail = "Informe a fase." });
            continue;
        }

        if (faseRequest.DataFim < faseRequest.DataInicio)
        {
            errors.Add(new { tipo = "cronograma_fase", fase = faseRequest.Fase, detail = "Data final nao pode ser menor que a data inicial." });
            continue;
        }

        var cliente = await ResolveCronogramaCliente(db, faseRequest.ClienteId, faseRequest.IdAprovado, clientesPorIdAprovado, transaction);
        if (cliente is null)
        {
            errors.Add(new { tipo = "cronograma_fase", fase = faseRequest.Fase, cliente_id = faseRequest.ClienteId, id_aprovado = faseRequest.IdAprovado, detail = "Cliente nao encontrado." });
            continue;
        }

        var faseId = await UpsertCronogramaFase(
            db,
            cliente.Id,
            faseRequest.Fase,
            faseRequest.DataInicio,
            faseRequest.DataFim,
            faseRequest.Funcoes,
            transaction,
            faseRequest.Id);

        fasesImportadas.Add(new
        {
            id = faseId,
            cliente_id = cliente.Id,
            id_aprovado = cliente.IdAprovado,
            fase = faseRequest.Fase,
            data_inicio = faseRequest.DataInicio,
            data_fim = faseRequest.DataFim,
            funcoes = faseRequest.Funcoes?.Count ?? 0
        });
    }

    if (errors.Count > 0)
    {
        await transaction.RollbackAsync();
        return Results.BadRequest(new
        {
            detail = "Importacao recusada. Corrija os itens indicados e envie novamente.",
            errors
        });
    }

    await transaction.CommitAsync();
    return Results.Ok(new
    {
        clientes = clientesImportados.Count,
        cronograma_fases = fasesImportadas.Count,
        detalhes = new
        {
            clientes = clientesImportados,
            cronograma_fases = fasesImportadas
        }
    });
}).AllowAnonymous();
api.MapGet("/cronograma-excecoes/grupo/{id}/aprovar", async (
    string id,
    string token,
    ExPontoDataSource dataSource,
    CronogramaExceptionEmailService emailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var result = await DecideCronogramaExceptionGroup(db, id, token, "aprovado");
    if (result is null) return Results.NotFound("Solicitacao nao encontrada ou token invalido.");
    await emailService.SendCronogramaExceptionGroupDecisionAsync(result, cancellationToken);
    return Results.Content($"<h2>Data aprovada</h2><p>{result.Count} apontamento(s) fora da regra foram aprovados.</p>", "text/html; charset=utf-8");
}).AllowAnonymous();
api.MapGet("/cronograma-excecoes/grupo/{id}/recusar", async (
    string id,
    string token,
    ExPontoDataSource dataSource,
    CronogramaExceptionEmailService emailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var result = await DecideCronogramaExceptionGroup(db, id, token, "recusado");
    if (result is null) return Results.NotFound("Solicitacao nao encontrada ou token invalido.");
    await emailService.SendCronogramaExceptionGroupDecisionAsync(result, cancellationToken);
    return Results.Content($"<h2>Data recusada</h2><p>{result.Count} apontamento(s) fora da regra foram recusados.</p>", "text/html; charset=utf-8");
}).AllowAnonymous();
api.MapGet("/cronograma-excecoes/{id}/aprovar", async (
    string id,
    string token,
    ExPontoDataSource dataSource,
    CronogramaExceptionEmailService emailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var result = await DecideCronogramaException(db, id, token, "aprovado");
    if (result is null) return Results.NotFound("Solicitacao nao encontrada ou token invalido.");
    await emailService.SendCronogramaExceptionDecisionAsync(result.User, result.Apontamento, result.Status, cancellationToken);
    return Results.Content("<h2>Apontamento aprovado</h2><p>O apontamento fora da regra foi aprovado.</p>", "text/html; charset=utf-8");
}).AllowAnonymous();
api.MapGet("/cronograma-excecoes/{id}/recusar", async (
    string id,
    string token,
    ExPontoDataSource dataSource,
    CronogramaExceptionEmailService emailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var result = await DecideCronogramaException(db, id, token, "recusado");
    if (result is null) return Results.NotFound("Solicitacao nao encontrada ou token invalido.");
    await emailService.SendCronogramaExceptionDecisionAsync(result.User, result.Apontamento, result.Status, cancellationToken);
    return Results.Content("<h2>Apontamento recusado</h2><p>O apontamento fora da regra foi recusado.</p>", "text/html; charset=utf-8");
}).AllowAnonymous();
api.MapGet("/manutencoes-excecoes/{id}/{decisao}", async (
    string id,
    string decisao,
    string token,
    ExPontoDataSource dataSource) =>
{
    if (decisao is not ("aprovar" or "recusar")) return Results.NotFound();

    await using var db = await dataSource.OpenConnectionAsync();
    var exists = await db.ExecuteScalarAsync<bool>(
        "select exists(select 1 from cronograma_excecoes where id = @Id and approval_token = @Token and tipo = 'manutencao_nao_identificada')",
        new { Id = id, Token = token });
    if (!exists) return Results.NotFound("Solicitacao nao encontrada ou token invalido.");

    return Results.Content(BuildMaintenanceDecisionForm(id, token, decisao), "text/html; charset=utf-8");
}).AllowAnonymous();
api.MapPost("/manutencoes-excecoes/{id}/decidir", async (
    string id,
    HttpContext http,
    ExPontoDataSource dataSource,
    MaintenanceAdAuthenticator authenticator,
    MaintenanceExceptionEmailService emailService,
    CancellationToken cancellationToken) =>
{
    var form = await http.Request.ReadFormAsync(cancellationToken);
    var token = form["token"].ToString();
    var decisao = form["decisao"].ToString();
    var usuario = form["usuario"].ToString();
    var senha = form["senha"].ToString();
    var motivo = form["motivo"].ToString();

    if (decisao is not ("aprovar" or "recusar"))
    {
        return Results.BadRequest("Decisao invalida.");
    }

    var auth = await authenticator.ValidateAsync(usuario, senha, cancellationToken);
    if (!auth.Success)
    {
        return Results.Content(BuildMaintenanceDecisionForm(id, token, decisao, auth.Message), "text/html; charset=utf-8");
    }

    await using var db = await dataSource.OpenConnectionAsync();
    var status = decisao == "aprovar" ? "aprovado" : "recusado";
    var result = await DecideMaintenanceException(db, id, token, status, usuario, motivo);
    if (result is null) return Results.NotFound("Solicitacao nao encontrada ou token invalido.");

    await emailService.SendMaintenanceDecisionAsync(result.User, result.Apontamento, result.Status, motivo, cancellationToken);

    var title = status == "aprovado" ? "Manutencao aprovada" : "Manutencao recusada";
    return Results.Content($"""
        <html><body style="font-family:Arial;padding:24px;">
        <h2>{Html(title)}</h2>
        <p>A decisao foi registrada com sucesso.</p>
        </body></html>
        """, "text/html; charset=utf-8");
}).AllowAnonymous();
api.MapPost("/solicitacoes-ajuste", async (
    SolicitacaoAjusteCreate request,
    HttpContext http,
    ExPontoDataSource dataSource,
    LocationAdjustmentEmailService emailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    var ownerId = DataOwnerId(user);

    var cliente = await QueryCliente(db, request.ClienteId, ownerId);
    if (cliente is null) return Results.NotFound(new { detail = "Cliente nao encontrado" });

    var id = NewId();
    var approvalToken = NewToken();
    await db.ExecuteAsync("""
        insert into solicitacoes_ajuste
            (id, id_usuario, cliente_id, latitude_atual, longitude_atual, nova_latitude, nova_longitude, motivo, status, approval_token, created_at)
        values
            (@Id, @UserId, @ClienteId, @LatitudeAtual, @LongitudeAtual, @NovaLatitude, @NovaLongitude, @Motivo, 'pendente', @ApprovalToken, @CreatedAt)
        """, new
    {
        Id = id,
        UserId = ownerId,
        request.ClienteId,
        LatitudeAtual = cliente.Latitude,
        LongitudeAtual = cliente.Longitude,
        NovaLatitude = request.NovaGeolocalizacao.Latitude,
        NovaLongitude = request.NovaGeolocalizacao.Longitude,
        request.Motivo,
        ApprovalToken = approvalToken,
        CreatedAt = DateTime.UtcNow
    });

    await emailService.SendAdjustmentRequestedAsync(user, cliente, id, approvalToken, request.NovaGeolocalizacao, request.Motivo, cancellationToken);

    return Results.Ok(new { id, cliente_nome = cliente.Nome, status = "pendente", approval_token = approvalToken });
}).RequireAuthorization("ExPontoUser");

api.MapGet("/solicitacoes-ajuste/minhas", async (HttpContext http, ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    var ownerId = DataOwnerId(user);

    var rows = await db.QueryAsync<LocationAdjustmentRow>("""
        select s.id, s.id_usuario as UserId, s.cliente_id as ClienteId,
               s.latitude_atual as LatitudeAtual, s.longitude_atual as LongitudeAtual,
               s.nova_latitude as NovaLatitude, s.nova_longitude as NovaLongitude,
               s.motivo, s.status, s.approval_token as ApprovalToken,
               s.created_at as CreatedAt, s.updated_at as UpdatedAt,
               u.nome as UserName, u.email as UserEmail,
               c.id_aprovado as ClienteIdAprovado, c.nome as ClienteNome, c.sigla as ClienteSigla
        from solicitacoes_ajuste s
        join users u on u.id = s.id_usuario
        join clientes c on c.id = s.cliente_id
        where s.id_usuario = @UserId
        order by s.created_at desc
        limit 100
        """, new { UserId = ownerId });

    return Results.Ok(rows.Select(row => new
    {
        id = row.Id,
        cliente_id = row.ClienteId,
        cliente_nome = row.ClienteNome,
        status = row.Status,
        geolocalizacao_atual = new GeoLocation(row.LatitudeAtual, row.LongitudeAtual),
        nova_geolocalizacao = new GeoLocation(row.NovaLatitude, row.NovaLongitude),
        created_at = row.CreatedAt,
        updated_at = row.UpdatedAt
    }));
}).RequireAuthorization("ExPontoUser");

api.MapGet("/solicitacoes-ajuste/{id}/aprovar", async (
    string id,
    string token,
    ExPontoDataSource dataSource,
    LocationAdjustmentEmailService emailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var result = await DecideSolicitacaoAjuste(db, id, token, "aprovado");
    if (result is null) return Results.NotFound("Solicitacao nao encontrada ou token invalido.");
    await emailService.SendAdjustmentDecisionAsync(result.User, result.Cliente, result.Status, cancellationToken);
    return Results.Content("<h2>Solicitacao aprovada</h2><p>A nova localizacao foi aplicada e os apontamentos pendentes foram aprovados.</p>", "text/html; charset=utf-8");
}).AllowAnonymous();
api.MapGet("/solicitacoes-ajuste/{id}/recusar", async (
    string id,
    string token,
    ExPontoDataSource dataSource,
    LocationAdjustmentEmailService emailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var result = await DecideSolicitacaoAjuste(db, id, token, "recusado");
    if (result is null) return Results.NotFound("Solicitacao nao encontrada ou token invalido.");
    await emailService.SendAdjustmentDecisionAsync(result.User, result.Cliente, result.Status, cancellationToken);
    return Results.Content("<h2>Solicitacao recusada</h2><p>Os apontamentos vinculados foram marcados como recusados.</p>", "text/html; charset=utf-8");
}).AllowAnonymous();
api.MapPost("/solicitacoes-ajuste-batida", async (
    SolicitacaoAjusteBatidaCreate request,
    HttpContext http,
    ExPontoDataSource dataSource,
    BeatAdjustmentEmailService emailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    var ownerId = DataOwnerId(user);

    var cliente = await QueryCliente(db, request.ClienteId, ownerId);
    if (cliente is null) return Results.NotFound(new { detail = "Cliente nao encontrado" });

    var integrante = await QueryIntegrante(db, request.IntegranteId, ownerId);
    if (integrante is null) return Results.NotFound(new { detail = "Integrante nao encontrado" });

    if (request.Tipo is not ("entrada" or "saida"))
    {
        return Results.BadRequest(new { detail = "Tipo deve ser entrada ou saida." });
    }

    var timestamp = ParseTimestamp(request.Timestamp);
    var id = NewId();
    var approvalToken = NewToken();

    await db.ExecuteAsync("""
        insert into solicitacoes_ajuste_batida
            (id, id_usuario, cliente_id, integrante_id, tipo, timestamp_ajuste, motivo, status, approval_token, created_at)
        values
            (@Id, @UserId, @ClienteId, @IntegranteId, @Tipo, @TimestampAjuste, @Motivo, 'pendente', @ApprovalToken, @CreatedAt)
        """, new
    {
        Id = id,
        UserId = ownerId,
        request.ClienteId,
        request.IntegranteId,
        request.Tipo,
        TimestampAjuste = timestamp,
        request.Motivo,
        ApprovalToken = approvalToken,
        CreatedAt = DateTime.UtcNow
    });

    await emailService.SendBeatAdjustmentRequestedAsync(user, cliente, integrante, id, approvalToken, request.Tipo, timestamp, request.Motivo, cancellationToken);

    return Results.Ok(new { id, status = "pendente" });
}).RequireAuthorization("ExPontoUser");

api.MapGet("/solicitacoes-ajuste-batida/minhas", async (
    string? status,
    HttpContext http,
    ExPontoDataSource dataSource) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var user = await CurrentUser(http, db);
    if (user is null) return Results.Unauthorized();
    var ownerId = DataOwnerId(user);

    var rows = await db.QueryAsync<BeatAdjustmentRow>("""
        select s.id, s.id_usuario as UserId, s.cliente_id as ClienteId, s.integrante_id as IntegranteId,
               s.tipo, s.timestamp_ajuste as TimestampAjuste, s.motivo, s.status,
               s.approval_token as ApprovalToken, s.apontamento_id as ApontamentoId,
               s.created_at as CreatedAt, s.updated_at as UpdatedAt,
               u.nome as UserName, u.email as UserEmail,
               c.id_aprovado as ClienteIdAprovado, c.nome as ClienteNome, c.sigla as ClienteSigla,
               c.latitude as ClienteLatitude, c.longitude as ClienteLongitude, c.raio as ClienteRaio,
               i.nome as IntegranteNome, i.cpf as IntegranteCpf, i.funcao_id as FuncaoId
        from solicitacoes_ajuste_batida s
        join users u on u.id = s.id_usuario
        join clientes c on c.id = s.cliente_id
        join integrantes i on i.id = s.integrante_id
        where s.id_usuario = @UserId
          and (cast(@Status as text) is null or s.status = @Status)
        order by coalesce(s.decided_at, s.updated_at, s.created_at) desc
        limit 200
        """, new { UserId = ownerId, Status = string.IsNullOrWhiteSpace(status) ? null : status });

    return Results.Ok(rows.Select(row => new
    {
        id = row.Id,
        cliente_id = row.ClienteId,
        cliente_nome = row.ClienteNome,
        cliente_sigla = row.ClienteSigla,
        integrante_id = row.IntegranteId,
        integrante_nome = row.IntegranteNome,
        tipo = row.Tipo,
        timestamp = row.TimestampAjuste,
        motivo = row.Motivo,
        status = row.Status,
        apontamento_id = row.ApontamentoId,
        created_at = row.CreatedAt,
        updated_at = row.UpdatedAt
    }));
}).RequireAuthorization("ExPontoUser");

api.MapGet("/solicitacoes-ajuste-batida/{id}/aprovar", async (
    string id,
    string token,
    ExPontoDataSource dataSource,
    BeatAdjustmentEmailService emailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var result = await DecideSolicitacaoAjusteBatida(db, id, token, "aprovado");
    if (result is null) return Results.NotFound("Solicitacao nao encontrada ou token invalido.");
    await emailService.SendBeatAdjustmentDecisionAsync(result.User, result.Cliente, result.Integrante, result.Status, result.TimestampAjuste, cancellationToken);
    return Results.Content("<h2>Ajuste de batida aprovado</h2><p>A batida foi criada e sera sincronizada no aplicativo.</p>", "text/html; charset=utf-8");
}).AllowAnonymous();
api.MapGet("/solicitacoes-ajuste-batida/{id}/recusar", async (
    string id,
    string token,
    ExPontoDataSource dataSource,
    BeatAdjustmentEmailService emailService,
    CancellationToken cancellationToken) =>
{
    await using var db = await dataSource.OpenConnectionAsync();
    var result = await DecideSolicitacaoAjusteBatida(db, id, token, "recusado");
    if (result is null) return Results.NotFound("Solicitacao nao encontrada ou token invalido.");
    await emailService.SendBeatAdjustmentDecisionAsync(result.User, result.Cliente, result.Integrante, result.Status, result.TimestampAjuste, cancellationToken);
    return Results.Content("<h2>Ajuste de batida recusado</h2><p>A batida solicitada nao foi criada.</p>", "text/html; charset=utf-8");
}).AllowAnonymous();
    }

    public static async Task InitializeExPontoAsync(this IServiceProvider services, IConfiguration configuration)
    {
        var dataSource = services.GetRequiredService<ExPontoDataSource>();
        await EnsureRuntimeSchema(dataSource);
        await PurgeRetentionData(dataSource, configuration.GetSection("ExPonto:DataRetention").Get<DataRetentionOptions>() ?? new DataRetentionOptions());
    }

static async Task EnsureRuntimeSchema(ExPontoDataSource dataSource)
{
    await using var db = await dataSource.OpenConnectionAsync();
    await db.ExecuteAsync("""
        create table if not exists user_clientes (
            user_id text not null references users(id) on delete cascade,
            cliente_id text not null references clientes(id) on delete cascade,
            created_at timestamptz not null default now(),
            primary key (user_id, cliente_id)
        )
        """);
    await db.ExecuteAsync("create index if not exists ix_user_clientes_user on user_clientes(user_id)");
    await db.ExecuteAsync("create index if not exists ix_user_clientes_cliente on user_clientes(cliente_id)");
    await db.ExecuteAsync("create unique index if not exists ux_clientes_id_aprovado on clientes(id_aprovado)");
    await db.ExecuteAsync("""
        create table if not exists user_devices (
            user_id text not null references users(id) on delete cascade,
            device_id text not null,
            status text not null default 'active',
            blocked_at timestamptz null,
            last_seen_at timestamptz null,
            created_at timestamptz not null default now(),
            updated_at timestamptz null,
            primary key (user_id, device_id)
        )
        """);
    await db.ExecuteAsync("create index if not exists ix_user_devices_user_status on user_devices(user_id, status)");
    await db.ExecuteAsync("""
        create table if not exists funcoes_base (
            id text primary key,
            funcao text not null,
            funcao_normalizada text not null,
            created_at timestamptz not null default now(),
            updated_at timestamptz null,
            unique (funcao_normalizada)
        )
        """);
    await db.ExecuteAsync("create index if not exists ix_funcoes_base_normalizada on funcoes_base(funcao_normalizada)");
    await db.ExecuteAsync("alter table funcoes add column if not exists funcao_base_id text null references funcoes_base(id)");
    await db.ExecuteAsync("""
        insert into funcoes_base (id, funcao, funcao_normalizada, created_at)
        select 'fb-' || md5(lower(trim(funcao))), min(trim(funcao)), lower(trim(funcao)), now()
        from funcoes
        where trim(funcao) <> ''
          and lower(trim(funcao)) not in (select funcao_normalizada from funcoes_base)
        group by lower(trim(funcao))
        on conflict (funcao_normalizada) do nothing
        """);
    await db.ExecuteAsync("""
        update funcoes f
        set funcao_base_id = fb.id
        from funcoes_base fb
        where f.funcao_base_id is null
          and fb.funcao_normalizada = lower(trim(f.funcao))
        """);
    await db.ExecuteAsync("alter table apontamentos add column if not exists offline_face_verified boolean not null default false");
    await db.ExecuteAsync("alter table apontamentos add column if not exists offline_device_biometric_verified boolean not null default false");
    await db.ExecuteAsync("alter table apontamentos add column if not exists localizacao_status text not null default 'aprovado'");
    await db.ExecuteAsync("alter table apontamentos add column if not exists solicitacao_ajuste_id text null");
    await db.ExecuteAsync("alter table apontamentos add column if not exists cronograma_fase_id text null");
    await db.ExecuteAsync("alter table apontamentos add column if not exists cronograma_fase_sigla text null");
    await db.ExecuteAsync("alter table apontamentos add column if not exists dia_extra boolean not null default false");
    await db.ExecuteAsync("alter table apontamentos add column if not exists funcao_extra boolean not null default false");
    await db.ExecuteAsync("alter table apontamentos add column if not exists cronograma_status text not null default 'aprovado'");
    await db.ExecuteAsync("alter table apontamentos add column if not exists cronograma_excecao_id text null");
    await db.ExecuteAsync("alter table apontamentos add column if not exists cronograma_justificativa text null");
    await db.ExecuteAsync("alter table apontamentos add column if not exists manutencao_status text not null default 'aprovado'");
    await db.ExecuteAsync("alter table apontamentos add column if not exists manutencao_excecao_id text null");
    await db.ExecuteAsync("alter table solicitacoes_ajuste add column if not exists approval_token text null");
    await db.ExecuteAsync("alter table solicitacoes_ajuste add column if not exists decided_at timestamptz null");
    await db.ExecuteAsync("alter table solicitacoes_ajuste add column if not exists decided_by text null");
    await db.ExecuteAsync("create index if not exists ix_apontamentos_solicitacao_status on apontamentos(solicitacao_ajuste_id, localizacao_status)");
    await db.ExecuteAsync("create index if not exists ix_solicitacoes_approval_token on solicitacoes_ajuste(approval_token)");
    await db.ExecuteAsync("""
        create table if not exists solicitacoes_ajuste_batida (
            id text primary key,
            id_usuario text not null references users(id) on delete cascade,
            cliente_id text not null references clientes(id),
            integrante_id text not null references integrantes(id),
            tipo text not null,
            timestamp_ajuste timestamptz not null,
            motivo text,
            status text not null default 'pendente',
            approval_token text,
            apontamento_id text null references apontamentos(id),
            created_at timestamptz not null default now(),
            updated_at timestamptz,
            decided_at timestamptz,
            decided_by text
        )
        """);
    await db.ExecuteAsync("create index if not exists ix_solicitacoes_batida_usuario_status on solicitacoes_ajuste_batida(id_usuario, status)");
    await db.ExecuteAsync("create index if not exists ix_solicitacoes_batida_approval_token on solicitacoes_ajuste_batida(approval_token)");
    await db.ExecuteAsync("""
        create table if not exists cronograma_fases (
            id text primary key,
            cliente_id text not null references clientes(id) on delete cascade,
            fase text not null,
            data_inicio date not null,
            data_fim date not null,
            created_at timestamptz not null default now(),
            updated_at timestamptz null
        )
        """);
    await db.ExecuteAsync("""
        do $$
        begin
            if exists (
                select 1 from information_schema.columns
                where table_schema = 'public' and table_name = 'cronograma_fases' and column_name = 'sigla'
            ) and not exists (
                select 1 from information_schema.columns
                where table_schema = 'public' and table_name = 'cronograma_fases' and column_name = 'fase'
            ) then
                alter table cronograma_fases rename column sigla to fase;
            elsif exists (
                select 1 from information_schema.columns
                where table_schema = 'public' and table_name = 'cronograma_fases' and column_name = 'sigla'
            ) and exists (
                select 1 from information_schema.columns
                where table_schema = 'public' and table_name = 'cronograma_fases' and column_name = 'fase'
            ) then
                update cronograma_fases set fase = coalesce(nullif(fase, ''), sigla);
                alter table cronograma_fases drop column sigla;
            end if;
        end $$;
        """);
    await db.ExecuteAsync("""
        create table if not exists cronograma_fase_funcoes (
            id text primary key,
            fase_id text not null references cronograma_fases(id) on delete cascade,
            funcao_id text null,
            funcao_base_id text null references funcoes_base(id),
            funcao_nome text null,
            limite_integrantes integer not null default 0,
            created_at timestamptz not null default now(),
            updated_at timestamptz null,
            unique (fase_id, funcao_base_id)
        )
        """);
    await db.ExecuteAsync("""
        create table if not exists cronograma_excecoes (
            id text primary key,
            id_usuario text not null references users(id) on delete cascade,
            apontamento_id text not null references apontamentos(id) on delete cascade,
            tipo text not null,
            status text not null default 'pendente',
            motivo text null,
            approval_token text null,
            decisao_motivo text null,
            decisao_usuario text null,
            decided_at timestamptz null,
            decided_by text null,
            created_at timestamptz not null default now(),
            updated_at timestamptz null
        )
        """);
    await db.ExecuteAsync("create index if not exists ix_cronograma_fases_cliente_datas on cronograma_fases(cliente_id, data_inicio, data_fim)");
    await db.ExecuteAsync("alter table cronograma_fase_funcoes alter column funcao_id drop not null");
    await db.ExecuteAsync("alter table cronograma_fase_funcoes add column if not exists funcao_base_id text null references funcoes_base(id)");
    await db.ExecuteAsync("alter table cronograma_fase_funcoes add column if not exists funcao_nome text null");
    await db.ExecuteAsync("""
        update cronograma_fase_funcoes cff
        set funcao_base_id = f.funcao_base_id,
            funcao_nome = coalesce(cff.funcao_nome, f.funcao)
        from funcoes f
        where cff.funcao_base_id is null
          and cff.funcao_id = f.id
        """);
    await db.ExecuteAsync("create index if not exists ix_cronograma_fase_funcoes_fase_funcao on cronograma_fase_funcoes(fase_id, funcao_id)");
    await db.ExecuteAsync("create unique index if not exists ux_cronograma_fase_funcoes_base on cronograma_fase_funcoes(fase_id, funcao_base_id) where funcao_base_id is not null");
    await db.ExecuteAsync("create index if not exists ix_cronograma_excecoes_usuario_status on cronograma_excecoes(id_usuario, status)");
    await db.ExecuteAsync("create index if not exists ix_cronograma_excecoes_token on cronograma_excecoes(approval_token)");
    await db.ExecuteAsync("alter table cronograma_excecoes add column if not exists notification_sent_at timestamptz null");
    await db.ExecuteAsync("""
        create table if not exists integrante_emprestimos (
            id text primary key,
            integrante_id text not null references integrantes(id),
            equipe_origem_id text not null references users(id),
            equipe_destino_id text not null references users(id),
            data_inicio date not null,
            data_fim date not null,
            status text not null default 'ativo',
            devolvido_em timestamptz null,
            created_at timestamptz not null default now(),
            updated_at timestamptz null,
            check (data_fim >= data_inicio),
            check (equipe_origem_id <> equipe_destino_id)
        )
        """);
    await db.ExecuteAsync("""
        create table if not exists emprestimo_renovacoes (
            id text primary key,
            emprestimo_id text not null references integrante_emprestimos(id) on delete cascade,
            data_fim_atual date not null,
            nova_data_fim date not null,
            status text not null default 'pendente',
            approval_token text not null,
            solicitado_em timestamptz not null default now(),
            decidido_em timestamptz null,
            updated_at timestamptz null
        )
        """);
    await db.ExecuteAsync("""
        create table if not exists notificacoes_usuario (
            id text primary key,
            usuario_id text not null references users(id) on delete cascade,
            tipo text not null,
            titulo text not null,
            mensagem text not null,
            referencia_id text null,
            dedup_key text null,
            lida_em timestamptz null,
            created_at timestamptz not null default now()
        )
        """);
    await db.ExecuteAsync("create unique index if not exists ux_notificacoes_usuario_dedup on notificacoes_usuario(usuario_id, dedup_key) where dedup_key is not null");
    await db.ExecuteAsync("create index if not exists ix_emprestimos_destino_periodo on integrante_emprestimos(equipe_destino_id, data_inicio, data_fim, status)");
    await db.ExecuteAsync("create index if not exists ix_emprestimos_origem on integrante_emprestimos(equipe_origem_id, status)");
    await db.ExecuteAsync("alter table apontamentos add column if not exists id_usuario_pagador text null references users(id)");
    await db.ExecuteAsync("alter table apontamentos add column if not exists emprestimo_id text null references integrante_emprestimos(id)");
    await db.ExecuteAsync("update apontamentos set id_usuario_pagador = id_usuario where id_usuario_pagador is null");
    await db.ExecuteAsync("create index if not exists ix_apontamentos_cronograma_status on apontamentos(id_usuario, cronograma_status)");
    await db.ExecuteAsync("alter table cronograma_excecoes add column if not exists decisao_motivo text null");
    await db.ExecuteAsync("alter table cronograma_excecoes add column if not exists decisao_usuario text null");
    await db.ExecuteAsync("""
        create table if not exists password_reset_codes (
            id text primary key,
            user_id text not null references users(id) on delete cascade,
            code text not null,
            created_at timestamptz not null default now(),
            expires_at timestamptz not null,
            consumed_at timestamptz null
        )
        """);
    await db.ExecuteAsync("create index if not exists ix_password_reset_codes_user on password_reset_codes(user_id)");
    await db.ExecuteAsync("create index if not exists ix_password_reset_codes_code on password_reset_codes(code)");
    await db.ExecuteAsync("alter table users add column if not exists master_user_id text null references users(id) on delete set null");
    await db.ExecuteAsync("alter table users add column if not exists integrante_vinculado_id text null references integrantes(id) on delete set null");
    await db.ExecuteAsync("create index if not exists ix_users_master_user on users(master_user_id)");
    await db.ExecuteAsync("""
        create table if not exists configuracoes_sistema (
            chave text primary key,
            valor text not null,
            updated_at timestamptz not null default now()
        )
        """);
    await db.ExecuteAsync("""
        insert into configuracoes_sistema (chave, valor)
        values ('batida_impar_limite_hora', '10')
        on conflict (chave) do nothing
        """);
    await db.ExecuteAsync("""
        update integrantes
        set biometria_facial = null,
            updated_at = coalesce(updated_at, now())
        where face_encoding is not null
          and biometria_facial is not null
        """);
    await CreateOperationalSummaryView(db);
    await CreateResponsibleDailySummaryView(db);
    await CreateReportSummaryView(db);
    await CreateMissingSchedulePointingView(db);
}

public sealed class ExPontoLog;

static Task CreateOperationalSummaryView(NpgsqlConnection db) =>
    db.ExecuteAsync("""
        create or replace view vw_resumo_apontamentos_operacional as
        with apontamentos_ordenados as (
            select
                a.*,
                lag(a.timestamp) over (
                    partition by a.id_usuario, a.cliente_id
                    order by a.timestamp, a.id
                ) as timestamp_anterior
            from apontamentos a
        ),
        jornadas_marcadas as (
            select
                ao.*,
                case
                    when ao.timestamp_anterior is null then 1
                    when ao.timestamp - ao.timestamp_anterior > interval '18 hours' then 1
                    else 0
                end as nova_jornada
            from apontamentos_ordenados ao
        ),
        jornadas_numeradas as (
            select
                jm.*,
                sum(jm.nova_jornada) over (
                    partition by jm.id_usuario, jm.cliente_id
                    order by jm.timestamp, jm.id
                    rows between unbounded preceding and current row
                ) as jornada_numero
            from jornadas_marcadas jm
        ),
        base as (
            select
                jn.*,
                min(jn.timestamp) over (
                    partition by jn.id_usuario, jn.cliente_id, jn.jornada_numero
                ) as inicio_jornada
            from jornadas_numeradas jn
        ),
        pares as (
            select
                b.*,
                lead(b.tipo) over (
                    partition by b.id_usuario, b.cliente_id, b.integrante_id, b.jornada_numero
                    order by b.timestamp, b.id
                ) as proximo_tipo,
                lead(b.timestamp) over (
                    partition by b.id_usuario, b.cliente_id, b.integrante_id, b.jornada_numero
                    order by b.timestamp, b.id
                ) as proximo_timestamp
            from base b
        ),
        agregado as (
            select
                p.id_usuario,
                p.id_usuario_pagador,
                p.cliente_id,
                p.integrante_id,
                p.jornada_numero,
                (min(p.inicio_jornada) at time zone 'America/Sao_Paulo')::date as data_apontamento,
                min(p.timestamp) filter (where p.tipo = 'entrada') as entrada,
                max(p.timestamp) filter (where p.tipo = 'saida') as saida,
                count(*) as quantidade_batidas,
                count(*) filter (where p.tipo = 'entrada') as quantidade_entradas,
                count(*) filter (where p.tipo = 'saida') as quantidade_saidas,
                coalesce(sum(
                    case
                        when p.tipo = 'entrada'
                         and p.proximo_tipo = 'saida'
                         and p.proximo_timestamp > p.timestamp
                            then extract(epoch from (p.proximo_timestamp - p.timestamp)) / 60
                        else 0
                    end
                ), 0)::integer as total_minutos,
                case
                    when bool_or(coalesce(p.localizacao_status, 'aprovado') = 'recusado') then 'recusado'
                    when bool_or(coalesce(p.localizacao_status, 'aprovado') = 'pendente_aprovacao') then 'pendente_aprovacao'
                    else 'aprovado'
                end as localizacao_status,
                nullif((array_agg(p.cronograma_fase_sigla order by p.timestamp) filter (where p.cronograma_fase_sigla is not null))[1], '') as fase,
                bool_or(coalesce(p.dia_extra, false)) as dia_extra,
                bool_or(coalesce(p.funcao_extra, false)) as funcao_extra,
                case
                    when bool_or(coalesce(p.cronograma_status, 'aprovado') = 'recusado') then 'recusado'
                    when bool_or(coalesce(p.cronograma_status, 'aprovado') = 'pendente_aprovacao') then 'pendente_aprovacao'
                    else 'aprovado'
                end as cronograma_status,
                case
                    when bool_or(coalesce(p.manutencao_status, 'aprovado') = 'recusado') then 'recusado'
                    when bool_or(coalesce(p.manutencao_status, 'aprovado') = 'pendente_aprovacao') then 'pendente_aprovacao'
                    else 'aprovado'
                end as manutencao_status,
                bool_or(p.solicitacao_ajuste_id is not null) as possui_ajuste_localizacao,
                bool_or(p.cronograma_excecao_id is not null) as possui_excecao_cronograma,
                bool_or(p.manutencao_excecao_id is not null) as possui_excecao_manutencao
            from pares p
            group by p.id_usuario, p.id_usuario_pagador, p.cliente_id, p.integrante_id, p.jornada_numero
        ),
        resumo as (
            select
                ag.*,
                (ag.quantidade_batidas % 2 <> 0 or ag.quantidade_entradas <> ag.quantidade_saidas) as batidas_impares,
                i.nome as integrante_nome,
                i.cpf as integrante_cpf,
                i.funcao_id,
                f.funcao,
                coalesce(f.valor, 0)::numeric(12,2) as valor_diaria,
                u.nome as usuario_nome,
                u.email as usuario_email,
                up.master_user_id,
                coalesce(up.master_user_id, up.id) as responsavel_id,
                coalesce(ump.nome, up.nome) as responsavel_nome,
                coalesce(ump.email, up.email) as responsavel_email,
                c.id_aprovado as cliente_id_aprovado,
                c.sigla as cliente_sigla,
                c.nome as cliente_nome
            from agregado ag
            join integrantes i on i.id = ag.integrante_id
            join funcoes f on f.id = i.funcao_id
            join users u on u.id = ag.id_usuario
            join users up on up.id = coalesce(ag.id_usuario_pagador, ag.id_usuario)
            left join users ump on ump.id = up.master_user_id
            join clientes c on c.id = ag.cliente_id
        ),
        calculado as (
            select
                r.*,
                (
                    not r.batidas_impares
                    and r.localizacao_status = 'aprovado'
                    and r.cronograma_status = 'aprovado'
                    and r.manutencao_status = 'aprovado'
                ) as aprovado,
                case
                    when r.batidas_impares then 'batida_impar'
                    when r.localizacao_status = 'recusado'
                      or r.cronograma_status = 'recusado'
                      or r.manutencao_status = 'recusado' then 'recusado'
                    when r.localizacao_status = 'pendente_aprovacao'
                      or r.cronograma_status = 'pendente_aprovacao'
                      or r.manutencao_status = 'pendente_aprovacao' then 'pendente_aprovacao'
                    else 'aprovado'
                end as status_resumo
            from resumo r
        )
        select
            calculado.id_usuario as usuario_id,
            calculado.usuario_nome,
            calculado.usuario_email,
            calculado.master_user_id,
            calculado.responsavel_id,
            calculado.responsavel_nome,
            calculado.responsavel_email,
            calculado.cliente_id,
            calculado.cliente_id_aprovado,
            calculado.cliente_sigla,
            calculado.cliente_nome,
            calculado.data_apontamento,
            to_char(calculado.data_apontamento, 'DD/MM/YYYY') as data_apontamento_br,
            calculado.integrante_id,
            calculado.integrante_nome,
            calculado.integrante_cpf,
            calculado.funcao_id,
            calculado.funcao,
            calculado.entrada,
            calculado.saida,
            to_char(calculado.entrada at time zone 'America/Sao_Paulo', 'HH24:MI') as entrada_hora,
            to_char(calculado.saida at time zone 'America/Sao_Paulo', 'HH24:MI') as saida_hora,
            calculado.total_minutos,
            round((calculado.total_minutos::numeric / 60), 2) as total_horas,
            calculado.quantidade_batidas,
            calculado.batidas_impares,
            case when calculado.batidas_impares then 0 else 1 end as total_pessoas_base,
            calculado.valor_diaria,
            case when calculado.aprovado then calculado.valor_diaria else 0::numeric(12,2) end as valor_aprovado,
            case when calculado.aprovado then 0::numeric(12,2) else calculado.valor_diaria end as valor_nao_aprovado,
            calculado.localizacao_status,
            calculado.fase,
            calculado.dia_extra,
            calculado.funcao_extra,
            calculado.cronograma_status,
            calculado.manutencao_status,
            calculado.possui_ajuste_localizacao,
            calculado.possui_excecao_cronograma,
            calculado.possui_excecao_manutencao,
            calculado.status_resumo,
            sum(case when calculado.batidas_impares then 0 else 1 end) over (
                partition by calculado.responsavel_id, calculado.cliente_id, calculado.data_apontamento
            ) as total_pessoas_dia,
            sum(case when calculado.aprovado then calculado.valor_diaria else 0::numeric(12,2) end) over (
                partition by calculado.responsavel_id, calculado.cliente_id, calculado.data_apontamento
            ) as valor_aprovado_dia,
            sum(case when calculado.aprovado then 0::numeric(12,2) else calculado.valor_diaria end) over (
                partition by calculado.responsavel_id, calculado.cliente_id, calculado.data_apontamento
            ) as valor_nao_aprovado_dia
        from calculado
        """);

static Task CreateResponsibleDailySummaryView(NpgsqlConnection db) =>
    db.ExecuteAsync("""
        create or replace view vw_resumo_apontamentos_dia_responsavel as
        select
            responsavel_id,
            responsavel_nome,
            responsavel_email,
            cliente_id,
            cliente_id_aprovado,
            cliente_sigla,
            cliente_nome,
            data_apontamento,
            data_apontamento_br,
            max(total_pessoas_dia) as total_pessoas,
            round(sum(total_minutos)::numeric / 60, 2) as total_horas,
            max(valor_aprovado_dia) as valor_aprovado,
            max(valor_nao_aprovado_dia) as valor_nao_aprovado,
            max(valor_aprovado_dia) + max(valor_nao_aprovado_dia) as total_valor,
            bool_or(batidas_impares) as possui_ajustes,
            bool_or(dia_extra) as possui_dia_extra,
            bool_or(funcao_extra) as possui_funcao_extra,
            bool_or(possui_ajuste_localizacao) as possui_ajuste_localizacao,
            bool_or(possui_excecao_cronograma) as possui_excecao_cronograma,
            bool_or(possui_excecao_manutencao) as possui_excecao_manutencao,
            case
                when bool_or(status_resumo = 'recusado') then 'recusado'
                when bool_or(status_resumo in ('pendente_aprovacao', 'batida_impar')) then 'pendente_aprovacao'
                else 'aprovado'
            end as status_resumo,
            count(*) as total_integrantes_com_apontamento,
            count(*) filter (where status_resumo = 'aprovado') as total_integrantes_aprovados,
            count(*) filter (where status_resumo <> 'aprovado') as total_integrantes_nao_aprovados
        from vw_resumo_apontamentos_operacional
        group by
            responsavel_id,
            responsavel_nome,
            responsavel_email,
            cliente_id,
            cliente_id_aprovado,
            cliente_sigla,
            cliente_nome,
            data_apontamento,
            data_apontamento_br
        """);

static Task CreateReportSummaryView(NpgsqlConnection db) =>
    db.ExecuteAsync("""
        create or replace view vw_resumo_apontamentos_relatorio as
        select
            responsavel_id,
            responsavel_nome,
            cliente_sigla as cliente,
            data_apontamento as data,
            data_apontamento_br as data_br,
            integrante_nome as integrante,
            funcao,
            entrada_hora as entrada,
            saida_hora as saida,
            case
                when batidas_impares then 0::numeric
                else total_horas
            end as total_de_horas,
            valor_aprovado as valor,
            localizacao_status as status_localizacao,
            coalesce(fase, '-') as fase,
            case when dia_extra then 'Sim' else 'Nao' end as dia_extra,
            case when funcao_extra then 'Sim' else 'Nao' end as funcao_extra,
            cronograma_status as status_cronograma,
            manutencao_status as status_manutencao
        from vw_resumo_apontamentos_operacional
        """);

static Task CreateMissingSchedulePointingView(NpgsqlConnection db) =>
    db.ExecuteAsync("""
        create or replace view vw_cronograma_dias_sem_apontamento as
        with responsaveis_clientes as (
            select distinct
                coalesce(u.master_user_id, u.id) as responsavel_id,
                coalesce(um.nome, u.nome) as responsavel_nome,
                coalesce(um.email, u.email) as responsavel_email,
                uc.cliente_id
            from user_clientes uc
            join users u on u.id = uc.user_id
            left join users um on um.id = u.master_user_id
            where u.ativo = true

            union

            select distinct
                coalesce(u.master_user_id, u.id) as responsavel_id,
                coalesce(um.nome, u.nome) as responsavel_nome,
                coalesce(um.email, u.email) as responsavel_email,
                a.cliente_id
            from apontamentos a
            join users u on u.id = a.id_usuario
            left join users um on um.id = u.master_user_id
            where u.ativo = true
        ),
        cronograma_dias as (
            select
                cf.id as fase_id,
                cf.cliente_id,
                cf.fase,
                gs.data::date as data_cronograma
            from cronograma_fases cf
            cross join lateral generate_series(cf.data_inicio, cf.data_fim, interval '1 day') as gs(data)
            where gs.data::date <= (now() at time zone 'America/Sao_Paulo')::date
        ),
        funcoes_previstas as (
            select
                cff.fase_id,
                string_agg(
                    concat(coalesce(cff.funcao_nome, fb.funcao, 'Funcao'), ' (', cff.limite_integrantes, ')'),
                    ', '
                    order by coalesce(cff.funcao_nome, fb.funcao, 'Funcao')
                ) as funcoes_previstas,
                count(*) as total_funcoes_previstas
            from cronograma_fase_funcoes cff
            left join funcoes_base fb on fb.id = cff.funcao_base_id
            group by cff.fase_id
        )
        select
            rc.responsavel_id,
            rc.responsavel_nome,
            rc.responsavel_email,
            c.id as cliente_id,
            c.id_aprovado as cliente_id_aprovado,
            c.sigla as cliente_sigla,
            c.nome as cliente_nome,
            cd.fase_id,
            cd.fase,
            cd.data_cronograma,
            to_char(cd.data_cronograma, 'DD/MM/YYYY') as data_cronograma_br,
            coalesce(fp.funcoes_previstas, '') as funcoes_previstas,
            coalesce(fp.total_funcoes_previstas, 0) as total_funcoes_previstas,
            'sem_apontamento'::text as status_envio
        from cronograma_dias cd
        join clientes c on c.id = cd.cliente_id
        join responsaveis_clientes rc on rc.cliente_id = cd.cliente_id
        left join funcoes_previstas fp on fp.fase_id = cd.fase_id
        left join vw_resumo_apontamentos_dia_responsavel rd
            on rd.responsavel_id = rc.responsavel_id
           and rd.cliente_id = cd.cliente_id
           and rd.data_apontamento = cd.data_cronograma
        where rd.responsavel_id is null
        """);

static async Task PurgeRetentionData(ExPontoDataSource dataSource, DataRetentionOptions options)
{
    await using var db = await dataSource.OpenConnectionAsync();

    var expiredSessionGraceDays = Math.Max(1, options.ExpiredSessionGraceDays);
    var passwordResetCodeDays = Math.Max(1, options.PasswordResetCodeDays);
    var blockedDeviceDays = Math.Max(7, options.BlockedDeviceDays);
    var decidedAdjustmentDays = Math.Max(30, options.DecidedAdjustmentDays);

    await db.ExecuteAsync("""
        delete from sessions
        where expires_at < now() - make_interval(days => @ExpiredSessionGraceDays)
        """, new { ExpiredSessionGraceDays = expiredSessionGraceDays });

    await db.ExecuteAsync("""
        delete from password_reset_codes
        where expires_at < now() - make_interval(days => @PasswordResetCodeDays)
           or (consumed_at is not null and consumed_at < now() - make_interval(days => @PasswordResetCodeDays))
        """, new { PasswordResetCodeDays = passwordResetCodeDays });

    await db.ExecuteAsync("""
        delete from user_devices d
        where d.status = 'blocked'
          and coalesce(d.blocked_at, d.updated_at, d.created_at) < now() - make_interval(days => @BlockedDeviceDays)
          and not exists (
              select 1
              from sessions s
              where s.user_id = d.user_id
                and s.device_id = d.device_id
          )
        """, new { BlockedDeviceDays = blockedDeviceDays });

    await db.ExecuteAsync("""
        delete from solicitacoes_ajuste
        where status in ('aprovado', 'recusado')
          and coalesce(decided_at, updated_at, created_at) < now() - make_interval(days => @DecidedAdjustmentDays)
        """, new { DecidedAdjustmentDays = decidedAdjustmentDays });

    await db.ExecuteAsync("""
        delete from solicitacoes_ajuste_batida
        where status in ('aprovado', 'recusado')
          and coalesce(decided_at, updated_at, created_at) < now() - make_interval(days => @DecidedAdjustmentDays)
        """, new { DecidedAdjustmentDays = decidedAdjustmentDays });
}

static async Task<UserRow?> CurrentUser(HttpContext http, NpgsqlConnection db)
{
    var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? http.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? http.User.FindFirstValue("sub");
    var token = BearerToken(http);
    if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(token)) return null;

    var deviceId = await db.ExecuteScalarAsync<string?>("""
        select s.device_id
        from sessions s
        join user_devices d on d.user_id = s.user_id and d.device_id = s.device_id
        where s.user_id = @UserId
          and s.token = @Token
          and s.expires_at > now()
          and d.status = 'active'
        """, new { UserId = userId, Token = token });
    if (string.IsNullOrWhiteSpace(deviceId)) return null;

    await db.ExecuteAsync("""
        update user_devices
        set last_seen_at = @Now, updated_at = @Now
        where user_id = @UserId and device_id = @DeviceId
        """, new { UserId = userId, DeviceId = deviceId, Now = DateTime.UtcNow });

    return await QueryUser(db, userId, onlyActive: true);
}

static async Task<UserRow?> QueryUser(NpgsqlConnection db, string userId, bool onlyActive = false)
{
    return await db.QuerySingleOrDefaultAsync<UserRow>($"""
        select id, nome, email, password_hash as PasswordHash, must_change_password as MustChangePassword,
               ativo, master_user_id as MasterUserId, integrante_vinculado_id as IntegranteVinculadoId,
               created_at as CreatedAt, updated_at as UpdatedAt
        from users
        where id = @UserId
        {(onlyActive ? "and ativo = true" : "")}
        """, new { UserId = userId });
}

static async Task<UserRow?> QueryUserByEmail(NpgsqlConnection db, string email)
{
    return await db.QuerySingleOrDefaultAsync<UserRow>("""
        select id, nome, email, password_hash as PasswordHash, must_change_password as MustChangePassword,
               ativo, master_user_id as MasterUserId, integrante_vinculado_id as IntegranteVinculadoId,
               created_at as CreatedAt, updated_at as UpdatedAt
        from users
        where lower(email) = lower(@Email)
        """, new { Email = email });
}

static async Task<IReadOnlyList<EmprestimoRow>> QueryEmprestimos(NpgsqlConnection db, string userId)
{
    var rows = await db.QueryAsync<EmprestimoRow>("""
        select e.id, e.integrante_id as IntegranteId, i.nome as IntegranteNome,
               e.equipe_origem_id as EquipeOrigemId, uo.nome as EquipeOrigemNome, uo.email as EquipeOrigemEmail,
               e.equipe_destino_id as EquipeDestinoId, ud.nome as EquipeDestinoNome, ud.email as EquipeDestinoEmail,
               e.data_inicio as DataInicio, e.data_fim as DataFim, e.status,
               e.devolvido_em as DevolvidoEm, e.created_at as CreatedAt, e.updated_at as UpdatedAt
        from integrante_emprestimos e
        join integrantes i on i.id=e.integrante_id
        join users uo on uo.id=e.equipe_origem_id
        join users ud on ud.id=e.equipe_destino_id
        where e.equipe_origem_id=@UserId or e.equipe_destino_id=@UserId
        order by e.created_at desc
        """, new { UserId = userId });
    return rows.ToArray();
}

static Task<EmprestimoRow?> QueryEmprestimo(NpgsqlConnection db, string id) =>
    db.QuerySingleOrDefaultAsync<EmprestimoRow>("""
        select e.id, e.integrante_id as IntegranteId, i.nome as IntegranteNome,
               e.equipe_origem_id as EquipeOrigemId, uo.nome as EquipeOrigemNome, uo.email as EquipeOrigemEmail,
               e.equipe_destino_id as EquipeDestinoId, ud.nome as EquipeDestinoNome, ud.email as EquipeDestinoEmail,
               e.data_inicio as DataInicio, e.data_fim as DataFim, e.status,
               e.devolvido_em as DevolvidoEm, e.created_at as CreatedAt, e.updated_at as UpdatedAt
        from integrante_emprestimos e
        join integrantes i on i.id=e.integrante_id
        join users uo on uo.id=e.equipe_origem_id
        join users ud on ud.id=e.equipe_destino_id
        where e.id=@Id
        """, new { Id = id });

static Task<int> CriarNotificacao(
    NpgsqlConnection db,
    string userId,
    string tipo,
    string titulo,
    string mensagem,
    string? referenciaId,
    string? dedupKey) =>
    db.ExecuteAsync("""
        insert into notificacoes_usuario
            (id, usuario_id, tipo, titulo, mensagem, referencia_id, dedup_key, created_at)
        values (@Id, @UserId, @Tipo, @Titulo, @Mensagem, @ReferenciaId, @DedupKey, @Now)
        on conflict (usuario_id, dedup_key) where dedup_key is not null do nothing
        """, new
    {
        Id = NewId(),
        UserId = userId,
        Tipo = tipo,
        Titulo = titulo,
        Mensagem = mensagem,
        ReferenciaId = referenciaId,
        DedupKey = dedupKey,
        Now = DateTime.UtcNow
    });

static async Task EncerrarEmprestimosVencidos(NpgsqlConnection db)
{
    var today = ExPontoSql.DateParameter(TodaySaoPaulo());
    await db.ExecuteAsync("""
        update integrante_emprestimos
        set status='vencido', updated_at=@Now
        where status='ativo' and data_fim < cast(@Today as date)
        """, new { Today = today, Now = DateTime.UtcNow });
}

static async Task GerarNotificacoesVencimentoEmprestimo(NpgsqlConnection db, string userId)
{
    await EncerrarEmprestimosVencidos(db);
    var today = TodaySaoPaulo();
    var rows = await db.QueryAsync<EmprestimoRow>("""
        select e.id, e.integrante_id as IntegranteId, i.nome as IntegranteNome,
               e.equipe_origem_id as EquipeOrigemId, e.equipe_destino_id as EquipeDestinoId,
               e.data_inicio as DataInicio, e.data_fim as DataFim, e.status,
               uo.nome as EquipeOrigemNome, uo.email as EquipeOrigemEmail,
               ud.nome as EquipeDestinoNome, ud.email as EquipeDestinoEmail,
               e.created_at as CreatedAt, e.updated_at as UpdatedAt
        from integrante_emprestimos e
        join integrantes i on i.id=e.integrante_id
        join users uo on uo.id=e.equipe_origem_id
        join users ud on ud.id=e.equipe_destino_id
        where e.equipe_destino_id=@UserId and e.status='ativo'
          and e.data_fim between cast(@Today as date) and cast(@Limit as date)
        """, new
    {
        UserId = userId,
        Today = ExPontoSql.DateParameter(today),
        Limit = ExPontoSql.DateParameter(today.AddDays(3))
    });
    foreach (var loan in rows)
    {
        var days = loan.DataFim.DayNumber - today.DayNumber;
        var message = days == 0
            ? $"O emprestimo de {loan.IntegranteNome} vence hoje. Solicite renovacao ou devolva o integrante."
            : $"O emprestimo de {loan.IntegranteNome} vence em {days} dia(s), em {loan.DataFim:dd/MM/yyyy}.";
        await CriarNotificacao(db, userId, "emprestimo_vencimento", "Emprestimo proximo do vencimento",
            message, loan.Id, $"loan:{loan.Id}:expires:{loan.DataFim:yyyyMMdd}:{days}");
    }
}

static async Task<EmprestimoRenovacaoDecision?> DecideEmprestimoRenovacao(
    NpgsqlConnection db,
    string id,
    string token,
    string status)
{
    var row = await db.QuerySingleOrDefaultAsync<EmprestimoRenovacaoDecision>("""
        select r.id, r.emprestimo_id as EmprestimoId, r.nova_data_fim as DataFim,
               e.integrante_id as IntegranteId, i.nome as IntegranteNome,
               e.equipe_origem_id as EquipeOrigemId, e.equipe_destino_id as EquipeDestinoId,
               @Status as Status
        from emprestimo_renovacoes r
        join integrante_emprestimos e on e.id=r.emprestimo_id
        join integrantes i on i.id=e.integrante_id
        where r.id=@Id and r.approval_token=@Token and r.status='pendente'
        """, new { Id = id, Token = token, Status = status });
    if (row is null) return null;
    var now = DateTime.UtcNow;
    await using var tx = await db.BeginTransactionAsync();
    await db.ExecuteAsync("""
        update emprestimo_renovacoes
        set status=@Status, decidido_em=@Now, updated_at=@Now
        where id=@Id
        """, new { Id = id, Status = status, Now = now }, tx);
    if (status == "aprovada")
    {
        await db.ExecuteAsync("""
            update integrante_emprestimos set data_fim=cast(@DataFim as date), status='ativo', updated_at=@Now where id=@Id
            """, new { Id = row.EmprestimoId, DataFim = ExPontoSql.DateParameter(row.DataFim), Now = now }, tx);
    }
    await tx.CommitAsync();
    await CriarNotificacao(db, row.EquipeDestinoId, "renovacao_decidida", $"Renovacao {status}",
        status == "aprovada"
            ? $"O emprestimo de {row.IntegranteNome} foi renovado ate {row.DataFim:dd/MM/yyyy}."
            : $"A renovacao de {row.IntegranteNome} foi recusada pela equipe de origem.",
        row.EmprestimoId, $"renewal:{id}:decision");
    return row;
}

static DateOnly TodaySaoPaulo() =>
    DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "America/Sao_Paulo"));

static string DataOwnerId(UserRow user) =>
    string.IsNullOrWhiteSpace(user.MasterUserId) ? user.Id : user.MasterUserId;

static bool IsLinkedUser(UserRow user) => !string.IsNullOrWhiteSpace(user.MasterUserId);

static async Task<int> GetBatidaImparLimiteHora(NpgsqlConnection db)
{
    var rawValue = await db.ExecuteScalarAsync<string?>(
        "select valor from configuracoes_sistema where chave = 'batida_impar_limite_hora'");

    if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour))
    {
        return 10;
    }

    return Math.Clamp(hour, 0, 23);
}

static async Task<IEnumerable<IntegranteRow>> QueryIntegrantes(NpgsqlConnection db, string userId, bool onlyActiveWithFaceEncoding = false)
{
    return await db.QueryAsync<IntegranteRow>($"""
        select i.id, i.id_usuario as IdUsuario, i.cpf, i.nome, i.funcao_id as FuncaoId,
               f.funcao as FuncaoNome, i.biometria_facial as BiometriaFacial,
               i.face_encoding as FaceEncoding, i.ativo, i.created_at as CreatedAt, i.updated_at as UpdatedAt,
               ep.id as EmprestimoId, ep.data_inicio as EmprestimoDataInicio,
               ep.data_fim as EmprestimoDataFim, uo.nome as EquipeOrigemNome,
               (ep.id is not null) as Emprestado
        from integrantes i
        left join funcoes f on f.id = i.funcao_id
        left join lateral (
            select e.id, e.equipe_origem_id, e.data_inicio, e.data_fim
            from integrante_emprestimos e
            where e.integrante_id = i.id
              and e.equipe_destino_id = @UserId
              and e.status = 'ativo'
              and (now() at time zone 'America/Sao_Paulo')::date between e.data_inicio and e.data_fim
            order by e.data_fim desc
            limit 1
        ) ep on true
        left join users uo on uo.id = ep.equipe_origem_id
        where (i.id_usuario = @UserId or ep.id is not null)
        {(onlyActiveWithFaceEncoding ? "and i.ativo = true and i.face_encoding is not null" : "")}
        order by i.nome
        """, new { UserId = userId });
}

static async Task<IntegranteRow?> QueryIntegrante(NpgsqlConnection db, string id, string userId)
{
    return await db.QuerySingleOrDefaultAsync<IntegranteRow>("""
        select i.id, i.id_usuario as IdUsuario, i.cpf, i.nome, i.funcao_id as FuncaoId,
               f.funcao as FuncaoNome, i.biometria_facial as BiometriaFacial,
               i.face_encoding as FaceEncoding, i.ativo, i.created_at as CreatedAt, i.updated_at as UpdatedAt,
               ep.id as EmprestimoId, ep.data_inicio as EmprestimoDataInicio,
               ep.data_fim as EmprestimoDataFim, uo.nome as EquipeOrigemNome,
               (ep.id is not null) as Emprestado
        from integrantes i
        left join funcoes f on f.id = i.funcao_id
        left join lateral (
            select e.id, e.equipe_origem_id, e.data_inicio, e.data_fim
            from integrante_emprestimos e
            where e.integrante_id = i.id and e.equipe_destino_id = @UserId
              and e.status = 'ativo'
              and (now() at time zone 'America/Sao_Paulo')::date between e.data_inicio and e.data_fim
            order by e.data_fim desc limit 1
        ) ep on true
        left join users uo on uo.id = ep.equipe_origem_id
        where i.id = @Id and (i.id_usuario = @UserId or ep.id is not null)
        """, new { Id = id, UserId = userId });
}

static async Task<IntegranteRow?> QueryIntegranteByIdOrCpf(NpgsqlConnection db, string? id, string cpf, string userId)
{
    return await db.QueryFirstOrDefaultAsync<IntegranteRow>("""
        select i.id, i.id_usuario as IdUsuario, i.cpf, i.nome, i.funcao_id as FuncaoId,
               f.funcao as FuncaoNome, i.biometria_facial as BiometriaFacial,
               i.face_encoding as FaceEncoding, i.ativo, i.created_at as CreatedAt, i.updated_at as UpdatedAt
        from integrantes i
        left join funcoes f on f.id = i.funcao_id
        where i.id_usuario = @UserId
          and ((@Id is not null and i.id = @Id) or i.cpf = @Cpf)
        order by case when @Id is not null and i.id = @Id then 0 else 1 end
        limit 1
        """, new { Id = string.IsNullOrWhiteSpace(id) ? null : id, Cpf = cpf, UserId = userId });
}

static async Task<IReadOnlyList<ClienteRow>> QueryClientes(NpgsqlConnection db, string userId)
{
    var rows = await db.QueryAsync<ClienteRow>("""
        select id, id_aprovado as IdAprovado, nome, sigla, latitude, longitude, raio,
               created_at as CreatedAt, updated_at as UpdatedAt
        from clientes
        order by nome
        """);

    return rows.ToArray();
}

static async Task<ClienteRow?> QueryCliente(NpgsqlConnection db, string id, string userId)
{
    return await db.QuerySingleOrDefaultAsync<ClienteRow>("""
        select id, id_aprovado as IdAprovado, nome, sigla, latitude, longitude, raio,
               created_at as CreatedAt, updated_at as UpdatedAt
        from clientes
        where id = @Id
        """, new { Id = id });
}

static async Task<ApontamentoResponse?> CreateApontamento(
    NpgsqlConnection db,
    string userId,
    string clienteId,
    string integranteId,
    string tipo,
    GeoLocation geo,
    string? biometria,
    string? timestamp,
    bool liveness,
    bool offlineFaceVerified,
    bool offlineDeviceBiometric,
    bool syncedFromOffline = false,
    string? localizacaoStatus = null,
    string? solicitacaoAjusteId = null,
    string? cronogramaJustificativa = null,
    CronogramaExceptionEmailService? cronogramaEmailService = null,
    MaintenanceExceptionEmailService? maintenanceEmailService = null,
    UserRow? requester = null,
    CancellationToken cancellationToken = default)
{
    var cliente = await QueryCliente(db, clienteId, userId);
    var integrante = await QueryIntegrante(db, integranteId, userId);
    if (cliente is null || integrante is null) return null;
    var payerUserId = integrante.Emprestado ? integrante.IdUsuario : userId;

    var distance = DistanceMeters(geo.Latitude, geo.Longitude, cliente.Latitude, cliente.Longitude);
    var dentroDoRaio = distance <= cliente.Raio;
    var biometriaVerificada = offlineFaceVerified;
    var apontamentoStatus = NormalizeLocationStatus(localizacaoStatus);
    if (!string.IsNullOrWhiteSpace(biometria))
    {
        biometriaVerificada = Similarity(integrante.FaceEncoding ?? Array.Empty<double>(), ImageSignature(biometria)) >= 0.72
            || offlineFaceVerified;
    }
    var timestampUtc = ParseTimestamp(timestamp);
    var cronograma = await ClassificarCronograma(db, userId, clienteId, integrante.FuncaoId, integranteId, timestampUtc);
    var cronogramaExcecaoId = cronograma.Pendente ? NewId() : null;
    var approvalToken = cronograma.Pendente ? NewToken() : null;
    var manutencaoExcecaoId = cronograma.ManutencaoNaoIdentificada ? cronogramaExcecaoId : null;

    var row = new ApontamentoListRow
    {
        Id = NewId(),
        IdUsuario = userId,
        IdUsuarioPagador = payerUserId,
        EmprestimoId = integrante.EmprestimoId,
        ClienteId = clienteId,
        ClienteNome = cliente.Nome,
        IntegranteId = integranteId,
        IntegranteNome = integrante.Nome,
        Tipo = tipo,
        Latitude = geo.Latitude,
        Longitude = geo.Longitude,
        Timestamp = timestampUtc,
        DentroDoRaio = dentroDoRaio,
        BiometriaVerificada = biometriaVerificada,
        LocalizacaoStatus = apontamentoStatus,
        SolicitacaoAjusteId = solicitacaoAjusteId,
        CronogramaFaseId = cronograma.FaseId,
        CronogramaFaseSigla = cronograma.FaseSigla,
        DiaExtra = cronograma.DiaExtra,
        FuncaoExtra = cronograma.FuncaoExtra,
        CronogramaStatus = cronograma.Pendente ? "pendente_aprovacao" : "aprovado",
        CronogramaExcecaoId = cronogramaExcecaoId,
        CronogramaJustificativa = string.IsNullOrWhiteSpace(cronogramaJustificativa) ? null : cronogramaJustificativa.Trim(),
        ManutencaoStatus = cronograma.ManutencaoNaoIdentificada ? "pendente_aprovacao" : "aprovado",
        ManutencaoExcecaoId = manutencaoExcecaoId,
        CreatedAt = DateTime.UtcNow
    };

    await db.ExecuteAsync("""
        insert into apontamentos
            (id, id_usuario, id_usuario_pagador, emprestimo_id, cliente_id, integrante_id, tipo, latitude, longitude, timestamp,
             dentro_do_raio, biometria_verificada, liveness_verificado,
             offline_face_verified, offline_device_biometric_verified, synced_from_offline,
             localizacao_status, solicitacao_ajuste_id, cronograma_fase_id, cronograma_fase_sigla,
             dia_extra, funcao_extra, cronograma_status, cronograma_excecao_id, cronograma_justificativa,
             manutencao_status, manutencao_excecao_id, created_at)
        values
            (@Id, @IdUsuario, @IdUsuarioPagador, @EmprestimoId, @ClienteId, @IntegranteId, @Tipo, @Latitude, @Longitude, @Timestamp,
             @DentroDoRaio, @BiometriaVerificada, @Liveness, @OfflineFaceVerified, @OfflineDeviceBiometric,
             @SyncedFromOffline, @LocalizacaoStatus, @SolicitacaoAjusteId, @CronogramaFaseId, @CronogramaFaseSigla,
             @DiaExtra, @FuncaoExtra, @CronogramaStatus, @CronogramaExcecaoId, @CronogramaJustificativa,
             @ManutencaoStatus, @ManutencaoExcecaoId, @CreatedAt)
        """, new
    {
        row.Id,
        row.IdUsuario,
        row.IdUsuarioPagador,
        row.EmprestimoId,
        row.ClienteId,
        row.IntegranteId,
        row.Tipo,
        row.Latitude,
        row.Longitude,
        row.Timestamp,
        row.DentroDoRaio,
        row.BiometriaVerificada,
        Liveness = liveness,
        OfflineFaceVerified = offlineFaceVerified,
        OfflineDeviceBiometric = offlineDeviceBiometric,
        SyncedFromOffline = syncedFromOffline,
        row.LocalizacaoStatus,
        row.SolicitacaoAjusteId,
        row.CronogramaFaseId,
        row.CronogramaFaseSigla,
        row.DiaExtra,
        row.FuncaoExtra,
        row.CronogramaStatus,
        row.CronogramaExcecaoId,
        row.CronogramaJustificativa,
        row.ManutencaoStatus,
        row.ManutencaoExcecaoId,
        row.CreatedAt
    });

    if (cronograma.Pendente && cronogramaExcecaoId is not null && approvalToken is not null)
    {
        var tipoExcecao = cronograma.ManutencaoNaoIdentificada
            ? "manutencao_nao_identificada"
            : row.DiaExtra && row.FuncaoExtra
            ? "dia_extra,funcao_extra"
            : row.DiaExtra
                ? "dia_extra"
                : "funcao_extra";

        await db.ExecuteAsync("""
            insert into cronograma_excecoes
                (id, id_usuario, apontamento_id, tipo, status, motivo, approval_token, created_at)
            values
                (@Id, @UserId, @ApontamentoId, @Tipo, 'pendente', @Motivo, @ApprovalToken, @CreatedAt)
            """, new
        {
            Id = cronogramaExcecaoId,
            UserId = userId,
            ApontamentoId = row.Id,
            Tipo = tipoExcecao,
            Motivo = row.CronogramaJustificativa,
            ApprovalToken = approvalToken,
            row.CreatedAt
        });

        // O aviso interno e enviado em lote por data ao solicitar o resumo por email.
    }

    return ToApontamentoResponse(row);
}

static async Task<CronogramaClassification> ClassificarCronograma(
    NpgsqlConnection db,
    string userId,
    string clienteId,
    string funcaoId,
    string integranteId,
    DateTime timestampUtc)
{
    var data = DateOnly.FromDateTime(timestampUtc.ToLocalTime());
    var dataSql = ExPontoSql.DateParameter(data);
    var funcaoBaseId = await EnsureFuncaoBaseForUserFuncao(db, funcaoId);
    var fase = await db.QuerySingleOrDefaultAsync<CronogramaFaseRow>("""
        select id, cliente_id as ClienteId, fase, data_inicio as DataInicio, data_fim as DataFim
        from cronograma_fases
        where cliente_id = @ClienteId
          and cast(@Data as date) between data_inicio and data_fim
        order by data_inicio desc
        limit 1
        """, new { ClienteId = clienteId, Data = dataSql });

    if (fase is null)
    {
        var ultimaData = await db.ExecuteScalarAsync<DateOnly?>(
            "select max(data_fim) from cronograma_fases where cliente_id = @ClienteId",
            new { ClienteId = clienteId });

        var diaExtra = ultimaData.HasValue && data > ultimaData.Value;
        var montagemFim = await db.ExecuteScalarAsync<DateOnly?>("""
            select max(data_fim)
            from cronograma_fases
            where cliente_id = @ClienteId and upper(fase) = 'MONTAGEM'
            """, new { ClienteId = clienteId });
        var desmontagemInicio = await db.ExecuteScalarAsync<DateOnly?>("""
            select min(data_inicio)
            from cronograma_fases
            where cliente_id = @ClienteId and upper(fase) = 'DESMONTAGEM'
            """, new { ClienteId = clienteId });
        var manutencaoNaoIdentificada = montagemFim.HasValue
            && data > montagemFim.Value
            && (!desmontagemInicio.HasValue || data < desmontagemInicio.Value);

        return new CronogramaClassification(
            null,
            null,
            diaExtra,
            false,
            diaExtra || manutencaoNaoIdentificada,
            manutencaoNaoIdentificada);
    }

    var limite = string.IsNullOrWhiteSpace(funcaoBaseId)
        ? null
        : await db.ExecuteScalarAsync<int?>("""
        select limite_integrantes
        from cronograma_fase_funcoes
        where fase_id = @FaseId and funcao_base_id = @FuncaoBaseId
        """, new { FaseId = fase.Id, FuncaoBaseId = funcaoBaseId });

    var funcaoExtra = false;
    if (limite.HasValue)
    {
        var usados = await db.ExecuteScalarAsync<int>("""
            select count(distinct a.integrante_id)
            from apontamentos a
            join integrantes i on i.id = a.integrante_id
            join funcoes f on f.id = i.funcao_id
            where a.id_usuario = @UserId
              and a.cliente_id = @ClienteId
              and a.cronograma_fase_id = @FaseId
              and date(a.timestamp at time zone 'America/Sao_Paulo') = cast(@Data as date)
              and a.timestamp <= @Timestamp
              and f.funcao_base_id = @FuncaoBaseId
              and a.integrante_id <> @IntegranteId
              and coalesce(a.cronograma_status, 'aprovado') <> 'recusado'
            """, new
        {
            UserId = userId,
            ClienteId = clienteId,
            FaseId = fase.Id,
            Data = dataSql,
            Timestamp = timestampUtc,
            FuncaoBaseId = funcaoBaseId,
            IntegranteId = integranteId
        });

        funcaoExtra = ExPontoSql.IsFuncaoExtra(limite, usados);
    }

    return new CronogramaClassification(
        fase.Id,
        fase.Fase,
        false,
        funcaoExtra,
        funcaoExtra,
        false);
}

static async Task<int> ReclassificarApontamentosSemCronograma(
    NpgsqlConnection db,
    UserRow user,
    CronogramaExceptionEmailService cronogramaEmailService,
    MaintenanceExceptionEmailService maintenanceEmailService,
    CancellationToken cancellationToken)
{
    var userId = user.Id;
    var apontamentos = (await db.QueryAsync<ApontamentoReclassificationRow>(new CommandDefinition("""
        select a.id,
               a.cliente_id as ClienteId,
               a.integrante_id as IntegranteId,
               i.funcao_id as FuncaoId,
               a.timestamp,
               a.cronograma_fase_id as CronogramaFaseId,
               a.cronograma_fase_sigla as CronogramaFaseSigla,
               a.dia_extra as DiaExtra,
               a.funcao_extra as FuncaoExtra,
               a.cronograma_justificativa as CronogramaJustificativa
        from apontamentos a
        join integrantes i on i.id = a.integrante_id
        left join cronograma_excecoes e on e.id = a.cronograma_excecao_id
        where a.id_usuario = @UserId
          and e.id is null
          and coalesce(a.cronograma_status, 'aprovado') in ('aprovado', 'pendente_aprovacao')
        order by a.timestamp, a.id
        """, new { UserId = userId }, cancellationToken: cancellationToken))).ToList();

    var updated = 0;
    foreach (var apontamento in apontamentos)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var classification = await ClassificarCronograma(
            db,
            userId,
            apontamento.ClienteId,
            apontamento.FuncaoId,
            apontamento.IntegranteId,
            apontamento.Timestamp);

        if (classification.FaseId is null && !classification.Pendente)
        {
            continue;
        }

        if (classification.FaseId == apontamento.CronogramaFaseId
            && classification.FaseSigla == apontamento.CronogramaFaseSigla
            && classification.DiaExtra == apontamento.DiaExtra
            && classification.FuncaoExtra == apontamento.FuncaoExtra
            && !classification.Pendente)
        {
            continue;
        }

        var exceptionId = classification.Pendente ? NewId() : null;
        var approvalToken = classification.Pendente ? NewToken() : null;
        var cronogramaStatus = classification.Pendente ? "pendente_aprovacao" : "aprovado";
        var manutencaoStatus = classification.ManutencaoNaoIdentificada ? "pendente_aprovacao" : "aprovado";
        var manutencaoExcecaoId = classification.ManutencaoNaoIdentificada ? exceptionId : null;

        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        await db.ExecuteAsync(new CommandDefinition("""
            update apontamentos
            set cronograma_fase_id = @FaseId,
                cronograma_fase_sigla = @FaseSigla,
                dia_extra = @DiaExtra,
                funcao_extra = @FuncaoExtra,
                cronograma_status = @CronogramaStatus,
                cronograma_excecao_id = @CronogramaExcecaoId,
                manutencao_status = @ManutencaoStatus,
                manutencao_excecao_id = @ManutencaoExcecaoId
            where id = @Id and id_usuario = @UserId
            """, new
        {
            apontamento.Id,
            UserId = userId,
            classification.FaseId,
            FaseSigla = classification.FaseSigla,
            classification.DiaExtra,
            classification.FuncaoExtra,
            CronogramaStatus = cronogramaStatus,
            CronogramaExcecaoId = exceptionId,
            ManutencaoStatus = manutencaoStatus,
            ManutencaoExcecaoId = manutencaoExcecaoId
        }, transaction, cancellationToken: cancellationToken));

        if (exceptionId is not null && approvalToken is not null)
        {
            var tipo = classification.ManutencaoNaoIdentificada
                ? "manutencao_nao_identificada"
                : classification.DiaExtra && classification.FuncaoExtra
                    ? "dia_extra,funcao_extra"
                    : classification.DiaExtra ? "dia_extra" : "funcao_extra";
            await db.ExecuteAsync(new CommandDefinition("""
                insert into cronograma_excecoes
                    (id, id_usuario, apontamento_id, tipo, status, motivo, approval_token, created_at)
                values
                    (@Id, @UserId, @ApontamentoId, @Tipo, 'pendente', @Motivo, @ApprovalToken, @CreatedAt)
                """, new
            {
                Id = exceptionId,
                UserId = userId,
                ApontamentoId = apontamento.Id,
                Tipo = tipo,
                Motivo = apontamento.CronogramaJustificativa,
                ApprovalToken = approvalToken,
                CreatedAt = DateTime.UtcNow
            }, transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);

        updated++;
    }

    return updated;
}

static async Task<int> EnviarNotificacoesExcecoesPendentes(
    NpgsqlConnection db,
    UserRow user,
    CronogramaExceptionEmailService cronogramaEmailService,
    MaintenanceExceptionEmailService maintenanceEmailService,
    CancellationToken cancellationToken)
{
    var rows = (await db.QueryAsync<PendingExceptionNotificationRow>(new CommandDefinition("""
        select e.id,
               e.tipo,
               e.approval_token as ApprovalToken,
               a.id as ApontamentoId,
               a.tipo as ApontamentoTipo,
               a.cliente_id as ClienteId,
               a.integrante_id as IntegranteId,
               a.timestamp,
               a.cronograma_fase_id as CronogramaFaseId,
               a.cronograma_fase_sigla as CronogramaFaseSigla,
               a.dia_extra as DiaExtra,
               a.funcao_extra as FuncaoExtra,
               a.cronograma_justificativa as CronogramaJustificativa
        from cronograma_excecoes e
        join apontamentos a on a.id = e.apontamento_id
        where e.id_usuario = @UserId
          and e.status = 'pendente'
          and e.notification_sent_at is null
          and e.approval_token is not null
        order by e.created_at, e.id
        """, new { UserId = user.Id }, cancellationToken: cancellationToken))).ToList();

    var sent = 0;
    var groups = rows.GroupBy(pending => new
    {
        pending.ClienteId,
        pending.IntegranteId,
        Data = pending.Timestamp.ToLocalTime().Date,
        Manutencao = pending.Tipo == "manutencao_nao_identificada"
    });

    foreach (var group in groups)
    {
        var cliente = await QueryCliente(db, group.Key.ClienteId, user.Id);
        var integrante = await QueryIntegrante(db, group.Key.IntegranteId, user.Id);
        if (cliente is null || integrante is null) continue;

        var items = group
            .Where(pending => !string.IsNullOrWhiteSpace(pending.ApprovalToken))
            .Select(pending => new ExceptionEmailItem(
                new ApontamentoListRow
                {
                    Id = pending.ApontamentoId,
                    IdUsuario = user.Id,
                    ClienteId = pending.ClienteId,
                    ClienteNome = cliente.Nome,
                    IntegranteId = pending.IntegranteId,
                    IntegranteNome = integrante.Nome,
                    Tipo = pending.ApontamentoTipo,
                    Timestamp = pending.Timestamp,
                    CronogramaFaseId = pending.CronogramaFaseId,
                    CronogramaFaseSigla = pending.CronogramaFaseSigla,
                    DiaExtra = pending.DiaExtra,
                    FuncaoExtra = pending.FuncaoExtra,
                    CronogramaStatus = "pendente_aprovacao",
                    CronogramaExcecaoId = pending.Id,
                    CronogramaJustificativa = pending.CronogramaJustificativa,
                    ManutencaoStatus = group.Key.Manutencao ? "pendente_aprovacao" : "aprovado",
                    ManutencaoExcecaoId = group.Key.Manutencao ? pending.Id : null
                },
                pending.Id,
                pending.ApprovalToken!))
            .OrderBy(item => item.Apontamento.Timestamp)
            .ToList();
        if (items.Count == 0) continue;

        var delivered = group.Key.Manutencao
            ? await maintenanceEmailService.SendMaintenanceExceptionGroupRequestedAsync(
                user, cliente, integrante, items, cancellationToken)
            : await cronogramaEmailService.SendCronogramaExceptionGroupRequestedAsync(
                user, cliente, integrante, items, cancellationToken);

        if (!delivered) continue;
        foreach (var item in items)
        {
            await MarkExceptionNotificationSent(db, item.ExceptionId, cancellationToken);
        }
        sent++;
    }

    return sent;
}

static Task<int> MarkExceptionNotificationSent(
    NpgsqlConnection db,
    string exceptionId,
    CancellationToken cancellationToken) =>
    db.ExecuteAsync(new CommandDefinition("""
        update cronograma_excecoes
        set notification_sent_at = coalesce(notification_sent_at, @SentAt),
            updated_at = @SentAt
        where id = @Id
        """, new { Id = exceptionId, SentAt = DateTime.UtcNow }, cancellationToken: cancellationToken));

static string? CronogramaMessage(ApontamentoResponse apontamento)
{
    if (apontamento.CronogramaStatus != "pendente_aprovacao") return null;
    if (apontamento.DiaExtra && apontamento.FuncaoExtra)
    {
        return "Apontamento fora do cronograma e com funcao excedida. Enviado para aprovacao.";
    }

    if (apontamento.DiaExtra)
    {
        return "Apontamento em dia extra. Enviado para aprovacao.";
    }

    if (apontamento.FuncaoExtra)
    {
        return "Limite da funcao excedido na fase. Enviado para aprovacao.";
    }

    return "Apontamento enviado para aprovacao do cronograma.";
}

static string NormalizeLocationStatus(string? status)
{
    return status is "pendente_aprovacao" or "recusado" or "aprovado"
        ? status
        : "aprovado";
}

static DateTime ParseTimestamp(string? timestamp)
{
    return string.IsNullOrWhiteSpace(timestamp)
        ? DateTime.UtcNow
        : DateTime.Parse(timestamp).ToUniversalTime();
}

static string? ResolvePublicBackendUrl(string contentRootPath)
{
    var envValue = Environment.GetEnvironmentVariable("EXPO_PUBLIC_BACKEND_URL");
    if (!string.IsNullOrWhiteSpace(envValue)) return envValue;

    var candidates = new[]
    {
        Path.Combine(contentRootPath, ".env"),
        Path.Combine(contentRootPath, "..", "app", ".env"),
        Path.Combine(contentRootPath, "app", ".env"),
        Path.Combine(Directory.GetCurrentDirectory(), ".env"),
        Path.Combine(Directory.GetCurrentDirectory(), "..", "app", ".env")
    };

    foreach (var candidate in candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
    {
        var value = ReadDotEnvValue(candidate, "EXPO_PUBLIC_BACKEND_URL");
        if (!string.IsNullOrWhiteSpace(value)) return value;
    }

    return null;
}

static string? ReadDotEnvValue(string path, string key)
{
    if (!File.Exists(path)) return null;

    foreach (var rawLine in File.ReadLines(path))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;

        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0) continue;

        var name = line[..separatorIndex].Trim();
        if (!string.Equals(name, key, StringComparison.Ordinal)) continue;

        return line[(separatorIndex + 1)..].Trim().Trim('"', '\'');
    }

    return null;
}

static string FirstNonBlank(params string?[] values)
{
    return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "http://localhost:5000";
}

static string NormalizePublicBaseUrl(string value)
{
    var normalized = value.Trim().TrimEnd('/');
    return normalized.EndsWith("/api", StringComparison.OrdinalIgnoreCase)
        ? normalized[..^4]
        : normalized;
}

static string CreateToken(string userId, string deviceId, string secret, int hours)
{
    var claims = new[]
    {
        new Claim(JwtRegisteredClaimNames.Sub, userId),
        new Claim(ClaimTypes.NameIdentifier, userId),
        new Claim("device_id", deviceId)
    };
    var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddHours(hours), signingCredentials: credentials);
    return new JwtSecurityTokenHandler().WriteToken(token);
}

static string? BearerToken(HttpContext http)
{
    var header = http.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..] : null;
}

static string Html(string? value) => WebUtility.HtmlEncode(value ?? "");
static string NewId() => Guid.NewGuid().ToString();
static string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

static async Task<LocationAdjustmentDecisionResult?> DecideSolicitacaoAjuste(
    NpgsqlConnection db,
    string id,
    string token,
    string decision)
{
    var row = await db.QuerySingleOrDefaultAsync<LocationAdjustmentRow>("""
        select s.id, s.id_usuario as UserId, s.cliente_id as ClienteId,
               s.latitude_atual as LatitudeAtual, s.longitude_atual as LongitudeAtual,
               s.nova_latitude as NovaLatitude, s.nova_longitude as NovaLongitude,
               s.motivo, s.status, s.approval_token as ApprovalToken,
               s.created_at as CreatedAt, s.updated_at as UpdatedAt,
               u.nome as UserName, u.email as UserEmail,
               c.id_aprovado as ClienteIdAprovado, c.nome as ClienteNome, c.sigla as ClienteSigla
        from solicitacoes_ajuste s
        join users u on u.id = s.id_usuario
        join clientes c on c.id = s.cliente_id
        where s.id = @Id and s.approval_token = @Token
        """, new { Id = id, Token = token });

    if (row is null) return null;

    var status = decision == "aprovado" ? "aprovado" : "recusado";
    var apontamentoStatus = status == "aprovado" ? "aprovado" : "recusado";
    var now = DateTime.UtcNow;

    await using var tx = await db.BeginTransactionAsync();
    if (status == "aprovado")
    {
        await db.ExecuteAsync("""
            update clientes
            set latitude = @Latitude, longitude = @Longitude, updated_at = @Now
            where id = @ClienteId
            """, new { Latitude = row.NovaLatitude, Longitude = row.NovaLongitude, Now = now, row.ClienteId }, tx);
    }

    await db.ExecuteAsync("""
        update solicitacoes_ajuste
        set status = @Status, updated_at = @Now, decided_at = @Now, decided_by = 'email'
        where id = @Id
        """, new { Status = status, Now = now, row.Id }, tx);

    await db.ExecuteAsync("""
        update apontamentos
        set localizacao_status = @ApontamentoStatus
        where solicitacao_ajuste_id = @SolicitacaoId and localizacao_status = 'pendente_aprovacao'
        """, new { ApontamentoStatus = apontamentoStatus, SolicitacaoId = row.Id }, tx);

    await tx.CommitAsync();

    var user = new UserRow { Id = row.UserId, Nome = row.UserName, Email = row.UserEmail, Ativo = true };
    var cliente = new ClienteRow
    {
        Id = row.ClienteId,
        IdAprovado = row.ClienteIdAprovado,
        Nome = row.ClienteNome,
        Sigla = row.ClienteSigla,
        Latitude = row.NovaLatitude,
        Longitude = row.NovaLongitude
    };
    return new LocationAdjustmentDecisionResult(user, cliente, status);
}

static async Task<BeatAdjustmentDecisionResult?> DecideSolicitacaoAjusteBatida(
    NpgsqlConnection db,
    string id,
    string token,
    string decision)
{
    var row = await db.QuerySingleOrDefaultAsync<BeatAdjustmentRow>("""
        select s.id, s.id_usuario as UserId, s.cliente_id as ClienteId, s.integrante_id as IntegranteId,
               s.tipo, s.timestamp_ajuste as TimestampAjuste, s.motivo, s.status,
               s.approval_token as ApprovalToken, s.apontamento_id as ApontamentoId,
               s.created_at as CreatedAt, s.updated_at as UpdatedAt,
               u.nome as UserName, u.email as UserEmail,
               c.id_aprovado as ClienteIdAprovado, c.nome as ClienteNome, c.sigla as ClienteSigla,
               c.latitude as ClienteLatitude, c.longitude as ClienteLongitude, c.raio as ClienteRaio,
               i.nome as IntegranteNome, i.cpf as IntegranteCpf, i.funcao_id as FuncaoId
        from solicitacoes_ajuste_batida s
        join users u on u.id = s.id_usuario
        join clientes c on c.id = s.cliente_id
        join integrantes i on i.id = s.integrante_id
        where s.id = @Id and s.approval_token = @Token
        """, new { Id = id, Token = token });

    if (row is null) return null;

    var status = decision == "aprovado" ? "aprovado" : "recusado";
    var now = DateTime.UtcNow;
    string? apontamentoId = row.ApontamentoId;

    if (status == "aprovado" && string.IsNullOrWhiteSpace(apontamentoId))
    {
        var created = await CreateApontamento(
            db,
            row.UserId,
            row.ClienteId,
            row.IntegranteId,
            row.Tipo,
            new GeoLocation(row.ClienteLatitude, row.ClienteLongitude),
            null,
            row.TimestampAjuste.ToString("O", CultureInfo.InvariantCulture),
            liveness: false,
            offlineFaceVerified: false,
            offlineDeviceBiometric: false,
            syncedFromOffline: false,
            localizacaoStatus: "aprovado",
            solicitacaoAjusteId: null);
        apontamentoId = created?.Id;
    }

    await db.ExecuteAsync("""
        update solicitacoes_ajuste_batida
        set status = @Status, apontamento_id = @ApontamentoId, decided_at = @Now, decided_by = 'admin-email', updated_at = @Now
        where id = @Id
        """, new { Id = id, Status = status, ApontamentoId = apontamentoId, Now = now });

    var user = new UserRow { Id = row.UserId, Nome = row.UserName, Email = row.UserEmail, Ativo = true };
    var cliente = new ClienteRow
    {
        Id = row.ClienteId,
        IdAprovado = row.ClienteIdAprovado,
        Nome = row.ClienteNome,
        Sigla = row.ClienteSigla,
        Latitude = row.ClienteLatitude,
        Longitude = row.ClienteLongitude,
        Raio = row.ClienteRaio
    };
    var integrante = new IntegranteRow
    {
        Id = row.IntegranteId,
        IdUsuario = row.UserId,
        Nome = row.IntegranteNome,
        Cpf = row.IntegranteCpf,
        FuncaoId = row.FuncaoId,
        Ativo = true
    };
    return new BeatAdjustmentDecisionResult(user, cliente, integrante, status, row.TimestampAjuste);
}

static async Task<CronogramaExceptionDecisionResult?> DecideCronogramaException(
    NpgsqlConnection db,
    string id,
    string token,
    string decision)
{
    var row = await db.QuerySingleOrDefaultAsync<CronogramaExceptionRow>("""
        select e.id, e.id_usuario as UserId, e.apontamento_id as ApontamentoId,
               e.tipo, e.status, e.motivo, e.approval_token as ApprovalToken,
               e.created_at as CreatedAt, e.updated_at as UpdatedAt,
               u.nome as UserName, u.email as UserEmail,
               a.cliente_id as ClienteId, c.nome as ClienteNome, c.sigla as ClienteSigla,
               a.integrante_id as IntegranteId, i.nome as IntegranteNome,
               f.funcao as FuncaoNome, a.tipo as ApontamentoTipo,
               a.timestamp as Timestamp, a.cronograma_fase_sigla as CronogramaFaseSigla,
               a.dia_extra as DiaExtra, a.funcao_extra as FuncaoExtra,
               a.cronograma_status as CronogramaStatus,
               a.manutencao_status as ManutencaoStatus
        from cronograma_excecoes e
        join users u on u.id = e.id_usuario
        join apontamentos a on a.id = e.apontamento_id
        join clientes c on c.id = a.cliente_id
        join integrantes i on i.id = a.integrante_id
        left join funcoes f on f.id = i.funcao_id
        where e.id = @Id and e.approval_token = @Token
        """, new { Id = id, Token = token });

    if (row is null) return null;

    var status = decision == "aprovado" ? "aprovado" : "recusado";
    var now = DateTime.UtcNow;

    await using var tx = await db.BeginTransactionAsync();
    await db.ExecuteAsync("""
        update cronograma_excecoes
        set status = @Status, updated_at = @Now, decided_at = @Now, decided_by = 'email'
        where id = @Id
        """, new { Status = status, Now = now, row.Id }, tx);

    await db.ExecuteAsync("""
        update apontamentos
        set cronograma_status = @Status,
            manutencao_status = case
                when manutencao_excecao_id = @Id then @Status
                else manutencao_status
            end
        where id = @ApontamentoId
        """, new { Id = row.Id, Status = status, row.ApontamentoId }, tx);

    await tx.CommitAsync();

    var user = new UserRow { Id = row.UserId, Nome = row.UserName, Email = row.UserEmail, Ativo = true };
    return new CronogramaExceptionDecisionResult(user, row, status);
}

static async Task<CronogramaExceptionGroupDecisionResult?> DecideCronogramaExceptionGroup(
    NpgsqlConnection db,
    string id,
    string token,
    string decision)
{
    var anchor = await db.QuerySingleOrDefaultAsync<CronogramaExceptionRow>("""
        select e.id, e.id_usuario as UserId, e.apontamento_id as ApontamentoId,
               e.tipo, e.status, e.motivo, e.approval_token as ApprovalToken,
               e.created_at as CreatedAt, e.updated_at as UpdatedAt,
               u.nome as UserName, u.email as UserEmail,
               a.cliente_id as ClienteId, c.nome as ClienteNome, c.sigla as ClienteSigla,
               a.integrante_id as IntegranteId, i.nome as IntegranteNome,
               f.funcao as FuncaoNome, a.tipo as ApontamentoTipo, a.timestamp as Timestamp,
               a.cronograma_fase_sigla as CronogramaFaseSigla,
               a.dia_extra as DiaExtra, a.funcao_extra as FuncaoExtra,
               a.cronograma_status as CronogramaStatus,
               a.manutencao_status as ManutencaoStatus
        from cronograma_excecoes e
        join users u on u.id = e.id_usuario
        join apontamentos a on a.id = e.apontamento_id
        join clientes c on c.id = a.cliente_id
        join integrantes i on i.id = a.integrante_id
        left join funcoes f on f.id = i.funcao_id
        where e.id = @Id and e.approval_token = @Token
          and e.tipo <> 'manutencao_nao_identificada'
        """, new { Id = id, Token = token });
    if (anchor is null) return null;

    var status = decision == "aprovado" ? "aprovado" : "recusado";
    var data = ExPontoSql.DateParameter(DateOnly.FromDateTime(anchor.Timestamp.ToLocalTime()));
    var now = DateTime.UtcNow;
    await using var tx = await db.BeginTransactionAsync();
    var items = (await db.QueryAsync<CronogramaExceptionDecisionItem>("""
        select e.id, a.tipo as ApontamentoTipo, a.timestamp,
               a.cronograma_fase_sigla as CronogramaFaseSigla,
               a.dia_extra as DiaExtra, a.funcao_extra as FuncaoExtra,
               e.motivo
        from cronograma_excecoes e
        join apontamentos a on a.id = e.apontamento_id
        where e.id_usuario = @UserId
          and a.cliente_id = @ClienteId
          and a.integrante_id = @IntegranteId
          and date(a.timestamp at time zone 'America/Sao_Paulo') = cast(@Data as date)
          and e.tipo <> 'manutencao_nao_identificada'
          and e.status = 'pendente'
        """, new
    {
        anchor.UserId,
        anchor.ClienteId,
        anchor.IntegranteId,
        Data = data
    }, tx)).ToArray();
    var exceptionIds = items.Select(item => item.Id).ToArray();
    if (exceptionIds.Length == 0) return null;

    await db.ExecuteAsync("""
        update cronograma_excecoes
        set status = @Status, updated_at = @Now, decided_at = @Now, decided_by = 'email-data'
        where id = any(@Ids)
        """, new { Status = status, Now = now, Ids = exceptionIds }, tx);
    await db.ExecuteAsync("""
        update apontamentos
        set cronograma_status = @Status
        where cronograma_excecao_id = any(@Ids)
        """, new { Status = status, Ids = exceptionIds }, tx);
    await tx.CommitAsync();

    var user = new UserRow { Id = anchor.UserId, Nome = anchor.UserName, Email = anchor.UserEmail, Ativo = true };
    return new CronogramaExceptionGroupDecisionResult(
        new CronogramaExceptionDecisionResult(user, anchor, status),
        exceptionIds.Length,
        items);
}

static async Task<CronogramaExceptionDecisionResult?> DecideMaintenanceException(
    NpgsqlConnection db,
    string id,
    string token,
    string decision,
    string usuarioAd,
    string motivo)
{
    var row = await db.QuerySingleOrDefaultAsync<CronogramaExceptionRow>("""
        select e.id, e.id_usuario as UserId, e.apontamento_id as ApontamentoId,
               e.tipo, e.status, e.motivo, e.approval_token as ApprovalToken,
               e.created_at as CreatedAt, e.updated_at as UpdatedAt,
               u.nome as UserName, u.email as UserEmail,
               a.cliente_id as ClienteId, c.nome as ClienteNome, c.sigla as ClienteSigla,
               a.integrante_id as IntegranteId, i.nome as IntegranteNome,
               f.funcao as FuncaoNome,
               a.timestamp as Timestamp, a.cronograma_fase_sigla as CronogramaFaseSigla,
               a.dia_extra as DiaExtra, a.funcao_extra as FuncaoExtra,
               a.cronograma_status as CronogramaStatus,
               a.manutencao_status as ManutencaoStatus
        from cronograma_excecoes e
        join users u on u.id = e.id_usuario
        join apontamentos a on a.id = e.apontamento_id
        join clientes c on c.id = a.cliente_id
        join integrantes i on i.id = a.integrante_id
        left join funcoes f on f.id = i.funcao_id
        where e.id = @Id and e.approval_token = @Token and e.tipo = 'manutencao_nao_identificada'
        """, new { Id = id, Token = token });

    if (row is null) return null;

    var status = decision == "aprovado" ? "aprovado" : "recusado";
    var now = DateTime.UtcNow;

    await using var tx = await db.BeginTransactionAsync();
    await db.ExecuteAsync("""
        update cronograma_excecoes
        set status = @Status,
            decisao_motivo = @Motivo,
            decisao_usuario = @Usuario,
            updated_at = @Now,
            decided_at = @Now,
            decided_by = 'manutencao-ad'
        where id = @Id
        """, new { Id = id, Status = status, Motivo = motivo, Usuario = usuarioAd, Now = now }, tx);

    await db.ExecuteAsync("""
        update apontamentos
        set cronograma_status = @Status,
            manutencao_status = @Status
        where id = @ApontamentoId
        """, new { Status = status, row.ApontamentoId }, tx);

    if (status == "aprovado")
    {
        var data = DateOnly.FromDateTime(row.Timestamp.ToLocalTime());
        IReadOnlyList<CronogramaFaseFuncaoCreate>? funcoes = string.IsNullOrWhiteSpace(row.FuncaoNome)
            ? null
            : [new CronogramaFaseFuncaoCreate(row.FuncaoNome!, 999)];
        await UpsertCronogramaFase(db, row.ClienteId, "MANUTENCAO", data, data, funcoes, tx);
    }

    await tx.CommitAsync();

    var user = new UserRow { Id = row.UserId, Nome = row.UserName, Email = row.UserEmail, Ativo = true };
    row.ManutencaoStatus = status;
    return new CronogramaExceptionDecisionResult(user, row, status);
}

static string GenerateTemporaryPassword()
{
    const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    const string lower = "abcdefghijkmnopqrstuvwxyz";
    const string digits = "23456789";
    const string all = upper + lower + digits;

    var chars = new List<char>
    {
        PickChar(upper),
        PickChar(lower),
        PickChar(digits)
    };

    while (chars.Count < 12)
    {
        chars.Add(PickChar(all));
    }

    for (var i = chars.Count - 1; i > 0; i--)
    {
        var j = RandomNumberGenerator.GetInt32(i + 1);
        (chars[i], chars[j]) = (chars[j], chars[i]);
    }

    return new string(chars.ToArray());
}

static string GeneratePasswordResetCode()
{
    const string digits = "0123456789";
    return new string(Enumerable.Range(0, 6).Select(_ => PickChar(digits)).ToArray());
}

static char PickChar(string source) => source[RandomNumberGenerator.GetInt32(source.Length)];

static string BuildMaintenanceDecisionForm(string id, string token, string decisao, string? error = null)
{
    var title = decisao == "aprovar" ? "Aprovar manutencao" : "Recusar manutencao";
    var button = decisao == "aprovar" ? "Aprovar" : "Recusar";
    var errorHtml = string.IsNullOrWhiteSpace(error)
        ? ""
        : $"<div style=\"background:#ffebee;color:#b71c1c;border:1px solid #ffcdd2;border-radius:8px;padding:12px;margin-bottom:16px;\">{Html(error)}</div>";

    return $"""
    <!doctype html>
    <html lang="pt-BR">
    <body style="margin:0;background:#f5f7fb;font-family:Arial,Helvetica,sans-serif;color:#222;">
      <main style="max-width:520px;margin:40px auto;background:white;border:1px solid #ddd;border-radius:8px;padding:24px;">
        <h1 style="margin-top:0;font-size:22px;">{Html(title)}</h1>
        {errorHtml}
        <form method="post" action="/api/manutencoes-excecoes/{Html(id)}/decidir">
          <input type="hidden" name="token" value="{Html(token)}" />
          <input type="hidden" name="decisao" value="{Html(decisao)}" />
          <label style="display:block;margin-bottom:6px;font-weight:bold;">Usuario AD</label>
          <input name="usuario" required style="width:100%;box-sizing:border-box;padding:12px;margin-bottom:14px;border:1px solid #bbb;border-radius:6px;" />
          <label style="display:block;margin-bottom:6px;font-weight:bold;">Senha AD</label>
          <input name="senha" type="password" required style="width:100%;box-sizing:border-box;padding:12px;margin-bottom:14px;border:1px solid #bbb;border-radius:6px;" />
          <label style="display:block;margin-bottom:6px;font-weight:bold;">Motivo</label>
          <textarea name="motivo" required rows="5" style="width:100%;box-sizing:border-box;padding:12px;margin-bottom:18px;border:1px solid #bbb;border-radius:6px;"></textarea>
          <button type="submit" style="width:100%;padding:14px;border:0;border-radius:6px;background:#0b7cff;color:white;font-weight:bold;">{Html(button)}</button>
        </form>
      </main>
    </body>
    </html>
    """;
}

static IReadOnlyList<ClienteCreate> ParseClientePayload(JsonElement body)
{
    if (body.ValueKind == JsonValueKind.Array)
    {
        return body.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(ParseClienteObject)
            .ToArray();
    }

    if (body.ValueKind == JsonValueKind.Object)
    {
        return [ParseClienteObject(body)];
    }

    return [];
}

static ClienteCreate ParseClienteObject(JsonElement item)
{
    var latitude = ReadDouble(item, "latitude", "lat");
    var longitude = ReadDouble(item, "longitude", "lng", "lon");

    if (TryGetProperty(item, out var geo, "geolocalizacao", "geolocalização", "localizacao", "localização", "geo") &&
        geo.ValueKind == JsonValueKind.Object)
    {
        latitude ??= ReadDouble(geo, "latitude", "lat");
        longitude ??= ReadDouble(geo, "longitude", "lng", "lon");
    }

    return new ClienteCreate(
        IdAprovado: ReadString(item, "id_aprovado", "idAprovado", "codigo", "código", "cod", "id") ?? "",
        Nome: ReadString(item, "nome", "name", "cliente") ?? "",
        Sigla: ReadString(item, "sigla", "apelido") ?? "",
        Geolocalizacao: latitude.HasValue && longitude.HasValue ? new GeoLocation(latitude.Value, longitude.Value) : null,
        Raio: ReadDouble(item, "raio", "radius") ?? 0,
        Latitude: latitude,
        Longitude: longitude);
}

static bool TryGetProperty(JsonElement item, out JsonElement value, params string[] names)
{
    foreach (var property in item.EnumerateObject())
    {
        if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            value = property.Value;
            return true;
        }
    }

    value = default;
    return false;
}

static string? ReadString(JsonElement item, params string[] names)
{
    if (!TryGetProperty(item, out var value, names))
    {
        return null;
    }

    return value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()?.Trim(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };
}

static double? ReadDouble(JsonElement item, params string[] names)
{
    if (!TryGetProperty(item, out var value, names))
    {
        return null;
    }

    if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
    {
        return number;
    }

    if (value.ValueKind == JsonValueKind.String)
    {
        var text = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Replace(',', '.');
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
    }

    return null;
}

static string? ValidateCliente(ClienteCreate request)
{
    if (string.IsNullOrWhiteSpace(request.IdAprovado)) return "Informe id_aprovado.";
    if (string.IsNullOrWhiteSpace(request.Nome)) return "Informe nome.";
    if (string.IsNullOrWhiteSpace(request.Sigla)) return "Informe sigla.";
    if (request.Raio <= 0) return "Informe raio maior que zero.";

    var geolocalizacao = request.GetGeolocalizacao();
    if (geolocalizacao is null) return "Informe geolocalizacao.latitude/geolocalizacao.longitude ou latitude/longitude.";
    if (geolocalizacao.Latitude is < -90 or > 90) return "Latitude invalida.";
    if (geolocalizacao.Longitude is < -180 or > 180) return "Longitude invalida.";

    return null;
}

static async Task<ClienteRow> UpsertCliente(NpgsqlConnection db, ClienteCreate request, IDbTransaction? transaction = null)
{
    var geolocalizacao = request.GetGeolocalizacao()!;
    var row = new ClienteRow
    {
        Id = NewId(),
        IdAprovado = request.IdAprovado.Trim(),
        Nome = request.Nome.Trim(),
        Sigla = request.Sigla.Trim(),
        Latitude = geolocalizacao.Latitude,
        Longitude = geolocalizacao.Longitude,
        Raio = request.Raio,
        CreatedAt = DateTime.UtcNow
    };

    return await db.QuerySingleAsync<ClienteRow>("""
        insert into clientes (id, id_aprovado, nome, sigla, latitude, longitude, raio, created_at)
        values (@Id, @IdAprovado, @Nome, @Sigla, @Latitude, @Longitude, @Raio, @CreatedAt)
        on conflict (id_aprovado) do update
        set nome = excluded.nome,
            sigla = excluded.sigla,
            latitude = excluded.latitude,
            longitude = excluded.longitude,
            raio = excluded.raio,
            updated_at = now()
        returning id, id_aprovado as IdAprovado, nome, sigla, latitude, longitude, raio,
                  created_at as CreatedAt, updated_at as UpdatedAt
        """, row, transaction);
}

static async Task<ClienteRow?> ResolveCronogramaCliente(
    NpgsqlConnection db,
    string? clienteId,
    string? idAprovado,
    IReadOnlyDictionary<string, ClienteRow> clientesImportados,
    IDbTransaction? transaction = null)
{
    if (!string.IsNullOrWhiteSpace(clienteId))
    {
        return await db.QuerySingleOrDefaultAsync<ClienteRow>("""
            select id, id_aprovado as IdAprovado, nome, sigla, latitude, longitude, raio,
                   created_at as CreatedAt, updated_at as UpdatedAt
            from clientes
            where id = @Id
            """, new { Id = clienteId.Trim() }, transaction);
    }

    if (string.IsNullOrWhiteSpace(idAprovado)) return null;
    if (clientesImportados.TryGetValue(idAprovado.Trim(), out var imported)) return imported;

    return await db.QuerySingleOrDefaultAsync<ClienteRow>("""
        select id, id_aprovado as IdAprovado, nome, sigla, latitude, longitude, raio,
               created_at as CreatedAt, updated_at as UpdatedAt
        from clientes
        where lower(id_aprovado) = lower(@IdAprovado)
        """, new { IdAprovado = idAprovado.Trim() }, transaction);
}

static async Task<string> UpsertCronogramaFase(
    NpgsqlConnection db,
    string clienteId,
    string fase,
    DateOnly dataInicio,
    DateOnly dataFim,
    IReadOnlyList<CronogramaFaseFuncaoCreate>? funcoes,
    IDbTransaction? transaction = null,
    string? requestedId = null)
{
    var faseNome = fase.Trim();
    var dataInicioSql = ExPontoSql.DateParameter(dataInicio);
    var dataFimSql = ExPontoSql.DateParameter(dataFim);
    var faseId = string.IsNullOrWhiteSpace(requestedId)
        ? null
        : await db.ExecuteScalarAsync<string?>("""
            select id
            from cronograma_fases
            where id = @Id and cliente_id = @ClienteId
            """, new { Id = requestedId.Trim(), ClienteId = clienteId }, transaction);

    faseId ??= await db.ExecuteScalarAsync<string?>("""
        select id
        from cronograma_fases
        where cliente_id = @ClienteId
          and lower(fase) = lower(@Fase)
          and data_inicio = cast(@DataInicio as date)
          and data_fim = cast(@DataFim as date)
        limit 1
        """, new { ClienteId = clienteId, Fase = faseNome, DataInicio = dataInicioSql, DataFim = dataFimSql }, transaction);

    if (string.IsNullOrWhiteSpace(faseId))
    {
        var sameNameIds = (await db.QueryAsync<string>("""
            select id
            from cronograma_fases
            where cliente_id = @ClienteId and lower(fase) = lower(@Fase)
            order by data_inicio, data_fim, id
            limit 2
            """, new { ClienteId = clienteId, Fase = faseNome }, transaction)).ToArray();
        if (sameNameIds.Length == 1) faseId = sameNameIds[0];
    }

    if (string.IsNullOrWhiteSpace(faseId))
    {
        faseId = NewId();
        await db.ExecuteAsync("""
            insert into cronograma_fases (id, cliente_id, fase, data_inicio, data_fim, created_at)
            values (@Id, @ClienteId, @Fase, cast(@DataInicio as date), cast(@DataFim as date), @CreatedAt)
            """, new
        {
            Id = faseId,
            ClienteId = clienteId,
            Fase = faseNome,
            DataInicio = dataInicioSql,
            DataFim = dataFimSql,
            CreatedAt = DateTime.UtcNow
        }, transaction);
    }
    else
    {
        await db.ExecuteAsync("""
            update cronograma_fases
            set fase = @Fase,
                data_inicio = cast(@DataInicio as date),
                data_fim = cast(@DataFim as date),
                updated_at = @UpdatedAt
            where id = @Id
            """, new
        {
            Id = faseId,
            Fase = faseNome,
            DataInicio = dataInicioSql,
            DataFim = dataFimSql,
            UpdatedAt = DateTime.UtcNow
        }, transaction);
    }

    await db.ExecuteAsync(
        "delete from cronograma_fase_funcoes where fase_id = @FaseId",
        new { FaseId = faseId },
        transaction);

    foreach (var funcao in funcoes ?? Array.Empty<CronogramaFaseFuncaoCreate>())
    {
        if (string.IsNullOrWhiteSpace(funcao.Funcao)) continue;
        var funcaoBaseId = await EnsureFuncaoBase(db, funcao.Funcao, transaction);
        await db.ExecuteAsync("""
            insert into cronograma_fase_funcoes (id, fase_id, funcao_base_id, funcao_nome, limite_integrantes, created_at)
            values (@Id, @FaseId, @FuncaoBaseId, @FuncaoNome, @LimiteIntegrantes, @CreatedAt)
            on conflict (fase_id, funcao_base_id) where funcao_base_id is not null do update
            set limite_integrantes = excluded.limite_integrantes,
                funcao_nome = excluded.funcao_nome,
                updated_at = excluded.created_at
            """, new
        {
            Id = NewId(),
            FaseId = faseId,
            FuncaoBaseId = funcaoBaseId,
            FuncaoNome = funcao.Funcao.Trim(),
            LimiteIntegrantes = Math.Max(0, funcao.LimiteIntegrantes),
            CreatedAt = DateTime.UtcNow
        }, transaction);
    }

    return faseId;
}

static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
{
    const double earth = 6371000;
    var dLat = ToRadians(lat2 - lat1);
    var dLon = ToRadians(lon2 - lon1);
    var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
            Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
            Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
    return earth * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
}

static double ToRadians(double degrees) => degrees * Math.PI / 180;

static IReadOnlyList<double> ImageSignature(string base64)
{
    var normalized = base64.Contains(',') ? base64[(base64.IndexOf(',') + 1)..] : base64;
    using var sha = SHA256.Create();
    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
    return hash.Select(b => b / 255d).ToArray();
}

static double Similarity(IReadOnlyList<double> known, IReadOnlyList<double> unknown)
{
    if (known.Count == 0 || unknown.Count == 0) return 0;
    var len = Math.Min(known.Count, unknown.Count);
    var sum = 0d;
    for (var i = 0; i < len; i++)
    {
        var diff = known[i] - unknown[i];
        sum += diff * diff;
    }
    var distance = Math.Sqrt(sum / len);
    return Math.Clamp(1 - distance, 0, 1);
}

static IReadOnlyList<(string Nome, double Valor)> DefaultFuncoes() =>
[
    ("Encarregado", 0),
    ("Motorista", 0),
    ("Operador", 0),
    ("Ajudante", 0)
];

static async Task<IReadOnlyList<FuncaoRow>> QueryFuncoes(NpgsqlConnection db, string userId, IDbTransaction? transaction = null)
{
    var rows = await db.QueryAsync<FuncaoRow>("""
        select id, id_usuario as IdUsuario, funcao_base_id as FuncaoBaseId, funcao, valor, created_at as CreatedAt, updated_at as UpdatedAt
        from funcoes
        where id_usuario = @UserId
        order by funcao
        """, new { UserId = userId }, transaction);

    return rows.ToArray();
}

static async Task<IReadOnlyList<FuncaoRow>> EnsureDefaultFuncoes(NpgsqlConnection db, string userId, IDbTransaction? transaction = null)
{
    var current = await QueryFuncoes(db, userId, transaction);
    if (current.Count > 0) return current;

    var now = DateTime.UtcNow;
    foreach (var (nome, valor) in DefaultFuncoes())
    {
        var funcaoBaseId = await EnsureFuncaoBase(db, nome, transaction);
        await db.ExecuteAsync("""
            insert into funcoes (id, id_usuario, funcao_base_id, funcao, valor, created_at)
            values (@Id, @UserId, @FuncaoBaseId, @Funcao, @Valor, @CreatedAt)
            """, new
        {
            Id = NewId(),
            UserId = userId,
            FuncaoBaseId = funcaoBaseId,
            Funcao = nome,
            Valor = valor,
            CreatedAt = now
        }, transaction);
    }

    return await QueryFuncoes(db, userId, transaction);
}

static async Task ReplaceFuncoes(NpgsqlConnection db, string userId, IReadOnlyList<FuncaoCreate> funcoes, IDbTransaction? transaction = null)
{
    await db.ExecuteAsync("delete from funcoes where id_usuario = @UserId", new { UserId = userId }, transaction);

    foreach (var funcao in funcoes.Where(f => !string.IsNullOrWhiteSpace(f.Funcao)))
    {
        var funcaoBaseId = await EnsureFuncaoBase(db, funcao.Funcao, transaction);
        await db.ExecuteAsync("""
            insert into funcoes (id, id_usuario, funcao_base_id, funcao, valor, created_at)
            values (@Id, @UserId, @FuncaoBaseId, @Funcao, @Valor, @CreatedAt)
            """, new
        {
            Id = NewId(),
            UserId = userId,
            FuncaoBaseId = funcaoBaseId,
            Funcao = funcao.Funcao.Trim(),
            funcao.Valor,
            CreatedAt = DateTime.UtcNow
        }, transaction);
    }
}

static async Task<string> EnsureFuncaoBase(NpgsqlConnection db, string funcao, IDbTransaction? transaction = null)
{
    var nome = funcao.Trim();
    var normalizada = NormalizeFuncaoName(nome);
    var existing = await db.ExecuteScalarAsync<string?>(
        "select id from funcoes_base where funcao_normalizada = @Normalizada",
        new { Normalizada = normalizada },
        transaction);
    if (!string.IsNullOrWhiteSpace(existing)) return existing;

    var id = NewId();
    await db.ExecuteAsync("""
        insert into funcoes_base (id, funcao, funcao_normalizada, created_at)
        values (@Id, @Funcao, @Normalizada, @CreatedAt)
        on conflict (funcao_normalizada) do nothing
        """, new { Id = id, Funcao = nome, Normalizada = normalizada, CreatedAt = DateTime.UtcNow }, transaction);

    return await db.ExecuteScalarAsync<string?>(
        "select id from funcoes_base where funcao_normalizada = @Normalizada",
        new { Normalizada = normalizada },
        transaction) ?? id;
}

static async Task<string?> EnsureFuncaoBaseForUserFuncao(NpgsqlConnection db, string funcaoId)
{
    var row = await db.QuerySingleOrDefaultAsync<FuncaoRow>("""
        select id, id_usuario as IdUsuario, funcao_base_id as FuncaoBaseId, funcao, valor,
               created_at as CreatedAt, updated_at as UpdatedAt
        from funcoes
        where id = @FuncaoId
        """, new { FuncaoId = funcaoId });
    if (row is null) return null;
    if (!string.IsNullOrWhiteSpace(row.FuncaoBaseId)) return row.FuncaoBaseId;

    var funcaoBaseId = await EnsureFuncaoBase(db, row.Funcao);
    await db.ExecuteAsync(
        "update funcoes set funcao_base_id = @FuncaoBaseId where id = @FuncaoId",
        new { FuncaoBaseId = funcaoBaseId, FuncaoId = funcaoId });
    return funcaoBaseId;
}

static string NormalizeFuncaoName(string value) => value.Trim().ToLowerInvariant();

static async Task AssignUserCliente(NpgsqlConnection db, string userId, string clienteId, IDbTransaction? transaction = null)
{
    await db.ExecuteAsync("""
        insert into user_clientes (user_id, cliente_id, created_at)
        values (@UserId, @ClienteId, @CreatedAt)
        on conflict (user_id, cliente_id) do nothing
        """, new { UserId = userId, ClienteId = clienteId, CreatedAt = DateTime.UtcNow }, transaction);
}

static async Task ReplaceUserClientes(NpgsqlConnection db, string userId, IReadOnlyList<string> clienteIds, IDbTransaction? transaction = null)
{
    await db.ExecuteAsync("delete from user_clientes where user_id = @UserId", new { UserId = userId }, transaction);

    foreach (var clienteId in clienteIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct())
    {
        await AssignUserCliente(db, userId, clienteId, transaction);
    }
}

static async Task<DeviceLoginResult> RegisterDeviceLogin(NpgsqlConnection db, string userId, string deviceId)
{
    var currentStatus = await db.ExecuteScalarAsync<string?>("""
        select status
        from user_devices
        where user_id = @UserId and device_id = @DeviceId
        """, new { UserId = userId, DeviceId = deviceId });

    var activeOtherDevices = (await db.QueryAsync<string>("""
        select device_id
        from user_devices
        where user_id = @UserId and device_id <> @DeviceId and status = 'active'
        """, new { UserId = userId, DeviceId = deviceId })).ToArray();

    var now = DateTime.UtcNow;
    await db.ExecuteAsync("""
        update user_devices
        set status = 'blocked', blocked_at = @Now, updated_at = @Now
        where user_id = @UserId and device_id <> @DeviceId and status = 'active'
        """, new { UserId = userId, DeviceId = deviceId, Now = now });

    await db.ExecuteAsync("""
        insert into user_devices (user_id, device_id, status, last_seen_at, created_at, updated_at)
        values (@UserId, @DeviceId, 'active', @Now, @Now, @Now)
        on conflict (user_id, device_id) do update set
            status = 'active',
            blocked_at = null,
            last_seen_at = excluded.last_seen_at,
            updated_at = excluded.updated_at
        """, new { UserId = userId, DeviceId = deviceId, Now = now });

    return new DeviceLoginResult(
        PreviousDeviceBlocked: activeOtherDevices.Length > 0,
        CurrentDeviceWasBlocked: string.Equals(currentStatus, "blocked", StringComparison.OrdinalIgnoreCase));
}

static UserResponse ToUserResponse(
    UserRow row,
    IReadOnlyList<FuncaoRow>? funcoes = null,
    IReadOnlyList<ClienteRow>? clientes = null) => new(
    row.Id,
    DataOwnerId(row),
    row.MasterUserId,
    row.IntegranteVinculadoId,
    row.Nome,
    row.Email,
    row.MustChangePassword,
    row.Ativo,
    row.CreatedAt,
    row.UpdatedAt,
    funcoes?.Select(ToFuncaoResponse).ToArray(),
    clientes?.Select(ToClienteResponse).ToArray());

static FuncaoResponse ToFuncaoResponse(FuncaoRow row) => new(row.Id, row.Funcao, row.Valor, row.CreatedAt, row.UpdatedAt);

static IntegranteResponse ToIntegranteResponse(IntegranteRow row, bool includeEncoding = false) => new(
    row.Id,
    row.IdUsuario,
    row.Cpf,
    row.Nome,
    row.FuncaoId,
    row.FuncaoNome,
    row.Ativo,
    row.CreatedAt,
    row.UpdatedAt,
    includeEncoding ? row.FaceEncoding : null,
    row.Emprestado,
    row.EmprestimoId,
    row.EmprestimoDataInicio,
    row.EmprestimoDataFim,
    row.EquipeOrigemNome);

static ClienteResponse ToClienteResponse(ClienteRow row) => new(
    row.Id,
    row.IdAprovado,
    row.Nome,
    row.Sigla,
    new GeoLocation(row.Latitude, row.Longitude),
    row.Raio,
    row.CreatedAt,
    row.UpdatedAt,
    null,
    null);

static ApontamentoResponse ToApontamentoResponse(ApontamentoListRow row) => new(
    row.Id,
    row.IdUsuario,
    row.ClienteId,
    row.ClienteNome,
    row.IntegranteId,
    row.IntegranteNome,
    row.Tipo,
    new GeoLocation(row.Latitude, row.Longitude),
    row.Timestamp,
    row.DentroDoRaio,
    row.BiometriaVerificada,
    row.LocalizacaoStatus,
    row.SolicitacaoAjusteId,
    row.CronogramaFaseId,
    row.CronogramaFaseSigla,
    row.DiaExtra,
    row.FuncaoExtra,
    row.CronogramaStatus,
    row.CronogramaExcecaoId,
    row.CronogramaJustificativa,
    row.ManutencaoStatus,
    row.ManutencaoExcecaoId,
    row.CreatedAt);

}

public sealed class UserRow
{
    public string Id { get; set; } = "";
    public string Nome { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool MustChangePassword { get; set; }
    public bool Ativo { get; set; }
    public string? MasterUserId { get; set; }
    public string? IntegranteVinculadoId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class EmprestimoRow
{
    public string Id { get; set; } = "";
    public string IntegranteId { get; set; } = "";
    public string IntegranteNome { get; set; } = "";
    public string EquipeOrigemId { get; set; } = "";
    public string EquipeOrigemNome { get; set; } = "";
    public string EquipeOrigemEmail { get; set; } = "";
    public string EquipeDestinoId { get; set; } = "";
    public string EquipeDestinoNome { get; set; } = "";
    public string EquipeDestinoEmail { get; set; } = "";
    public DateOnly DataInicio { get; set; }
    public DateOnly DataFim { get; set; }
    public string Status { get; set; } = "ativo";
    public DateTime? DevolvidoEm { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class EmprestimoRenovacaoDecision
{
    public string Id { get; set; } = "";
    public string EmprestimoId { get; set; } = "";
    public string IntegranteId { get; set; } = "";
    public string IntegranteNome { get; set; } = "";
    public string EquipeOrigemId { get; set; } = "";
    public string EquipeDestinoId { get; set; } = "";
    public DateOnly DataFim { get; set; }
    public string Status { get; set; } = "";
}

public sealed class PasswordResetCodeRow
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Nome { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool MustChangePassword { get; set; }
    public bool Ativo { get; set; }
    public string Code { get; set; } = "";
    public DateTime UserCreatedAt { get; set; }
    public DateTime? UserUpdatedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}

public sealed class DataRetentionOptions
{
    public int ExpiredSessionGraceDays { get; set; } = 7;
    public int PasswordResetCodeDays { get; set; } = 7;
    public int BlockedDeviceDays { get; set; } = 90;
    public int DecidedAdjustmentDays { get; set; } = 180;
}

public sealed class FuncaoRow
{
    public string Id { get; set; } = "";
    public string IdUsuario { get; set; } = "";
    public string? FuncaoBaseId { get; set; }
    public string Funcao { get; set; } = "";
    public double Valor { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed record IntegranteRow
{
    public string Id { get; set; } = "";
    public string IdUsuario { get; set; } = "";
    public string Cpf { get; set; } = "";
    public string Nome { get; set; } = "";
    public string FuncaoId { get; set; } = "";
    public string? FuncaoNome { get; set; }
    public string? BiometriaFacial { get; set; }
    public double[]? FaceEncoding { get; set; }
    public bool Ativo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool Emprestado { get; set; }
    public string? EmprestimoId { get; set; }
    public DateOnly? EmprestimoDataInicio { get; set; }
    public DateOnly? EmprestimoDataFim { get; set; }
    public string? EquipeOrigemNome { get; set; }
}

public sealed class ClienteRow
{
    public string Id { get; set; } = "";
    public string IdAprovado { get; set; } = "";
    public string Nome { get; set; } = "";
    public string Sigla { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Raio { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class ApontamentoListRow
{
    public string Id { get; set; } = "";
    public string IdUsuario { get; set; } = "";
    public string IdUsuarioPagador { get; set; } = "";
    public string? EmprestimoId { get; set; }
    public string ClienteId { get; set; } = "";
    public string? ClienteNome { get; set; }
    public string IntegranteId { get; set; } = "";
    public string? IntegranteNome { get; set; }
    public string Tipo { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateTime Timestamp { get; set; }
    public bool DentroDoRaio { get; set; }
    public bool BiometriaVerificada { get; set; }
    public string LocalizacaoStatus { get; set; } = "aprovado";
    public string? SolicitacaoAjusteId { get; set; }
    public string? CronogramaFaseId { get; set; }
    public string? CronogramaFaseSigla { get; set; }
    public bool DiaExtra { get; set; }
    public bool FuncaoExtra { get; set; }
    public string CronogramaStatus { get; set; } = "aprovado";
    public string? CronogramaExcecaoId { get; set; }
    public string? CronogramaJustificativa { get; set; }
    public string ManutencaoStatus { get; set; } = "aprovado";
    public string? ManutencaoExcecaoId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class CronogramaFaseRow
{
    public string Id { get; set; } = "";
    public string ClienteId { get; set; } = "";
    public string Fase { get; set; } = "";
    public DateOnly DataInicio { get; set; }
    public DateOnly DataFim { get; set; }
}

public sealed record CronogramaClassification(
    string? FaseId,
    string? FaseSigla,
    bool DiaExtra,
    bool FuncaoExtra,
    bool Pendente,
    bool ManutencaoNaoIdentificada);

public sealed class ApontamentoReclassificationRow
{
    public string Id { get; set; } = "";
    public string ClienteId { get; set; } = "";
    public string IntegranteId { get; set; } = "";
    public string FuncaoId { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string? CronogramaFaseId { get; set; }
    public string? CronogramaFaseSigla { get; set; }
    public bool DiaExtra { get; set; }
    public bool FuncaoExtra { get; set; }
    public string? CronogramaJustificativa { get; set; }
}

public sealed class PendingExceptionNotificationRow
{
    public string Id { get; set; } = "";
    public string Tipo { get; set; } = "";
    public string? ApprovalToken { get; set; }
    public string ApontamentoId { get; set; } = "";
    public string ApontamentoTipo { get; set; } = "";
    public string ClienteId { get; set; } = "";
    public string IntegranteId { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string? CronogramaFaseId { get; set; }
    public string? CronogramaFaseSigla { get; set; }
    public bool DiaExtra { get; set; }
    public bool FuncaoExtra { get; set; }
    public string? CronogramaJustificativa { get; set; }
}

public sealed record ExceptionEmailItem(
    ApontamentoListRow Apontamento,
    string ExceptionId,
    string ApprovalToken);

public sealed class PresencaRow
{
    public string IntegranteId { get; set; } = "";
    public string Tipo { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public bool DentroDoRaio { get; set; }
    public bool BiometriaVerificada { get; set; }
    public string Id { get; set; } = "";
    public string IdUsuario { get; set; } = "";
    public string Cpf { get; set; } = "";
    public string Nome { get; set; } = "";
    public string FuncaoId { get; set; } = "";
    public string? FuncaoNome { get; set; }
    public bool Ativo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public IntegranteRow ToIntegrante() => new()
    {
        Id = Id,
        IdUsuario = IdUsuario,
        Cpf = Cpf,
        Nome = Nome,
        FuncaoId = FuncaoId,
        FuncaoNome = FuncaoNome,
        Ativo = Ativo,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt
    };
}

public sealed class LocationAdjustmentRow
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string ClienteId { get; set; } = "";
    public double LatitudeAtual { get; set; }
    public double LongitudeAtual { get; set; }
    public double NovaLatitude { get; set; }
    public double NovaLongitude { get; set; }
    public string? Motivo { get; set; }
    public string Status { get; set; } = "pendente";
    public string? ApprovalToken { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string UserName { get; set; } = "";
    public string UserEmail { get; set; } = "";
    public string ClienteIdAprovado { get; set; } = "";
    public string ClienteNome { get; set; } = "";
    public string ClienteSigla { get; set; } = "";
}

public sealed record LocationAdjustmentDecisionResult(UserRow User, ClienteRow Cliente, string Status);

public sealed class BeatAdjustmentRow
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string ClienteId { get; set; } = "";
    public string IntegranteId { get; set; } = "";
    public string Tipo { get; set; } = "";
    public DateTime TimestampAjuste { get; set; }
    public string? Motivo { get; set; }
    public string Status { get; set; } = "pendente";
    public string? ApprovalToken { get; set; }
    public string? ApontamentoId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string UserName { get; set; } = "";
    public string UserEmail { get; set; } = "";
    public string ClienteIdAprovado { get; set; } = "";
    public string ClienteNome { get; set; } = "";
    public string ClienteSigla { get; set; } = "";
    public double ClienteLatitude { get; set; }
    public double ClienteLongitude { get; set; }
    public double ClienteRaio { get; set; }
    public string IntegranteNome { get; set; } = "";
    public string IntegranteCpf { get; set; } = "";
    public string FuncaoId { get; set; } = "";
}

public sealed record BeatAdjustmentDecisionResult(
    UserRow User,
    ClienteRow Cliente,
    IntegranteRow Integrante,
    string Status,
    DateTime TimestampAjuste);

public sealed class CronogramaExceptionRow
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string ApontamentoId { get; set; } = "";
    public string Tipo { get; set; } = "";
    public string Status { get; set; } = "pendente";
    public string? Motivo { get; set; }
    public string? ApprovalToken { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string UserName { get; set; } = "";
    public string UserEmail { get; set; } = "";
    public string ClienteId { get; set; } = "";
    public string ClienteNome { get; set; } = "";
    public string ClienteSigla { get; set; } = "";
    public string IntegranteId { get; set; } = "";
    public string IntegranteNome { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string? CronogramaFaseSigla { get; set; }
    public bool DiaExtra { get; set; }
    public bool FuncaoExtra { get; set; }
    public string CronogramaStatus { get; set; } = "pendente_aprovacao";
    public string ManutencaoStatus { get; set; } = "aprovado";
    public string? FuncaoNome { get; set; }
    public string ApontamentoTipo { get; set; } = "";
}

public sealed record CronogramaExceptionDecisionResult(
    UserRow User,
    CronogramaExceptionRow Apontamento,
    string Status);

public sealed record CronogramaExceptionGroupDecisionResult(
    CronogramaExceptionDecisionResult Result,
    int Count,
    IReadOnlyList<CronogramaExceptionDecisionItem> Items);

public sealed class CronogramaExceptionDecisionItem
{
    public string Id { get; set; } = "";
    public string ApontamentoTipo { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string? CronogramaFaseSigla { get; set; }
    public bool DiaExtra { get; set; }
    public bool FuncaoExtra { get; set; }
    public string? Motivo { get; set; }
}

public sealed class ApontamentoEmailRow
{
    public string ClienteSigla { get; set; } = "";
    public string IntegranteId { get; set; } = "";
    public string IntegranteNome { get; set; } = "";
    public string FuncaoNome { get; set; } = "";
    public decimal Valor { get; set; }
    public string Tipo { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string LocalizacaoStatus { get; set; } = "aprovado";
    public string? CronogramaFaseSigla { get; set; }
    public bool DiaExtra { get; set; }
    public bool FuncaoExtra { get; set; }
    public string CronogramaStatus { get; set; } = "aprovado";
    public string ManutencaoStatus { get; set; } = "aprovado";
}

public sealed class ApontamentoEmailSummaryRow
{
    public string ClienteSigla { get; set; } = "";
    public DateTime Data { get; set; }
    public string IntegranteNome { get; set; } = "";
    public string FuncaoNome { get; set; } = "";
    public string Entrada { get; set; } = "";
    public string Saida { get; set; } = "";
    public string TotalHoras { get; set; } = "";
    public string Valor { get; set; } = "";
    public string LocalizacaoStatus { get; set; } = "";
    public string Fase { get; set; } = "";
    public string DiaExtra { get; set; } = "";
    public string FuncaoExtra { get; set; } = "";
    public string CronogramaStatus { get; set; } = "";
    public string ManutencaoStatus { get; set; } = "";
}

public sealed class ApontamentoSyncEmailService
{
    private readonly EmailOptions _emailOptions;
    private readonly ILogger<ApontamentoSyncEmailService> _logger;

    public ApontamentoSyncEmailService(IOptions<EmailOptions> emailOptions, ILogger<ApontamentoSyncEmailService> logger)
    {
        _emailOptions = emailOptions.Value;
        _logger = logger;
    }

    public async Task<bool> SendApontamentosResumoAsync(
        NpgsqlConnection db,
        UserRow user,
        CancellationToken cancellationToken)
    {
        if (!_emailOptions.Enabled) return false;

        try
        {
            var rows = (await db.QueryAsync<ApontamentoEmailRow>("""
                select c.sigla as ClienteSigla,
                       i.id as IntegranteId,
                       i.nome as IntegranteNome,
                       f.funcao as FuncaoNome,
                       f.valor as Valor,
                       a.tipo as Tipo,
                       a.timestamp as Timestamp,
                       a.localizacao_status as LocalizacaoStatus,
                       a.cronograma_fase_sigla as CronogramaFaseSigla,
                       a.dia_extra as DiaExtra,
                       a.funcao_extra as FuncaoExtra,
                       a.cronograma_status as CronogramaStatus,
                       a.manutencao_status as ManutencaoStatus
                from apontamentos a
                join clientes c on c.id = a.cliente_id
                join integrantes i on i.id = a.integrante_id
                join funcoes f on f.id = i.funcao_id
                where a.id_usuario = @UserId
                order by a.timestamp desc, c.sigla, i.nome
                limit 5000
                """, new { UserId = user.Id })).ToList();

            if (rows.Count == 0) return false;

            var culture = CultureInfo.GetCultureInfo("pt-BR");
            var summaryRows = AgruparApontamentosPorJornada(rows)
                .Select(group => ToEmailSummaryRow(group, culture))
                .OrderByDescending(row => row.Data)
                .ThenBy(row => row.ClienteSigla)
                .ThenBy(row => row.IntegranteNome)
                .ToList();

            var body = $"""
            <h2>Resumo de apontamentos</h2>
            <p>O resumo dos apontamentos sincronizados do usuario <strong>{Html(user.Nome)}</strong> esta no arquivo Excel em anexo.</p>
            <p>Total de linha(s): <strong>{summaryRows.Count}</strong></p>
            """;

            var attachment = BuildApontamentosXlsx(summaryRows, culture);
            await SendAsync(user.Email, "Resumo de apontamentos", body, attachment, "resumo-apontamentos.xlsx", cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao enviar email de resumo de apontamentos para {Email}", user.Email);
            return false;
        }
    }

    private async Task SendAsync(
        string to,
        string subject,
        string body,
        byte[] attachmentBytes,
        string attachmentName,
        CancellationToken cancellationToken)
    {
        using var message = new MailMessage
        {
            From = new MailAddress(_emailOptions.FromAddress, _emailOptions.FromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };
        message.To.Add(to);
        using var attachmentStream = new MemoryStream(attachmentBytes);
        message.Attachments.Add(new Attachment(
            attachmentStream,
            attachmentName,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));

        using var client = new SmtpClient(_emailOptions.Smtp.Host, _emailOptions.Smtp.Port)
        {
            EnableSsl = _emailOptions.Smtp.EnableSsl
        };

        if (!string.IsNullOrWhiteSpace(_emailOptions.Smtp.UserName))
        {
            client.Credentials = new NetworkCredential(_emailOptions.Smtp.UserName, _emailOptions.Smtp.Password);
        }

        await client.SendMailAsync(message, cancellationToken);
    }

    private static IReadOnlyList<IReadOnlyList<ApontamentoEmailRow>> AgruparApontamentosPorJornada(IReadOnlyList<ApontamentoEmailRow> rows)
    {
        var jornadas = new List<IReadOnlyList<ApontamentoEmailRow>>();
        var abertas = new Dictionary<string, (DateTime Inicio, List<ApontamentoEmailRow> Registros)>();
        var limiteJornada = TimeSpan.FromHours(18);

        foreach (var row in rows.OrderBy(row => row.Timestamp))
        {
            var key = $"{row.ClienteSigla}|{row.IntegranteId}";
            if (!abertas.TryGetValue(key, out var jornada) ||
                row.Timestamp - jornada.Inicio > limiteJornada)
            {
                jornada = (row.Timestamp, new List<ApontamentoEmailRow>());
                abertas[key] = jornada;
                jornadas.Add(jornada.Registros);
            }

            jornada.Registros.Add(row);
        }

        return jornadas;
    }

    private static ApontamentoEmailSummaryRow ToEmailSummaryRow(IReadOnlyList<ApontamentoEmailRow> group, CultureInfo culture)
    {
        var entrada = group.Where(row => row.Tipo == "entrada").OrderBy(row => row.Timestamp).FirstOrDefault();
        var saida = group.Where(row => row.Tipo == "saida").OrderByDescending(row => row.Timestamp).FirstOrDefault();
        DateTime? entradaAberta = null;
        var totalHoras = 0d;
        var saldoAberto = 0;

        foreach (var row in group.OrderBy(row => row.Timestamp))
        {
            if (row.Tipo == "entrada")
            {
                entradaAberta = row.Timestamp;
                saldoAberto++;
            }
            else if (row.Tipo == "saida" && entradaAberta is not null)
            {
                var diff = row.Timestamp - entradaAberta.Value;
                if (diff.TotalMinutes > 0) totalHoras += diff.TotalHours;
                entradaAberta = null;
                saldoAberto--;
            }
            else if (row.Tipo == "saida")
            {
                saldoAberto--;
            }
        }

        var first = group[0];
        var batidasImpares = group.Count % 2 != 0 || saldoAberto != 0;
        if (batidasImpares)
        {
            totalHoras = 0;
        }

        var statuses = group.Select(row => row.LocalizacaoStatus).Distinct().ToList();
        if (batidasImpares && !statuses.Contains("pendente_aprovacao"))
        {
            statuses.Add("pendente_aprovacao");
        }
        var status = string.Join(", ", statuses);
        var cronogramaStatuses = group.Select(row => row.CronogramaStatus).Distinct().ToList();
        var cronogramaStatus = string.Join(", ", cronogramaStatuses);
        var cronogramaAprovado = group.All(row => row.CronogramaStatus == "aprovado");
        var manutencaoStatus = string.Join(", ", group.Select(row => row.ManutencaoStatus).Distinct());

        return new ApontamentoEmailSummaryRow
        {
            ClienteSigla = first.ClienteSigla,
            Data = (entrada ?? first).Timestamp.ToLocalTime().Date,
            IntegranteNome = first.IntegranteNome,
            FuncaoNome = first.FuncaoNome,
            Entrada = entrada?.Timestamp.ToLocalTime().ToString("HH:mm", culture) ?? "-",
            Saida = saida?.Timestamp.ToLocalTime().ToString("HH:mm", culture) ?? "-",
            TotalHoras = totalHoras.ToString("0.##", culture),
            Valor = batidasImpares || !cronogramaAprovado ? 0d.ToString("C", culture) : first.Valor.ToString("C", culture),
            LocalizacaoStatus = status,
            Fase = first.CronogramaFaseSigla ?? "-",
            DiaExtra = group.Any(row => row.DiaExtra) ? "Sim" : "Nao",
            FuncaoExtra = group.Any(row => row.FuncaoExtra) ? "Sim" : "Nao",
            CronogramaStatus = string.IsNullOrWhiteSpace(cronogramaStatus) ? "aprovado" : cronogramaStatus,
            ManutencaoStatus = string.IsNullOrWhiteSpace(manutencaoStatus) ? "aprovado" : manutencaoStatus
        };
    }

    private static byte[] BuildApontamentosXlsx(IReadOnlyList<ApontamentoEmailSummaryRow> rows, CultureInfo culture)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddZipEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            </Types>
            """);
            AddZipEntry(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
            AddZipEntry(archive, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                <sheet name="Apontamentos" sheetId="1" r:id="rId1"/>
              </sheets>
            </workbook>
            """);
            AddZipEntry(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """);
            AddZipEntry(archive, "xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
              <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
              <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
              <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
              <cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0"/></cellXfs>
            </styleSheet>
            """);

            var headers = new[]
            {
                "Cliente", "Data", "Integrante", "Funcao", "Entrada", "Saida", "Total de horas", "Valor", "Status localizacao", "Fase", "Dia extra", "Funcao extra", "Status cronograma", "Status manutencao"
            };
            var sheet = new StringBuilder();
            sheet.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetViews><sheetView workbookViewId="0"/></sheetViews>
              <sheetFormatPr defaultRowHeight="15"/>
              <cols>
                <col min="1" max="1" width="14" customWidth="1"/>
                <col min="2" max="2" width="12" customWidth="1"/>
                <col min="3" max="3" width="32" customWidth="1"/>
                <col min="4" max="4" width="26" customWidth="1"/>
                <col min="5" max="6" width="12" customWidth="1"/>
                <col min="7" max="14" width="18" customWidth="1"/>
              </cols>
              <sheetData>
            """);
            sheet.Append(Row(1, headers, header: true));

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                sheet.Append(Row(i + 2, new[]
                {
                    row.ClienteSigla,
                    row.Data.ToString("dd/MM/yyyy", culture),
                    row.IntegranteNome,
                    row.FuncaoNome,
                    row.Entrada,
                    row.Saida,
                    row.TotalHoras,
                    row.Valor,
                    row.LocalizacaoStatus,
                    row.Fase,
                    row.DiaExtra,
                    row.FuncaoExtra,
                    row.CronogramaStatus,
                    row.ManutencaoStatus
                }));
            }

            sheet.Append("""
              </sheetData>
              <autoFilter ref="A1:N1"/>
            </worksheet>
            """);
            AddZipEntry(archive, "xl/worksheets/sheet1.xml", sheet.ToString());
        }

        return stream.ToArray();
    }

    private static string Row(int rowNumber, IReadOnlyList<string> values, bool header = false)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowNumber}\">");
        for (var i = 0; i < values.Count; i++)
        {
            builder.Append(Cell(rowNumber, i + 1, values[i], header));
        }
        builder.Append("</row>");
        return builder.ToString();
    }

    private static string Cell(int rowNumber, int columnNumber, string value, bool header)
    {
        var reference = $"{ColumnName(columnNumber)}{rowNumber}";
        var style = header ? " s=\"1\"" : "";
        return $"<c r=\"{reference}\" t=\"inlineStr\"{style}><is><t>{Xml(value)}</t></is></c>";
    }

    private static string ColumnName(int columnNumber)
    {
        var dividend = columnNumber;
        var columnName = "";
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            columnName = Convert.ToChar('A' + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }
        return columnName;
    }

    private static void AddZipEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content.Trim());
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);
    private static string Xml(string value) => WebUtility.HtmlEncode(value);
}

public sealed class CronogramaExceptionEmailService
{
    private readonly EmailOptions _emailOptions;
    private readonly LocationAdjustmentOptions _options;
    private readonly ILogger<CronogramaExceptionEmailService> _logger;

    public CronogramaExceptionEmailService(
        IOptions<EmailOptions> emailOptions,
        IOptions<LocationAdjustmentOptions> options,
        ILogger<CronogramaExceptionEmailService> logger)
    {
        _emailOptions = emailOptions.Value;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> SendCronogramaExceptionRequestedAsync(
        UserRow user,
        ClienteRow cliente,
        IntegranteRow integrante,
        ApontamentoListRow apontamento,
        string excecaoId,
        string token,
        CancellationToken cancellationToken)
    {
        var adminEmail = GetAdminEmail();
        if (!_emailOptions.Enabled)
        {
            _logger.LogWarning("Email de excecao de cronograma nao enviado porque Email:Enabled esta desativado.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(adminEmail))
        {
            _logger.LogWarning("Email de excecao de cronograma nao enviado. Configure Email:AdminAddress.");
            return false;
        }

        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        var approveUrl = $"{baseUrl}/api/cronograma-excecoes/{excecaoId}/aprovar?token={Uri.EscapeDataString(token)}";
        var rejectUrl = $"{baseUrl}/api/cronograma-excecoes/{excecaoId}/recusar?token={Uri.EscapeDataString(token)}";
        var motivos = new List<string>();
        if (apontamento.DiaExtra) motivos.Add("dia extra");
        if (apontamento.FuncaoExtra) motivos.Add("funcao extra");

        var body = $"""
        <h2>Apontamento fora do cronograma</h2>
        <p><strong>Usuario:</strong> {Html(user.Nome)} ({Html(user.Email)})</p>
        <p><strong>Cliente:</strong> {Html(cliente.Nome)} - {Html(cliente.Sigla)} / {Html(cliente.IdAprovado)}</p>
        <p><strong>Integrante:</strong> {Html(integrante.Nome)}</p>
        <p><strong>Data:</strong> {Html(apontamento.Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR")))}</p>
        <p><strong>Fase:</strong> {Html(apontamento.CronogramaFaseSigla ?? "Sem fase")}</p>
        <p><strong>Regra:</strong> {Html(string.Join(", ", motivos))}</p>
        <p><strong>Justificativa:</strong> {Html(apontamento.CronogramaJustificativa ?? "Nao informada")}</p>
        <p>
          <a href="{Html(approveUrl)}" style="display:inline-block;padding:12px 18px;background:#2e7d32;color:#fff;text-decoration:none;border-radius:6px;">Aprovar</a>
          <a href="{Html(rejectUrl)}" style="display:inline-block;padding:12px 18px;background:#c62828;color:#fff;text-decoration:none;border-radius:6px;margin-left:8px;">Recusar</a>
        </p>
        """;

        return await SendAsync(adminEmail, "Aprovacao de apontamento fora do cronograma", body, cancellationToken);
    }

    public async Task<bool> SendCronogramaExceptionGroupRequestedAsync(
        UserRow user,
        ClienteRow cliente,
        IntegranteRow integrante,
        IReadOnlyList<ExceptionEmailItem> items,
        CancellationToken cancellationToken)
    {
        var adminEmail = GetAdminEmail();
        if (!_emailOptions.Enabled || string.IsNullOrWhiteSpace(adminEmail) || items.Count == 0) return false;

        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        var details = new StringBuilder();
        foreach (var item in items)
        {
            var apontamento = item.Apontamento;
            var motivos = new List<string>();
            if (apontamento.DiaExtra) motivos.Add("dia extra");
            if (apontamento.FuncaoExtra) motivos.Add("funcao extra");
            details.Append($"""
                <hr>
                <p><strong>{Html(apontamento.Tipo)}</strong> - {Html(apontamento.Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.GetCultureInfo("pt-BR")))}</p>
                <p><strong>Fase:</strong> {Html(apontamento.CronogramaFaseSigla ?? "Sem fase")}</p>
                <p><strong>Regra:</strong> {Html(string.Join(", ", motivos))}</p>
                <p><strong>Justificativa:</strong> {Html(apontamento.CronogramaJustificativa ?? "Nao informada")}</p>
                """);
        }

        var anchor = items[0];
        var approveUrl = $"{baseUrl}/api/cronograma-excecoes/grupo/{anchor.ExceptionId}/aprovar?token={Uri.EscapeDataString(anchor.ApprovalToken)}";
        var rejectUrl = $"{baseUrl}/api/cronograma-excecoes/grupo/{anchor.ExceptionId}/recusar?token={Uri.EscapeDataString(anchor.ApprovalToken)}";
        var date = items[0].Apontamento.Timestamp.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR"));
        var body = $"""
            <h2>Apontamentos fora do cronograma</h2>
            <p><strong>Usuario:</strong> {Html(user.Nome)} ({Html(user.Email)})</p>
            <p><strong>Cliente:</strong> {Html(cliente.Nome)} - {Html(cliente.Sigla)} / {Html(cliente.IdAprovado)}</p>
            <p><strong>Integrante:</strong> {Html(integrante.Nome)}</p>
            <p><strong>Data:</strong> {Html(date)}</p>
            {details}
            <hr>
            <p><a href="{Html(approveUrl)}">Aprovar todos os apontamentos da data</a> | <a href="{Html(rejectUrl)}">Recusar todos os apontamentos da data</a></p>
            """;
        return await SendAsync(adminEmail, "Aprovacao de apontamentos fora do cronograma", body, cancellationToken);
    }

    public async Task SendCronogramaExceptionDecisionAsync(
        UserRow user,
        CronogramaExceptionRow apontamento,
        string status,
        CancellationToken cancellationToken)
    {
        if (!_emailOptions.Enabled) return;

        var subject = status == "aprovado"
            ? "Apontamento fora do cronograma aprovado"
            : "Apontamento fora do cronograma recusado";
        var regras = new List<string>();
        if (apontamento.DiaExtra) regras.Add("dia extra");
        if (apontamento.FuncaoExtra) regras.Add("funcao extra");
        var body = $"""
        <h2>{Html(subject)}</h2>
        <p><strong>Cliente:</strong> {Html(apontamento.ClienteNome)} ({Html(apontamento.ClienteSigla)})</p>
        <p><strong>Integrante:</strong> {Html(apontamento.IntegranteNome)}</p>
        <p><strong>Funcao:</strong> {Html(apontamento.FuncaoNome ?? "Nao informada")}</p>
        <p><strong>Batida:</strong> {Html(apontamento.ApontamentoTipo)} em {Html(apontamento.Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR")))}</p>
        <p><strong>Fase:</strong> {Html(apontamento.CronogramaFaseSigla ?? "Sem fase")}</p>
        <p><strong>Regra:</strong> {Html(string.Join(", ", regras))}</p>
        <p><strong>Justificativa:</strong> {Html(apontamento.Motivo ?? "Nao informada")}</p>
        <p><strong>Resultado:</strong> {Html(status)}</p>
        """;

        await SendAsync(user.Email, subject, body, cancellationToken);
    }

    public async Task SendCronogramaExceptionGroupDecisionAsync(
        CronogramaExceptionGroupDecisionResult decision,
        CancellationToken cancellationToken)
    {
        if (!_emailOptions.Enabled) return;
        var result = decision.Result;
        var apontamento = result.Apontamento;
        var subject = result.Status == "aprovado"
            ? "Apontamentos da data aprovados"
            : "Apontamentos da data recusados";
        var details = new StringBuilder();
        foreach (var item in decision.Items.OrderBy(item => item.Timestamp))
        {
            var regras = new List<string>();
            if (item.DiaExtra) regras.Add("dia extra");
            if (item.FuncaoExtra) regras.Add("funcao extra");
            details.Append($"""
                <hr>
                <p><strong>Batida:</strong> {Html(item.ApontamentoTipo)} - {Html(item.Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.GetCultureInfo("pt-BR")))}</p>
                <p><strong>Fase:</strong> {Html(item.CronogramaFaseSigla ?? "Sem fase")}</p>
                <p><strong>Regra:</strong> {Html(string.Join(", ", regras))}</p>
                <p><strong>Justificativa:</strong> {Html(item.Motivo ?? "Nao informada")}</p>
                """);
        }

        var body = $"""
            <h2>{Html(subject)}</h2>
            <p><strong>Cliente:</strong> {Html(apontamento.ClienteNome)} ({Html(apontamento.ClienteSigla)})</p>
            <p><strong>Integrante:</strong> {Html(apontamento.IntegranteNome)}</p>
            <p><strong>Funcao:</strong> {Html(apontamento.FuncaoNome ?? "Nao informada")}</p>
            <p><strong>Data:</strong> {Html(apontamento.Timestamp.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR")))}</p>
            <p><strong>Resultado:</strong> {Html(result.Status)}</p>
            <p><strong>Total de apontamentos:</strong> {decision.Count}</p>
            {details}
            """;
        await SendAsync(result.User.Email, subject, body, cancellationToken);
    }

    private async Task<bool> SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_emailOptions.FromAddress, _emailOptions.FromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };
            message.To.Add(to);

            using var client = new SmtpClient(_emailOptions.Smtp.Host, _emailOptions.Smtp.Port)
            {
                EnableSsl = _emailOptions.Smtp.EnableSsl
            };

            if (!string.IsNullOrWhiteSpace(_emailOptions.Smtp.UserName))
            {
                client.Credentials = new NetworkCredential(_emailOptions.Smtp.UserName, _emailOptions.Smtp.Password);
            }

            await client.SendMailAsync(message, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao enviar email de cronograma para {Email}", to);
            return false;
        }
    }

    private string? GetAdminEmail() => !string.IsNullOrWhiteSpace(_emailOptions.AdminAddress)
        ? _emailOptions.AdminAddress
        : _options.AdminEmail;
    private static string Html(string value) => WebUtility.HtmlEncode(value);
}

public sealed class MaintenanceAdAuthenticator
{
    private readonly IAdAuthenticationService _authentication;
    private readonly ILogger<MaintenanceAdAuthenticator> _logger;

    public MaintenanceAdAuthenticator(
        IAdAuthenticationService authentication,
        ILogger<MaintenanceAdAuthenticator> logger)
    {
        _authentication = authentication;
        _logger = logger;
    }

    public Task<MaintenanceAuthResult> ValidateAsync(string usuario, string senha, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(usuario) || string.IsNullOrWhiteSpace(senha))
        {
            return Task.FromResult(new MaintenanceAuthResult(false, "Informe usuario e senha do AD."));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _authentication.Authenticate(usuario, senha);
            return Task.FromResult(new MaintenanceAuthResult(true));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new MaintenanceAuthResult(false, "Usuario, senha ou permissao do AD invalida."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao validar login AD da manutencao.");
            return Task.FromResult(new MaintenanceAuthResult(false, "Falha ao consultar o AD."));
        }
    }
}

public sealed class MaintenanceExceptionEmailService
{
    private readonly EmailOptions _emailOptions;
    private readonly MaintenanceApprovalOptions _options;
    private readonly ILogger<MaintenanceExceptionEmailService> _logger;

    public MaintenanceExceptionEmailService(
        IOptions<EmailOptions> emailOptions,
        IOptions<MaintenanceApprovalOptions> options,
        ILogger<MaintenanceExceptionEmailService> logger)
    {
        _emailOptions = emailOptions.Value;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> SendMaintenanceExceptionRequestedAsync(
        UserRow user,
        ClienteRow cliente,
        IntegranteRow integrante,
        ApontamentoListRow apontamento,
        string excecaoId,
        string token,
        CancellationToken cancellationToken)
    {
        var adminEmail = !string.IsNullOrWhiteSpace(_options.AdminEmail)
            ? _options.AdminEmail
            : _emailOptions.AdminAddress;
        if (!_emailOptions.Enabled || string.IsNullOrWhiteSpace(adminEmail))
        {
            _logger.LogWarning("Email de manutencao nao enviado. Configure Email e MaintenanceApproval:AdminEmail.");
            return false;
        }

        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        var approveUrl = $"{baseUrl}/api/manutencoes-excecoes/{excecaoId}/aprovar?token={Uri.EscapeDataString(token)}";
        var rejectUrl = $"{baseUrl}/api/manutencoes-excecoes/{excecaoId}/recusar?token={Uri.EscapeDataString(token)}";

        var body = $"""
        <h2>Manutencao nao identificada no cronograma</h2>
        <p><strong>Usuario:</strong> {Html(user.Nome)} ({Html(user.Email)})</p>
        <p><strong>Cliente:</strong> {Html(cliente.Nome)} - {Html(cliente.Sigla)} / {Html(cliente.IdAprovado)}</p>
        <p><strong>Integrante:</strong> {Html(integrante.Nome)}</p>
        <p><strong>Data:</strong> {Html(apontamento.Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR")))}</p>
        <p>Esse apontamento ocorreu apos a montagem, mas nao existe manutencao programada/corretiva cadastrada para a data.</p>
        <p>
          <a href="{Html(approveUrl)}" style="display:inline-block;padding:12px 18px;background:#2e7d32;color:#fff;text-decoration:none;border-radius:6px;">Aprovar</a>
          <a href="{Html(rejectUrl)}" style="display:inline-block;padding:12px 18px;background:#c62828;color:#fff;text-decoration:none;border-radius:6px;margin-left:8px;">Recusar</a>
        </p>
        """;

        return await SendAsync(adminEmail, "Aprovacao de manutencao nao identificada", body, cancellationToken);
    }

    public async Task<bool> SendMaintenanceExceptionGroupRequestedAsync(
        UserRow user,
        ClienteRow cliente,
        IntegranteRow integrante,
        IReadOnlyList<ExceptionEmailItem> items,
        CancellationToken cancellationToken)
    {
        var adminEmail = !string.IsNullOrWhiteSpace(_options.AdminEmail)
            ? _options.AdminEmail
            : _emailOptions.AdminAddress;
        if (!_emailOptions.Enabled || string.IsNullOrWhiteSpace(adminEmail) || items.Count == 0) return false;

        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        var details = new StringBuilder();
        foreach (var item in items)
        {
            var apontamento = item.Apontamento;
            var approveUrl = $"{baseUrl}/api/manutencoes-excecoes/{item.ExceptionId}/aprovar?token={Uri.EscapeDataString(item.ApprovalToken)}";
            var rejectUrl = $"{baseUrl}/api/manutencoes-excecoes/{item.ExceptionId}/recusar?token={Uri.EscapeDataString(item.ApprovalToken)}";
            details.Append($"""
                <hr>
                <p><strong>{Html(apontamento.Tipo)}</strong> - {Html(apontamento.Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.GetCultureInfo("pt-BR")))}</p>
                <p><a href="{Html(approveUrl)}">Aprovar este apontamento</a> | <a href="{Html(rejectUrl)}">Recusar este apontamento</a></p>
                """);
        }

        var date = items[0].Apontamento.Timestamp.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR"));
        var body = $"""
            <h2>Manutencoes nao identificadas no cronograma</h2>
            <p><strong>Usuario:</strong> {Html(user.Nome)} ({Html(user.Email)})</p>
            <p><strong>Cliente:</strong> {Html(cliente.Nome)} - {Html(cliente.Sigla)} / {Html(cliente.IdAprovado)}</p>
            <p><strong>Integrante:</strong> {Html(integrante.Nome)}</p>
            <p><strong>Data:</strong> {Html(date)}</p>
            {details}
            """;
        return await SendAsync(adminEmail, "Aprovacao de manutencoes nao identificadas", body, cancellationToken);
    }

    public async Task SendMaintenanceDecisionAsync(
        UserRow user,
        CronogramaExceptionRow apontamento,
        string status,
        string? motivo,
        CancellationToken cancellationToken)
    {
        if (!_emailOptions.Enabled) return;

        var subject = status == "aprovado" ? "Manutencao aprovada" : "Manutencao recusada";
        var body = $"""
        <h2>{Html(subject)}</h2>
        <p>O apontamento de manutencao do integrante <strong>{Html(apontamento.IntegranteNome)}</strong> em <strong>{Html(apontamento.ClienteSigla)}</strong> foi <strong>{Html(status)}</strong>.</p>
        <p><strong>Motivo:</strong> {Html(motivo ?? "Nao informado")}</p>
        """;

        await SendAsync(user.Email, subject, body, cancellationToken);
    }

    private async Task<bool> SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_emailOptions.FromAddress, _emailOptions.FromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };
            message.To.Add(to);

            using var client = new SmtpClient(_emailOptions.Smtp.Host, _emailOptions.Smtp.Port)
            {
                EnableSsl = _emailOptions.Smtp.EnableSsl
            };

            if (!string.IsNullOrWhiteSpace(_emailOptions.Smtp.UserName))
            {
                client.Credentials = new NetworkCredential(_emailOptions.Smtp.UserName, _emailOptions.Smtp.Password);
            }

            await client.SendMailAsync(message, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao enviar email de manutencao para {Email}", to);
            return false;
        }
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);
}

public sealed class LocationAdjustmentEmailService
{
    private readonly EmailOptions _emailOptions;
    private readonly LocationAdjustmentOptions _options;
    private readonly ILogger<LocationAdjustmentEmailService> _logger;

    public LocationAdjustmentEmailService(
        IOptions<EmailOptions> emailOptions,
        IOptions<LocationAdjustmentOptions> options,
        ILogger<LocationAdjustmentEmailService> logger)
    {
        _emailOptions = emailOptions.Value;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAdjustmentRequestedAsync(
        UserRow user,
        ClienteRow cliente,
        string solicitacaoId,
        string token,
        GeoLocation novaLocalizacao,
        string? motivo,
        CancellationToken cancellationToken)
    {
        var adminEmail = GetAdminEmail();
        if (!_emailOptions.Enabled)
        {
            _logger.LogWarning("Email de ajuste de localizacao nao enviado porque Email:Enabled esta desativado.");
            return;
        }

        if (string.IsNullOrWhiteSpace(adminEmail))
        {
            _logger.LogWarning("Email de ajuste de localizacao nao enviado. Configure LocationAdjustment:AdminEmail ou Email:AdminAddress.");
            return;
        }

        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        var approveUrl = $"{baseUrl}/api/solicitacoes-ajuste/{solicitacaoId}/aprovar?token={Uri.EscapeDataString(token)}";
        var rejectUrl = $"{baseUrl}/api/solicitacoes-ajuste/{solicitacaoId}/recusar?token={Uri.EscapeDataString(token)}";
        var mapUrl = $"https://www.google.com/maps?q={novaLocalizacao.Latitude.ToString(CultureInfo.InvariantCulture)},{novaLocalizacao.Longitude.ToString(CultureInfo.InvariantCulture)}";

        var body = $"""
        <h2>Solicitacao de ajuste de localizacao</h2>
        <p><strong>Usuario:</strong> {Html(user.Nome)} ({Html(user.Email)})</p>
        <p><strong>Cliente:</strong> {Html(cliente.Nome)} - {Html(cliente.Sigla)} / {Html(cliente.IdAprovado)}</p>
        <p><strong>Localizacao atual:</strong> {cliente.Latitude}, {cliente.Longitude}</p>
        <p><strong>Nova localizacao:</strong> {novaLocalizacao.Latitude}, {novaLocalizacao.Longitude}</p>
        <p><strong>Motivo:</strong> {Html(motivo ?? "Nao informado")}</p>
        <p><a href="{Html(mapUrl)}">Consultar nova localizacao no mapa</a></p>
        <p>
          <a href="{Html(approveUrl)}" style="display:inline-block;padding:12px 18px;background:#2e7d32;color:#fff;text-decoration:none;border-radius:6px;">Aprovar</a>
          <a href="{Html(rejectUrl)}" style="display:inline-block;padding:12px 18px;background:#c62828;color:#fff;text-decoration:none;border-radius:6px;margin-left:8px;">Recusar</a>
        </p>
        """;

        await SendAsync(adminEmail, "Aprovacao de ajuste de localizacao", body, cancellationToken);
    }

    public async Task SendAdjustmentDecisionAsync(UserRow user, ClienteRow cliente, string status, CancellationToken cancellationToken)
    {
        if (!_emailOptions.Enabled) return;

        var subject = status == "aprovado" ? "Ajuste de localizacao aprovado" : "Ajuste de localizacao recusado";
        var body = $"""
        <h2>{Html(subject)}</h2>
        <p>O ajuste de localizacao do cliente <strong>{Html(cliente.Nome)}</strong> foi <strong>{Html(status)}</strong>.</p>
        """;

        await SendAsync(user.Email, subject, body, cancellationToken);
    }

    private async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_emailOptions.FromAddress, _emailOptions.FromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };
            message.To.Add(to);

            using var client = new SmtpClient(_emailOptions.Smtp.Host, _emailOptions.Smtp.Port)
            {
                EnableSsl = _emailOptions.Smtp.EnableSsl
            };

            if (!string.IsNullOrWhiteSpace(_emailOptions.Smtp.UserName))
            {
                client.Credentials = new NetworkCredential(_emailOptions.Smtp.UserName, _emailOptions.Smtp.Password);
            }

            await client.SendMailAsync(message, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao enviar email de ajuste de localizacao para {Email}", to);
        }
    }

    private string GetAdminEmail() =>
        !string.IsNullOrWhiteSpace(_options.AdminEmail)
            ? _options.AdminEmail
            : _emailOptions.AdminAddress;
    private static string Html(string value) => WebUtility.HtmlEncode(value);
}

public sealed class IntegranteLoanEmailService
{
    private readonly EmailOptions _emailOptions;
    private readonly LocationAdjustmentOptions _options;
    private readonly ILogger<IntegranteLoanEmailService> _logger;

    public IntegranteLoanEmailService(
        IOptions<EmailOptions> emailOptions,
        IOptions<LocationAdjustmentOptions> options,
        ILogger<IntegranteLoanEmailService> logger)
    {
        _emailOptions = emailOptions.Value;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> SendRenewalRequestedAsync(
        EmprestimoRow loan,
        string renewalId,
        string token,
        DateOnly newEndDate,
        CancellationToken cancellationToken)
    {
        if (!_emailOptions.Enabled) return false;
        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        var approveUrl = $"{baseUrl}/api/emprestimos/renovacoes/{renewalId}/aprovar?token={Uri.EscapeDataString(token)}";
        var rejectUrl = $"{baseUrl}/api/emprestimos/renovacoes/{renewalId}/recusar?token={Uri.EscapeDataString(token)}";
        var body = $"""
            <h2>Solicitacao de renovacao de emprestimo</h2>
            <p><strong>Integrante:</strong> {WebUtility.HtmlEncode(loan.IntegranteNome)}</p>
            <p><strong>Equipe receptora:</strong> {WebUtility.HtmlEncode(loan.EquipeDestinoNome)} ({WebUtility.HtmlEncode(loan.EquipeDestinoEmail)})</p>
            <p><strong>Periodo atual:</strong> {loan.DataInicio:dd/MM/yyyy} a {loan.DataFim:dd/MM/yyyy}</p>
            <p><strong>Nova data final solicitada:</strong> {newEndDate:dd/MM/yyyy}</p>
            <p>A equipe de origem continua responsavel pelo pagamento das diarias.</p>
            <p><a href="{WebUtility.HtmlEncode(approveUrl)}">Aprovar renovacao</a> | <a href="{WebUtility.HtmlEncode(rejectUrl)}">Recusar renovacao</a></p>
            """;
        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_emailOptions.FromAddress, _emailOptions.FromName),
                Subject = "Renovacao de emprestimo de integrante",
                Body = body,
                IsBodyHtml = true
            };
            message.To.Add(loan.EquipeOrigemEmail);
            using var client = new SmtpClient(_emailOptions.Smtp.Host, _emailOptions.Smtp.Port)
            {
                EnableSsl = _emailOptions.Smtp.EnableSsl
            };
            if (!string.IsNullOrWhiteSpace(_emailOptions.Smtp.UserName))
                client.Credentials = new NetworkCredential(_emailOptions.Smtp.UserName, _emailOptions.Smtp.Password);
            await client.SendMailAsync(message, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao enviar renovacao de emprestimo para {Email}", loan.EquipeOrigemEmail);
            return false;
        }
    }
}

public sealed class BeatAdjustmentEmailService
{
    private readonly EmailOptions _emailOptions;
    private readonly LocationAdjustmentOptions _options;
    private readonly ILogger<BeatAdjustmentEmailService> _logger;

    public BeatAdjustmentEmailService(
        IOptions<EmailOptions> emailOptions,
        IOptions<LocationAdjustmentOptions> options,
        ILogger<BeatAdjustmentEmailService> logger)
    {
        _emailOptions = emailOptions.Value;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendBeatAdjustmentRequestedAsync(
        UserRow user,
        ClienteRow cliente,
        IntegranteRow integrante,
        string solicitacaoId,
        string token,
        string tipo,
        DateTime timestamp,
        string? motivo,
        CancellationToken cancellationToken)
    {
        var adminEmail = GetAdminEmail();
        if (!_emailOptions.Enabled)
        {
            _logger.LogWarning("Email de ajuste de batida nao enviado porque Email:Enabled esta desativado.");
            return;
        }

        if (string.IsNullOrWhiteSpace(adminEmail))
        {
            _logger.LogWarning("Email de ajuste de batida nao enviado. Configure LocationAdjustment:AdminEmail ou Email:AdminAddress.");
            return;
        }

        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        var approveUrl = $"{baseUrl}/api/solicitacoes-ajuste-batida/{solicitacaoId}/aprovar?token={Uri.EscapeDataString(token)}";
        var rejectUrl = $"{baseUrl}/api/solicitacoes-ajuste-batida/{solicitacaoId}/recusar?token={Uri.EscapeDataString(token)}";
        var horario = timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"));

        var body = $"""
        <h2>Solicitacao de ajuste de batida</h2>
        <p><strong>Usuario:</strong> {Html(user.Nome)} ({Html(user.Email)})</p>
        <p><strong>Cliente:</strong> {Html(cliente.Nome)} - {Html(cliente.Sigla)} / {Html(cliente.IdAprovado)}</p>
        <p><strong>Integrante:</strong> {Html(integrante.Nome)} - CPF {Html(integrante.Cpf)}</p>
        <p><strong>Batida solicitada:</strong> {Html(tipo)} em {Html(horario)}</p>
        <p><strong>Motivo:</strong> {Html(motivo ?? "Nao informado")}</p>
        <p>
          <a href="{Html(approveUrl)}" style="display:inline-block;padding:12px 18px;background:#2e7d32;color:#fff;text-decoration:none;border-radius:6px;">Aprovar</a>
          <a href="{Html(rejectUrl)}" style="display:inline-block;padding:12px 18px;background:#c62828;color:#fff;text-decoration:none;border-radius:6px;margin-left:8px;">Recusar</a>
        </p>
        """;

        await SendAsync(adminEmail, "Aprovacao de ajuste de batida", body, cancellationToken);
    }

    public async Task SendBeatAdjustmentDecisionAsync(
        UserRow user,
        ClienteRow cliente,
        IntegranteRow integrante,
        string status,
        DateTime timestamp,
        CancellationToken cancellationToken)
    {
        if (!_emailOptions.Enabled) return;

        var subject = status == "aprovado" ? "Ajuste de batida aprovado" : "Ajuste de batida recusado";
        var horario = timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"));
        var body = $"""
        <h2>{Html(subject)}</h2>
        <p>O ajuste de batida do cliente <strong>{Html(cliente.Nome)}</strong> para o integrante <strong>{Html(integrante.Nome)}</strong> foi <strong>{Html(status)}</strong>.</p>
        <p><strong>Horario solicitado:</strong> {Html(horario)}</p>
        """;

        await SendAsync(user.Email, subject, body, cancellationToken);
    }

    private async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_emailOptions.FromAddress, _emailOptions.FromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };
            message.To.Add(to);

            using var client = new SmtpClient(_emailOptions.Smtp.Host, _emailOptions.Smtp.Port)
            {
                EnableSsl = _emailOptions.Smtp.EnableSsl
            };

            if (!string.IsNullOrWhiteSpace(_emailOptions.Smtp.UserName))
            {
                client.Credentials = new NetworkCredential(_emailOptions.Smtp.UserName, _emailOptions.Smtp.Password);
            }

            await client.SendMailAsync(message, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao enviar email de ajuste de batida para {Email}", to);
        }
    }

    private string GetAdminEmail() =>
        !string.IsNullOrWhiteSpace(_options.AdminEmail)
            ? _options.AdminEmail
            : _emailOptions.AdminAddress;
    private static string Html(string value) => WebUtility.HtmlEncode(value);
}

public sealed class UserNotificationEmailService
{
    private const string UserManualRelativePath = "Assets/Manual_exPonto_com_Telas.pdf";
    private readonly EmailOptions _emailOptions;
    private readonly AppLinksOptions _appLinksOptions;
    private readonly string _userManualPath;
    private readonly ILogger<UserNotificationEmailService> _logger;

    public UserNotificationEmailService(
        IOptions<EmailOptions> emailOptions,
        IOptions<AppLinksOptions> appLinksOptions,
        IWebHostEnvironment environment,
        ILogger<UserNotificationEmailService> logger)
    {
        _emailOptions = emailOptions.Value;
        _appLinksOptions = appLinksOptions.Value;
        _userManualPath = Path.Combine(environment.ContentRootPath, UserManualRelativePath);
        _logger = logger;
    }

    public async Task<bool> SendUserRegisteredAsync(
        UserRow user,
        string temporaryPassword,
        string? urlAndroid,
        string? urlIos,
        CancellationToken cancellationToken)
    {
        if (!_emailOptions.Enabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_emailOptions.FromAddress) ||
            string.IsNullOrWhiteSpace(_emailOptions.Smtp.Host))
        {
            _logger.LogWarning("Email de cadastro nao enviado. Configure Email:FromAddress e Email:Smtp:Host.");
            return false;
        }

        if (!File.Exists(_userManualPath))
        {
            _logger.LogError("Email de cadastro nao enviado porque o manual nao foi encontrado em {ManualPath}.", _userManualPath);
            return false;
        }

        var androidUrl = FirstValidUrl(urlAndroid, _appLinksOptions.UrlAndroid);
        var iosUrl = FirstValidUrl(urlIos, _appLinksOptions.UrlIos);

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_emailOptions.FromAddress, _emailOptions.FromName),
                Subject = "Acesso ao aplicativo ExPonto",
                Body = BuildRegisteredUserEmail(user, temporaryPassword, androidUrl, iosUrl),
                IsBodyHtml = true
            };
            message.To.Add(new MailAddress(user.Email, user.Nome));
            var manualAttachment = new Attachment(_userManualPath, "application/pdf")
            {
                Name = "Manual_exPonto_com_Telas.pdf"
            };
            message.Attachments.Add(manualAttachment);

            using var client = new SmtpClient(_emailOptions.Smtp.Host, _emailOptions.Smtp.Port)
            {
                EnableSsl = _emailOptions.Smtp.EnableSsl
            };

            if (!string.IsNullOrWhiteSpace(_emailOptions.Smtp.UserName))
            {
                client.Credentials = new NetworkCredential(_emailOptions.Smtp.UserName, _emailOptions.Smtp.Password);
            }

            await client.SendMailAsync(message, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao enviar email de cadastro para {Email}", user.Email);
            return false;
        }
    }

    public async Task<bool> SendPasswordResetCodeAsync(
        UserRow user,
        string code,
        DateTime expiresAt,
        CancellationToken cancellationToken)
    {
        if (!_emailOptions.Enabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_emailOptions.FromAddress) ||
            string.IsNullOrWhiteSpace(_emailOptions.Smtp.Host))
        {
            _logger.LogWarning("Email de recuperacao nao enviado. Configure Email:FromAddress e Email:Smtp:Host.");
            return false;
        }

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_emailOptions.FromAddress, _emailOptions.FromName),
                Subject = "Codigo de recuperacao de senha",
                Body = BuildPasswordResetEmail(user, code, expiresAt),
                IsBodyHtml = true
            };
            message.To.Add(new MailAddress(user.Email, user.Nome));

            using var client = new SmtpClient(_emailOptions.Smtp.Host, _emailOptions.Smtp.Port)
            {
                EnableSsl = _emailOptions.Smtp.EnableSsl
            };

            if (!string.IsNullOrWhiteSpace(_emailOptions.Smtp.UserName))
            {
                client.Credentials = new NetworkCredential(_emailOptions.Smtp.UserName, _emailOptions.Smtp.Password);
            }

            await client.SendMailAsync(message, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao enviar codigo de recuperacao para {Email}", user.Email);
            return false;
        }
    }

    public async Task<bool> SendUserRegisteredAdminNotificationAsync(UserRow user, CancellationToken cancellationToken)
    {
        if (!_emailOptions.Enabled)
        {
            _logger.LogWarning("Notificacao administrativa de cadastro nao enviada porque Email:Enabled esta desativado.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(_emailOptions.AdminAddress))
        {
            _logger.LogWarning("Notificacao administrativa de cadastro nao enviada. Configure Email:AdminAddress.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(_emailOptions.FromAddress) ||
            string.IsNullOrWhiteSpace(_emailOptions.Smtp.Host))
        {
            _logger.LogWarning("Notificacao administrativa de cadastro nao enviada. Configure Email:FromAddress e Email:Smtp:Host.");
            return false;
        }

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_emailOptions.FromAddress, _emailOptions.FromName),
                Subject = "Novo usuario cadastrado no ExPonto",
                Body = BuildRegisteredUserAdminNotification(user),
                IsBodyHtml = true
            };
            message.To.Add(_emailOptions.AdminAddress);

            using var client = new SmtpClient(_emailOptions.Smtp.Host, _emailOptions.Smtp.Port)
            {
                EnableSsl = _emailOptions.Smtp.EnableSsl
            };

            if (!string.IsNullOrWhiteSpace(_emailOptions.Smtp.UserName))
            {
                client.Credentials = new NetworkCredential(_emailOptions.Smtp.UserName, _emailOptions.Smtp.Password);
            }

            await client.SendMailAsync(message, cancellationToken);
            _logger.LogInformation("Notificacao administrativa de cadastro enviada para {AdminEmail} sobre usuario {UserEmail}.", _emailOptions.AdminAddress, user.Email);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao enviar notificacao administrativa de cadastro para {AdminEmail}", _emailOptions.AdminAddress);
            return false;
        }
    }

    public Task<bool> SendExistingProvisionalPasswordWillBeResentAdminNotificationAsync(UserRow user, CancellationToken cancellationToken)
    {
        return SendAdminUserNoticeAsync(
            user,
            "Usuario ja cadastrado - nova senha provisoria",
            "O usuario abaixo ja possuia cadastro com senha provisoria pendente. Uma nova senha provisoria sera reenviada para o usuario.",
            "notificacao de reenvio de senha provisoria",
            cancellationToken);
    }

    public Task<bool> SendExistingUserAlreadyAccessedAdminNotificationAsync(UserRow user, CancellationToken cancellationToken)
    {
        return SendAdminUserNoticeAsync(
            user,
            "Usuario ja acessou o ExPonto",
            "O usuario abaixo ja acessou o ExPonto e alterou a senha provisoria. Caso necessario, a senha pode ser redefinida pelo fluxo de recuperacao.",
            "notificacao de usuario ja acessado",
            cancellationToken);
    }

    private async Task<bool> SendAdminUserNoticeAsync(
        UserRow user,
        string subject,
        string messageText,
        string logContext,
        CancellationToken cancellationToken)
    {
        if (!_emailOptions.Enabled)
        {
            _logger.LogWarning("{LogContext} nao enviada porque Email:Enabled esta desativado.", logContext);
            return false;
        }

        if (string.IsNullOrWhiteSpace(_emailOptions.AdminAddress))
        {
            _logger.LogWarning("{LogContext} nao enviada. Configure Email:AdminAddress.", logContext);
            return false;
        }

        if (string.IsNullOrWhiteSpace(_emailOptions.FromAddress) ||
            string.IsNullOrWhiteSpace(_emailOptions.Smtp.Host))
        {
            _logger.LogWarning("{LogContext} nao enviada. Configure Email:FromAddress e Email:Smtp:Host.", logContext);
            return false;
        }

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_emailOptions.FromAddress, _emailOptions.FromName),
                Subject = subject,
                Body = BuildRegisteredUserAdminNotification(user, subject, messageText),
                IsBodyHtml = true
            };
            message.To.Add(_emailOptions.AdminAddress);

            using var client = new SmtpClient(_emailOptions.Smtp.Host, _emailOptions.Smtp.Port)
            {
                EnableSsl = _emailOptions.Smtp.EnableSsl
            };

            if (!string.IsNullOrWhiteSpace(_emailOptions.Smtp.UserName))
            {
                client.Credentials = new NetworkCredential(_emailOptions.Smtp.UserName, _emailOptions.Smtp.Password);
            }

            await client.SendMailAsync(message, cancellationToken);
            _logger.LogInformation("{LogContext} enviada para {AdminEmail} sobre usuario {UserEmail}.", logContext, _emailOptions.AdminAddress, user.Email);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao enviar {LogContext} para {AdminEmail}", logContext, _emailOptions.AdminAddress);
            return false;
        }
    }

    private static string? FirstValidUrl(params string?[] urls)
    {
        foreach (var url in urls)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
                (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
            {
                return parsed.ToString();
            }
        }

        return null;
    }

    private static string BuildRegisteredUserAdminNotification(UserRow user)
    {
        return BuildRegisteredUserAdminNotification(
            user,
            "Novo usuario cadastrado no ExPonto",
            "O usuario abaixo recebeu a senha provisoria por email.");
    }

    private static string BuildRegisteredUserAdminNotification(UserRow user, string title, string messageText)
    {
        return $"""
        <!doctype html>
        <html lang="pt-BR">
        <body style="margin:0;padding:0;background:#f4f7f6;font-family:Arial,Helvetica,sans-serif;color:#1f2a2e;">
          <div style="max-width:640px;margin:0 auto;padding:28px 16px;">
            <div style="background:#ffffff;border:1px solid #dfe7e3;border-radius:8px;padding:28px;">
              <h1 style="margin:0 0 12px;color:#004d43;font-size:22px;">{Html(title)}</h1>
              <p style="margin:0 0 18px;font-size:15px;line-height:1.5;">{Html(messageText)}</p>

              <table style="width:100%;border-collapse:collapse;font-size:14px;">
                <tr>
                  <td style="padding:10px;border-bottom:1px solid #dfe7e3;font-weight:bold;width:120px;">Nome</td>
                  <td style="padding:10px;border-bottom:1px solid #dfe7e3;">{Html(user.Nome)}</td>
                </tr>
                <tr>
                  <td style="padding:10px;border-bottom:1px solid #dfe7e3;font-weight:bold;">Email</td>
                  <td style="padding:10px;border-bottom:1px solid #dfe7e3;">{Html(user.Email)}</td>
                </tr>
              </table>
            </div>
          </div>
        </body>
        </html>
        """;
    }

    private static string BuildRegisteredUserEmail(UserRow user, string temporaryPassword, string? androidUrl, string? iosUrl)
    {
        var buttons = new StringBuilder();
        AppendStoreBadge(buttons, androidUrl, "GET IT ON", "Google Play", "&#9654;");
        AppendStoreBadge(buttons, iosUrl, "Download on", "App Store", "&#63743;");

        var downloadSection = buttons.Length > 0
            ? $"""
              <p style="margin:24px 0 12px;color:#283237;font-size:15px;">Baixe o aplicativo no seu dispositivo:</p>
              <div style="margin:0 0 24px;">{buttons}</div>
              """
            : """
              <p style="margin:24px 0;color:#59656b;font-size:14px;">O link de download sera enviado assim que estiver disponivel.</p>
              """;

        return $"""
        <!doctype html>
        <html lang="pt-BR">
        <body style="margin:0;padding:0;background:#f4f7f6;font-family:Arial,Helvetica,sans-serif;color:#1f2a2e;">
          <div style="max-width:640px;margin:0 auto;padding:28px 16px;">
            <div style="background:#ffffff;border:1px solid #dfe7e3;border-radius:8px;padding:28px;">
              <h1 style="margin:0 0 12px;color:#004d43;font-size:22px;">Seu acesso foi criado</h1>
              <p style="margin:0 0 18px;font-size:15px;line-height:1.5;">Ola, {Html(user.Nome)}. Use os dados abaixo para acessar o aplicativo Equipe Externa.</p>

              <div style="background:#eef5df;border:1px solid #d9e8c4;border-radius:8px;padding:16px;margin:18px 0;">
                <p style="margin:0 0 8px;font-size:14px;"><strong>E-mail:</strong> {Html(user.Email)}</p>
                <p style="margin:0;font-size:14px;"><strong>Senha temporaria:</strong> {Html(temporaryPassword)}</p>
              </div>

              <p style="margin:0;color:#59656b;font-size:14px;line-height:1.5;">Por seguranca, o aplicativo solicitara a troca da senha no primeiro acesso.</p>
              {downloadSection}
            </div>
          </div>
        </body>
        </html>
        """;
    }

    private static string BuildPasswordResetEmail(UserRow user, string code, DateTime expiresAt)
    {
        var expiresText = expiresAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"));

        return $"""
        <!doctype html>
        <html lang="pt-BR">
        <body style="margin:0;padding:0;background:#f4f7f6;font-family:Arial,Helvetica,sans-serif;color:#1f2a2e;">
          <div style="max-width:640px;margin:0 auto;padding:28px 16px;">
            <div style="background:#ffffff;border:1px solid #dfe7e3;border-radius:8px;padding:28px;">
              <h1 style="margin:0 0 12px;color:#004d43;font-size:22px;">Recuperacao de senha</h1>
              <p style="margin:0 0 18px;font-size:15px;line-height:1.5;">Ola, {Html(user.Nome)}. Use o codigo abaixo para definir uma nova senha no aplicativo.</p>

              <div style="background:#eef5df;border:1px solid #d9e8c4;border-radius:8px;padding:20px;margin:18px 0;text-align:center;">
                <p style="margin:0 0 8px;font-size:13px;color:#59656b;">Codigo de recuperacao</p>
                <p style="margin:0;font-size:32px;letter-spacing:8px;font-weight:bold;color:#004d43;">{Html(code)}</p>
              </div>

              <p style="margin:0;color:#59656b;font-size:14px;line-height:1.5;">Este codigo expira em <strong>{Html(expiresText)}</strong>.</p>
              <p style="margin:16px 0 0;color:#59656b;font-size:14px;line-height:1.5;">Se voce nao solicitou a troca de senha, pode ignorar este email.</p>
            </div>
          </div>
        </body>
        </html>
        """;
    }

    private static void AppendStoreBadge(StringBuilder buttons, string? url, string caption, string storeName, string iconHtml)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        buttons.Append($"""
        <a href="{Html(url)}" target="_blank" style="display:inline-block;width:224px;margin:0 10px 10px 0;padding:8px 12px;background:#111111;color:#ffffff;text-decoration:none;border-radius:8px;border:1px solid #303030;font-family:Arial,Helvetica,sans-serif;">
          <span style="display:inline-block;width:42px;height:42px;margin-right:10px;color:#ffffff;text-align:center;line-height:42px;font-size:28px;vertical-align:middle;">{iconHtml}</span>
          <span style="display:inline-block;vertical-align:middle;line-height:1.05;">
            <span style="display:block;color:#ffffff;font-size:10px;letter-spacing:.4px;text-transform:uppercase;">{Html(caption)}</span>
            <span style="display:block;color:#ffffff;font-size:25px;font-weight:bold;">{Html(storeName)}</span>
          </span>
        </a>
        """);
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);
}

public sealed class EmailOptions
{
    public bool Enabled { get; set; }
    public string FromName { get; set; } = "Equipe Externa";
    public string FromAddress { get; set; } = "";
    public string AdminAddress { get; set; } = "";
    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class MissingPointingAlertOptions
{
    public bool Enabled { get; set; }
    public string OperationalEmail { get; set; } = "";
    public int Hour { get; set; } = 7;
    public int Minute { get; set; }
    public string TimeZoneId { get; set; } = "America/Sao_Paulo";
    public int TargetDayOffset { get; set; }
}

public sealed class MissingPointingAlertHostedService : BackgroundService
{
    private readonly ExPontoDataSource _dataSource;
    private readonly IOptionsMonitor<EmailOptions> _emailOptions;
    private readonly IOptionsMonitor<MissingPointingAlertOptions> _alertOptions;
    private readonly ILogger<MissingPointingAlertHostedService> _logger;

    public MissingPointingAlertHostedService(
        ExPontoDataSource dataSource,
        IOptionsMonitor<EmailOptions> emailOptions,
        IOptionsMonitor<MissingPointingAlertOptions> alertOptions,
        ILogger<MissingPointingAlertHostedService> logger)
    {
        _dataSource = dataSource;
        _emailOptions = emailOptions;
        _alertOptions = alertOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _alertOptions.CurrentValue;
            var nextRunUtc = NextRunUtc(options);
            var delay = nextRunUtc - DateTimeOffset.UtcNow;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

            try
            {
                _logger.LogInformation("Proximo alerta de apontamento ausente agendado para {NextRunUtc:u}", nextRunUtc);
                await Task.Delay(delay, stoppingToken);
                await SendMissingPointingAlertAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao enviar alerta de apontamento ausente.");
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
        }
    }

    private async Task SendMissingPointingAlertAsync(CancellationToken cancellationToken)
    {
        var alertOptions = _alertOptions.CurrentValue;
        var emailOptions = _emailOptions.CurrentValue;

        if (!alertOptions.Enabled)
        {
            _logger.LogInformation("Alerta de apontamento ausente desativado.");
            return;
        }

        var to = FirstNonBlank(alertOptions.OperationalEmail, emailOptions.AdminAddress);
        if (string.IsNullOrWhiteSpace(to))
        {
            _logger.LogWarning("Alerta de apontamento ausente nao enviado. Configure MissingPointingAlert:OperationalEmail ou Email:AdminAddress.");
            return;
        }

        if (!emailOptions.Enabled ||
            string.IsNullOrWhiteSpace(emailOptions.FromAddress) ||
            string.IsNullOrWhiteSpace(emailOptions.Smtp.Host))
        {
            _logger.LogWarning("Alerta de apontamento ausente nao enviado. Configure Email:Enabled, Email:FromAddress e Email:Smtp:Host.");
            return;
        }

        var timeZone = ResolveTimeZone(alertOptions.TimeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone).Date);
        var targetDate = today.AddDays(alertOptions.TargetDayOffset);

        await using var db = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = (await db.QueryAsync<MissingPointingAlertRow>("""
            select
                responsavel_id as ResponsavelId,
                responsavel_nome as ResponsavelNome,
                responsavel_email as ResponsavelEmail,
                cliente_id as ClienteId,
                cliente_sigla as ClienteSigla,
                cliente_nome as ClienteNome,
                fase as Fase,
                data_cronograma as DataCronograma,
                data_cronograma_br as DataCronogramaBr,
                funcoes_previstas as FuncoesPrevistas,
                status_envio as StatusEnvio
            from vw_cronograma_dias_sem_apontamento
            where data_cronograma = @Data
            order by responsavel_nome, cliente_sigla, fase
            """, new { Data = targetDate.ToDateTime(TimeOnly.MinValue) })).ToList();

        if (rows.Count == 0)
        {
            _logger.LogInformation("Nenhum apontamento ausente encontrado para {Data}.", targetDate);
            return;
        }

        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var body = BuildMissingPointingAlertBody(rows, targetDate.ToString("dd/MM/yyyy", culture));
        using var message = new MailMessage
        {
            From = new MailAddress(emailOptions.FromAddress, string.IsNullOrWhiteSpace(emailOptions.FromName) ? "ExPonto" : emailOptions.FromName),
            Subject = $"Responsaveis sem apontamento - {targetDate.ToString("dd/MM/yyyy", culture)}",
            Body = body,
            IsBodyHtml = true
        };
        message.To.Add(to);

        using var client = new SmtpClient(emailOptions.Smtp.Host, emailOptions.Smtp.Port)
        {
            EnableSsl = emailOptions.Smtp.EnableSsl
        };

        if (!string.IsNullOrWhiteSpace(emailOptions.Smtp.UserName))
        {
            client.Credentials = new NetworkCredential(emailOptions.Smtp.UserName, emailOptions.Smtp.Password);
        }

        await client.SendMailAsync(message, cancellationToken);
        _logger.LogInformation("Alerta de apontamento ausente enviado para {Email}. Total={Total}", to, rows.Count);
    }

    private static string BuildMissingPointingAlertBody(IReadOnlyList<MissingPointingAlertRow> rows, string data)
    {
        var tableRows = new StringBuilder();
        foreach (var row in rows)
        {
            tableRows.Append($"""
            <tr>
              <td style="padding:8px;border-bottom:1px solid #e5e7eb;">{Html(row.ResponsavelNome)}</td>
              <td style="padding:8px;border-bottom:1px solid #e5e7eb;">{Html(row.ClienteSigla)} - {Html(row.ClienteNome)}</td>
              <td style="padding:8px;border-bottom:1px solid #e5e7eb;">{Html(row.Fase)}</td>
              <td style="padding:8px;border-bottom:1px solid #e5e7eb;">{Html(row.FuncoesPrevistas)}</td>
              <td style="padding:8px;border-bottom:1px solid #e5e7eb;">Nao enviou apontamento</td>
            </tr>
            """);
        }

        return $"""
        <html>
        <body style="font-family:Arial,Helvetica,sans-serif;background:#f6f7f9;margin:0;padding:24px;color:#1f2937;">
          <div style="max-width:900px;margin:0 auto;background:#ffffff;border-radius:8px;padding:24px;border:1px solid #e5e7eb;">
            <h2 style="margin:0 0 8px;color:#004d43;">Apontamentos nao enviados</h2>
            <p style="margin:0 0 18px;">Foram encontrados responsaveis com cronograma para <strong>{Html(data)}</strong> sem apontamento enviado.</p>
            <table style="border-collapse:collapse;width:100%;font-size:14px;">
              <thead>
                <tr style="background:#eef5df;">
                  <th style="text-align:left;padding:8px;">Responsavel</th>
                  <th style="text-align:left;padding:8px;">Cliente</th>
                  <th style="text-align:left;padding:8px;">Fase</th>
                  <th style="text-align:left;padding:8px;">Funcoes previstas</th>
                  <th style="text-align:left;padding:8px;">Status</th>
                </tr>
              </thead>
              <tbody>
                {tableRows}
              </tbody>
            </table>
          </div>
        </body>
        </html>
        """;
    }

    private static DateTimeOffset NextRunUtc(MissingPointingAlertOptions options)
    {
        var timeZone = ResolveTimeZone(options.TimeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        var hour = Math.Clamp(options.Hour, 0, 23);
        var minute = Math.Clamp(options.Minute, 0, 59);
        var nextLocal = new DateTimeOffset(
            nowLocal.Year,
            nowLocal.Month,
            nowLocal.Day,
            hour,
            minute,
            0,
            nowLocal.Offset);

        if (nextLocal <= nowLocal)
        {
            nextLocal = nextLocal.AddDays(1);
        }

        return nextLocal.ToUniversalTime();
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        foreach (var id in new[] { timeZoneId, "America/Sao_Paulo", "E. South America Standard Time", "UTC" })
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
                // Try the next known identifier.
            }
        }

        return TimeZoneInfo.Utc;
    }

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? "");
}

public sealed class MissingPointingAlertRow
{
    public string ResponsavelId { get; set; } = "";
    public string ResponsavelNome { get; set; } = "";
    public string ResponsavelEmail { get; set; } = "";
    public string ClienteId { get; set; } = "";
    public string ClienteSigla { get; set; } = "";
    public string ClienteNome { get; set; } = "";
    public string Fase { get; set; } = "";
    public DateTime DataCronograma { get; set; }
    public string DataCronogramaBr { get; set; } = "";
    public string FuncoesPrevistas { get; set; } = "";
    public string StatusEnvio { get; set; } = "";
}

public sealed class LocationAdjustmentOptions
{
    public string AdminEmail { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "http://localhost:5000";
}

public sealed class MaintenanceApprovalOptions
{
    public string AdminEmail { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "http://localhost:5000";
}

public sealed record MaintenanceAuthResult(bool Success, string? Message = null);

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public bool EnableSsl { get; set; } = true;
}

public sealed class AppLinksOptions
{
    public string? UrlAndroid { get; set; }
    public string? UrlIos { get; set; }
}

public record GeoLocation(
    [property: JsonPropertyName("latitude")] double Latitude,
    [property: JsonPropertyName("longitude")] double Longitude);

public record UserCreate(
    [property: JsonPropertyName("nome")] string Nome,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("funcoes")] IReadOnlyList<FuncaoCreate>? Funcoes = null,
    [property: JsonPropertyName("cliente_ids")] IReadOnlyList<string>? ClienteIds = null,
    [property: JsonPropertyName("url_android")] string? UrlAndroid = null,
    [property: JsonPropertyName("url_ios")] string? UrlIos = null);

public record LoginRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("device_id")] string DeviceId);

public record DeviceLoginResult(
    bool PreviousDeviceBlocked,
    bool CurrentDeviceWasBlocked);

public record ChangePasswordRequest(
    [property: JsonPropertyName("current_password")] string CurrentPassword,
    [property: JsonPropertyName("new_password")] string NewPassword);

public record ForgotPasswordRequest(
    [property: JsonPropertyName("email")] string Email);

public record ResetPasswordRequest(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("new_password")] string NewPassword);

public record UserAuthorizationUpdate(
    [property: JsonPropertyName("funcoes")] IReadOnlyList<FuncaoCreate>? Funcoes = null,
    [property: JsonPropertyName("cliente_ids")] IReadOnlyList<string>? ClienteIds = null);

public record LinkedUserCreate(
    [property: JsonPropertyName("integrante_id")] string IntegranteId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("nome")] string? Nome = null,
    [property: JsonPropertyName("url_android")] string? UrlAndroid = null,
    [property: JsonPropertyName("url_ios")] string? UrlIos = null);

public record UserResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("owner_id")] string OwnerId,
    [property: JsonPropertyName("master_user_id")] string? MasterUserId,
    [property: JsonPropertyName("integrante_vinculado_id")] string? IntegranteVinculadoId,
    [property: JsonPropertyName("nome")] string Nome,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("must_change_password")] bool MustChangePassword,
    [property: JsonPropertyName("ativo")] bool Ativo,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime? UpdatedAt,
    [property: JsonPropertyName("funcoes")] IReadOnlyList<FuncaoResponse>? Funcoes = null,
    [property: JsonPropertyName("clientes")] IReadOnlyList<ClienteResponse>? Clientes = null);

public record FuncaoCreate(
    [property: JsonPropertyName("funcao")] string Funcao,
    [property: JsonPropertyName("valor")] double Valor);

public record FuncaoResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("funcao")] string Funcao,
    [property: JsonPropertyName("valor")] double Valor,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime? UpdatedAt);

public record IntegranteCreate(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("cpf")] string Cpf,
    [property: JsonPropertyName("nome")] string Nome,
    [property: JsonPropertyName("funcao_id")] string FuncaoId,
    [property: JsonPropertyName("biometria_facial")] string? BiometriaFacial,
    [property: JsonPropertyName("face_encoding")] IReadOnlyList<double>? FaceEncoding = null);

public record IntegranteUpdate(
    [property: JsonPropertyName("nome")] string? Nome,
    [property: JsonPropertyName("funcao_id")] string? FuncaoId,
    [property: JsonPropertyName("biometria_facial")] string? BiometriaFacial,
    [property: JsonPropertyName("ativo")] bool? Ativo,
    [property: JsonPropertyName("face_encoding")] IReadOnlyList<double>? FaceEncoding = null,
    [property: JsonPropertyName("remover_biometria")] bool RemoverBiometria = false);

public record EmprestimoCreate(
    [property: JsonPropertyName("equipe_destino_email")] string EquipeDestinoEmail,
    [property: JsonPropertyName("data_inicio")] DateOnly DataInicio,
    [property: JsonPropertyName("data_fim")] DateOnly DataFim);

public record EmprestimoRenovacaoCreate(
    [property: JsonPropertyName("nova_data_fim")] DateOnly NovaDataFim);

public record IntegranteResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("id_usuario")] string IdUsuario,
    [property: JsonPropertyName("cpf")] string Cpf,
    [property: JsonPropertyName("nome")] string Nome,
    [property: JsonPropertyName("funcao_id")] string FuncaoId,
    [property: JsonPropertyName("funcao_nome")] string? FuncaoNome,
    [property: JsonPropertyName("ativo")] bool Ativo,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime? UpdatedAt,
    [property: JsonPropertyName("face_encoding")] IReadOnlyList<double>? FaceEncoding = null,
    [property: JsonPropertyName("emprestado")] bool Emprestado = false,
    [property: JsonPropertyName("emprestimo_id")] string? EmprestimoId = null,
    [property: JsonPropertyName("emprestimo_data_inicio")] DateOnly? EmprestimoDataInicio = null,
    [property: JsonPropertyName("emprestimo_data_fim")] DateOnly? EmprestimoDataFim = null,
    [property: JsonPropertyName("equipe_origem_nome")] string? EquipeOrigemNome = null);

public record ClienteCreate(
    [property: JsonPropertyName("id_aprovado")] string IdAprovado,
    [property: JsonPropertyName("nome")] string Nome,
    [property: JsonPropertyName("sigla")] string Sigla,
    [property: JsonPropertyName("geolocalizacao")] GeoLocation? Geolocalizacao,
    [property: JsonPropertyName("raio")] double Raio,
    [property: JsonPropertyName("latitude")] double? Latitude = null,
    [property: JsonPropertyName("longitude")] double? Longitude = null)
{
    public GeoLocation? GetGeolocalizacao()
    {
        if (Geolocalizacao is not null)
        {
            return Geolocalizacao;
        }

        return Latitude.HasValue && Longitude.HasValue
            ? new GeoLocation(Latitude.Value, Longitude.Value)
            : null;
    }
}

public record ClienteResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("id_aprovado")] string IdAprovado,
    [property: JsonPropertyName("nome")] string Nome,
    [property: JsonPropertyName("sigla")] string Sigla,
    [property: JsonPropertyName("geolocalizacao")] GeoLocation Geolocalizacao,
    [property: JsonPropertyName("raio")] double Raio,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime? UpdatedAt,
    [property: JsonPropertyName("distancia")] double? Distancia,
    [property: JsonPropertyName("dentro_do_raio")] bool? DentroDoRaio);

public record IdentificacaoRequest([property: JsonPropertyName("biometria_facial")] string BiometriaFacial);

public record ApontamentoCreate(
    [property: JsonPropertyName("cliente_id")] string ClienteId,
    [property: JsonPropertyName("integrante_id")] string IntegranteId,
    [property: JsonPropertyName("tipo")] string Tipo,
    [property: JsonPropertyName("geolocalizacao")] GeoLocation Geolocalizacao,
    [property: JsonPropertyName("biometria_facial")] string? BiometriaFacial,
    [property: JsonPropertyName("localizacao_status")] string? LocalizacaoStatus,
    [property: JsonPropertyName("solicitacao_ajuste_id")] string? SolicitacaoAjusteId,
    [property: JsonPropertyName("cronograma_justificativa")] string? CronogramaJustificativa);

public record ApontamentoResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("id_usuario")] string IdUsuario,
    [property: JsonPropertyName("cliente_id")] string ClienteId,
    [property: JsonPropertyName("cliente_nome")] string? ClienteNome,
    [property: JsonPropertyName("integrante_id")] string IntegranteId,
    [property: JsonPropertyName("integrante_nome")] string? IntegranteNome,
    [property: JsonPropertyName("tipo")] string Tipo,
    [property: JsonPropertyName("geolocalizacao")] GeoLocation Geolocalizacao,
    [property: JsonPropertyName("timestamp")] DateTime Timestamp,
    [property: JsonPropertyName("dentro_do_raio")] bool DentroDoRaio,
    [property: JsonPropertyName("biometria_verificada")] bool BiometriaVerificada,
    [property: JsonPropertyName("localizacao_status")] string LocalizacaoStatus,
    [property: JsonPropertyName("solicitacao_ajuste_id")] string? SolicitacaoAjusteId,
    [property: JsonPropertyName("cronograma_fase_id")] string? CronogramaFaseId,
    [property: JsonPropertyName("cronograma_fase_sigla")] string? CronogramaFaseSigla,
    [property: JsonPropertyName("dia_extra")] bool DiaExtra,
    [property: JsonPropertyName("funcao_extra")] bool FuncaoExtra,
    [property: JsonPropertyName("cronograma_status")] string CronogramaStatus,
    [property: JsonPropertyName("cronograma_excecao_id")] string? CronogramaExcecaoId,
    [property: JsonPropertyName("cronograma_justificativa")] string? CronogramaJustificativa,
    [property: JsonPropertyName("manutencao_status")] string ManutencaoStatus,
    [property: JsonPropertyName("manutencao_excecao_id")] string? ManutencaoExcecaoId,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt);

public record SyncRequest([property: JsonPropertyName("apontamentos")] IReadOnlyList<SyncApontamentoItem> Apontamentos);

public record SyncApontamentoItem(
    [property: JsonPropertyName("offline_id")] string OfflineId,
    [property: JsonPropertyName("id_cliente")] string IdCliente,
    [property: JsonPropertyName("id_integrante")] string IdIntegrante,
    [property: JsonPropertyName("tipo")] string Tipo,
    [property: JsonPropertyName("geolocalizacao")] GeoLocation Geolocalizacao,
    [property: JsonPropertyName("biometria_facial")] string? BiometriaFacial,
    [property: JsonPropertyName("timestamp")] string? Timestamp,
    [property: JsonPropertyName("liveness_verified")] bool LivenessVerified,
    [property: JsonPropertyName("offline_face_verified")] bool OfflineFaceVerified,
    [property: JsonPropertyName("offline_device_biometric_verified")] bool OfflineDeviceBiometricVerified,
    [property: JsonPropertyName("localizacao_status")] string? LocalizacaoStatus,
    [property: JsonPropertyName("solicitacao_ajuste_id")] string? SolicitacaoAjusteId,
    [property: JsonPropertyName("cronograma_justificativa")] string? CronogramaJustificativa);

public record SyncItemResult(
    [property: JsonPropertyName("offline_id")] string OfflineId,
    [property: JsonPropertyName("server_id")] string? ServerId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("biometria_verificada")] bool? BiometriaVerificada,
    [property: JsonPropertyName("dentro_do_raio")] bool? DentroDoRaio,
    [property: JsonPropertyName("cronograma_fase_sigla")] string? CronogramaFaseSigla,
    [property: JsonPropertyName("dia_extra")] bool? DiaExtra,
    [property: JsonPropertyName("funcao_extra")] bool? FuncaoExtra,
    [property: JsonPropertyName("cronograma_status")] string? CronogramaStatus,
    [property: JsonPropertyName("cronograma_excecao_id")] string? CronogramaExcecaoId,
    [property: JsonPropertyName("manutencao_status")] string? ManutencaoStatus,
    [property: JsonPropertyName("manutencao_excecao_id")] string? ManutencaoExcecaoId);

public record CronogramaFaseCreate(
    [property: JsonPropertyName("cliente_id")] string ClienteId,
    [property: JsonPropertyName("fase")] string Fase,
    [property: JsonPropertyName("data_inicio")] DateOnly DataInicio,
    [property: JsonPropertyName("data_fim")] DateOnly DataFim,
    [property: JsonPropertyName("funcoes")] IReadOnlyList<CronogramaFaseFuncaoCreate>? Funcoes,
    [property: JsonPropertyName("id")] string? Id = null);

public record CronogramaFaseFuncaoCreate(
    [property: JsonPropertyName("funcao")] string Funcao,
    [property: JsonPropertyName("limite_integrantes")] int LimiteIntegrantes);

public record CronogramaImportRequest(
    [property: JsonPropertyName("clientes")] IReadOnlyList<ClienteCreate>? Clientes,
    [property: JsonPropertyName("cronograma_fases")] IReadOnlyList<CronogramaImportFase>? CronogramaFases);

public record CronogramaImportFase(
    [property: JsonPropertyName("cliente_id")] string? ClienteId,
    [property: JsonPropertyName("id_aprovado")] string? IdAprovado,
    [property: JsonPropertyName("fase")] string Fase,
    [property: JsonPropertyName("data_inicio")] DateOnly DataInicio,
    [property: JsonPropertyName("data_fim")] DateOnly DataFim,
    [property: JsonPropertyName("funcoes")] IReadOnlyList<CronogramaFaseFuncaoCreate>? Funcoes,
    [property: JsonPropertyName("id")] string? Id = null);

public record SolicitacaoAjusteCreate(
    [property: JsonPropertyName("cliente_id")] string ClienteId,
    [property: JsonPropertyName("nova_geolocalizacao")] GeoLocation NovaGeolocalizacao,
    [property: JsonPropertyName("motivo")] string? Motivo);

public record SolicitacaoAjusteBatidaCreate(
    [property: JsonPropertyName("cliente_id")] string ClienteId,
    [property: JsonPropertyName("integrante_id")] string IntegranteId,
    [property: JsonPropertyName("tipo")] string Tipo,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("motivo")] string? Motivo);
