using System;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EmoteCommander.Ui;

namespace EmoteCommander;

public sealed class Plugin : IDalamudPlugin
{
    private const string MainCommand = "/emotecommander";
    private const string ShortCommand = "/ec";
    private const string DebugCommand = "/ecdebug";

    /// <summary>Where /ecdebug importfile looks when given no path.</summary>
    private static string DefaultImportPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncher", "pluginConfigs", "EmoteCommander.import.txt");

    private readonly IPluginLog _log;
    private readonly ICommandManager _commands;
    private readonly IChatGui _chat;
    private readonly IDalamudPluginInterface _pi;

    private readonly Config _config;
    private readonly PenumbraBridge _penumbra;
    private readonly EmoteCatalogue _emotes;
    private readonly EmotePlayer _player;
    private readonly CommandRunner _runner;
    private readonly PresetImporter _importer;

    private readonly WindowSystem _windows = new("EmoteCommander");
    private readonly ConfigWindow _configWindow;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        ICommandManager commands,
        IChatGui chat,
        IDataManager data,
        IFramework framework,
        IObjectTable objects)
    {
        _pi = pluginInterface;
        _log = log;
        _commands = commands;
        _chat = chat;

        _config = _pi.GetPluginConfig() as Config ?? new Config();
        _config.Initialise(_pi);

        _emotes = new EmoteCatalogue(data, log);
        _penumbra = new PenumbraBridge(_pi, log, objects);
        _player = new EmotePlayer(log);
        _runner = new CommandRunner(_config, _penumbra, _emotes, _player, commands, chat,
                                    log, framework);

        _importer = new PresetImporter(_penumbra, _config, _emotes);
        _configWindow = new ConfigWindow(_config, _penumbra, _emotes, _runner, log, _importer);
        _windows.AddWindow(_configWindow);

        _pi.UiBuilder.Draw += _windows.Draw;
        _pi.UiBuilder.OpenConfigUi += ToggleWindow;
        _pi.UiBuilder.OpenMainUi += ToggleWindow;

        _commands.AddHandler(MainCommand, new CommandInfo((_, _) => ToggleWindow())
        {
            HelpMessage = "Open Emote Commander.",
            ShowInHelp = true,
        });
        _commands.AddHandler(ShortCommand, new CommandInfo((_, _) => ToggleWindow())
        {
            HelpMessage = "Open Emote Commander.",
            ShowInHelp = false,
        });
        _commands.AddHandler(DebugCommand, new CommandInfo(OnDebug)
        {
            HelpMessage = "Emote Commander: report what the plugin can see. "
                        + "'redraw' tests redraw completion.",
            ShowInHelp = false,
        });

        _runner.RegisterAll();

        if (!_penumbra.Available)
            _chat.PrintError("[EC] Penumbra was not found. Emote Commander needs it to work.");

        _log.Information("Emote Commander loaded.");
    }

    private void ToggleWindow() => _configWindow.Toggle();

    private void OnDebug(string command, string args)
    {
        var trimmed = args.Trim();

        if (trimmed.Equals("redraw", StringComparison.OrdinalIgnoreCase))
        {
            _ = TestRedrawAsync();
            return;
        }

        if (trimmed.StartsWith("importfile", StringComparison.OrdinalIgnoreCase))
        {
            // Reading from a file avoids two failure modes at once: chat
            // mangling a long code, and a human retyping one.
            var path = trimmed.Length > 10 ? trimmed[10..].Trim() : DefaultImportPath;
            try
            {
                ImportShareCode(System.IO.File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                _chat.PrintError($"[EC] Could not read {path}: {ex.Message}");
            }
            return;
        }

        if (trimmed.StartsWith("import ", StringComparison.OrdinalIgnoreCase))
        {
            ImportShareCode(trimmed[7..].Trim());
            return;
        }

        if (trimmed.StartsWith("export", StringComparison.OrdinalIgnoreCase))
        {
            if (_config.Presets.Count == 0)
                _chat.Print("[EC] No commands to export.");
            else
                _log.Information("Share code:\n" + ShareCode.Encode(_config.Presets));
            _chat.Print("[EC] Share code written to /xllog.");
            return;
        }

        _chat.Print($"[EC] Penumbra: {(_penumbra.Available ? "connected" : "UNAVAILABLE - " + _penumbra.Unavailable)}");
        if (_penumbra.Available)
        {
            var mods = _penumbra.ModList();
            _chat.Print($"[EC] Mods: {mods.Count}, collection {_penumbra.PlayerCollection()?.ToString() ?? "NONE"}");
            var mapped = mods.Count(m => _emotes.FromRedirectedPaths(_penumbra.ModFilePaths(m.Key)) is not null);
            _chat.Print($"[EC] Mods that map to an emote: {mapped}");
        }
        _chat.Print($"[EC] Emotes: {_emotes.All.Count} performable");
        _chat.Print($"[EC] Saved commands: {_config.Presets.Count}");
    }

    /// <summary>Chat-side wrapper around the shared import on CommandRunner.</summary>
    private void ImportShareCode(string code)
    {
        try
        {
            var result = _runner.ImportCode(code);
            foreach (var line in result.Messages)
                _chat.Print("[EC] " + line);
            _chat.Print($"[EC] Imported {result.Added} of {result.Total} command(s).");
        }
        catch (FormatException ex)
        {
            _chat.PrintError($"[EC] {ex.Message}");
        }
    }

    private async Task TestRedrawAsync()
    {
        // No try/catch here meant any throw became an unobserved task
        // exception: no chat line, no log entry. The worst possible behaviour
        // for the command whose whole job is answering "is redraw working".
        try
        {
            var started = Environment.TickCount64;
            var ok = await _penumbra.RedrawAndAwaitAsync(_config.RedrawTimeoutMs)
                                    .ConfigureAwait(false);
            var elapsed = Environment.TickCount64 - started;
            _chat.Print(ok ? $"[EC] redraw completed in {elapsed} ms"
                           : $"[EC] redraw did NOT signal completion (waited {elapsed} ms)");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "redraw test failed");
            _chat.PrintError($"[EC] redraw test threw: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _pi.UiBuilder.Draw -= _windows.Draw;
        _pi.UiBuilder.OpenConfigUi -= ToggleWindow;
        _pi.UiBuilder.OpenMainUi -= ToggleWindow;
        _windows.RemoveAllWindows();
        _configWindow.Dispose();

        _runner.Dispose();
        _penumbra.Dispose();

        _commands.RemoveHandler(MainCommand);
        _commands.RemoveHandler(ShortCommand);
        _commands.RemoveHandler(DebugCommand);

        _log.Information("Emote Commander unloaded.");
    }
}
