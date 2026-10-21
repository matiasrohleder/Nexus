using System.Linq;
using System.Reflection;

namespace Nexus.Services;

/// <summary>Product metadata. The version's single source of truth is &lt;Version&gt; in Nexus.csproj.</summary>
public static class AppInfo
{
    /// <summary>Semantic version, e.g. "0.1.0" (any "+buildmetadata" suffix stripped).</summary>
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly()
             .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
         ?? "0.0.0")
        .Split('+').First();
}
