using System;
namespace Tosh.Cli
{
    public class ReplInterruptException : Exception
    {
        public ReplInterruptException() : base("REPL interrupted by Ctrl+C") { }
    }
}