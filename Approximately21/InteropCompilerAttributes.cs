using System;

namespace System.Runtime.CompilerServices;

// The game interop assemblies expose IL2CPP versions without the constructors the C# compiler needs.
// Keep managed nullable metadata local to the plugin instead of binding to those proxy types.
[AttributeUsage(AttributeTargets.All, Inherited = false)]
internal sealed class NullableAttribute : Attribute
{
    public NullableAttribute(byte flag) => NullableFlags = new[] { flag };
    public NullableAttribute(byte[] flags) => NullableFlags = flags;
    public readonly byte[] NullableFlags;
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method |
                AttributeTargets.Interface | AttributeTargets.Delegate, Inherited = false)]
internal sealed class NullableContextAttribute : Attribute
{
    public NullableContextAttribute(byte flag) => Flag = flag;
    public readonly byte Flag;
}