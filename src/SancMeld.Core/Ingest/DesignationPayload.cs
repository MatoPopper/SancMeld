using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SancMeld.Core.Abstractions;

namespace SancMeld.Core.Ingest;

/// <summary>
/// Serialises a parsed record for staging, and hashes it so the diff can ask one question
/// per record instead of comparing every field of every child table.
/// </summary>
public static class DesignationPayload
{
    private static readonly JsonSerializerOptions Options = new()
    {
        // Enums as text for the same reason the database stores them that way: staging
        // rows are read by people debugging an import, and 3 tells them nothing.
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(ParsedDesignation designation) =>
        JsonSerializer.Serialize(designation, Options);

    public static ParsedDesignation Deserialize(string payload) =>
        JsonSerializer.Deserialize<ParsedDesignation>(payload, Options)
        ?? throw new InvalidOperationException("A staged payload deserialised to null.");

    /// <summary>
    /// Covers the mapped content and deliberately NOT the raw source fragment.
    ///
    /// The publisher reorders attributes and reformats whitespace between releases. If
    /// that counted as a change, every subject would get a new revision on a day when
    /// nothing about anyone was decided — and the version history is meant to answer
    /// "what did this listing say", not "when was the file regenerated".
    ///
    /// The corollary is that changing the mapping changes every hash, so a release that
    /// adds a field opens one new revision per record. That is the honest outcome: what
    /// is stored really did change.
    /// </summary>
    public static string ContentHash(ParsedDesignation designation)
    {
        ArgumentNullException.ThrowIfNull(designation);

        var comparable = designation with { RawRecordJson = string.Empty };
        var json = JsonSerializer.SerializeToUtf8Bytes(comparable, Options);

        return Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant();
    }

    internal static string Describe(ParsedDesignation designation) =>
        designation.Names.Count > 0
            ? designation.Names[0].Value
            : $"[{designation.SourceRecordKey}]";

    internal static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
