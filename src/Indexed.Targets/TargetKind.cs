using System.Text.Json.Serialization;

namespace Indexed.Targets;

[JsonConverter(typeof(JsonStringEnumConverter<TargetKind>))]
public enum TargetKind
{
    [JsonStringEnumMemberName("git-repo")]
    GitRepository = 0,

    [JsonStringEnumMemberName("directory-tree")]
    DirectoryTree = 1,

    [JsonStringEnumMemberName("directory-set")]
    DirectorySet = 2,
}
