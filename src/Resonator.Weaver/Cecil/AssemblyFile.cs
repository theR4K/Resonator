using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Pdb;

namespace Resonator.Weaver.Cecil;

/// <summary>Reading and writing assemblies the way every Resonator tool does it.</summary>
public static class AssemblyFile
{
    public static bool HasPdb(string dllPath) => File.Exists(Path.ChangeExtension(dllPath, ".pdb"));

    /// <summary>
    /// Read an assembly, resolving references from its own directory (plus <paramref name="searchDirs"/>).
    /// <paramref name="withSymbols"/> attaches the PDB next to it, if there is one.
    /// </summary>
    public static AssemblyDefinition Read(string path, bool withSymbols = false, bool readWrite = false, IEnumerable<string>? searchDirs = null)
    {
        path = Path.GetFullPath(path);
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(path));
        foreach (string dir in searchDirs ?? []) resolver.AddSearchDirectory(dir);

        bool symbols = withSymbols && HasPdb(path);
        return AssemblyDefinition.ReadAssembly(path, new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadSymbols = symbols,
            SymbolReaderProvider = symbols ? new PdbReaderProvider() : null,
            ReadWrite = readWrite,
        });
    }

    /// <summary>Write the assembly, with a fresh portable PDB when <paramref name="withSymbols"/>.</summary>
    public static void Write(AssemblyDefinition assembly, string path, bool withSymbols) =>
        assembly.Write(path, new WriterParameters
        {
            WriteSymbols = withSymbols,
            SymbolWriterProvider = withSymbols ? new PdbWriterProvider() : null,
        });

    /// <summary>
    /// Write the assembly back over the file it was read from, then dispose it. Cecil keeps a
    /// read handle on the source file, so it goes to a temp file first.
    /// </summary>
    public static void ReplaceInPlace(AssemblyDefinition assembly, string path)
    {
        string tmp = path + ".resonator.tmp";
        assembly.Write(tmp);
        assembly.Dispose();
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(500); }
        }
    }

    /// <summary>Direct assembly reference names of the assembly at <paramref name="path"/>.</summary>
    public static HashSet<string> ReferencedAssemblies(string path)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        return assembly.MainModule.AssemblyReferences.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
