using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Overrank")]
[assembly: AssemblyDescription("Overcooked! 2 community leaderboards")]
[assembly: AssemblyProduct("Overrank")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion(Overrank.BuildInfo.AssemblyVersion)]
[assembly: AssemblyFileVersion(Overrank.BuildInfo.AssemblyVersion)]
[assembly: AssemblyInformationalVersion(Overrank.BuildInfo.Version)]

namespace Overrank
{
    internal static class BuildInfo
    {
        internal const string Version = "0.3.0";
        internal const string AssemblyVersion = Version + ".0";
    }
}
