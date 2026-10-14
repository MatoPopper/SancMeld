namespace SancMeld.Core.Domain;

/// <summary>
/// Stored in the database as snake_case text, not as native PostgreSQL enum types.
/// A member's name is therefore part of the schema: renaming one is a data
/// migration. See SancMeld.Core.Data.EnumAsSnakeCaseText.
/// </summary>
public enum SubjectType { Person, Organisation, Vessel, Aircraft, Unknown }

/// <summary>
/// The product's core distinction. EU and UN designations are legally binding
/// inside the EU; OFAC and UK OFSI are not and are surfaced as red flags only.
/// </summary>
public enum LegalEffect { Binding, Advisory }

/// <summary>Sources routinely publish partial dates. Never collapse these into a bare date.</summary>
public enum DatePrecision { Day, Month, Year, Range, Circa, Unknown }

public enum DesignationStatus { Active, PossiblyMissing, Delisted, Suspended }

/// <summary>
/// An entry ban is not an asset freeze. Several member states publish entry
/// bans only; those must never trigger a freeze obligation.
/// </summary>
public enum MeasureType
{
    AssetFreeze, FundsProhibition, EntryBan, TravelBan,
    TradeRestriction, ServicesRestriction, Other
}

public enum NameType { Primary, Alias, Former, NativeScript }

public enum IdentifierType
{
    Passport, NationalId, TaxId, CompanyRegistration,
    Imo, AircraftRegistration, Lei, Bic, CryptoAddress, Other
}

/// <summary>Uncertainty must stay representable; collapsing it into "no" destroys the signal.</summary>
public enum AdjudicationDecision { Match, NoMatch, Undecided, NeedsMoreData }

/// <summary>
/// Which retrieval put a candidate in front of the scorer. Recorded with every candidate
/// because the two generators have different blind spots — the trigram cannot cross a
/// script, the embedding is weaker on Korean — and a decision has to say which one it
/// rested on. <see cref="Identifier"/> is the deterministic layer: a document matched
/// and the name search never saw the subject at all.
/// </summary>
public enum CandidateOrigin { Trigram, Semantic, Both, Identifier }
