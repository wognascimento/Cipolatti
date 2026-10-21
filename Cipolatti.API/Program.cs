using System.Text;
using Cipolatti.API.Infrastructure;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Cipolatti.API.Services;
using Cipolatti.API.ExPonto;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Configure ConnectionStrings__DefaultConnection por variável de ambiente ou Secret Manager.");

var signingKey = builder.Configuration["Jwt:SigningKey"];
if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
    throw new InvalidOperationException("Configure Jwt__SigningKey com pelo menos 32 bytes fora do repositório.");

DefaultTypeMap.MatchNamesWithUnderscores = true;
SqlMapper.AddTypeHandler(new DapperDateOnlyTypeHandler());
SqlMapper.AddTypeHandler(new DapperTimeOnlyTypeHandler());
var dataProtectionPath=Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath,"storage","keys"));
Directory.CreateDirectory(dataProtectionPath);
builder.Services.AddDataProtection().SetApplicationName("Cipolatti.API").PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
builder.Services.AddSingleton<IPrinterService, TscPrinterService>();
builder.Services.AddScoped<ICatalogoService, CatalogoService>();
builder.Services.Configure<CatalogoStorageOptions>(builder.Configuration.GetSection("CatalogoStorage"));
builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();
builder.Services.AddScoped<ICatalogoAdminService, CatalogoAdminService>();
builder.Services.AddSingleton<IPdfJobQueue, PdfJobQueue>();
builder.Services.AddScoped<IPdfRenderer, PdfiumRenderer>();
builder.Services.AddScoped<ICatalogoPdfAdminService, CatalogoPdfAdminService>();
builder.Services.AddScoped<ICatalogoVideoAdminService, CatalogoVideoAdminService>();
builder.Services.Configure<YoutubeOptions>(builder.Configuration.GetSection("Youtube"));
builder.Services.Configure<AdOptions>(builder.Configuration.GetSection("Ad"));
builder.Services.AddScoped<IAdAuthenticationService, AdAuthenticationService>();
builder.Services.Configure<RomaneioFotosOptions>(builder.Configuration.GetSection("RomaneioFotos"));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("ExPonto:Email"));
builder.Services.AddScoped<IRomaneioFotosService, RomaneioFotosService>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("CatalogoLogin", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("RomaneioDeviceActivation", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
});
builder.Services.AddHttpClient("youtube");
builder.Services.AddScoped<IYoutubeService,YoutubeService>();
builder.Services.AddHostedService<PdfProcessingWorker>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgresql");
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey))
        };
    })
    .AddScheme<AuthenticationSchemeOptions, RomaneioDeviceAuthenticationHandler>(
        RomaneioDeviceDefaults.AuthenticationScheme, _ => { });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("CatalogoAdmin", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
        context.User.IsInRole("CatalogoAdmin") ||
        context.User.HasClaim("catalogo_admin", "true") ||
        context.User.FindAll("scope").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains("catalogo.admin", StringComparer.Ordinal)))
    .AddPolicy("RomaneioFotos", policy => policy
        .AddAuthenticationSchemes(RomaneioDeviceDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireClaim("scope", RomaneioDeviceDefaults.Scope))
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
var exPontoEnabled = !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("ExPonto"));
if (exPontoEnabled)
    builder.Services.AddExPonto(builder.Configuration, builder.Environment);

var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();
if (exPontoEnabled)
{
    app.MapExPonto();
    await app.Services.InitializeExPontoAsync(app.Configuration);
}
app.Run();

public partial class Program;
