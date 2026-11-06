using System.Text;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed partial class ProjectService
{
    private static (MergeAddedTag[] Tags, MergeAddedDataType[] Types) PlanFreshAdditions(
        LogicValueMergeRequest request, LogixLexicalDocument baseline, LogixLexicalDocument logic, LogixLexicalDocument fresh,
        Dictionary<string, XElement> originalTags, Dictionary<string, XElement> reviewedTags, Dictionary<string, XElement> freshTags,
        List<MergeFieldFinding> findings, List<LogixLexicalDocument.Patch> patches)
    {
        const int maximumAdditions = 256;
        var tagChoices = request.TagAdditions ?? [];
        var typeChoices = request.DataTypeAdditions ?? [];
        if (tagChoices.Length > maximumAdditions || typeChoices.Length > maximumAdditions)
            throw new OperationException("merge_policy", "Approve at most 256 new tags and 256 new datatypes.");
        var tags = new Dictionary<string, MergeTagAddition>(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in tagChoices)
        {
            if (choice is null || choice.StartupValues is not ("reviewed-logic" or "native-default"))
                throw new OperationException("merge_policy", "Choose reviewed-logic or native-default startup values for each new tag.");
            WatchValidation.Identifier(choice.TagName);
            if (choice.ProgramName is not null) WatchValidation.Identifier(choice.ProgramName);
            if (!tags.TryAdd(LogixPreservation.Key(choice.ProgramName, choice.TagName), choice))
                throw new OperationException("merge_policy", "Approve each scoped tag only once.");
        }
        var approvedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in typeChoices)
        {
            WatchValidation.Identifier(name);
            if (!approvedTypes.Add(name)) throw new OperationException("merge_policy", "Approve each datatype only once.");
        }

        XElement Controller(LogixLexicalDocument input) => input.Document.Root!.Element("Controller")!;
        var ac = Controller(baseline); var bc = Controller(logic); var cc = Controller(fresh);
        if (new[] { ac, bc, cc }.Any(c => c.Elements("DataTypes").Count() > 1))
            throw new OperationException("merge_identity", "More than one datatype container is unsupported.");
        static string Name(XElement element) => (string?)element.Attribute("Name") ?? throw new OperationException("merge_identity", "A declaration has no name.");
        static Dictionary<string, XElement> DataTypes(XElement controller) => LogixPreservation.Index(controller.Elements("DataTypes").Elements("DataType"), Name);
        var originalTypes = DataTypes(ac); var reviewedTypes = DataTypes(bc); var currentTypes = DataTypes(cc);
        void Finding(string kind, string location, string status, string detail, XElement? old = null, XElement? reviewed = null, XElement? current = null) =>
            findings.Add(new(kind, location, status, LogixPreservation.Fingerprint(old), LogixPreservation.Fingerprint(reviewed), LogixPreservation.Fingerprint(current), detail));

        static XElement ContainerConfiguration(XElement? node) => new("DataTypes", node?.Attributes(),
            node?.Nodes().Where(n => n is XElement e ? e.Name != "DataType" : n is not XText t || !string.IsNullOrWhiteSpace(t.Value)));
        var originalContainer = ContainerConfiguration(ac.Element("DataTypes"));
        var reviewedContainer = ContainerConfiguration(bc.Element("DataTypes"));
        if (LogixPreservation.Fingerprint(originalContainer) != LogixPreservation.Fingerprint(reviewedContainer) ||
            !originalTypes.Keys.Where(reviewedTypes.ContainsKey).SequenceEqual(reviewedTypes.Keys.Where(originalTypes.ContainsKey), StringComparer.OrdinalIgnoreCase))
            Finding("configuration", "Controller/DataTypes", "unsupported", "Reviewed datatype container metadata or existing declaration order changed.",
                originalContainer, reviewedContainer, ContainerConfiguration(cc.Element("DataTypes")));

        foreach (var (name, original) in originalTypes)
        {
            reviewedTypes.TryGetValue(name, out var reviewed);
            if (LogixPreservation.Fingerprint(original) != LogixPreservation.Fingerprint(reviewed))
                Finding("data-type-contract", name, "unsupported", "An existing UDT changed or was removed. Use a separately reviewed layout/configuration workflow.",
                    original, reviewed, currentTypes.GetValueOrDefault(name));
        }
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in approvedTypes)
        {
            if (originalTypes.ContainsKey(name) || currentTypes.ContainsKey(name) || !reviewedTypes.ContainsKey(name))
                Finding("data-type-addition", name, "conflict", "An approved UDT must be new in reviewed logic and absent from both baseline and fresh.",
                    originalTypes.GetValueOrDefault(name), reviewedTypes.GetValueOrDefault(name), currentTypes.GetValueOrDefault(name));
            else roots.Add(name);
        }
        foreach (var (name, reviewed) in reviewedTypes)
            if (!originalTypes.ContainsKey(name) && !approvedTypes.Contains(name))
                Finding("data-type-addition", name, "unsupported", "Explicitly approve this new UDT and any new dependencies.", reviewed: reviewed,
                    current: currentTypes.GetValueOrDefault(name));

        var freshPrograms = LogixPreservation.Index(cc.Elements("Programs").Elements("Program"), Name);
        var eligibleTags = new List<(XElement Owner, XElement Source, string StartupValues)>();
        foreach (var (key, reviewed) in reviewedTags)
        {
            if (originalTags.ContainsKey(key)) continue;
            freshTags.TryGetValue(key, out var current);
            var program = (string?)reviewed.Parent?.Parent?.Attribute("Name");
            if (reviewed.Parent?.Parent?.Name == "Controller") program = null;
            var owner = program is null ? cc : freshPrograms.GetValueOrDefault(program);
            var data = reviewed.Elements("Data").ToArray();
            var formats = data.Select(d => (string?)d.Attribute("Format")).ToArray();
            var type = (string?)reviewed.Attribute("DataType");
            if (current is not null)
                Finding("tag-addition", key, "conflict", "The fresh snapshot already has a tag with this scoped name.", reviewed: reviewed, current: current);
            else if (!tags.TryGetValue(key, out var choice))
                Finding("tag-addition", key, "unsupported", "Explicitly approve this scoped tag and its startup value policy.", reviewed: reviewed);
            else if (program is not null && (freshTags.ContainsKey(LogixPreservation.Key(null, Name(reviewed))) ||
                reviewedTags.ContainsKey(LogixPreservation.Key(null, Name(reviewed)))))
                Finding("tag-addition", key, "conflict", "A new local tag would shadow a controller tag. Review reference binding separately.", reviewed: reviewed);
            else if (owner is null || owner.Elements("Tags").Count() > 1 || fresh.Locate(owner).Empty || LogixPreservation.Safety(owner) ||
                LogixPreservation.Safety(reviewed) || (string?)reviewed.Attribute("TagType") != "Base" || reviewed.Attribute("AliasFor") is not null ||
                reviewed.Descendants().Any(e => e.Name.LocalName is "ProduceInfo" or "ConsumeInfo" or "Connection" or "ForceData") ||
                string.IsNullOrWhiteSpace(type) || string.Equals(type, "MESSAGE", StringComparison.OrdinalIgnoreCase))
                Finding("tag-addition", key, "unsupported", "Additions require an existing standard scope and an ordinary base tag without alias, force, connection or MESSAGE configuration.", reviewed: reviewed);
            else if (formats.Length > 16 || formats.Any(string.IsNullOrWhiteSpace) || formats.Distinct(StringComparer.Ordinal).Count() != formats.Length ||
                ((choice.StartupValues == "reviewed-logic") != (data.Length > 0)))
                Finding("tag-addition", key, "unsupported", "Use reviewed-logic for explicit unique data formats or native-default when the reviewed tag contains no Data.", reviewed: reviewed);
            else
            {
                roots.Add(type);
                eligibleTags.Add((owner, reviewed, choice.StartupValues));
            }
        }
        foreach (var key in tags.Keys)
            if (originalTags.ContainsKey(key) || !reviewedTags.ContainsKey(key))
                Finding("tag-addition", key, "conflict", "The approved tag is absent from reviewed logic or already exists in the baseline.",
                    originalTags.GetValueOrDefault(key), reviewedTags.GetValueOrDefault(key), freshTags.GetValueOrDefault(key));

        var orderedTypes = new List<MergeAddedDataType>();
        if (roots.Count > 0)
        {
            TypeDependencyClosure closure;
            try { closure = LogixTypeDependencies.Resolve(logic, fresh, new(request.Logic, request.Values, DataTypes: roots.ToArray())); }
            catch (OperationException ex)
            {
                Finding("data-type-closure", "Controller/DataTypes", "unsupported", ex.Message);
                return ([], []);
            }
            foreach (var definition in closure.Definitions.Where(d => !d.ExistingDestinationDefinition))
            {
                var reviewed = reviewedTypes[definition.Name];
                if (!approvedTypes.Contains(definition.Name) || originalTypes.ContainsKey(definition.Name) || LogixPreservation.Safety(reviewed) ||
                    reviewed.Elements("Members").Count() != 1 || !reviewed.Elements("Members").Elements("Member").Any())
                {
                    Finding("data-type-closure", definition.Name, "unsupported", "A dependency must be an approved, new standard UDT.", reviewed: reviewed);
                    return ([], []);
                }
                orderedTypes.Add(new(definition.Name, definition.SourceRawSha256, definition.References));
            }
        }
        if (orderedTypes.Count > 0)
            patches.Add(fresh.InsertChildren(cc, "DataTypes", string.Concat(orderedTypes.Select(d => logic.Raw(reviewedTypes[d.Name]))),
                "Modules", "AddOnInstructionDefinitions", "Tags", "Programs", "Tasks", "ParameterConnections", "Trends", "QuickWatchLists"));

        var addedTags = new List<MergeAddedTag>();
        foreach (var group in eligibleTags.GroupBy(t => t.Owner))
        {
            var fragments = new List<string>();
            foreach (var (_, tag, startupValues) in group)
            {
                var raw = logic.Raw(tag);
                fragments.Add(raw);
                var program = group.Key == cc ? null : Name(group.Key);
                var data = tag.Elements("Data").Select(d =>
                {
                    var payload = logic.Raw(d);
                    return new MergeStartupData((string)d.Attribute("Format")!, Hash(Encoding.UTF8.GetBytes(payload)), Encoding.UTF8.GetByteCount(payload),
                        payload.Length <= 512 ? payload : payload[..512], payload.Length > 512);
                }).ToArray();
                addedTags.Add(new(Name(tag), program, (string)tag.Attribute("DataType")!, (string?)tag.Attribute("Dimensions"), startupValues,
                    Hash(Encoding.UTF8.GetBytes(raw)), data));
                Finding("tag-addition", LogixPreservation.Key(program, Name(tag)), "reviewed-added", "Copy the entire reviewed declaration and selected startup policy.", reviewed: tag);
            }
            patches.Add(fresh.InsertChildren(group.Key, "Tags", string.Concat(fragments),
                group.Key == cc ? ["Programs", "Tasks", "ParameterConnections", "Trends", "QuickWatchLists"] : ["Routines"]));
        }
        foreach (var added in orderedTypes)
            Finding("data-type-addition", added.Name, "reviewed-added", "Insert the exact reviewed UDT after its new transitive dependencies.", reviewed: reviewedTypes[added.Name]);
        return (addedTags.ToArray(), orderedTypes.ToArray());
    }
}
