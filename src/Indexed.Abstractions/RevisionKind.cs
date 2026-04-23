using System.Text.Json.Serialization;

namespace Indexed.Abstractions;

[JsonConverter(typeof(JsonStringEnumConverter<RevisionKind>))]
public enum RevisionKind
{
    [JsonStringEnumMemberName("none")]
    None = 0,

    [JsonStringEnumMemberName("git-head")]
    GitHead = 1,
}
