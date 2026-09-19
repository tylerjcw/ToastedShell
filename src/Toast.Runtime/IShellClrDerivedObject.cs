namespace Tosh.Runtime;

/// <summary>
/// Implemented by a shell object that extends a CLR type and holds an instance of it.
/// </summary>
/// <remarks>
/// <para>
/// A tōsh class written <c>extends System.Uri</c> is not a CLR <c>Uri</c>. It keeps one
/// inside itself and forwards to it, which is enough to read <c>$u.Host</c> and to override
/// a member, but not enough to hand <c>$u</c> to anything that asks for a <c>Uri</c> — every
/// such call failed overload resolution, because nothing at the boundary knew the base was
/// in there.
/// </para>
/// <para>
/// This is what tells it. The interface lives here, below the language, for the same reason
/// <see cref="IShellTypeCheckable"/> does: conversion is part of the portable runtime and
/// cannot reference <c>ToshClassInstance</c>.
/// </para>
/// <para>
/// Handing the contained instance across is not the whole of "a real subclass" — the tōsh
/// identity does not survive the trip, so a CLR caller holding it sees the base's own
/// members and not the overrides. What it does buy is that the call happens at all, which
/// is the difference between an object that can be used with the platform and one that
/// cannot.
/// </para>
/// </remarks>
public interface IShellClrDerivedObject
{
    /// <summary>The CLR type this object extends, or null where it extends none.</summary>
    /// <remarks>
    /// Recorded on whichever class in the chain named it, which need not be this object's
    /// own: for <c>class E2 extends E1</c> over <c>class E1 extends Error</c> the base
    /// belongs to <c>E1</c>. Implementations walk the chain.
    /// </remarks>
    Type? ClrBaseType { get; }

    /// <summary>
    /// The instance of <see cref="ClrBaseType"/> this object holds, once constructed.
    /// </summary>
    /// <remarks>
    /// Null while the base constructor has not run yet, which a conversion must treat as
    /// "not convertible" rather than passing null to a parameter that cannot take it.
    /// </remarks>
    object? ClrBaseInstance { get; }
}
