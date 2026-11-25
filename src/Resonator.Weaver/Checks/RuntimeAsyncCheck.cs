using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Resonator.Weaver.Cecil;

namespace Resonator.Weaver.Checks;

/// <summary>Framework await intrinsics require a runtime-async caller, not a state-machine MoveNext or ordinary wrapper.</summary>
public static class RuntimeAsyncCheck
{
    // Cecil 0.11 predates the runtime-async method implementation flag.
    const MethodImplAttributes Async = (MethodImplAttributes)0x2000;

    public static int Run(string dllPath, TextWriter log)
    {
        using var assembly = AssemblyFile.Read(dllPath);
        int calls = 0, failures = 0;
        foreach (var method in assembly.MainModule.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody))
        {
            bool invalid = false;
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt) ||
                    instruction.Operand is not MethodReference callee ||
                    callee.DeclaringType.FullName != "System.Runtime.CompilerServices.AsyncHelpers" ||
                    callee.DeclaringType.Scope.Name is not ("System.Runtime" or "System.Private.CoreLib") ||
                    callee.Name is not ("Await" or "AwaitAwaiter" or "UnsafeAwaitAwaiter")) continue;
                calls++;
                if ((method.ImplAttributes & Async) != 0) continue;
                invalid = true;
                log.WriteLine($"[runtimeasync] {method.FullName}: {callee.Name} requires a runtime-async caller; compile with runtime-async=on and remove ordinary helper wrappers");
            }
            if (invalid) failures++;
        }
        log.WriteLine($"runtimeasync: {calls} intrinsic call(s) checked, {failures} invalid caller(s) -> {(failures == 0 ? "OK" : "FAIL")}");
        return failures == 0 ? 0 : 1;
    }
}
