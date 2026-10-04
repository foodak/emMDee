using System.Reflection;

namespace emMDee.Services;

/// <summary>
/// The running application's version, as stamped into the build.
/// <c>PublishRelease.ps1</c> passes <c>-p:Version=&lt;tag&gt;</c> so a released
/// binary reports the version it was tagged as; a local build reports whatever
/// <c>&lt;Version&gt;</c> says in the project file.
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // The SDK appends "+<commit sha>" when source revision metadata is
            // available; that is build detail, not part of the version.
            int plus = informational.IndexOf('+');
            var version = (plus >= 0 ? informational[..plus] : informational).Trim();
            if (version.Length > 0)
                return version;
        }

        var name = assembly.GetName().Version;
        return name == null ? "0.0.0" : $"{name.Major}.{name.Minor}.{name.Build}";
    }
}
