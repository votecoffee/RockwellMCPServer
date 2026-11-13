using System.Text.Json;

namespace Studio5000.Core;

internal static class LogicTraceResults
{
    internal static void Validate(LogicTraceRequest request)
    {
        if (request.Offset is < 0 or > 1_000_000 || request.DependencyOffset is < 0 or > 1_000_000 ||
            request.Limit is < 1 or > 200 || request.DependencyLimit is < 1 or > 200 ||
            request.Role is not ("all" or "read" or "write" or "unknown"))
            throw new OperationException("logic_trace_query", "Use nonnegative offsets up to 1000000, page limits 1..200 and role all/read/write/unknown.");
    }

    internal static LogicTraceReport Build(ProjectInfo project, LogicTraceRequest request, LogicTraceRegion root, bool precise,
        List<LogicUsage> usages, List<LogicDependency> dependencies, string[] issues, bool limited)
    {
        var edges = dependencies.Where(e => precise ? e.Mapping != "aggregate-rung" : !LogicContext.Same(e.From, e.To)).Distinct().ToArray();
        var visited = new HashSet<LogicTraceRegion> { precise ? root : new(root.Symbol, "") };
        var frontier = visited.ToArray();
        var truncated = false;
        LogicTraceRegion From(LogicDependency edge) => precise ? edge.FromRegion ?? new(edge.From, "", Issue: "region_unavailable") : new(edge.From, "");
        LogicTraceRegion To(LogicDependency edge) => precise ? edge.ToRegion ?? new(edge.To, "", Issue: "region_unavailable") : new(edge.To, "");
        var outgoing = edges.ToLookup(e => From(e).Symbol, StringComparer.OrdinalIgnoreCase);
        var incoming = edges.ToLookup(e => To(e).Symbol, StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; depth < request.Depth; depth++)
        {
            var next = new HashSet<LogicTraceRegion>();
            void Add(LogicTraceRegion? region)
            {
                if (region is null || visited.Contains(region) || next.Contains(region)) return;
                if (visited.Count + next.Count >= 1000) { truncated = true; return; }
                next.Add(region);
            }
            foreach (var selected in frontier)
            {
                if (request.Direction != "upstream")
                    foreach (var edge in outgoing[selected.Symbol]) Add(LogicTraceRegions.Map(selected, From(edge), To(edge), precise ? edge.Mapping : null));
                if (request.Direction != "downstream")
                    foreach (var edge in incoming[selected.Symbol]) Add(LogicTraceRegions.Map(selected, To(edge), From(edge), precise ? edge.Mapping : null));
            }
            visited.UnionWith(next); frontier = next.ToArray();
            if (frontier.Length == 0) break;
        }
        var bySymbol = visited.ToLookup(v => v.Symbol, StringComparer.OrdinalIgnoreCase);
        bool Contains(LogicTraceRegion region) => bySymbol[region.Symbol].Any(v => LogicTraceRegions.Overlaps(v, region));
        bool Role(LogicUsage usage) => request.Role == "all" || usage.Role == request.Role ||
            (usage.Role is "unknown" or "read-write" && request.Role is "read" or "write");
        bool SelectUsage(LogicUsage usage)
        {
            if (!Role(usage)) return false;
            var region = precise ? usage.Region ?? new(usage.Symbol, "", Issue: "region_unavailable") : new(usage.Symbol, "");
            return (request.IncludeRelatedUsages ?? !precise) ? Contains(region) : LogicTraceRegions.Overlaps(root, region);
        }
        var usagePage = Page(usages.Where(SelectUsage), request.Offset, request.Limit);
        var dependencyPage = Page(edges.Where(e => Contains(From(e)) && Contains(To(e))), request.DependencyOffset, request.DependencyLimit);
        limited |= truncated || usagePage.Next is not null || dependencyPage.Next is not null ||
            edges.Any(e => request.Direction != "upstream" && Contains(From(e)) && !Contains(To(e)) ||
                request.Direction != "downstream" && Contains(To(e)) && !Contains(From(e)));
        var diagnostics = truncated ? issues.Take(99).Append("traversal_symbol_limit: 1000 selected regions; use a lower depth or a narrower member.").ToArray() : issues;
        var partial = truncated || diagnostics.Length > 0 || visited.Any(v => v.Issue is not null) ||
            usages.Any(u => SelectUsage(u) && (u.Role == "unknown" || u.Region?.Issue is not null));
        var report = new LogicTraceReport(project.Id, project.CurrentSha256, precise ? LogicTraceRegions.Label(root) : root.Symbol,
            visited.Select(LogicTraceRegions.Label).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            usagePage.Values, dependencyPage.Values, diagnostics, limited, LogicTracing.Boundary,
            usagePage.Total, request.Offset, request.Limit, usagePage.Next, dependencyPage.Total,
            request.DependencyOffset, request.DependencyLimit, dependencyPage.Next, partial);
        if (JsonSerializer.SerializeToUtf8Bytes(report, ProjectService.Json).Length > 4 * 1024 * 1024)
            throw new OperationException("logic_trace_limit", "Trace metadata exceeds 4 MiB. Reduce traversal depth.");
        return report;
    }

    // Retain consecutive rows within both the row and byte budgets. The next
    // offset resumes immediately after the emitted rows, even on a short page.
    private static (T[] Values, int Total, int? Next) Page<T>(IEnumerable<T> source, int offset, int limit)
    {
        const int maximumBytes = 1024 * 1024;
        var values = new List<T>(); var total = 0; var bytes = 0; var full = false;
        foreach (var value in source)
        {
            var position = total++;
            if (position < offset || full || values.Count >= limit) continue;
            var size = JsonSerializer.SerializeToUtf8Bytes(value, ProjectService.Json).Length;
            if (size > maximumBytes && values.Count == 0)
                throw new OperationException("logic_trace_limit", "One trace row exceeds 1 MiB. Inspect the saved source at its exact routine selector.");
            if (bytes + size > maximumBytes) { full = true; continue; }
            bytes += size; values.Add(value);
        }
        return (values.ToArray(), total, offset + values.Count < total ? offset + values.Count : null);
    }
}
