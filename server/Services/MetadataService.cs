using System.Xml.Linq;
using B1DataImporter.Api.Models;

namespace B1DataImporter.Api.Services;

/// <summary>
/// Parses the Service Layer $metadata (EDMX/OData CSDL) document into a list of
/// B1 entities with their properties and child collections. This is what lets the
/// tool support "any object" without hardcoding: everything is discovered here,
/// including UDFs (U_*) and UDO entity sets.
/// </summary>
public class MetadataService
{
    // OData/EDM XML namespaces (Service Layer uses OData v3-style CSDL).
    private static readonly XNamespace Edmx = "http://schemas.microsoft.com/ado/2007/06/edmx";
    private static readonly XNamespace[] EdmCandidates =
    {
        "http://schemas.microsoft.com/ado/2008/09/edm",
        "http://schemas.microsoft.com/ado/2009/11/edm",
        "http://docs.oasis-open.org/odata/ns/edm",
    };

    /// <summary>Cache of parsed metadata per BaseUrl+CompanyDB, so we parse the big EDMX once.</summary>
    private readonly Dictionary<string, List<B1Entity>> _cache = new();

    public List<B1Entity> Parse(string edmxXml, string cacheKey)
    {
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        var doc = XDocument.Parse(edmxXml);
        var edm = EdmCandidates.FirstOrDefault(ns => doc.Descendants(ns + "Schema").Any())
                  ?? EdmCandidates[0];

        // 1. Index all complex + entity types by name so nav collections can resolve properties.
        var typesByName = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in doc.Descendants(edm + "EntityType").Concat(doc.Descendants(edm + "ComplexType")))
        {
            var name = t.Attribute("Name")?.Value;
            if (name != null) typesByName[name] = t;
        }

        // 2. Collect enum members for enum types (BoYesNoEnum, CardTypeEnum, ...).
        var enumMembers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in doc.Descendants(edm + "EnumType"))
        {
            var name = e.Attribute("Name")?.Value;
            if (name == null) continue;
            enumMembers[name] = e.Elements(edm + "Member")
                .Select(m => m.Attribute("Name")?.Value ?? "")
                .Where(s => s.Length > 0).ToList();
        }

        // 3. EntitySets in the container define the URL-addressable objects.
        var entities = new List<B1Entity>();
        foreach (var set in doc.Descendants(edm + "EntitySet"))
        {
            var setName = set.Attribute("Name")?.Value;
            var typeRef = set.Attribute("EntityType")?.Value;   // e.g. SAPB1.BusinessPartners
            if (setName == null || typeRef == null) continue;
            var typeName = typeRef.Split('.').Last();
            if (!typesByName.TryGetValue(typeName, out var typeEl)) continue;

            var entity = new B1Entity { Name = setName, EntityType = typeName };
            var keys = typeEl.Element(edm + "Key")?.Elements(edm + "PropertyRef")
                .Select(k => k.Attribute("Name")?.Value).Where(x => x != null).Select(x => x!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>();

            entity.Properties = ReadProperties(typeEl, edm, keys, enumMembers);

            // Child collections. Service Layer's CSDL is inconsistent: some are proper
            // NavigationProperty elements, but line tables like DocumentLines are plain
            // <Property Type="Collection(SAPB1.DocumentLine)"/> — both forms are scanned here.
            var collectionRefs = typeEl.Elements(edm + "NavigationProperty")
                .Select(nav => (Name: nav.Attribute("Name")?.Value, Type: nav.Attribute("Type")?.Value))
                .Concat(typeEl.Elements(edm + "Property")
                    .Where(p => (p.Attribute("Type")?.Value ?? "").StartsWith("Collection(", StringComparison.OrdinalIgnoreCase))
                    .Select(p => (Name: p.Attribute("Name")?.Value, Type: p.Attribute("Type")?.Value)));

            foreach (var (navName, navType) in collectionRefs)
            {
                if (navName == null) continue;

                string? childTypeName = null;
                if (navType != null && navType.StartsWith("Collection(", StringComparison.OrdinalIgnoreCase))
                    childTypeName = navType[11..^1].Split('.').Last();

                if (childTypeName != null && typesByName.TryGetValue(childTypeName, out var childEl))
                {
                    entity.Collections.Add(new B1NavCollection
                    {
                        Name = navName,
                        TargetEntity = childTypeName,
                        Properties = ReadProperties(childEl, edm, new HashSet<string>(), enumMembers),
                    });
                }
            }

            entities.Add(entity);
        }

        entities = entities.OrderBy(e => e.Name).ToList();
        _cache[cacheKey] = entities;
        return entities;
    }

    public void Invalidate(string cacheKey) => _cache.Remove(cacheKey);

    private static List<B1Property> ReadProperties(
        XElement typeEl, XNamespace edm, HashSet<string> keys,
        Dictionary<string, List<string>> enumMembers)
    {
        var props = new List<B1Property>();
        foreach (var p in typeEl.Elements(edm + "Property"))
        {
            var name = p.Attribute("Name")?.Value;
            if (name == null) continue;
            var type = p.Attribute("Type")?.Value ?? "Edm.String";
            if (type.StartsWith("Collection(", StringComparison.OrdinalIgnoreCase)) continue;   // handled as a child collection
            var shortType = type.Split('.').Last();
            var prop = new B1Property
            {
                Name = name,
                Type = type,
                Nullable = (p.Attribute("Nullable")?.Value ?? "true") != "false",
                MaxLength = int.TryParse(p.Attribute("MaxLength")?.Value, out var ml) ? ml : null,
                IsKey = keys.Contains(name),
            };
            if (enumMembers.TryGetValue(shortType, out var members))
                prop.EnumMembers = members;
            props.Add(prop);
        }
        return props;
    }
}
