﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.Util;
using ICSharpCode.ILSpyX;
using ILSpyX.Backend.Application;
using ILSpyX.Backend.Model;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;

namespace ILSpyX.Backend.Decompiler;

using ICSharpCode.ILSpyX.Extensions;
using System.Reflection.Metadata;

public class DecompilerBackend(
    ILoggerFactory loggerFactory,
    ILSpyBackendSettings ilspyBackendSettings,
    SingleThreadAssemblyList assemblyList)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<DecompilerBackend>();

    public async Task<AssemblyData?> AddAssemblyAsync(string? path)
    {
        if (path is not null)
        {
            try
            {
                var loadedAssembly = await assemblyList.AddAssembly(path);
                if (loadedAssembly is not null)
                {
                    return await CreateAssemblyDataAsync(loadedAssembly);
                }
            }
            catch (Exception ex)
            {
                logger?.LogError("An exception occurred when reading assembly {assembly}: {exception}", path, ex);
            }
        }

        return null;
    }

    public async Task<AssemblyData?> CreateAssemblyDataAsync(LoadedAssembly loadedAssembly)
    {
        var metaDataFile = await loadedAssembly.GetMetadataFileOrNullAsync();
        if (metaDataFile is null)
        {
            return null;
        }

        var version = metaDataFile.Metadata.GetAssemblyDefinition().Version;
        var targetFrameworkId = await loadedAssembly.GetTargetFrameworkIdAsync();
        return new AssemblyData(loadedAssembly.ShortName, loadedAssembly.FileName, loadedAssembly.IsAutoLoaded)
        {
            Version = version.ToString(),
            TargetFramework = !string.IsNullOrEmpty(targetFrameworkId)
                ? targetFrameworkId.Replace("Version=", " ")
                : null
        };
    }

    public async Task<bool> RemoveAssemblyAsync(string? path)
    {
        if (path is null)
        {
            return false;
        }

        await assemblyList.RemoveAssembly(path);
        return true;
    }

    public async Task<(T Result, bool NewAutoLoadedAssemblies)> DetectAutoLoadedAssemblies<T>(Func<Task<T>> operation)
    {
        var autoLoadedAssembliesBefore = (await GetLoadedAssembliesAsync()).Where(
            assembly => assembly.IsAutoLoaded);
        var result = await operation();
        var autoLoadedAssembliesAfter = (await GetLoadedAssembliesAsync()).Where(
            assembly => assembly.IsAutoLoaded);
        return (result, autoLoadedAssembliesBefore.Count() != autoLoadedAssembliesAfter.Count());
    }

    public CSharpDecompiler? CreateDecompiler(string assembly, string? outputLanguage = null)
    {
        var loadedAssembly = assemblyList.FindAssembly(assembly);
        var metadataFile = loadedAssembly?.GetMetadataFileOrNull();
        if (loadedAssembly is not null && metadataFile is not null)
        {
            return new CSharpDecompiler(
                metadataFile,
                loadedAssembly.GetAssemblyResolver(true),
                ilspyBackendSettings.CreateDecompilerSettings(outputLanguage ?? LanguageName.CSharpLatest));
        }

        return null;
    }

    public async Task<IEnumerable<AssemblyData>> GetLoadedAssembliesAsync()
    {
        return (await Task.WhenAll(
            (await assemblyList.GetAllAssemblies()).Select(async loadedAssembly => await CreateAssemblyDataAsync(loadedAssembly))))
            .Where(data => data is not null)
            .Cast<AssemblyData>();
    }

    public IEnumerable<MemberData> GetMembers(string? assemblyPath, TypeDefinitionHandle handle)
    {
        if (handle.IsNil || (assemblyPath is null))
        {
            return Array.Empty<MemberData>();
        }

        var loadedAssembly = assemblyList.FindAssembly(assemblyPath);
        if (loadedAssembly is null)
        {
            return Array.Empty<MemberData>();
        }

        var decompiler = CreateDecompiler(assemblyPath);
        if (decompiler is null)
        {
            return Array.Empty<MemberData>();
        }

        var typeSystem = decompiler.TypeSystem;
        var definition = typeSystem.MainModule.GetDefinition(handle);

        return definition == null
            ? new List<MemberData>()
            : definition.NestedTypes
                .Select(typeDefinition => new MemberData(
                    Name: typeDefinition.TypeToString(includeNamespace: false),
                    Token: MetadataTokens.GetToken(typeDefinition.MetadataToken),
                    SubKind: typeDefinition.Kind))
                .Union(definition.Fields.Select(GetMemberData).OrderBy(m => m.Name))
                .Union(definition.Properties.Select(GetMemberData).OrderBy(m => m.Name))
                .Union(definition.Events.Select(GetMemberData).OrderBy(m => m.Name))
                .Union(definition.Methods.Select(GetMemberData).OrderBy(m => m.Name));

        static MemberData GetMemberData(IMember member)
        {
            string memberName = member is IMethod method
                ? method.MethodToString(false, false, false)
                : member.Name;
            return new MemberData(
                Name: memberName,
                Token: MetadataTokens.GetToken(member.MetadataToken),
                SubKind: TypeKind.None);
        }
    }

    public DecompileResult GetCode(string? assemblyPath, EntityHandle handle, string outputLanguage)
    {
        if (assemblyPath is not null)
        {
            return outputLanguage switch
            {
                LanguageName.IL => DecompileResult.WithCode(GetILCode(assemblyPath, handle)),
                LanguageName.ILCharp => DecompileResult.WithCode(GetIlWithCSharpCode(assemblyPath, handle, LanguageName.CSharpLatest)),
                _ => DecompileResult.WithCode(GetCSharpCode(assemblyPath, handle, outputLanguage))
            };
        }

        return DecompileResult.WithError("No assembly given");
    }

    public IEntity? GetEntityFromHandle(string assemblyPath, EntityHandle handle)
    {
        if (!handle.IsNil)
        {
            var decompiler = CreateDecompiler(assemblyPath);
            if (decompiler is not null)
            {
                var module = decompiler.TypeSystem.MainModule;
                return handle.Kind switch
                {
                    HandleKind.TypeDefinition => module.GetDefinition((TypeDefinitionHandle) handle),
                    HandleKind.FieldDefinition => module.GetDefinition((FieldDefinitionHandle) handle),
                    HandleKind.MethodDefinition => module.GetDefinition((MethodDefinitionHandle) handle),
                    HandleKind.PropertyDefinition => module.GetDefinition((PropertyDefinitionHandle) handle),
                    HandleKind.EventDefinition => module.GetDefinition((EventDefinitionHandle) handle),
                    _ => null,
                };
            }
        }

        return null;
    }

    private string GetCSharpCode(string assemblyPath, EntityHandle handle, string outputLanguage)
    {
        if (handle.IsNil)
        {
            return string.Empty;
        }

        var decompiler = CreateDecompiler(assemblyPath, outputLanguage);
        if (decompiler is null)
        {
            return string.Empty;
        }

        var module = decompiler.TypeSystem.MainModule;

        switch (handle.Kind)
        {
            case HandleKind.AssemblyDefinition:
                return GetAssemblyCode(assemblyPath, decompiler);
            case HandleKind.TypeDefinition:
                var typeDefinition = module.GetDefinition((TypeDefinitionHandle) handle);
                if (typeDefinition.DeclaringType == null)
                    return decompiler.DecompileTypesAsString(new[] { (TypeDefinitionHandle) handle });
                return decompiler.DecompileAsString(handle);
            case HandleKind.FieldDefinition:
            case HandleKind.MethodDefinition:
            case HandleKind.PropertyDefinition:
            case HandleKind.EventDefinition:
                return decompiler.DecompileAsString(handle);
        }

        return string.Empty;
    }

    private string GetIlWithCSharpCode(string assemblyPath, EntityHandle handle, string outputLanguage)
    {
        if (handle.IsNil)
        {
            return string.Empty;
        }

        var decompiler = CreateDecompiler(assemblyPath, outputLanguage);
        if (decompiler is null)
        {
            return string.Empty;
        }

        var module = decompiler.TypeSystem.MainModule;
        var textOutput = new PlainTextOutput();
        var disassembler = CreateDisassembler(assemblyPath, module, textOutput);
        string code;

        switch (handle.Kind)
        {
            case HandleKind.AssemblyDefinition:
                code = GetAssemblyCode(assemblyPath, decompiler);
                GetAssemblyILCode(disassembler, assemblyPath, module, textOutput);
                return textOutput.ToString() + code;
            case HandleKind.TypeDefinition:
                var typeDefinition = module.GetDefinition((TypeDefinitionHandle) handle);
                disassembler.DisassembleType(module.MetadataFile, (TypeDefinitionHandle) handle);
                if (typeDefinition.DeclaringType == null)
                    code = decompiler.DecompileTypesAsString(new[] { (TypeDefinitionHandle) handle });
                else
                    code = decompiler.DecompileAsString(handle);
                return textOutput.ToString() + code;
            case HandleKind.FieldDefinition:
                disassembler.DisassembleField(module.MetadataFile, (FieldDefinitionHandle) handle);
                return textOutput.ToString() + decompiler.DecompileAsString(handle);
            case HandleKind.MethodDefinition:
                disassembler.DisassembleMethod(module.MetadataFile, (MethodDefinitionHandle) handle);
                return textOutput.ToString() + decompiler.DecompileAsString(handle);
            case HandleKind.PropertyDefinition:
                disassembler.DisassembleProperty(module.MetadataFile, (PropertyDefinitionHandle) handle);
                return textOutput.ToString() + decompiler.DecompileAsString(handle);
            case HandleKind.EventDefinition:
                disassembler.DisassembleEvent(module.MetadataFile, (EventDefinitionHandle) handle);
                return textOutput.ToString() + decompiler.DecompileAsString(handle);
        }
        return string.Empty;
    }

    private string GetILCode(string assemblyPath, EntityHandle handle)
    {
        if (handle.IsNil)
        {
            return string.Empty;
        }

        var decompiler = CreateDecompiler(assemblyPath);
        if (decompiler is null)
        {
            return string.Empty;
        }

        var module = decompiler.TypeSystem.MainModule;
        var textOutput = new PlainTextOutput();
        var disassembler = CreateDisassembler(assemblyPath, module, textOutput);

        switch (handle.Kind)
        {
            case HandleKind.AssemblyDefinition:
                GetAssemblyILCode(disassembler, assemblyPath, module, textOutput);
                return textOutput.ToString();
            case HandleKind.TypeDefinition:
                disassembler.DisassembleType(module.MetadataFile, (TypeDefinitionHandle) handle);
                return textOutput.ToString();
            case HandleKind.FieldDefinition:
                disassembler.DisassembleField(module.MetadataFile, (FieldDefinitionHandle) handle);
                return textOutput.ToString();
            case HandleKind.MethodDefinition:
                disassembler.DisassembleMethod(module.MetadataFile, (MethodDefinitionHandle) handle);
                return textOutput.ToString();
            case HandleKind.PropertyDefinition:
                disassembler.DisassembleProperty(module.MetadataFile, (PropertyDefinitionHandle) handle);
                return textOutput.ToString();
            case HandleKind.EventDefinition:
                disassembler.DisassembleEvent(module.MetadataFile, (EventDefinitionHandle) handle);
                return textOutput.ToString();
        }

        return string.Empty;
    }

    private static ReflectionDisassembler CreateDisassembler(string assemblyPath, MetadataModule module, ITextOutput textOutput)
    {
        var dis = new ReflectionDisassembler(textOutput, CancellationToken.None)
        {
            DetectControlStructure = true,
            ShowSequencePoints = false,
            ShowMetadataTokens = true,
            ExpandMemberDefinitions = true,
        };
        var resolver = new UniversalAssemblyResolver(assemblyPath,
            throwOnError: true,
            targetFramework: module.MetadataFile.DetectTargetFrameworkId());
        dis.AssemblyResolver = resolver;
        dis.DebugInfo = null;

        return dis;
    }

    private static void GetAssemblyILCode(ReflectionDisassembler disassembler, string assemblyPath, MetadataModule module, ITextOutput output)
    {
        output.WriteLine("// " + assemblyPath);
        output.WriteLine();
        var peFile = module.MetadataFile;
        var metadata = peFile.Metadata;

        disassembler.WriteAssemblyReferences(metadata);
        if (metadata.IsAssembly)
        {
            disassembler.WriteAssemblyHeader(peFile);
        }
        output.WriteLine();
        disassembler.WriteModuleHeader(peFile);
    }

    private string GetAssemblyCode(string assemblyPath, CSharpDecompiler decompiler)
    {
        using var output = new StringWriter();
        WriteCommentLine(output, assemblyPath);
        var module = decompiler.TypeSystem.MainModule.MetadataFile;
        var metadata = module.Metadata;
        if (metadata.IsAssembly)
        {
            var name = metadata.GetAssemblyDefinition();
            if ((name.Flags & System.Reflection.AssemblyFlags.WindowsRuntime) != 0)
            {
                WriteCommentLine(output, metadata.GetString(name.Name) + " [WinRT]");
            }
            else
            {
                WriteCommentLine(output, metadata.GetFullAssemblyName());
            }
        }
        else
        {
            WriteCommentLine(output, module.Name);
        }

        var mainModule = decompiler.TypeSystem.MainModule;
        var globalType = mainModule.TypeDefinitions.FirstOrDefault();
        if (globalType != null)
        {
            output.Write("// Global type: ");
            output.Write(globalType.FullName);
            output.WriteLine();
        }
        var corHeader = module.CorHeader;
        if (corHeader != null)
        {
            var entrypointHandle = MetadataTokenHelpers.EntityHandleOrNil(corHeader.EntryPointTokenOrRelativeVirtualAddress);
            if (!entrypointHandle.IsNil && entrypointHandle.Kind == HandleKind.MethodDefinition)
            {
                var entrypoint = mainModule.ResolveMethod(entrypointHandle, new ICSharpCode.Decompiler.TypeSystem.GenericContext());
                if (entrypoint != null)
                {
                    output.Write("// Entry point: ");
                    output.Write(entrypoint.DeclaringType.FullName + "." + entrypoint.Name);
                    output.WriteLine();
                }
            }
            if (module is PEFile peFileModule)
            {
                output.WriteLine("// Architecture: " + peFileModule.GetPlatformDisplayName());
            }
            if ((corHeader.Flags & System.Reflection.PortableExecutable.CorFlags.ILOnly) == 0)
            {
                output.WriteLine("// This assembly contains unmanaged code.");
            }
        }
        if (module is PEFile peFile)
        {
            string runtimeName = peFile.GetRuntimeDisplayName();
            if (runtimeName != null)
            {
                output.WriteLine("// Runtime: " + runtimeName);
            }
        }
        output.WriteLine();

        output.Write(decompiler.DecompileModuleAndAssemblyAttributesToString());

        output.WriteLine();

        return output.ToString();
    }

    private static void WriteCommentLine(StringWriter output, string s)
    {
        output.WriteLine($"// {s}");
    }

    public IEnumerable<MemberData> ListTypes(string? assemblyPath, string? @namespace)
    {
        if ((assemblyPath == null) || (@namespace == null))
        {
            yield break;
        }

        var loadedAssembly = assemblyList.FindAssembly(assemblyPath);
        if (loadedAssembly is null)
        {
            yield break;
        }

        var decompiler = CreateDecompiler(assemblyPath);
        if (decompiler is null)
        {
            yield break;
        }

        var currentNamespace = decompiler.TypeSystem.MainModule.RootNamespace;
        string[] parts = @namespace.Split('.');

        if (!(parts.Length == 1 && string.IsNullOrEmpty(parts[0])))
        {
            // not the global namespace
            foreach (var part in parts)
            {
                var nested = currentNamespace.GetChildNamespace(part);
                if (nested == null)
                    yield break;
                currentNamespace = nested;
            }
        }

        foreach (var t in currentNamespace.Types.OrderBy(t => t.FullName))
        {
            yield return new MemberData(
                Name: t.TypeToString(includeNamespace: false),
                Token: MetadataTokens.GetToken(t.MetadataToken),
                SubKind: t.Kind);
        }
    }
}
