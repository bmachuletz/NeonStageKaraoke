// Unity's .NET Standard 2.1 reference assemblies do not provide this compiler
// marker, although the bundled Roslyn compiler supports init-only properties.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
