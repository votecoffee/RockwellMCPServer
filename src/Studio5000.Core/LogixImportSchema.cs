using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Studio5000.Core;

public sealed record ProgramImportSchemaRequest(string Path, string Sha256, string Scope = "partial_definitions");
public sealed record ProgramImportSchemaReport(string Scope, string? SchemaSha256, bool Valid,
    bool FullDocumentValidated, string[] Definitions, string[] Errors, bool NativeImportAccepted = false, bool XsdApplied = false);

// Use the original compiled declarations, not a relaxed/synthetic controller schema.
public static class LogixImportSchema
{
    public const int MaximumSchemaBytes = 4 * 1024 * 1024;
    public static ProgramImportSchemaReport Validate(XDocument document, byte[] schemaBytes, string scope)
    {
        if (scope is not ("partial_definitions" or "full_document"))
            throw new OperationException("program_import_schema_scope", "Choose partial_definitions or full_document explicitly.");
        if (schemaBytes.Length is 0 or > MaximumSchemaBytes)
            throw new OperationException("program_import_schema_limit", "Use a self-contained local XSD of at most 4 MiB.");
        var errors = new List<string>();
        void Error(object? sender, ValidationEventArgs e)
        {
            if (errors.Count == 32) throw new OperationException("program_import_schema_limit", "Schema validation exceeded 32 findings.");
            errors.Add(e.Message.Length <= 1024 ? e.Message : e.Message[..1024]);
        }
        using var reader = XmlReader.Create(new MemoryStream(schemaBytes), new()
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumSchemaBytes });
        var schema = XmlSchema.Read(reader, Error) ?? throw new OperationException("program_import_schema", "An XSD schema is required.");
        if (schema.Includes.Count != 0)
            throw new OperationException("program_import_schema_external", "External include/import/redefine is not resolved. Supply a reviewed, self-contained schema; no network loading or schema weakening is performed.");
        var schemas = new XmlSchemaSet { XmlResolver = null }; schemas.ValidationEventHandler += Error;
        schemas.Add(schema); schemas.Compile();
        var definitions = new List<string>();
        if (errors.Count == 0 && scope == "full_document") document.Validate(schemas, Error, false);
        else if (errors.Count == 0)
        {
            var root = schemas.GlobalElements[new XmlQualifiedName("RSLogix5000Content", "")] as XmlSchemaElement
                ?? throw new OperationException("program_import_schema_shape", "The schema must declare the full RSLogix5000Content root.");
            var controllerDeclaration = Child(root, "Controller");
            var controller = document.Root?.Element("Controller") ?? throw new OperationException("invalid_project", "Controller context is missing.");
            foreach (var (container, name) in new[] { ("Programs", "Program"), ("Tags", "Tag"), ("DataTypes", "DataType") })
            {
                var nodes = controller.Elements(container).Elements(name).ToArray();
                if (nodes.Length == 0) continue;
                var declaration = Child(Child(controllerDeclaration, container), name);
                foreach (var node in nodes)
                {
                    // No wrapper, attribute, type, or value is removed from the named definition.
                    node.Validate(declaration, schemas, Error, false);
                    definitions.Add(name + ":" + (string?)node.Attribute("Name"));
                }
            }
            if (definitions.Count == 0) throw new OperationException("program_import_schema_shape", "No named program/tag/datatype definitions were selected.");
        }
        return new(scope, ProjectService.Hash(schemaBytes), errors.Count == 0,
            scope == "full_document" && errors.Count == 0, definitions.ToArray(), errors.ToArray(), false, true);
    }
    private static XmlSchemaElement Child(XmlSchemaElement parent, string name)
    {
        IEnumerable<XmlSchemaElement> children = parent.ElementSchemaType is XmlSchemaComplexType type ? Elements(type.ContentTypeParticle) : [];
        var matches = children.Where(e => e.QualifiedName.Name == name && e.QualifiedName.Namespace.Length == 0).ToArray();
        return matches.Length == 1 ? matches[0] : throw new OperationException("program_import_schema_shape", "Expected one compiled declaration at " + parent.QualifiedName.Name + "/" + name + ".");
    }
    private static IEnumerable<XmlSchemaElement> Elements(XmlSchemaParticle? particle)
    {
        if (particle is XmlSchemaElement element) yield return element;
        else if (particle is XmlSchemaGroupBase group)
        {
            foreach (var child in group.Items.OfType<XmlSchemaParticle>())
                foreach (var e in Elements(child)) yield return e;
        }
        else if (particle is XmlSchemaGroupRef reference)
            foreach (var e in Elements(reference.Particle)) yield return e;
    }
}
