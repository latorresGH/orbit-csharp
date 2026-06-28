using Npgsql;

namespace OrbIT.Infrastructure.Models;

/// <summary>
/// Name translator de identidad: preserva exactamente los nombres CLR al mapear
/// enums de PostgreSQL. Los tipos enum en la DB usan PascalCase ("EstadoPedido")
/// y los labels UPPER_SNAKE ("EN_CAMINO"), que coinciden 1:1 con los miembros CLR,
/// por lo que no se debe aplicar la traducción snake_case por defecto de Npgsql.
/// </summary>
public sealed class ExactNameTranslator : INpgsqlNameTranslator
{
    public string TranslateTypeName(string clrName) => clrName;

    public string TranslateMemberName(string clrName) => clrName;
}
