using System.Collections.Immutable;
using Systemic.Content;

namespace Systemic.Tests.Content;

public sealed class ContentTuningSchemaTests
{
    private static readonly string[] s_requiredTags = ["hostile", "mobile"];

    [Fact]
    public void ValidTuningHasTypedValuesDefaultsAndRequiredTags()
    {
        var schema = new ContentTuningSchema([
            TuningProperty.Number("speed", minimum: 0, maximum: 10, required: true),
            TuningProperty.Integer("count", minimum: 0, maximum: 9, defaultValue: 5),
            TuningProperty.Boolean("enabled", defaultValue: false),
            TuningProperty.Text("label", maxLength: 20, defaultValue: "scout"),
            TuningProperty.Number("optional")
        ], requiredTags: ["hostile"]);
        var document = schema.Parse("""
            {
              "properties": { "speed": 2.25, "count": 3, "enabled": true },
              "tags": ["mobile", "hostile"]
            }
            """, "enemies/scout.json");

        Assert.Equal(2.25, document.GetNumber("speed"));
        Assert.Equal(3, document.GetInteger("count"));
        Assert.True(document.GetBoolean("enabled"));
        Assert.Equal("scout", document.GetText("label"));
        Assert.Equal(s_requiredTags, document.Tags);
        Assert.False(document.TryGetValue("optional", out _));
        Assert.Throws<InvalidOperationException>(() => document.GetInteger("speed"));
        Assert.Throws<KeyNotFoundException>(() => document.GetNumber("missing"));
    }

