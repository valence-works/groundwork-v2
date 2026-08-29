using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class PortableJsonSerializerTests
{
    [Fact]
    public void Closed_portable_graph_serializes_without_changing_json_semantics()
    {
        using var elementSource = JsonDocument.Parse("{\"nested\":true}");
        var value = new Dictionary<string, object?>
        {
            ["text"] = "line\nbreak",
            ["number"] = 42,
            ["items"] = new object?[] { false, null, 1.5m },
            ["element"] = elementSource.RootElement.Clone(),
            ["node"] = JsonNode.Parse("[\"a\",\"b\"]")
        };

        using var result = JsonDocument.Parse(PortableJsonSerializer.Serialize(value));

        Assert.Equal("line\nbreak", result.RootElement.GetProperty("text").GetString());
        Assert.Equal(42, result.RootElement.GetProperty("number").GetInt32());
        Assert.Equal(3, result.RootElement.GetProperty("items").GetArrayLength());
        Assert.True(result.RootElement.GetProperty("element").GetProperty("nested").GetBoolean());
        Assert.Equal("b", result.RootElement.GetProperty("node")[1].GetString());
    }

    [Fact]
    public void Managed_compatibility_fallback_still_serializes_an_arbitrary_clr_object()
    {
        var json = PortableJsonSerializer.Serialize(new CompatibilityValue("ready", 3));

        using var result = JsonDocument.Parse(json);
        Assert.Equal("ready", result.RootElement.GetProperty("State").GetString());
        Assert.Equal(3, result.RootElement.GetProperty("Count").GetInt32());
    }

    [Fact]
    public void String_serialization_preserves_json_escaping()
    {
        using var result = JsonDocument.Parse(PortableJsonSerializer.SerializeString("quote\" and \\ slash"));

        Assert.Equal("quote\" and \\ slash", result.RootElement.GetString());
    }

    private sealed record CompatibilityValue(string State, int Count);
}
