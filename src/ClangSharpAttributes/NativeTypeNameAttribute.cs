// SPDX-FileCopyrightText: © 2023-2024 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace ClangSharp
{
  /// <summary>Defines the type of a member as it was used in the native signature.</summary>
  /// <remarks>Initializes a new instance of the <see cref="NativeTypeNameAttribute" /> class.</remarks>
  /// <param name="name">The name of the type that was used in the native signature.</param>
  [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = false, Inherited = true)]
  [Conditional("DEBUG")]
  internal sealed partial class NativeTypeNameAttribute(string name) : Attribute
  {

    /// <summary>Gets the name of the type that was used in the native signature.</summary>
    public string Name { get; } = name;
  }
}
