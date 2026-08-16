using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Groundwork.Documents.Serialization;
using Xunit;

namespace Groundwork.Documents.Tests;

/// <summary>Contract coverage for version validation, canonical serialization, and upcasting.</summary>
public sealed class VersionedJsonDocumentCodecTests
{
    private static readonly DocumentSchemaVersionFormat VersionFormat = new(
        (_, stamp) =>
        {
            if (!stamp.StartsWith('v') ||
                !int.TryParse(stamp.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
                return null;

            return version;
        },
        (_, version) => $"v{version}");

    [Fact]
    public void Registry_rejects_a_policy_whose_minimum_exceeds_current()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DocumentSchemaVersionPolicy("ticket", minimumReadableVersion: 3, currentVersion: 2));

        Assert.Equal("minimumReadableVersion", exception.ParamName);
    }

    [Fact]
    public void Registry_rejects_duplicate_document_kind_policies_eagerly()
    {
        var exception = Assert.Throws<DocumentSchemaVersionException>(() =>
            CreateCodec(
                [
                    new DocumentSchemaVersionPolicy("ticket", 1, 1),
                    new DocumentSchemaVersionPolicy("ticket", 1, 1)
                ], []));

        Assert.Equal(DocumentSchemaVersionFailure.InvalidPolicy, exception.Failure);
        Assert.Equal("ticket", exception.DocumentKind);
    }

    [Fact]
    public void Registry_rejects_duplicate_steps_eagerly()
    {
        var exception = Assert.Throws<DocumentSchemaVersionException>(() =>
            CreateCodec(
                [new DocumentSchemaVersionPolicy("ticket", 1, 2)],
                [
                    new RenamePropertyUpcaster("ticket", 1, "title", "subject"),
                    new RenamePropertyUpcaster("ticket", 1, "name", "subject")
                ]));

        Assert.Equal(DocumentSchemaVersionFailure.InvalidUpcasterChain, exception.Failure);
        Assert.Equal(1, exception.ParsedVersion);
    }

    [Fact]
    public void Registry_rejects_a_step_below_the_minimum_readable_version_eagerly()
    {
        var exception = Assert.Throws<DocumentSchemaVersionException>(() =>
            CreateCodec(
                [new DocumentSchemaVersionPolicy("ticket", 2, 3)],
                [
                    new RenamePropertyUpcaster("ticket", 1, "old", "older"),
                    new RenamePropertyUpcaster("ticket", 2, "title", "subject")
                ]));

        Assert.Equal(DocumentSchemaVersionFailure.InvalidUpcasterChain, exception.Failure);
        Assert.Equal(1, exception.ParsedVersion);
    }

    [Fact]
    public void Registry_rejects_a_missing_supported_step_eagerly()
    {
        var exception = Assert.Throws<DocumentSchemaVersionException>(() =>
            CreateCodec(
                [new DocumentSchemaVersionPolicy("ticket", 1, 3)],
                [new RenamePropertyUpcaster("ticket", 2, "title", "subject")]));

        Assert.Equal(DocumentSchemaVersionFailure.InvalidUpcasterChain, exception.Failure);
        Assert.Equal(1, exception.ParsedVersion);
    }

    [Fact]
    public void Registry_rejects_an_upcaster_for_an_unknown_document_kind_eagerly()
    {
        var exception = Assert.Throws<DocumentSchemaVersionException>(() =>
            CreateCodec(
                [new DocumentSchemaVersionPolicy("ticket", 1, 1)],
                [new RenamePropertyUpcaster("unknown", 1, "title", "subject")]));

        Assert.Equal(DocumentSchemaVersionFailure.InvalidUpcasterChain, exception.Failure);
        Assert.Equal("unknown", exception.DocumentKind);
    }

    [Fact]
    public void Registry_rejects_a_step_at_the_current_version_eagerly()
    {
        var exception = Assert.Throws<DocumentSchemaVersionException>(() =>
            CreateCodec(
                [new DocumentSchemaVersionPolicy("ticket", 1, 1)],
                [new RenamePropertyUpcaster("ticket", 1, "title", "subject")]));

        Assert.Equal(DocumentSchemaVersionFailure.InvalidUpcasterChain, exception.Failure);
        Assert.Equal(1, exception.ParsedVersion);
    }

    [Fact]
    public void Deserialize_rejects_a_malformed_stamp_before_parsing_content()
    {
        var codec = Codec(
            new DocumentSchemaVersionPolicy("ticket", 1, 2),
            new RenamePropertyUpcaster("ticket", 1, "title", "subject"));

        var exception = Assert.Throws<DocumentSchemaVersionException>(() =>
            codec.Deserialize<Ticket>(Payload("not-a-version", "not json")));

        Assert.Equal(DocumentSchemaVersionFailure.MalformedStamp, exception.Failure);
        Assert.Equal("not-a-version", exception.SchemaVersion);
        Assert.IsNotType<JsonException>(exception.InnerException);
    }

