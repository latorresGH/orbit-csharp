using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using OrbIT.Domain.Enums;

namespace OrbIT.Infrastructure.Models;

/// <summary>
/// Mapeo de los 11 enums CLR a sus tipos enum de PostgreSQL en la capa de EF Core.
///
/// El <see cref="NpgsqlDataSourceBuilder"/> ya mapea estos enums a nivel ADO (lectura/
/// escritura de valores). Pero EF Core necesita además conocerlos a nivel del modelo para
/// generar el DDL correcto: sin esto, <c>EnsureCreated()</c> crea las columnas como
/// <c>integer</c> y choca con los defaults <c>'...'::"Enum"</c> del modelo. Por eso se
/// declaran también en <see cref="NpgsqlDbContextOptionsBuilder"/>.
///
/// Se usa el mismo <see cref="ExactNameTranslator"/> y los nombres PascalCase exactos del
/// tipo para que coincidan 1:1 con lo que declara <c>OnModelCreating</c> vía
/// <c>HasPostgresEnum</c>.
/// </summary>
public static class OrbitEnumMappings
{
    public static NpgsqlDbContextOptionsBuilder MapOrbitEnums(this NpgsqlDbContextOptionsBuilder options)
    {
        var translator = new ExactNameTranslator();
        options.MapEnum<EstadoMesa>("EstadoMesa", nameTranslator: translator);
        options.MapEnum<EstadoOferta>("EstadoOferta", nameTranslator: translator);
        options.MapEnum<EstadoPago>("EstadoPago", nameTranslator: translator);
        options.MapEnum<EstadoPedido>("EstadoPedido", nameTranslator: translator);
        options.MapEnum<MetodoPago>("MetodoPago", nameTranslator: translator);
        options.MapEnum<Role>("Role", nameTranslator: translator);
        options.MapEnum<TipoMovimientoCaja>("TipoMovimientoCaja", nameTranslator: translator);
        options.MapEnum<TipoOferta>("TipoOferta", nameTranslator: translator);
        options.MapEnum<TipoPedido>("TipoPedido", nameTranslator: translator);
        options.MapEnum<TipoTurno>("TipoTurno", nameTranslator: translator);
        options.MapEnum<UnidadMedida>("UnidadMedida", nameTranslator: translator);
        return options;
    }
}
