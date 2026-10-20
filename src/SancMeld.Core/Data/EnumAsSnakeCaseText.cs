using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SancMeld.Core.Data;

/// <summary>
/// Stores an enum as lowercase snake_case text: <c>PossiblyMissing</c> becomes
/// <c>possibly_missing</c>.
///
/// PostgreSQL has real enum types and they read better in psql, but Npgsql needs
/// every one of them registered on the data source before the first connection is
/// opened. Miss one and nothing complains until a query touches that column at
/// runtime. This project is meant to be cloned and run against any PostgreSQL with
/// nothing but a connection string, so text wins over tidiness.
///
/// The stored text is part of the schema contract, not an implementation detail:
/// the designation_current view filters on <c>status = 'active'</c>. Renaming an
/// enum member is therefore a data migration, not a rename.
/// </summary>
public sealed class EnumAsSnakeCaseText<TEnum>()
    : ValueConverter<TEnum, string>(v => ToSnakeCase(v.ToString()!), v => FromSnakeCase(v))
    where TEnum : struct, Enum
{
    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(name[i]));
        }

        return sb.ToString();
    }

    // Dropping the separators reduces this to a case-insensitive member lookup,
    // which is exactly the inverse of the split above.
    private static TEnum FromSnakeCase(string value) =>
        Enum.Parse<TEnum>(value.Replace("_", string.Empty), ignoreCase: true);
}
