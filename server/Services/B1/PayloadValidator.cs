using System.Globalization;
using B1DataImporter.Api.Models;

namespace B1DataImporter.Api.Services.B1;

/// <summary>
/// Pre-flight validation against the live B1 metadata, so bad rows are caught before
/// they are posted rather than one Service Layer round trip at a time.
/// </summary>
public class PayloadValidator
{
    public List<string> Validate(Dictionary<string, object?> payload, TargetEntity entity, HashSet<string> explicitlyRequired)
    {
        var errors = new List<string>();
        ValidateLevel(payload, entity.Properties, explicitlyRequired, "", errors);

        foreach (var coll in entity.Collections)
        {
            if (!payload.TryGetValue(coll.Name, out var raw) || raw is not List<Dictionary<string, object?>> lines)
                continue;
            for (int i = 0; i < lines.Count; i++)
                ValidateLevel(lines[i], coll.Properties, explicitlyRequired, $"{coll.Name}[{i}].", errors);
        }
        return errors;
    }

    private static void ValidateLevel(
        Dictionary<string, object?> obj, List<TargetProperty> props,
        HashSet<string> explicitlyRequired, string prefix, List<string> errors)
    {
        foreach (var p in props)
        {
            obj.TryGetValue(p.Name, out var value);
            var isEmpty = value is null || (value is string s && s.Length == 0);

            // Mandatory: metadata says non-nullable (and it isn't a server-assigned key),
            // or the mapping marked it required.
            var required = explicitlyRequired.Contains(p.Name) || (!p.Nullable && !p.IsKey);
            if (required && isEmpty && obj.ContainsKey(p.Name))
            {
                errors.Add($"{prefix}{p.Name} is required but empty.");
                continue;
            }
            if (isEmpty) continue;

            var text = value as string;

            if (p.MaxLength is int max && text != null && text.Length > max)
                errors.Add($"{prefix}{p.Name} is {text.Length} chars, max {max}.");

            if (p.EnumMembers is { Count: > 0 } && text != null &&
                !p.EnumMembers.Contains(text, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{prefix}{p.Name} = '{text}' is not a valid value ({string.Join(", ", p.EnumMembers.Take(6))}...).");

            switch (p.Type)
            {
                case "Edm.Int32" or "Edm.Int16" or "Edm.Int64":
                    if (value is not long && value is not int &&
                        !long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                        errors.Add($"{prefix}{p.Name} = '{text}' is not a whole number.");
                    break;
                case "Edm.Double" or "Edm.Decimal" or "Edm.Single":
                    if (value is not double && value is not decimal &&
                        !double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                        errors.Add($"{prefix}{p.Name} = '{text}' is not a number.");
                    break;
                case "Edm.DateTime" or "Edm.DateTimeOffset":
                    if (text != null && !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        errors.Add($"{prefix}{p.Name} = '{text}' is not a valid date.");
                    break;
            }
        }
    }
}
