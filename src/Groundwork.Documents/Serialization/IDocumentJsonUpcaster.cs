using System.Text.Json.Nodes;

namespace Groundwork.Documents.Serialization;

/// <summary>Rewrites one JSON object from FromVersion to FromVersion + 1.</summary>
public interface IDocumentJsonUpcaster
{
    string DocumentKind { get; }
    int FromVersion { get; }
    JsonObject Upcast(JsonObject content);
}
