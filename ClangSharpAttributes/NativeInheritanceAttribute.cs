using System.Diagnostics;

namespace ClangSharp
{
  /// <summary>Defines the base type of a struct as it was in the native signature.</summary>
  [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
  [Conditional("DEBUG")]
  internal sealed partial class NativeInheritanceAttribute : Attribute
  {

    /// <summary>Initializes a new instance of the <see cref="NativeInheritanceAttribute" /> class.</summary>
    /// <param name="name">The name of the base type that was inherited from in the native signature.</param>
    public NativeInheritanceAttribute(string name)
    {
      Name = name;
    }

    /// <summary>Gets the name of the base type that was inherited from in the native signature.</summary>
    public string Name { get; }
  }
}
