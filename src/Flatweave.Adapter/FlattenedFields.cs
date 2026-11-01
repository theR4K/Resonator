using System;
using System.Reflection;
using Resonator;

namespace Flatweave.Adapters;

/// <summary>Shared reflection semantics for sync-member discovery and data-model compatibility hashing.</summary>
public static class FlattenedFields
{
    public static bool IsInitOnlyOrFlattened(this FieldInfo field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.IsInitOnly || field.IsDefined(typeof(FlattenedInitOnlyAttribute), inherit: false)) return true;
        // Keep older flattened plugins usable: previous versions embedded a private-to-the-assembly marker type.
        foreach (var attribute in field.CustomAttributes)
            if (attribute.AttributeType.FullName == typeof(FlattenedInitOnlyAttribute).FullName) return true;
        return false;
    }
}
