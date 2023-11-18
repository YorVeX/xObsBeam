// SPDX-FileCopyrightText: © 2023 YorVeX, https://github.com/YorVeX
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace ClangSharp
{
  /// <summary>Defines the vtbl index of a method as it was in the native signature.</summary>
  /// <remarks>Initializes a new instance of the <see cref="VtblIndexAttribute" /> class.</remarks>
  /// <param name="index">The vtbl index of a method as it was in the native signature.</param>
  [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
  [Conditional("DEBUG")]
  internal sealed partial class VtblIndexAttribute(uint index) : Attribute
  {

    /// <summary>Gets the vtbl index of a method as it was in the native signature.</summary>
    public uint Index { get; } = index;
  }
}
