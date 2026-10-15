using System.Collections.Generic;
using Mono.Cecil;

namespace Resonator.Weaver.Deweaving;

/// <summary>The exact inverse of ONE weaver modification (see <see cref="WeaverSpec"/>).</summary>
public interface IRevertOperation
{
    void Apply(RevertContext context);
}

/// <summary>What every operation works on, computed once before the operations run.</summary>
public sealed class RevertContext(AssemblyDefinition assembly, DeweaveOptions options, DeweaveReport report)
{
    public AssemblyDefinition Assembly { get; } = assembly;
    public ModuleDefinition Module => Assembly.MainModule;
    public DeweaveOptions Options { get; } = options;
    public DeweaveReport Report { get; } = report;

    /// <summary>Worker subclasses (the base Worker excluded).</summary>
    public List<TypeDefinition> Workers { get; } = WeaverSpec.CollectWorkers(assembly.MainModule);

    /// <summary>Sync fields accessed from outside their type family (<see cref="WeaverSpec.FieldKey"/>).</summary>
    public HashSet<string> ExternallyAccessed { get; } = WeaverSpec.ComputeExternallyAccessed(assembly.MainModule);
}
