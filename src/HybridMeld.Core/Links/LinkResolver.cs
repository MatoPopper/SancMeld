using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HybridMeld.Core.Links;

public sealed record SanctionTarget(string SourceCode, string Name, bool IsCurrent);

public sealed record PepTarget(string SourceCode, string Name);

/// <summary>
/// Looks a linked record up in the sanctions and PEP databases, read-only, by plain SQL
/// over a handful of columns: a link is an id, and this is what makes it a name on the
/// screen. Both connections are optional; a development host without them stores the
/// ids unverified and says so.
///
///   ConnectionStrings:SancMeldAccounts   SancMeld's database (the same one the grant is read from)
///   ConnectionStrings:PepMeld            PepMeld's database
/// </summary>
public sealed class LinkResolver(IConfiguration configuration, ILogger<LinkResolver> logger)
{
    public const string SanctionsConnectionName = "SancMeldAccounts";
    public const string PepConnectionName = "PepMeld";

    private readonly string? sanctions = configuration.GetConnectionString(SanctionsConnectionName);
    private readonly string? pep = configuration.GetConnectionString(PepConnectionName);

    public bool SanctionsConfigured => !string.IsNullOrWhiteSpace(sanctions);
    public bool PepConfigured => !string.IsNullOrWhiteSpace(pep);

    public async Task<SanctionTarget?> FindDesignationAsync(Guid id, CancellationToken ct)
    {
        if (!SanctionsConfigured)
        {
            return null;
        }

        try
        {
            await using var connection = new NpgsqlConnection(sanctions);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                """
                SELECT s.code, n.name_original, d.status = 'active'
                FROM designation d
                JOIN sanctions_source s ON s.id = d.source_id
                LEFT JOIN designation_name n ON n.designation_id = d.id AND n.kind = 'primary'
                WHERE d.id = @id
                ORDER BY n.id
                LIMIT 1
                """, connection);
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return new SanctionTarget(reader.GetString(0), reader.IsDBNull(1) ? "?" : reader.GetString(1), reader.GetBoolean(2));
        }
        catch (NpgsqlException ex)
        {
            logger.LogError(ex, "Could not read designation {Id} from the sanctions database.", id);
            throw;
        }
    }

    /// <summary>
    /// A PEP person by exact name in the normal form, for case files, which travel between
    /// instances where the ids differ. One match or nothing: a namesake is a lead for a
    /// person, not a link a file may set.
    /// </summary>
    public async Task<(Guid Id, PepTarget Target)?> FindPepPersonByNameAsync(string nameNormalized, CancellationToken ct)
    {
        if (!PepConfigured || string.IsNullOrWhiteSpace(nameNormalized))
        {
            return null;
        }

        await using var connection = new NpgsqlConnection(pep);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT p.id, s.code, p.display_name
            FROM pep_person p
            JOIN pep_source s ON s.id = p.source_id
            WHERE p.name_normalized = @name
            ORDER BY s.code
            LIMIT 2
            """, connection);
        command.Parameters.AddWithValue("name", nameNormalized);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var first = (reader.GetGuid(0), new PepTarget(reader.GetString(1), reader.GetString(2)));
        // The same person in two lists is two rows (PepMeld merges nothing); any first row
        // names the person, so a second row of the same name is fine.
        return first;
    }

    public async Task<PepTarget?> FindPepPersonAsync(Guid id, CancellationToken ct)
    {
        if (!PepConfigured)
        {
            return null;
        }

        try
        {
            await using var connection = new NpgsqlConnection(pep);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                """
                SELECT s.code, p.display_name
                FROM pep_person p
                JOIN pep_source s ON s.id = p.source_id
                WHERE p.id = @id
                """, connection);
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? new PepTarget(reader.GetString(0), reader.GetString(1)) : null;
        }
        catch (NpgsqlException ex)
        {
            logger.LogError(ex, "Could not read PEP person {Id} from the PEP database.", id);
            throw;
        }
    }
}
