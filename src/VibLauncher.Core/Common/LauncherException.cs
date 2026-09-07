namespace VibLauncher.Core.Common;

/// <summary>
/// An error that the launcher knows how to explain to a person.
/// </summary>
/// <remarks>
/// Every failure surfaced in the UI goes through this type so the presentation
/// layer never has to guess at wording. <see cref="Message"/> says what failed,
/// <see cref="Remedy"/> says what the user can do about it, and the inner
/// exception carries the technical detail that belongs only in the log.
/// </remarks>
public class LauncherException : Exception
{
    public LauncherException(string message, string? remedy = null, Exception? inner = null)
        : base(message, inner)
    {
        Remedy = remedy;
    }

    /// <summary>A concrete next step, or <c>null</c> when there is nothing useful to suggest.</summary>
    public string? Remedy { get; }

    /// <summary>The message and its remedy joined for display in a dialog or banner.</summary>
    public string DisplayText =>
        string.IsNullOrWhiteSpace(Remedy) ? Message : Message + Environment.NewLine + Environment.NewLine + Remedy;
}

/// <summary>The instance, server or account being acted on is not in a usable state.</summary>
public sealed class InvalidConfigurationException : LauncherException
{
    public InvalidConfigurationException(string message, string? remedy = null, Exception? inner = null)
        : base(message, remedy, inner)
    {
    }
}

/// <summary>A required Java runtime is missing or is the wrong major version.</summary>
public sealed class JavaNotFoundException : LauncherException
{
    public JavaNotFoundException(string message, string? remedy = null, Exception? inner = null)
        : base(message, remedy, inner)
    {
    }
}

/// <summary>A download failed, or completed but did not match its published hash.</summary>
public sealed class DownloadFailedException : LauncherException
{
    public DownloadFailedException(string message, string? remedy = null, Exception? inner = null)
        : base(message, remedy, inner)
    {
    }
}

/// <summary>A mod cannot be installed into the instance it was requested for.</summary>
public sealed class ModCompatibilityException : LauncherException
{
    public ModCompatibilityException(string message, string? remedy = null, Exception? inner = null)
        : base(message, remedy, inner)
    {
    }
}
