using Microsoft.EntityFrameworkCore;
using SancMeld.Core.Access;
using SancMeld.Core.Data;

namespace SancMeld.Core.Relations;

/// <summary>
/// An analyst's verdict on a link: confirmed, rejected, or taken back to candidate.
///
/// The link's own columns hold the latest decision so every reader sees it at once; the
/// decision itself is also appended to relation_decision, because the latest state alone
/// cannot answer "was this ever rejected, and by whom". A rejected link is kept, not
/// deleted — the next register run would only propose it again, and the rejection is
/// what stops it being shown in checks (party-to-party ties stay in the graph, greyed).
///
/// A link of the additional information is decided only by someone who may see it; to
/// anyone else it does not exist, and the answer says so in the same words.
/// </summary>
public sealed class RelationDecisionService(SancMeldDbContext db, TimeProvider clock, IAdditionalInfoAccess access)
{
    public async Task<RelationDecisionResult> DecideAsync(RelationDecisionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DecidedBy))
        {
            throw new InvalidOperationException("Say who decided. An unattributed decision is not an audit trail.");
        }

        var now = clock.GetUtcNow();
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var by = request.DecidedBy.Trim();
        var cleared = request.Decision == LinkStatus.Candidate;
        var seesAll = await access.AllowedAsync(ct).ConfigureAwait(false);
        LinkStatus previous;

        switch (request.Target)
        {
            case DecisionTarget.ListingRelation:
                {
                    var r = await db.DesignationRelations
                        .Where(x => x.Id == request.Id)
                        .Where(x => seesAll || !(RelationSystems.AdditionalInfo.Contains(x.Identity!.System) || RelationSystems.AdditionalInfo.Contains(x.Party!.System)))
                        .FirstOrDefaultAsync(ct).ConfigureAwait(false)
                        ?? throw new KeyNotFoundException($"No listing relation {request.Id}.");
                    previous = r.Status;
                    r.Status = request.Decision;
                    r.DecidedBy = cleared ? null : by;
                    r.DecidedAt = cleared ? null : now;
                    r.DecisionNote = note;
                    break;
                }

            case DecisionTarget.PartyRelation:
                {
                    var r = await db.PartyRelations
                        .Where(x => x.Id == request.Id)
                        .Where(x => seesAll || !(RelationSystems.AdditionalInfo.Contains(x.System)
                            || RelationSystems.AdditionalInfo.Contains(x.From!.System) || RelationSystems.AdditionalInfo.Contains(x.To!.System)))
                        .FirstOrDefaultAsync(ct).ConfigureAwait(false)
                        ?? throw new KeyNotFoundException($"No party relation {request.Id}.");
                    previous = r.Status;
                    r.Status = request.Decision;
                    r.DecidedBy = cleared ? null : by;
                    r.DecidedAt = cleared ? null : now;
                    r.DecisionNote = note;
                    break;
                }

            case DecisionTarget.Identity:
                {
                    var i = await db.ExternalIdentities
                        .Where(x => x.Id == request.Id)
                        .Where(x => seesAll || !RelationSystems.AdditionalInfo.Contains(x.System))
                        .FirstOrDefaultAsync(ct).ConfigureAwait(false)
                        ?? throw new KeyNotFoundException($"No identity {request.Id}.");
                    previous = i.Status;
                    i.Status = request.Decision;
                    i.DecidedBy = cleared ? null : by;
                    i.DecidedAt = cleared ? null : now;
                    i.DecisionNote = note;
                    break;
                }

            default:
                // Claim belongs to HybridMeld, which has its own decision endpoint.
                throw new InvalidOperationException($"Target {request.Target} is not decided here.");
        }

        db.RelationDecisions.Add(new RelationDecision
        {
            Target = request.Target,
            TargetId = request.Id,
            PreviousStatus = previous,
            Decision = request.Decision,
            Note = note,
            DecidedBy = by,
            DecidedAt = now,
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new RelationDecisionResult(request.Target, request.Id, request.Decision, by, now);
    }
}
