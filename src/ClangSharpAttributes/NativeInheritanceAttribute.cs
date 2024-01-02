// SPDX-FileCopyrightText: © 2023-2024 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace ClangSharp
{
  /// <summary>Defines the base type of a struct as it was in the native signature.</summary>
  /// <remarks>Initializes a new instance of the <see cref="NativeInheritanceAttribute" /> class.</remarks>
  /// <param name="name">The name of the base type that was inherited from in the native signature.</param>
  [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
  [Conditional("DEBUG")]
  internal sealed partial class NativeInheritanceAttribute(string name) : Attribute
  {

    /// <summary>Gets the name of the base type that was inherited from in the native signature.</summary>
    public string Name { get; } = name;
  }
}
