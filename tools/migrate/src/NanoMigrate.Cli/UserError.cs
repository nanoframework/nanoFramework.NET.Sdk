namespace NanoFramework.Migrate.Cli;

/// <summary>A user-facing error that prints cleanly without a stack trace.</summary>
internal sealed class UserError(string message) : Exception(message);
