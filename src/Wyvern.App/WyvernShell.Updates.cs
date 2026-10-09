using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using Wyvern.App.Interop;
using Wyvern.Core.Settings;
using Wyvern.Core.Update;

namespace Wyvern.App;

// Updates: looking for a newer Wyvern on the owner's own update server, saying when there
// is one, and installing it when he asks. The server's address and the key an update has to
// be signed with are built into the program; a Wyvern built without them never looks.
// Everything that is said here is fixed words and version numbers.
public sealed partial class WyvernShell
{
    private UpdateService? _updates;
    private volatile UpdateMode _updateMode = UpdateMode.Ask;
    private DispatcherTimer? _updateTimer;

    // The newest version the server has offered in this run, and the one the owner was told of.
    private UpdateOffer? _updateOffer;
    private string _updateTold = "";

    // True while a look or a download is under way: one at a time.
    private bool _updateBusy;
    private bool _updateRefusalLogged;

    // Called once, at the end of startup, on the UI thread.
    private void StartUpdates()
    {
        var running = typeof(WyvernShell).Assembly.GetName().Version ?? new Version(0, 0, 0);
        string version = UpdateManifest.ThreeNumbers(running).ToString(3);
        if (UpdateChannel.Parse(BuiltInChannel()) is not { } channel)
        {
            _log.Info($"Version {version}. No update server is built into this Wyvern, so it never looks for updates");
            return;
        }

        if (!UpdateFiles.RunsAsInstalled())
        {
            _log.Info($"Version {version}. This copy does not run as an installed Wyvern, so it never looks for updates");
            return;
        }

        _updates = new UpdateService(channel, running, new UpdateFiles(_paths.UpdatesFolder), _paths.UpdatesFolder, Report);
        _updates.Tidy();
        _log.Info(_updateMode switch
        {
            UpdateMode.Off => $"Version {version}. Updates are switched off in settings.json, so Wyvern does not look for them by itself",
            UpdateMode.Auto => $"Version {version}. Updates are looked for on the update server built into this Wyvern, and installed as soon as one is found",
            _ => $"Version {version}. Updates are looked for on the update server built into this Wyvern",
        });

        Later(UpdateOptions.FirstLook, () => LookForUpdateByItself());
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background, _app.Dispatcher) { Interval = UpdateOptions.Between };
        _updateTimer.Tick += (_, _) => LookForUpdateByItself();
        _updateTimer.Start();
    }

    private void StopUpdates() => _updateTimer?.Stop();

    // The setting is read again at every Reload.
    private void ReadUpdateSetting() => _updateMode = UpdateOptions.From(SettingsDocument.Read(_paths.SettingsFile));

    private void LookForUpdateByItself()
    {
        if (_updateMode != UpdateMode.Off)
        {
            LookForUpdate(asked: false, thenInstall: false);
        }
    }

    // The Check for Updates command.
    private void CheckForUpdates() => LookForUpdate(asked: true, thenInstall: false);

    // asked: the owner ran a command for it, so he is told what came of it whatever it is.
    private void LookForUpdate(bool asked, bool thenInstall)
    {
        if (_closing)
        {
            return;
        }

        if (_updates is not { } updates)
        {
            if (asked)
            {
                _hud.Show("This Wyvern Has No Update Server");
            }

            return;
        }

        if (_updateBusy)
        {
            if (asked)
            {
                _hud.Show("Already Looking for an Update");
            }

            return;
        }

        // The server is asked away from this thread; what it said comes back to it.
        _updateBusy = true;
        Task.Run(updates.Check).ContinueWith(
            looked => _app.Dispatcher.BeginInvoke(() => Looked(looked.Result, asked, thenInstall)),
            TaskScheduler.Default);
    }

    private void Looked(UpdateCheck check, bool asked, bool thenInstall)
    {
        _updateBusy = false;
        if (_closing)
        {
            return;
        }

        switch (check.Verdict)
        {
            case UpdateVerdict.Newer when check.Offer is { } offer:
                _updateOffer = offer;
                _tray.ShowUpdate(offer.VersionText, Safely("Update Wyvern", InstallUpdate));
                bool installNow = thenInstall || _updateMode == UpdateMode.Auto;
                if (_updateTold != offer.VersionText)
                {
                    _updateTold = offer.VersionText;
                    _log.Info($"An update was found: version {offer.VersionText}");
                    if (!installNow && !asked)
                    {
                        Notify($"Wyvern {offer.VersionText} is available. Run Update Wyvern in the launcher, or choose it in the tray icon's menu.");
                    }
                }

                if (installNow)
                {
                    InstallUpdate();
                }
                else if (asked)
                {
                    _hud.Show($"Wyvern {offer.VersionText} Is Available: Run Update Wyvern");
                }

                break;
            case UpdateVerdict.UpToDate:
                if (asked)
                {
                    _hud.Show("Wyvern Is Up to Date");
                }

                break;
            case UpdateVerdict.Unreachable:
                if (asked)
                {
                    _hud.Show("The Update Server Could Not Be Reached");
                }

                break;
            default:
                // Said once for each run: it stays so until what is on the server is put right.
                if (!_updateRefusalLogged)
                {
                    _updateRefusalLogged = true;
                    _log.Error(check.Verdict == UpdateVerdict.NotSigned
                        ? "What the update server offers is not signed with the key built into this Wyvern, so nothing of it is used"
                        : "What the update server offers is signed and still cannot be read as an update, so nothing of it is used");
                }

                if (asked)
                {
                    _hud.Show("The Update Could Not Be Trusted");
                }

                break;
        }
    }

    // The Update Wyvern command, the tray menu's "Update to", and what an update that
    // installs itself does: fetch the setup program, check it, start it, and close.
    private void InstallUpdate()
    {
        if (_closing)
        {
            return;
        }

        if (_updates is not { } updates)
        {
            _hud.Show("This Wyvern Has No Update Server");
            return;
        }

        if (_updateBusy)
        {
            _hud.Show("Already Working on the Update");
            return;
        }

        if (_updateOffer is not { } offer)
        {
            // Nothing is known of yet: the server is asked first.
            LookForUpdate(asked: true, thenInstall: true);
            return;
        }

        _updateBusy = true;
        _hud.Show($"Downloading Wyvern {offer.VersionText}");
        Task.Run(() =>
        {
            try
            {
                return updates.Download(offer);
            }
            catch (Exception ex)
            {
                Report("An update could not be fetched", ex);
                return null;
            }
        }).ContinueWith(
            fetched => _app.Dispatcher.BeginInvoke(() => Fetched(offer, fetched.Result)),
            TaskScheduler.Default);
    }

    private void Fetched(UpdateOffer offer, string? setup)
    {
        _updateBusy = false;
        if (_closing)
        {
            return;
        }

        // Looked at once more right before it is run: only the file that was signed is started.
        if (setup is null || !UpdateManifest.FileMatches(setup, offer))
        {
            _log.Error($"The update to version {offer.VersionText} could not be fetched from the update server");
            Notify($"Wyvern {offer.VersionText} could not be downloaded from your update server. Wyvern was not changed.");
            return;
        }

        try
        {
            UpdateFiles.StartSetup(setup);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log.Error("The setup program of an update could not be started", ex);
            Notify("The update could not be started. Wyvern was not changed. Details are in the log file.");
            return;
        }

        // The setup waits for this Wyvern to have quit, replaces it, and starts the new one.
        _log.Info($"The setup program of version {offer.VersionText} was started, and this Wyvern closes for it");
        Quit();
    }

    // The address and the key, as the build put them into the program, or null when it put none.
    private static string? BuiltInChannel()
    {
        using var stream = typeof(WyvernShell).Assembly.GetManifestResourceStream("update-channel.json");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