    [Fact]
    public void Deserialize_rejects_a_version_below_the_readable_boundary_before_parsing_content()
    {
        var codec = Codec(new DocumentSchemaVersionPolicy("ticket", 2, 2));

        var exception = Assert.Throws<DocumentSchemaVersionException>(() =>
            codec.Deserialize<Ticket>(Payload("v1", "not json")));

        Assert.Equal(DocumentSchemaVersionFailure.TooOld, exception.Failure);
        Assert.Equal(1, exception.ParsedVersion);
        Assert.Equal(2, exception.MinimumReadableVersion);
        Assert.Equal(2, exception.CurrentVersion);
        Assert.IsNotType<JsonException>(exception.InnerException);
    }

    [Fact]
    public void Deserialize_rejects_a_future_version_before_parsing_content()
    {
        var codec = Codec(
            new DocumentSchemaVersionPolicy("ticket", 1, 2),
            new RenamePropertyUpcaster("ticket", 1, "title", "subject"));

        var exception = Assert.Throws<DocumentSchemaVersionException>(() =>
            codec.Deserialize<Ticket>(Payload("v3", "not json")));

        Assert.Equal(DocumentSchemaVersionFailure.Future, exception.Failure);
        Assert.Equal(3, exception.ParsedVersion);
        Assert.Equal(1, exception.MinimumReadableVersion);
        Assert.Equal(2, exception.CurrentVersion);
        Assert.IsNotType<JsonException>(exception.InnerException);
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v3")]
    public void IsCurrentVersion_returns_false_for_recognized_non_current_versions(string schemaVersion)
    {
        var codec = Codec(
            new DocumentSchemaVersionPolicy("ticket", 1, 2),
            new RenamePropertyUpcaster("ticket", 1, "title", "subject"));

        Assert.False(codec.IsCurrentVersion(Payload(schemaVersion, "not json")));
    }

    [Fact]
    public void Deserialize_applies_every_supported_step_before_typed_deserialization()
    {
        var codec = Codec(
            new DocumentSchemaVersionPolicy("ticket", 1, 3),
            new RenamePropertyUpcaster("ticket", 1, "title", "name"),
            new RenamePropertyUpcaster("ticket", 2, "name", "subject"));

        var result = codec.Deserialize<Ticket>(Payload("v1", "{\"title\":\"A printer is on fire\"}"));

        Assert.Equal("A printer is on fire", result.Subject);
    }

    [Fact]
    public void Deserialize_wraps_invalid_current_content_in_a_structured_failure()
    {
        var codec = Codec(new DocumentSchemaVersionPolicy("ticket", 1, 1));

        var exception = Assert.Throws<DocumentSchemaVersionException>(() =>
            codec.Deserialize<Ticket>(Payload("v1", "not json")));

        Assert.Equal(DocumentSchemaVersionFailure.InvalidContent, exception.Failure);
        Assert.IsType<JsonException>(exception.InnerException);
        Assert.Equal(1, exception.ParsedVersion);
    }

    [Fact]
    public void Deserialize_rejects_non_object_historical_content_as_invalid_content()
    {
        var codec = Codec(
            new DocumentSchemaVersionPolicy("ticket", 1, 2),
            new RenamePropertyUpcaster("ticket", 1, "title", "subject"));

        var exception = Assert.Throws<DocumentSchemaVersionException>(() =>
            codec.Deserialize<Ticket>(Payload("v1", "[]")));

        Assert.Equal(DocumentSchemaVersionFailure.InvalidContent, exception.Failure);
        Assert.Equal(1, exception.ParsedVersion);
    }

    [Fact]
    public void Deserialize_wraps_an_upcaster_exception_in_a_structured_failure()
    {
        var codec = Codec(
            new DocumentSchemaVersionPolicy("ticket", 1, 2),
            new ThrowingUpcaster("ticket", 1));

        var exception = Assert.Throws<DocumentSchemaVersionException>(() =>
            codec.Deserialize<Ticket>(Payload("v1", "{}")));

        Assert.Equal(DocumentSchemaVersionFailure.UpcastFailed, exception.Failure);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal(1, exception.ParsedVersion);
    }

    [Fact]
    public void Historical_content_uses_the_same_json_read_options_as_current_content()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        var codec = new VersionedJsonDocumentCodec(
            [new DocumentSchemaVersionPolicy("ticket", 1, 2)],
            [new RenamePropertyUpcaster("ticket", 1, "title", "subject")],
            VersionFormat,
            options);

        var result = codec.Deserialize<Ticket>(Payload("v1", """
            {
              /* retained historical producer comment */
              "title": "A printer is on fire",
            }
            """));

        Assert.Equal("A printer is on fire", result.Subject);
    }

