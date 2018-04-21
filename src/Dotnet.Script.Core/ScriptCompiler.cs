using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dotnet.Script.Core.Internal;
using Dotnet.Script.DependencyModel.Environment;
using Dotnet.Script.DependencyModel.NuGet;
using Dotnet.Script.DependencyModel.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Scripting.Hosting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using RuntimeAssembly = Dotnet.Script.DependencyModel.Runtime.RuntimeAssembly;

namespace Dotnet.Script.Core
{
    public class ScriptCompiler
    {
        // Note: Windows only, Mac and Linux needs something else?
        [DllImport("Kernel32.dll")]
        private static extern IntPtr LoadLibrary(string path);
        
        static ScriptCompiler()
        {
            // force Roslyn to use ReferenceManager for the first time
            Task.Run(() =>
            {
                CSharpScript.Create<object>("1", ScriptOptions.Default, typeof(CommandLineScriptGlobals), new InteractiveAssemblyLoader()).RunAsync(new CommandLineScriptGlobals(Console.Out, CSharpObjectFormatter.Instance)).GetAwaiter().GetResult();
            });
        }

        protected virtual IEnumerable<string> ImportedNamespaces => new[]
        {
            "System",
            "System.IO",
            "System.Collections.Generic",
            "System.Console",
            "System.Diagnostics",
            "System.Dynamic",
            "System.Linq",
            "System.Linq.Expressions",
            "System.Text",
            "System.Threading.Tasks"
        };

        // see: https://github.com/dotnet/roslyn/issues/5501
        protected virtual IEnumerable<string> SuppressedDiagnosticIds => new[] { "CS1701", "CS1702", "CS1705" };        

        public CSharpParseOptions ParseOptions { get; } = new CSharpParseOptions(LanguageVersion.Latest, kind: SourceCodeKind.Script);

        public RuntimeDependencyResolver RuntimeDependencyResolver { get; }

        public ScriptLogger Logger { get; }

        public ScriptCompiler(ScriptLogger logger, RuntimeDependencyResolver runtimeDependencyResolver)
        {
            Logger = logger;
            RuntimeDependencyResolver = runtimeDependencyResolver;
        }

        public virtual ScriptOptions CreateScriptOptions(ScriptContext context, IList<RuntimeDependency> runtimeDependencies)
        {
            var scriptMap = runtimeDependencies.ToDictionary(rdt => rdt.Name, rdt => rdt.Scripts);
            var opts = ScriptOptions.Default.AddImports(ImportedNamespaces)                
                .WithSourceResolver(new NuGetSourceReferenceResolver(new SourceFileResolver(ImmutableArray<string>.Empty, context.WorkingDirectory),scriptMap))
                .WithMetadataResolver(new NuGetMetadataReferenceResolver(ScriptMetadataResolver.Default.WithBaseDirectory(context.WorkingDirectory)))
                .WithEmitDebugInformation(true)
                .WithFileEncoding(context.Code.Encoding);

            if (!string.IsNullOrWhiteSpace(context.FilePath))
            {
                opts = opts.WithFilePath(context.FilePath);
            }

            return opts;
        }

        public virtual ScriptCompilationContext<TReturn> CreateCompilationContext<TReturn, THost>(ScriptContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            
            Logger.Verbose($"Current runtime is '{RuntimeHelper.GetPlatformIdentifier()}'.");

            var runtimeDependencies = RuntimeDependencyResolver.GetDependencies(context.WorkingDirectory, context.ScriptMode, context.Code.ToString()).ToArray();

            var scriptOptions = CreateScriptOptions(context, runtimeDependencies.ToList());

            var loadedAssembliesMap = CreateLoadedAssembliesMap();

            var scriptDependenciesMap = CreateScriptDependenciesMap(runtimeDependencies);

            scriptOptions = AddScriptReferences(scriptOptions, loadedAssembliesMap, scriptDependenciesMap);

            LoadNativeAssets(runtimeDependencies);

            AppDomain.CurrentDomain.AssemblyResolve +=
                (sender, args) => MapUnresolvedAssemblyToRuntimeLibrary(scriptDependenciesMap, loadedAssembliesMap, args);

            string code = GetScriptCode(context);

            var loader = new InteractiveAssemblyLoader();

            var script = CSharpScript.Create<TReturn>(code, scriptOptions, typeof(THost), loader);

            SetOptimizationLevel(context, script);

            EvaluateDiagnostics(script);

            return new ScriptCompilationContext<TReturn>(script, context.Code, loader, scriptOptions);
        }

        private static void LoadNativeAssets(RuntimeDependency[] runtimeDependencies)
        {
            foreach (var nativeAsset in runtimeDependencies.SelectMany(rtd => rtd.NativeAssets).Distinct())
            {
                if (RuntimeHelper.IsWindows())
                {
                    LoadLibrary(nativeAsset);
                }
            }
        }

