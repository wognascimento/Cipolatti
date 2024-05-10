using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Cipolatti.API.Repositories;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddDbContext<CipolattiContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

//builder.Services.AddHttpsRedirection(opt => opt.HttpsPort = 443);

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddScoped<IAprovadoRepository, AprovadoRepository>();
builder.Services.AddScoped<ILookupRepository, LookupRepository>();
builder.Services.AddScoped<IConfCargaGeral, ConfCargaGeralRepository>();
builder.Services.AddScoped<IMovimentacaoVolumeShoppingRepository, MovimentacaoVolumeShoppingRepository>();
builder.Services.AddScoped<IEnderecamentoGalpaoRepository, EnderecamentoGalpaoRepository>();
builder.Services.AddScoped<IVolumeControladoRepository, VolumeControladoRepository>();
builder.Services.AddScoped<IFuncionarioRepository, FuncionarioRepository>();
builder.Services.AddScoped<IAtendentesAlmoxRepository, AtendentesAlmoxRepository>();
builder.Services.AddScoped<IDescricoesRepository, DescricoesRepository>();
builder.Services.AddScoped<ISaidaAlmoxRepository, SaidaAlmoxRepository>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsProduction() || app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}



app.UseHttpsRedirection();

app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();

app.Run();
