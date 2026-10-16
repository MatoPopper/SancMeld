using SancMeld.Core.Relations;

namespace SancMeld.Connectors.Wikidata;

/// <summary>
/// From a Wikidata property and the direction it was read in, to what the listed person
/// is to the other party. The listed item is always a person here, so a statement that
/// only makes sense of an organisation — "founded by" pointing <em>from</em> the item —
/// maps to nothing and is dropped.
/// </summary>
public static class WikidataRelationMapper
{
    public static RelationKind? Map(string property, bool outgoing) => (property, outgoing) switch
    {
        ("P1830", true) => RelationKind.OwnerOf,            // item: owner of → target
        ("P127", false) => RelationKind.OwnerOf,            // target: owned by → item
        ("P127", true) => RelationKind.OwnedBy,
        ("P1830", false) => RelationKind.OwnedBy,
        ("P112", false) => RelationKind.FounderOf,          // target: founded by → item
        ("P169", false) => RelationKind.ChiefExecutiveOf,   // target: chief executive officer → item
        ("P488", false) => RelationKind.ChairpersonOf,
        ("P3320", false) => RelationKind.BoardMemberOf,
        ("P1037", false) => RelationKind.DirectorOf,        // target: director / manager → item
        ("P749", true) => RelationKind.SubsidiaryOf,        // item: parent organisation → target
        ("P749", false) => RelationKind.ParentOrganisationOf,
        ("P355", true) => RelationKind.ParentOrganisationOf,
        ("P355", false) => RelationKind.SubsidiaryOf,
        _ => null,
    };

    public static RelatedPartyKind PartyKind(bool human, bool organisation, bool ship) => (human, organisation, ship) switch
    {
        (true, _, _) => RelatedPartyKind.Person,
        (_, true, _) => RelatedPartyKind.Organisation,
        (_, _, true) => RelatedPartyKind.Vessel,
        _ => RelatedPartyKind.Other,
    };
}
