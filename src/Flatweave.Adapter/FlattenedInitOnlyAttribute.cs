using System;

namespace Resonator;

/// <summary>Marks a readonly sync field whose InitOnly flag was cleared to make flattened code legal C#.</summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class FlattenedInitOnlyAttribute : Attribute;
