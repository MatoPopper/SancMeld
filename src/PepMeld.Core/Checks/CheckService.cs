using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;

namespace PepMeld.Core.Checks;

/// <summary>
/// Starts checks and reads them back, always scoped to the caller: a check belongs to the
/// address that started it and nobody else sees it, not even to learn that it exists.
/// </summary>
public sealed class CheckService(PepMeldDbContext db, CheckQueue queue, TimeProvider clock)
{
    public const int MaxHistory = 100;

    private static readonly CheckStage[] AllStages = [CheckStage.Lists, CheckStage.Boards, CheckStage.Family];

    public async Task<CreateCheckResult> CreateAsync(string email, NewCheck request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 500)
        {
            return new CreateCheckResult(CreateOutcome.Invalid, null, "Supply the person's name in 'name' (at most 500 characters).");
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (request.DateOfBirth is not { } birthDate)
        {
            return new CreateCheckResult(CreateOutcome.Invalid, null, "The date of birth is mandatory: 'dateOfBirth' as YYYY-MM-DD. Without it a PEP check is a guess among namesakes.");
        }

        if (birthDate > today || birthDate.Year < 1900)
        {
            return new CreateCheckResult(CreateOutcome.Invalid, null, "'dateOfBirth' must be a real date between 1900 and today.");
        }

        var scope = (request.Scope is { Count: > 0 } s ? AllStages.Where(s.Contains) : AllStages).ToArray();

        var check = new PepCheck
        {
            Id = Guid.CreateVersion7(),
            UserEmail = email.Trim().ToLowerInvariant(),
            Name = name,
            BirthDate = birthDate,
            Country = request.Country,
            Nationality = Trim(request.Nationality),
            PlaceOfBirth = Trim(request.PlaceOfBirth),
            Reference = Trim(request.Reference),
            Scope = scope.Select(x => x.ToString()).ToArray(),
            Status = CheckStatus.Running,
            StartedAt = clock.GetUtcNow(),
            Stages = scope.Select(stage => new PepCheckStage { Stage = stage, Status = StageStatus.Pending }).ToList(),
        };

        db.Checks.Add(check);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The partial unique index said no: this address already has a check running.
            // Returned rather than thrown, with the running check, so the caller can poll it.
            db.ChangeTracker.Clear();
            var running = await CurrentAsync(check.UserEmail, ct).ConfigureAwait(false);
            return new CreateCheckResult(CreateOutcome.AlreadyRunning, running, "A check is already running for this user. Wait for it to finish; GET /checks/current shows it.");
        }

        queue.Enqueue(check.Id);
        return new CreateCheckResult(CreateOutcome.Created, ToView(check), null);
    }

    public async Task<CheckView?> GetAsync(string email, Guid id, CancellationToken ct)
    {
        var key = email.Trim().ToLowerInvariant();
        var check = await Query().FirstOrDefaultAsync(c => c.Id == id && c.UserEmail == key, ct).ConfigureAwait(false);
        return check is null ? null : ToView(check);
    }

    public async Task<CheckView?> CurrentAsync(string email, CancellationToken ct)
    {
        var key = email.Trim().ToLowerInvariant();
        var check = await Query().FirstOrDefaultAsync(c => c.UserEmail == key && c.Status == CheckStatus.Running, ct).ConfigureAwait(false);
        return check is null ? null : ToView(check);
    }

    public async Task<IReadOnlyList<CheckSummaryView>> ListAsync(string email, int limit, CancellationToken ct)
    {
        var key = email.Trim().ToLowerInvariant();
        return await db.Checks.AsNoTracking()
            .Where(c => c.UserEmail == key)
            .OrderByDescending(c => c.StartedAt)
            .Take(Math.Clamp(limit, 1, MaxHistory))
            .Select(c => new CheckSummaryView(c.Id, c.Status, c.Name, c.BirthDate, c.Country, c.Reference, c.StartedAt, c.FinishedAt, c.Outcome, c.Summary))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fails every check still running. Called when the worker starts: whatever was running
    /// belonged to a process that is gone, and the user would otherwise wait on it forever
    /// and be refused a new one.
    /// </summary>
    public async Task<int> InterruptRunningAsync(string reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var ids = await db.Checks.Where(c => c.Status == CheckStatus.Running).Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return 0;
        }

        await db.CheckStages
            .Where(s => ids.Contains(s.CheckId) && (s.Status == StageStatus.Pending || s.Status == StageStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, StageStatus.Failed)
                .SetProperty(x => x.Error, reason)
                .SetProperty(x => x.FinishedAt, now), ct)
            .ConfigureAwait(false);

        return await db.Checks
            .Where(c => ids.Contains(c.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, CheckStatus.Failed)
                .SetProperty(x => x.Error, reason)
                .SetProperty(x => x.FinishedAt, now), ct)
            .ConfigureAwait(false);
    }

    private IQueryable<PepCheck> Query() => db.Checks.AsNoTracking().Include(c => c.Stages).Include(c => c.Findings);

    public static CheckView ToView(PepCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);

        var scope = check.Scope.Select(s => Enum.Parse<CheckStage>(s)).ToList();
        var findingsByStage = check.Findings
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id)
            .GroupBy(f => f.Stage)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<FindingView>)g.Select(f => JsonSerializer.Deserialize<FindingView>(f.PayloadJson, PepMeldJson.Options)!).ToList());

        var stages = check.Stages
            .OrderBy(s => s.Stage)
            .Select(s => new StageView(s.Stage, s.Status, s.StartedAt, s.FinishedAt, s.Note, s.Error,
                findingsByStage.TryGetValue(s.Stage, out var list) ? list : []))
            .ToList();

        var coverage = check.CoverageJson is null
            ? []
            : JsonSerializer.Deserialize<List<CoverageView>>(check.CoverageJson, PepMeldJson.Options) ?? [];

        return new CheckView(
            check.Id,
            check.Status,
            new CheckQueryView(check.Name, check.BirthDate, check.Country, check.Nationality, check.PlaceOfBirth, check.Reference, scope),
            check.StartedAt,
            check.FinishedAt,
            stages,
            check.Outcome,
            check.Summary,
            check.Error,
            coverage);
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
