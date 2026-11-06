namespace Studio5000.Core;

public sealed partial class ProjectService
{
    public LadderPreviewReport PreviewLadder(string id, LadderPreviewRequest request)
    {
        var project = Get(id); Expect(project, request.ExpectedSha256);
        if (project.Kind != "logix") throw new OperationException("not_logix", "Select a registered Logix snapshot.");
        if (string.IsNullOrWhiteSpace(request.RoutineXPath) || request.RoutineXPath.Length > 8192)
            throw new OperationException("invalid_ladder_query", "Provide an exact routine selector up to 8192 characters.");
        var document = XmlProjects.Parse(File.ReadAllBytes(RevisionFile(id, project.Revision)));
        var routine = XmlProjects.Select(document, request.RoutineXPath);
        if (routine.Name != "Routine" || (string?)routine.Attribute("Type") != "RLL")
            throw new OperationException("not_ladder", "Select exactly one RLL routine.");
        return LadderPreview.Build(project, routine, request);
    }

    public ProjectComparisonReport CompareProjects(ProjectCompareRequest request)
    {
        var (left, a, right, b) = ComparisonDocuments(request.LeftProjectId, request.LeftSha256, request.RightProjectId, request.RightSha256);
        return ProjectComparison.Compare(left, a, right, b, request);
    }

    private (ProjectInfo Project, byte[] Bytes) ReadBoundedLogixRevision(string id, string expectedSha256)
    {
        var project = Read<ProjectInfo>(ProjectMeta(id));
        Expect(project, expectedSha256);
        if (project.Kind != "logix") throw new OperationException("not_logix", "Select an exact managed Logix snapshot.");
        using var file = File.OpenRead(RevisionFile(id, project.Revision));
        if (file.Length > 64 * 1024 * 1024) throw new OperationException("file_too_large", "An analysis input supports at most 64 MiB.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (Hash(bytes) != project.CurrentSha256) throw new OperationException("state_tampered", "Working revision hash does not match its manifest.");
        return (project, bytes);
    }
}
