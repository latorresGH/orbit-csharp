using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrbIT.Domain.Enums;
using OrbIT.Infrastructure.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// ── PostgreSQL: DataSource con los 11 enums CLR mapeados ──────────────────
// La cadena se lee de appsettings (placeholder) + appsettings.Development.json
// (valor real, gitignoreado). El DataSource registra los enums con el mismo
// ExactNameTranslator que usa OnModelCreating en Infrastructure, para que el
// mapeo CLR <-> tipo enum de PostgreSQL sea idéntico en runtime.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "Falta la cadena de conexión 'DefaultConnection'. Definila en " +
        "appsettings.Development.json o en variables de entorno.");

var enumNameTranslator = new ExactNameTranslator();
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder
    .MapEnum<EstadoMesa>(nameTranslator: enumNameTranslator)
    .MapEnum<EstadoOferta>(nameTranslator: enumNameTranslator)
    .MapEnum<EstadoPago>(nameTranslator: enumNameTranslator)
    .MapEnum<EstadoPedido>(nameTranslator: enumNameTranslator)
    .MapEnum<MetodoPago>(nameTranslator: enumNameTranslator)
    .MapEnum<Role>(nameTranslator: enumNameTranslator)
    .MapEnum<TipoMovimientoCaja>(nameTranslator: enumNameTranslator)
    .MapEnum<TipoOferta>(nameTranslator: enumNameTranslator)
    .MapEnum<TipoPedido>(nameTranslator: enumNameTranslator)
    .MapEnum<TipoTurno>(nameTranslator: enumNameTranslator)
    .MapEnum<UnidadMedida>(nameTranslator: enumNameTranslator);
var dataSource = dataSourceBuilder.Build();

builder.Services.AddDbContext<OrbitDbContext>(options =>
    options.UseNpgsql(dataSource));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapControllers();

app.Run();