        private ScriptOptions AddScriptReferences(ScriptOptions scriptOptions, Dictionary<string, Assembly> loadedAssembliesMap, Dictionary<string, RuntimeAssembly> scriptDependenciesMap)
        {
            foreach (var runtimeAssembly in scriptDependenciesMap.Values)
            {
                loadedAssembliesMap.TryGetValue(runtimeAssembly.Name.Name, out var loadedAssembly);
                if (loadedAssembly == null)
                {
                    Logger.Verbose("Adding reference to a runtime dependency => " + runtimeAssembly);
                    scriptOptions = scriptOptions.AddReferences(MetadataReference.CreateFromFile(runtimeAssembly.Path));
                }
                else
                {
                    //Add the reference from the AssemblyLoadContext if present. 
                    scriptOptions = scriptOptions.AddReferences(loadedAssembly);
                    Logger.Verbose("Already loaded => " + loadedAssembly);
                }
            }

            return scriptOptions;
        }

        private void EvaluateDiagnostics<TReturn>(Script<TReturn> script)
        {
            var orderedDiagnostics = script.GetDiagnostics();

            var suppressedDiagnostics = orderedDiagnostics.Where(d => SuppressedDiagnosticIds.Contains(d.Id));
            foreach (var suppressedDiagnostic in suppressedDiagnostics)
            {
                Logger.Verbose($"Suppressed diagnostic {suppressedDiagnostic.Id}: {suppressedDiagnostic.ToString()}");
            }

            if (orderedDiagnostics.Except(suppressedDiagnostics).Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                throw new CompilationErrorException("Script compilation failed due to one or more errors.",
                    orderedDiagnostics.ToImmutableArray());
            }
        }

        private void SetOptimizationLevel<TReturn>(ScriptContext context, Script<TReturn> script)
        {
            if (context.OptimizationLevel == OptimizationLevel.Release)
            {
                Logger.Verbose("Configuration/Optimization mode: Release");
                SetReleaseOptimizationLevel(script.GetCompilation());
            }
            else
            {
                Logger.Verbose("Configuration/Optimization mode: Debug");
            }
        }

        private string GetScriptCode(ScriptContext context)
        {
            string code;
            
            // when processing raw code, make sure we inject new lines after preprocessor directives
            if (context.FilePath == null)
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(context.Code, ParseOptions);
                var syntaxRewriter = new PreprocessorLineRewriter();
                var newSyntaxTree = syntaxRewriter.Visit(syntaxTree.GetRoot());
                code = newSyntaxTree.ToFullString();
            }
            else
            {
                code = context.Code.ToString();
            }

            return code;
        }

        private static Dictionary<string, RuntimeAssembly> CreateScriptDependenciesMap(RuntimeDependency[] runtimeDependencies)
        {
            // Build up a dependency map that picks runtime assembly with the highest version.
            // This aligns with the CoreCLR that uses the highest version strategy.
            return runtimeDependencies.SelectMany(rtd => rtd.Assemblies).Distinct().GroupBy(rdt => rdt.Name.Name, rdt => rdt)
                .Select(gr => new { Name = gr.Key, ResolvedRuntimeAssembly = gr.OrderBy(rdt => rdt.Name.Version).Last() })
                .ToDictionary(f => f.Name, f => f.ResolvedRuntimeAssembly, StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, Assembly> CreateLoadedAssembliesMap()
        {
            // Build up a map of loaded assemblies that picks runtime assembly with the highest version.
            // This aligns with the CoreCLR that uses the highest version strategy.
            return AppDomain.CurrentDomain.GetAssemblies().Distinct().GroupBy(a => a.GetName().Name, a => a)
                .Select(gr => new { Name = gr.Key, ResolvedRuntimeAssembly = gr.OrderBy(a => a.GetName().Version).Last() })
                .ToDictionary(f => f.Name, f => f.ResolvedRuntimeAssembly, StringComparer.OrdinalIgnoreCase);
        }

        private static void SetReleaseOptimizationLevel(Compilation compilation)
        {
            var compilationOptionsField = typeof(CSharpCompilation).GetTypeInfo().GetDeclaredField("_options");
            var compilationOptions = (CSharpCompilationOptions)compilationOptionsField.GetValue(compilation);
            compilationOptions = compilationOptions.WithOptimizationLevel(OptimizationLevel.Release);
            compilationOptionsField.SetValue(compilation, compilationOptions);
        }

        private Assembly MapUnresolvedAssemblyToRuntimeLibrary(IDictionary<string, RuntimeAssembly> dependencyMap, IDictionary<string, Assembly> loadedAssemblyMap, ResolveEventArgs args)
        {
            var assemblyName = new AssemblyName(args.Name);
            if (dependencyMap.TryGetValue(assemblyName.Name, out var runtimeAssembly))
            {
                if (runtimeAssembly.Name.Version > assemblyName.Version)
                {
                    loadedAssemblyMap.TryGetValue(assemblyName.Name, out var loadedAssembly);                    
                    if(loadedAssembly != null)
                    {
                        Logger.Log($"Redirecting {assemblyName} to already loaded {loadedAssembly.GetName().Name}");
                        return loadedAssembly;
                    }
                    Logger.Log($"Redirecting {assemblyName} to {runtimeAssembly.Name}");
                    return Assembly.LoadFrom(runtimeAssembly.Path);
                }
            }

            return null;
        }
    }
}