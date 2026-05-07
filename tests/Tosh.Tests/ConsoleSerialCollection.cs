// SPDX-License-Identifier: MIT
//
// Test classes that compile a Tōsh program and capture Console.Out
// (or write to it via the redirection helpers) MUST share this xUnit
// collection. Console.Out is process-global state, so two tests that
// redirect or capture stdout in parallel will clobber each other —
// which is observable as empty stdout, "interleaved" stdout, or
// content from one test bleeding into another's redirected file.
using Xunit;

namespace Tosh.Tests;

[CollectionDefinition(Name)]
public sealed class ConsoleSerialCollection
{
    public const string Name = "ConsoleSerial";
}