    [Fact]
    public void Serialize_uses_the_current_version_stamp_and_caller_options()
    {
        var codec = new VersionedJsonDocumentCodec(
            [new DocumentSchemaVersionPolicy("ticket", 2, 2)],
            [],
            VersionFormat,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var serialized = codec.Serialize("ticket", new Ticket("A printer is on fire"));

        Assert.Equal("v2", serialized.SchemaVersion);
        Assert.Equal("{\"subject\":\"A printer is on fire\"}", serialized.ContentJson);
    }

    [Fact]
    public void Deserialize_rejects_an_unknown_document_kind()
    {
        var codec = Codec(new DocumentSchemaVersionPolicy("ticket", 1, 1));

        var exception = Assert.Throws<DocumentSchemaVersionException>(() =>
            codec.Deserialize<Ticket>(new VersionedJsonPayload("unknown", "v1", "{}")));

        Assert.Equal(DocumentSchemaVersionFailure.UnknownDocumentKind, exception.Failure);
        Assert.Equal("unknown", exception.DocumentKind);
    }

    [Fact]
    public void Constructor_treats_document_kinds_as_ordinal_case_sensitive_values()
    {
        var codec = CreateCodec(
            [
                new DocumentSchemaVersionPolicy("ticket", 1, 1),
                new DocumentSchemaVersionPolicy("Ticket", 1, 1)
            ], []);

        Assert.NotNull(codec);
    }

    [Fact]
    public void Constructor_rejects_a_version_format_that_does_not_round_trip_supported_versions()
    {
        var format = new DocumentSchemaVersionFormat(
            (_, _) => 1,
            (_, version) => $"v{version}");

        var exception = Assert.Throws<DocumentSchemaVersionException>(() =>
            new VersionedJsonDocumentCodec(
                [new DocumentSchemaVersionPolicy("ticket", 1, 2)],
                [new RenamePropertyUpcaster("ticket", 1, "title", "subject")],
                format));

        Assert.Equal(DocumentSchemaVersionFailure.InvalidVersionFormat, exception.Failure);
        Assert.Equal("ticket", exception.DocumentKind);
        Assert.Equal(1, exception.ParsedVersion);
        Assert.Equal(1, exception.MinimumReadableVersion);
        Assert.Equal(2, exception.CurrentVersion);
    }

    [Fact]
    public void Caller_owned_format_can_map_a_legacy_alias_without_teaching_groundwork_that_alias()
    {
        var format = new DocumentSchemaVersionFormat(
            (_, stamp) => stamp == "legacy" ? 1 : int.Parse(stamp.AsSpan(1), CultureInfo.InvariantCulture),
            (_, version) => $"v{version}");
        var codec = new VersionedJsonDocumentCodec(
            [new DocumentSchemaVersionPolicy("ticket", 1, 2)],
            [new RenamePropertyUpcaster("ticket", 1, "title", "subject")],
            format,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var result = codec.Deserialize<Ticket>(Payload("legacy", "{\"title\":\"A printer is on fire\"}"));

        Assert.Equal("A printer is on fire", result.Subject);
    }

    [Fact]
    public void Version_format_does_not_wrap_cancellation()
    {
        var format = new DocumentSchemaVersionFormat(
            (_, _) => throw new OperationCanceledException(),
            (_, version) => $"v{version}");

        Assert.Throws<OperationCanceledException>(() =>
            new VersionedJsonDocumentCodec(
                [new DocumentSchemaVersionPolicy("ticket", 1, 1)],
                [],
                format));
    }

    [Fact]
    public void Version_format_does_not_wrap_out_of_memory_failures()
    {
        var format = new DocumentSchemaVersionFormat(
            (_, stamp) => int.Parse(stamp.AsSpan(1), CultureInfo.InvariantCulture),
            (_, _) => throw new OutOfMemoryException());

        Assert.Throws<OutOfMemoryException>(() =>
            new VersionedJsonDocumentCodec(
                [new DocumentSchemaVersionPolicy("ticket", 1, 1)],
                [],
                format));
    }

    private static VersionedJsonDocumentCodec Codec(
        DocumentSchemaVersionPolicy policy,
        params IDocumentJsonUpcaster[] upcasters) => CreateCodec([policy], upcasters);

    private static VersionedJsonDocumentCodec CreateCodec(
        IEnumerable<DocumentSchemaVersionPolicy> policies,
        IEnumerable<IDocumentJsonUpcaster> upcasters) =>
        new(policies, upcasters, VersionFormat, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static VersionedJsonPayload Payload(string schemaVersion, string contentJson) =>
        new("ticket", schemaVersion, contentJson);

    private sealed record Ticket(string Subject);

    private sealed class RenamePropertyUpcaster(
        string documentKind,
        int fromVersion,
        string sourceProperty,
        string targetProperty) : IDocumentJsonUpcaster
    {
        public string DocumentKind { get; } = documentKind;
        public int FromVersion { get; } = fromVersion;

        public JsonObject Upcast(JsonObject content)
        {
            content[targetProperty] = content[sourceProperty]?.DeepClone();
            content.Remove(sourceProperty);
            return content;
        }
    }

    private sealed class ThrowingUpcaster(string documentKind, int fromVersion) : IDocumentJsonUpcaster
    {
        public string DocumentKind { get; } = documentKind;
        public int FromVersion { get; } = fromVersion;

        public JsonObject Upcast(JsonObject content) =>
            throw new InvalidOperationException("The migration failed.");
    }
}
