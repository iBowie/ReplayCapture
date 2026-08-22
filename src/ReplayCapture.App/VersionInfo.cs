using System.Reflection;

namespace ReplayCapture.App;

/// <summary>
/// The build's version string, e.g. "0.1.0-alpha+9.cd1e75d" — read back from the
/// <see cref="AssemblyInformationalVersionAttribute"/> that <c>Directory.Build.props</c>'
/// <c>SetVersionFromGit</c> target derives from <c>git describe</c> at build time.
/// </summary>
internal static class VersionInfo
{
    public static string Display { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        is { Length: > 0 } version
            ? version
            : "unknown";
}
