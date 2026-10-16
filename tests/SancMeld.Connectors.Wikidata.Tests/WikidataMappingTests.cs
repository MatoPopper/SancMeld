using System.Text;
using SancMeld.Core.Relations;
using Xunit;

namespace SancMeld.Connectors.Wikidata.Tests;

public sealed class WikidataMappingTests
{
    [Theory]
    [InlineData("P1830", true, RelationKind.OwnerOf)]
    [InlineData("P127", false, RelationKind.OwnerOf)]
    [InlineData("P127", true, RelationKind.OwnedBy)]
    [InlineData("P112", false, RelationKind.FounderOf)]
    [InlineData("P169", false, RelationKind.ChiefExecutiveOf)]
    [InlineData("P488", false, RelationKind.ChairpersonOf)]
    [InlineData("P3320", false, RelationKind.BoardMemberOf)]
    [InlineData("P1037", false, RelationKind.DirectorOf)]
    [InlineData("P749", true, RelationKind.SubsidiaryOf)]
    [InlineData("P355", true, RelationKind.ParentOrganisationOf)]
    public void APropertyAndADirectionNameTheRelationFromThePersonsSide(string property, bool outgoing, RelationKind expected)
    {
        Assert.Equal(expected, WikidataRelationMapper.Map(property, outgoing));
    }

    [Theory]
    [InlineData("P112", true)]    // a person is not "founded by" anyone
    [InlineData("P169", true)]
    [InlineData("P9999", false)]
    public void AStatementThatMakesNoSenseOfAPersonIsDropped(string property, bool outgoing)
    {
        Assert.Null(WikidataRelationMapper.Map(property, outgoing));
    }

    [Fact]
    public void AHumanIsAPersonBeforeAnythingElse()
    {
        Assert.Equal(RelatedPartyKind.Person, WikidataRelationMapper.PartyKind(human: true, organisation: true, ship: false));
        Assert.Equal(RelatedPartyKind.Organisation, WikidataRelationMapper.PartyKind(false, true, false));
        Assert.Equal(RelatedPartyKind.Vessel, WikidataRelationMapper.PartyKind(false, false, true));
        Assert.Equal(RelatedPartyKind.Other, WikidataRelationMapper.PartyKind(false, false, false));
    }

    [Fact]
    public void TheBirthDateLookupInsistsOnDayPrecision()
    {
        var query = WikidataQueries.PersonsBornOn(new DateOnly(1950, 1, 1));

        Assert.Contains("\"1950-01-01T00:00:00Z\"^^xsd:dateTime", query, StringComparison.Ordinal);
        Assert.Contains("wikibase:timePrecision 11", query, StringComparison.Ordinal);
        Assert.Contains("skos:altLabel", query, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyItemIdsReachAValuesClause()
    {
        Assert.Throws<ArgumentException>(() => WikidataQueries.RelationsOf(["Q1", "Q2 } . ?x ?y ?z"]));
        Assert.Contains("VALUES ?item { wd:Q1 wd:Q2 }", WikidataQueries.RelationsOf(["Q1", "Q2"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SparqlJsonResultsBecomeRows()
    {
        const string json = """
            {
              "head": { "vars": ["item", "label", "start"] },
              "results": { "bindings": [
                {
                  "item": { "type": "uri", "value": "http://www.wikidata.org/entity/Q217115" },
                  "label": { "xml:lang": "ru", "type": "literal", "value": "Рамзан Кадыров" },
                  "start": { "datatype": "http://www.w3.org/2001/XMLSchema#dateTime", "type": "literal", "value": "2007-04-05T00:00:00Z" }
                }
              ] }
            }
            """;

        var rows = await WikidataSparqlClient.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("Q217115", row.EntityId("item"));
        Assert.Null(row.EntityId("label"));
        Assert.Equal("ru", row.Get("label")?.Lang);
        Assert.Equal(new DateOnly(2007, 4, 5), row.Get("start")?.Date);
        Assert.Null(row["missing"]);
    }
}