    [Fact]
    public void AnInheritedValueSatisfiesARequiredDeclarationAndBeatsTheDefault()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "entities": [{ "id": "garden", "kind": "actor", "archetype": "actor.base" }]
            }
            """;
        var schema = new ContentTuningSchema(
            [TuningProperty.Number("speed", minimum: 0, maximum: 10, defaultValue: 1, required: true)],
            requiredTags: ["hostile"]);
        var merged = new EntityDefinition("garden", "actor", ["hostile"], [],
            new Dictionary<string, double> { ["speed"] = 3 }.ToImmutableDictionary(),
            ImmutableDictionary<string, string>.Empty, null, null);

        var document = schema.ParseEntity(
            System.Text.Encoding.UTF8.GetBytes(json), "entities.json", "garden", merged);

        Assert.Equal(3, document.GetNumber("speed"));
        Assert.Contains("hostile", document.Tags);
    }

    [Fact]
    public void LoaderValidatesTuningsAgainstValuesInheritedFromAnArchetype()
    {
        using var pack = CoreOnlyPack.Create();
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            {
              "schemaVersion": 2,
              "archetypes": [
                { "id": "actor.base", "kind": "actor", "tags": ["hostile"], "properties": { "health": 5 } }
              ],
              "entities": [
                { "id": "actor.target", "archetype": "actor.base" }
              ]
            }
            """);
        var schema = new ContentTuningSchema(
            [TuningProperty.Number("health", minimum: 0, maximum: 10, required: true)],
            requiredTags: ["hostile"]);

        var content = ContentPackLoader.Load(pack.Root,
            new ContentModuleRegistry().AddTuningSchema("actor.target", schema));

        Assert.Equal(5, content.Tuning("actor.target").GetNumber("health"));
        Assert.Contains("hostile", content.Tuning("actor.target").Tags);
    }

    [Fact]
    public void InheritedIntegersOutsideTheExactDoubleRangeAreNotRounded()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "entities": [{ "id": "garden", "kind": "actor" }]
            }
            """;
        var schema = new ContentTuningSchema([TuningProperty.Integer("count", required: true)]);
        var merged = new EntityDefinition("garden", "actor", [], [],
            new Dictionary<string, double> { ["count"] = 9007199254740992 }.ToImmutableDictionary(),
            ImmutableDictionary<string, string>.Empty, null, null);

        var error = Assert.Throws<ContentTuningException>(() =>
            schema.ParseEntity(System.Text.Encoding.UTF8.GetBytes(json), "entities.json", "garden", merged));

        Assert.Contains(error.Diagnostics, d => d.Code == "tuning.required"
            && d.Path == "$.entities[0].properties.count");
    }

    [Fact]
    public void InheritedIntegersInsideTheExactDoubleRangeStillInherit()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "entities": [{ "id": "garden", "kind": "actor" }]
            }
            """;
        var schema = new ContentTuningSchema([TuningProperty.Integer("count", required: true)]);
        var merged = new EntityDefinition("garden", "actor", [], [],
            new Dictionary<string, double> { ["count"] = 9007199254740991 }.ToImmutableDictionary(),
            ImmutableDictionary<string, string>.Empty, null, null);

        var document = schema.ParseEntity(
            System.Text.Encoding.UTF8.GetBytes(json), "entities.json", "garden", merged);

        Assert.Equal(9007199254740991, document.GetInteger("count"));
    }

    [Fact]
    public void EntityTuningKeepsFullFilePathsAndOriginalSourceLocations()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "entities": [
                {
                  "id": "garden",
                  "kind": "actor",
                  "tags": ["garden"],
                  "properties": {
                    "speed": 11
                  }
                }
              ]
            }
            """;
        var schema = new ContentTuningSchema(
            [TuningProperty.Number("speed", minimum: 0, maximum: 10)],
            requiredTags: ["garden"]);

        var error = Assert.Throws<ContentTuningException>(() =>
            schema.ParseEntity(System.Text.Encoding.UTF8.GetBytes(json), "entities.json", "garden"));

        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("tuning.range", diagnostic.Code);
        Assert.Equal("$.entities[0].properties.speed", diagnostic.Path);
        Assert.Equal(9, diagnostic.Line);
        Assert.Equal(9, diagnostic.Column);
    }

    [Fact]
    public void InvalidValuesUnknownPropertiesAndMissingTagsReportFilePathAndLine()
    {
        var schema = new ContentTuningSchema([
            TuningProperty.Number("speed", minimum: 0, maximum: 10, required: true),
            TuningProperty.Integer("count"), TuningProperty.Boolean("enabled"), TuningProperty.Text("label")
        ], requiredTags: ["hostile"]);
        var exception = Assert.Throws<ContentTuningException>(() => schema.Parse("""
            {
              "properties": {
                "speed": 11,
                "nmae": "scout",
                "count": 1.5,
                "enabled": "yes",
                "label": null
              },
              "tags": ["mobile"]
            }
            """, "enemies/scout.json"));

        Assert.Contains(exception.Diagnostics, d => d.Code == "tuning.range" && d.Path == "$.properties.speed" && d.Line == 3);
        Assert.Contains(exception.Diagnostics, d => d.Code == "tuning.unknown_property" && d.Path == "$.properties.nmae" && d.Line == 4);
        Assert.Contains(exception.Diagnostics, d => d.Code == "tuning.type" && d.Path == "$.properties.count" && d.Line == 5);
        Assert.Contains(exception.Diagnostics, d => d.Code == "tuning.type" && d.Path == "$.properties.enabled" && d.Line == 6);
        Assert.Contains(exception.Diagnostics, d => d.Code == "tuning.type" && d.Path == "$.properties.label" && d.Line == 7);
        Assert.Contains(exception.Diagnostics, d => d.Code == "tuning.required_tag" && d.Path == "$.tags" && d.Line == 9);
        Assert.All(exception.Diagnostics, d =>
        {
            Assert.Equal("enemies/scout.json", d.File);
            Assert.True(d.Column > 0);
        });
        Assert.Contains("enemies/scout.json:3", exception.Message);
    }

    [Fact]
    public void MissingRequiredValuesAndTagsPointToTheirContainer()
    {
        var schema = new ContentTuningSchema([TuningProperty.Integer("count", required: true)], requiredTags: ["hostile"]);
        var error = Assert.Throws<ContentTuningException>(() => schema.Parse("""
            {
              "properties": {}
            }
            """, "scout.json"));

        Assert.Contains(error.Diagnostics, d => d.Code == "tuning.required" && d.Path == "$.properties.count" && d.Line == 2);
        Assert.Contains(error.Diagnostics, d => d.Code == "tuning.required_tag" && d.Path == "$.tags" && d.Line == 1);
    }

    [Fact]
    public void DuplicateAndUnknownFieldsAreRejectedWithEscapedPaths()
    {
        var schema = new ContentTuningSchema([TuningProperty.Number("speed")]);
        var error = Assert.Throws<ContentTuningException>(() => schema.Parse("""
            {
              "extra": true,
              "properties": { "speed": 1, "speed": 2, "odd/name": 3 },
              "tags": ["mobile", "mobile"]
            }
            """, "scout.json"));

        Assert.Contains(error.Diagnostics, d => d.Code == "tuning.unknown_field" && d.Path == "$.extra" && d.Line == 2);
        Assert.Contains(error.Diagnostics, d => d.Code == "tuning.duplicate" && d.Path == "$.properties.speed" && d.Line == 3);
        Assert.Contains(error.Diagnostics, d => d.Code == "tuning.unknown_property" && d.Path == "$.properties['odd/name']" && d.Line == 3);
        Assert.Contains(error.Diagnostics, d => d.Code == "tuning.duplicate" && d.Path == "$.tags[1]" && d.Line == 4);
    }

    [Fact]
    public void MalformedJsonAndWrongContainerTypesProduceDiagnostics()
    {
        var schema = new ContentTuningSchema([TuningProperty.Number("speed")]);
        var parseError = Assert.Throws<ContentTuningException>(() => schema.Parse("{\n  \"properties\": { \"speed\": }\n}", "broken.json"));
        Assert.Contains(parseError.Diagnostics, d => d.Code == "tuning.invalid_json" && d.File == "broken.json" && d.Line == 2);

        var typeError = Assert.Throws<ContentTuningException>(() => schema.Parse("{\"properties\": [], \"tags\": {}}", "broken.json"));
        Assert.Contains(typeError.Diagnostics, d => d.Code == "tuning.type" && d.Path == "$.properties");
        Assert.Contains(typeError.Diagnostics, d => d.Code == "tuning.type" && d.Path == "$.tags");
    }

    [Fact]
    public void SchemaRejectsConflictingDeclarationsAndInvalidDefaults()
    {
        Assert.Throws<ArgumentException>(() => new ContentTuningSchema([TuningProperty.Number("a"), TuningProperty.Boolean("a")]));
        Assert.Throws<ArgumentException>(() => new ContentTuningSchema([], requiredTags: [" "]));
        Assert.Throws<ArgumentOutOfRangeException>(() => TuningProperty.Number("a", minimum: 2, maximum: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TuningProperty.Number("a", minimum: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => TuningProperty.Integer("a", minimum: 1, defaultValue: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TuningProperty.Text("a", maxLength: 2, defaultValue: "long"));
    }

    [Fact]
    public void DefaultsAndSchemaAreImmutableAcrossParses()
    {
        var declarations = new[] { TuningProperty.Integer("count", defaultValue: 7) };
        var schema = new ContentTuningSchema(declarations, requiredTags: ["hostile"]);
        declarations[0] = TuningProperty.Integer("other", defaultValue: 0);
        var first = schema.Parse("{\"properties\":{},\"tags\":[\"hostile\"]}", "first.json");
        var second = schema.Parse("{\"properties\":{\"count\":2},\"tags\":[\"hostile\"]}", "second.json");

        Assert.Equal(7, first.GetInteger("count"));
        Assert.Equal(2, second.GetInteger("count"));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)first.Tags)[0] = "changed");
    }

    [Fact]
    public void DiagnosticsCountBareCarriageReturnsAndCrlfAsLineBreaks()
    {
        var schema = new ContentTuningSchema([TuningProperty.Number("speed", maximum: 1)]);
        var error = Assert.Throws<ContentTuningException>(() => schema.Parse(
            "{\r\n  \"properties\": {\r    \"speed\": 2\r  }\r\n}", "cr.json"));

        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("$.properties.speed", diagnostic.Path);
        Assert.Equal(3, diagnostic.Line);
        Assert.Equal(5, diagnostic.Column);

        var malformed = Assert.Throws<ContentTuningException>(() => schema.Parse(
            "{\r  \"properties\": {\r    \"speed\": }\r  }\r}", "bad-cr.json"));
        Assert.Equal(3, Assert.Single(malformed.Diagnostics).Line);
    }

    [Fact]
    public void InvalidValueIsNotReplacedSilentlyByADefault()
    {
        var schema = new ContentTuningSchema([TuningProperty.Integer("count", defaultValue: 5)]);
        var error = Assert.Throws<ContentTuningException>(() => schema.Parse(
            "{\"properties\":{\"count\":\"not an integer\"}}", "bad.json"));

        Assert.Contains(error.Diagnostics, d => d.Code == "tuning.type" && d.Path == "$.properties.count");
    }

    [Fact]
    public void IntegerValuesAreExactAndDocumentSizeIsBounded()
    {
        var schema = new ContentTuningSchema([TuningProperty.Integer("count", minimum: long.MinValue, maximum: long.MaxValue)]);
        Assert.Equal(long.MinValue, schema.Parse("{\"properties\":{\"count\":-9223372036854775808}}", "value.json").GetInteger("count"));
        var error = Assert.Throws<ContentTuningException>(() => schema.Parse(
            "{\"properties\":{\"count\":9223372036854775808}}", "value.json"));
        Assert.Contains(error.Diagnostics, d => d.Code == "tuning.type" && d.Path == "$.properties.count");

        var oversized = Assert.Throws<ContentTuningException>(() => schema.Parse(new string(' ', ContentTuningSchema.MaximumDocumentBytes + 1), "huge.json"));
        Assert.Contains(oversized.Diagnostics, d => d.Code == "tuning.size" && d.File == "huge.json");
    }

    [Fact]
    public void InvalidUnicodeInJsonIsReportedAsAContentDiagnostic()
    {
        var schema = new ContentTuningSchema([TuningProperty.Text("label")]);
        var error = Assert.Throws<ContentTuningException>(() => schema.Parse(
            "{\"properties\":{\"label\":\"\\uD800\"}}", "bad.json"));
        Assert.Contains(error.Diagnostics, d => d.Code == "tuning.invalid_json" && d.File == "bad.json");
        Assert.Throws<ContentTuningException>(() => schema.Parse("{\"properties\":{\"\\uD800\":1}}", "bad-key.json"));
        Assert.Throws<ContentTuningException>(() => schema.Parse("{\"tags\":[\"\\uDC00\"]}", "bad-tag.json"));
        byte[] invalidUtf8 = [(byte)'{', (byte)'\"', (byte)'x', (byte)'\"', (byte)':', (byte)'\"', 0xff, (byte)'\"', (byte)'}'];
        Assert.Throws<ContentTuningException>(() => schema.Parse(invalidUtf8, "bad-utf8.json"));
    }
}
