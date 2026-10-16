using System;

namespace LiveClash.Core
{
    /// <summary>
    /// Marks a member that needs a live Revit process to run: it reads an Element, a Document or
    /// an ElementId-typed value. The headless coverage run (ci\coverage.ps1) leaves these out of its
    /// number, because no build machine can execute them; they are verified inside Revit by the
    /// in-Revit checks instead.
    ///
    /// Keep what carries it SMALL: a thin wrapper that reads from Revit and hands plain values to a
    /// Revit-free method, which is then tested headless. A large member with this attribute is
    /// logic hiding from the coverage gate.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Constructor
                    | AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
    public sealed class RevitBoundAttribute : Attribute
    {
    }
}
