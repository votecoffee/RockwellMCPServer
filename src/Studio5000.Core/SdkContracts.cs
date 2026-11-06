using System.Text.Json;
using System.Text.Json.Serialization;

namespace Studio5000.Core;

public sealed record ControllerTarget(string Id, string CommunicationsPath, SdkTargetPolicy? Policy = null, CipReadTarget? CipRead = null)
{
    public ControllerTarget() : this("", "") { } // Configuration binder must not bind constructor arrays twice.
}
public sealed record SdkTargetPolicy(string[] AllowedMethods, string? ProgramName = null, string? RoutineName = null)
{
    public SdkTargetPolicy() : this([]) { }
}
public sealed record SdkCall(string Method, Dictionary<string, JsonElement> Arguments);
public sealed record SdkInputFile(string? Path, string Sha256, string? ArtifactId = null);
public sealed record SdkRunRequest(SdkCall[] Calls, string? ProjectId = null, string? ExpectedSha256 = null,
    string? SourcePath = null, string? SourceArtifactId = null, string? ControllerTargetId = null,
    Dictionary<string, SdkInputFile>? InputFiles = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? BoundL5kMajorRevision = null);
public sealed record SdkParameter(string Name, string Type, bool Optional, string? DefaultValue);
public sealed record SdkMethod(string Name, bool IsStatic, string Returns, SdkParameter[] Parameters);
public sealed record SdkEnum(string Name, string[] Values);
public sealed record SdkSurface(string Assembly, SdkMethod[] Methods, SdkEnum[] Enums);
public sealed record SdkMethodInfo(string Name, string Scope, string Effect, SdkParameter[] Parameters);
public sealed record SdkStepResult(int Index, string Method, string Status, JsonElement? Value);
public sealed record SdkEvent(string Kind, string Message, int? Progress = null);
public sealed record SdkBridgeRequest(string WorkDirectory, string? SourcePath, string? CommunicationsPath, SdkCall[] Calls,
    string ExecutionProfile = NativeExecutionPolicy.Standard, bool BoundL5k = false);
public sealed record SdkBridgeResult(string Status, SdkStepResult[] Steps, SdkEvent[] Events, string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SdkAssembly = null);
public sealed record SdkJob(string Id, string Status, DateTimeOffset CreatedAt, string[] Methods,
    string? SourceSha256, string? ControllerTargetId, bool ControllerEffectsPossible,
    SdkStepResult[] Steps, SdkEvent[] Events, Artifact[] Artifacts, ProjectInfo? ResultProject = null, string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BoundL5kSource? BoundL5kSource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SdkAssembly = null);
