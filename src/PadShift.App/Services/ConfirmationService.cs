using PadShift.App.Infrastructure;

namespace PadShift.App.Services;

/// <summary>Stable keys for the "不再询问" preference dictionary.</summary>
public static class ConfirmationKeys
{
    public const string DeleteProfile = "delete-profile";
    public const string ImportDuplicate = "import-duplicate";
    public const string DisableRemap = "disable-remap";
    public const string ExitWhileRemapping = "exit-while-remapping";

    /// <summary>Keys shown in the 确认 preferences tab, with their Chinese captions.</summary>
    public static IReadOnlyList<(string Key, string Caption)> All =>
    [
        (DeleteProfile, Loc.T("conf.delete")),
        (ImportDuplicate, Loc.T("conf.importdup")),
        (DisableRemap, Loc.T("conf.disable")),
        (ExitWhileRemapping, Loc.T("conf.exit")),
    ];

    public static string CaptionOf(string key)
    {
        foreach ((string candidate, string caption) in All)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                return caption;
            }
        }

        return key;
    }
}

/// <summary>One confirmation question.</summary>
public sealed record ConfirmationRequest(string Key, string Title, string Message, string ActionText);

/// <summary>The user's answer: whether to proceed and whether future questions are suppressed.</summary>
public sealed record ConfirmationAnswer(bool Confirmed, bool DoNotAskAgain);

/// <summary>Injectable prompt so <see cref="ConfirmationService"/> stays testable without a window.</summary>
public interface IConfirmationPrompt
{
    ConfirmationAnswer Ask(ConfirmationRequest request);
}

/// <summary>Adapter that turns a delegate into an <see cref="IConfirmationPrompt"/>.</summary>
public sealed class DelegateConfirmationPrompt : IConfirmationPrompt
{
    private readonly Func<ConfirmationRequest, ConfirmationAnswer> _ask;

    public DelegateConfirmationPrompt(Func<ConfirmationRequest, ConfirmationAnswer> ask)
    {
        _ask = ask ?? throw new ArgumentNullException(nameof(ask));
    }

    public ConfirmationAnswer Ask(ConfirmationRequest request) => _ask(request);
}

/// <summary>
/// Reusable confirmation gate. A <c>true</c> entry in <see cref="AppSettings.Confirmations"/> means
/// "不再询问": the question is skipped and the safe default (proceed) is taken. Suppression is
/// persisted to the settings file immediately so it survives a crash.
/// </summary>
public sealed class ConfirmationService
{
    private readonly AppSettings _settings;
    private readonly string? _settingsPath;
    private readonly IConfirmationPrompt _prompt;

    public ConfirmationService(AppSettings settings, IConfirmationPrompt prompt, string? settingsPath = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _settingsPath = settingsPath;
        _settings.Normalize();
    }

    /// <summary>True when the key is suppressed and the prompt would be skipped.</summary>
    public bool IsSuppressed(string key) =>
        !string.IsNullOrWhiteSpace(key) &&
        _settings.Confirmations.TryGetValue(key, out bool suppressed) && suppressed;

    /// <summary>Asks unless suppressed. Failure to persist a suppression never blocks the action.</summary>
    public bool Confirm(string key, string title, string message, string? actionText = null)
    {
        if (IsSuppressed(key))
        {
            return true;
        }

        ConfirmationAnswer answer = _prompt.Ask(new ConfirmationRequest(
            key,
            title,
            message,
            string.IsNullOrWhiteSpace(actionText) ? "确定" : actionText!));

        if (answer.Confirmed && answer.DoNotAskAgain)
        {
            _settings.Confirmations[key] = true;
            _settings.Save(_settingsPath);
        }

        return answer.Confirmed;
    }

    /// <summary>Re-enables one prompt (used by the preferences tab).</summary>
    public void Reset(string key)
    {
        if (_settings.Confirmations.Remove(key))
        {
            _settings.Save(_settingsPath);
        }
    }

    public void ResetAll()
    {
        if (_settings.Confirmations.Count == 0)
        {
            return;
        }

        _settings.Confirmations.Clear();
        _settings.Save(_settingsPath);
    }

    /// <summary>Persists a change made through the settings object (preferences window).</summary>
    public void Persist() => _settings.Save(_settingsPath);
}
