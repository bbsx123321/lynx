using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;

namespace PadShift.Output.VirtualPad;

/// <summary>
/// Common ViGEm plumbing shared by <see cref="Xbox360PadSink"/> and <see cref="Ds4PadSink"/>:
/// client ownership, exception translation and client lifetime.
/// </summary>
/// <remarks>
/// The constructor performs the only throwing operation in the whole output layer. If ViGEmBus is
/// missing the caller gets a single, actionable <see cref="ViGEmNotInstalledException"/> and can
/// decide whether to fall back to keyboard/mouse-only output, retry after install, or exit.
/// </remarks>
public abstract class ViGEmPadSink : VirtualPadSink
{
    private readonly ViGEmClient _client;
    private readonly bool _ownsClient;
    private bool _clientDisposed;

    protected ViGEmPadSink(ViGEmClient? sharedClient)
    {
        ViGEmClient client;
        bool owns;

        if (sharedClient is null)
        {
            try
            {
                client = new ViGEmClient();
            }
            catch (VigemBusNotFoundException ex)
            {
                throw new ViGEmNotInstalledException(ViGEmNotInstalledException.DefaultMessage, ex);
            }
            catch (VigemBusVersionMismatchException ex)
            {
                throw new ViGEmNotInstalledException(
                    "The installed ViGEmBus driver is older than the client library expects. " +
                    "Install ViGEmBus 1.22 or newer from https://github.com/ViGEm/ViGEmBus/releases.",
                    ex);
            }
            catch (VigemBusAccessFailedException ex)
            {
                throw new VirtualPadException(
                    "ViGEmBus is installed but could not be opened. Check that the ViGEmBus service is " +
                    "running and that PadShift runs at the same integrity level as the driver.",
                    ex);
            }
            catch (DllNotFoundException ex)
            {
                throw new ViGEmNotInstalledException(
                    "The native ViGEm client library could not be loaded. Reinstall ViGEmBus 1.22 or newer.",
                    ex);
            }
            catch (BadImageFormatException ex)
            {
                throw new ViGEmNotInstalledException(
                    "The native ViGEm client library has the wrong architecture for this process.",
                    ex);
            }
            catch (Exception ex)
            {
                throw new VirtualPadException(
                    $"The ViGEm client could not be initialised: {ex.Message}",
                    ex);
            }

            owns = true;
        }
        else
        {
            client = sharedClient;
            owns = false;
        }

        _client = client;
        _ownsClient = owns;
    }

    /// <summary>The underlying ViGEm client (owned or shared).</summary>
    protected ViGEmClient Client => _client;

    /// <summary>
    /// Probes whether ViGEmBus is reachable, without creating a pad. Useful for UI/diagnostics.
    /// Never throws.
    /// </summary>
    public static bool TryProbeBus(out string? error)
    {
        error = null;

        if (!OperatingSystem.IsWindows())
        {
            error = "ViGEmBus is only available on Windows.";
            return false;
        }

        try
        {
            using ViGEmClient probe = new();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Connects a target, translating the ViGEm error codes into actionable exceptions.</summary>
    protected void ConnectTarget(IVirtualGamepad target)
    {
        try
        {
            target.Connect();
        }
        catch (VigemBusNotFoundException ex)
        {
            throw new ViGEmNotInstalledException(ViGEmNotInstalledException.DefaultMessage, ex);
        }
        catch (VigemBusVersionMismatchException ex)
        {
            throw new ViGEmNotInstalledException(
                "The installed ViGEmBus driver is older than the client library expects. " +
                "Install ViGEmBus 1.22 or newer from https://github.com/ViGEm/ViGEmBus/releases.",
                ex);
        }
        catch (VigemBusAccessFailedException ex)
        {
            throw new VirtualPadException(
                "ViGEmBus is installed but the emulated pad could not be plugged in. Check the " +
                "ViGEmBus service and try again.",
                ex);
        }
        catch (DllNotFoundException ex)
        {
            throw new ViGEmNotInstalledException(
                "The native ViGEm client library could not be loaded. Reinstall ViGEmBus 1.22 or newer.",
                ex);
        }
        catch (BadImageFormatException ex)
        {
            throw new ViGEmNotInstalledException(
                "The native ViGEm client library has the wrong architecture for this process.",
                ex);
        }
        catch (Exception ex)
        {
            throw new VirtualPadException(
                $"{BackendName} virtual pad could not be connected: {ex.Message}",
                ex);
        }
    }

    /// <summary>Releases the owned client exactly once (safe to call from a failed constructor).</summary>
    protected void DisposeClient()
    {
        if (!_ownsClient || _clientDisposed)
        {
            return;
        }

        _clientDisposed = true;
        try
        {
            _client.Dispose();
        }
        catch
        {
            // Best effort: the process is shutting down or the driver is already gone.
        }
    }

    /// <inheritdoc />
    protected sealed override void DisconnectCore()
    {
        try
        {
            DisconnectTarget();
        }
        finally
        {
            DisposeClient();
        }
    }

    /// <summary>Unplugs and unsubscribes the concrete pad. Exceptions are swallowed by the base sink.</summary>
    protected abstract void DisconnectTarget();
}
