using System;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace EmoteCommander;

public sealed class Plugin : IDalamudPlugin
{
    private const string DebugCommand = "/ecdebug";

    private readonly IPluginLog _log;
    private readonly ICommandManager _commands;
    private readonly IChatGui _chat;

    private readonly PenumbraBridge _penumbra;
    private readonly EmoteCatalogue _emotes;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        ICommandManager commands,
        IChatGui chat,
        IDataManager data)
    {
        _log = log;
        _commands = commands;
        _chat = chat;

        _emotes = new EmoteCatalogue(data, log);
        _penumbra = new PenumbraBridge(pluginInterface, log);

        _commands.AddHandler(DebugCommand, new CommandInfo(OnDebug)
        {
            HelpMessage = "Emote Commander: report what the plugin can see. "
                        + "Add 'redraw' to test redraw completion.",
            ShowInHelp = true,
        });

        _log.Information("Emote Commander loaded.");
    }

    private void OnDebug(string command, string args)
    {
        var arg = args.Trim().ToLowerInvariant();

        if (arg == "redraw")
        {
            _ = TestRedrawAsync();
            return;
        }

        _chat.Print($"[EC] Penumbra: {(_penumbra.Available ? "connected" : "UNAVAILABLE - " + _penumbra.Unavailable)}");

        if (_penumbra.Available)
        {
            var mods = _penumbra.ModList();
            _chat.Print($"[EC] Mods: {mods.Count}");
            _chat.Print($"[EC] Mod root: {_penumbra.ModDirectoryRoot()}");

            var collection = _penumbra.PlayerCollection();
            _chat.Print($"[EC] Player collection: {(collection?.ToString() ?? "NONE")}");

            // Which of your mods replace a body emote animation? This is the
            // auto-detection the UI will use, exercised for real.
            var resolved = 0;
            foreach (var (dir, name) in mods.Take(400))
            {
                var paths = _penumbra.ModFilePaths(dir);
                var emote = _emotes.FromRedirectedPaths(paths);
                if (emote is null) continue;

                resolved++;
                if (resolved <= 8)
                    _chat.Print($"[EC]   {name}  ->  {emote.TextCommand} ({emote.Name})");
            }
            _chat.Print($"[EC] Mods that map to an emote: {resolved}");
        }

        _chat.Print($"[EC] Emotes: {_emotes.All.Count} performable (pose-family excluded)");
        var sample = _emotes.ByTimelineKey("emote/loop_emot24_loop");
        _chat.Print($"[EC] loop_emot24_loop -> {(sample is null ? "NOT FOUND" : sample.TextCommand + " / " + sample.Name)}");
    }

    private async Task TestRedrawAsync()
    {
        _chat.Print("[EC] redrawing...");
        var started = Environment.TickCount64;
        var ok = await _penumbra.RedrawAndAwaitAsync().ConfigureAwait(false);
        var elapsed = Environment.TickCount64 - started;
        _chat.Print(ok
            ? $"[EC] redraw completed in {elapsed} ms"
            : $"[EC] redraw did NOT signal completion (waited {elapsed} ms)");
    }

    public void Dispose()
    {
        _commands.RemoveHandler(DebugCommand);
        _penumbra.Dispose();
        _log.Information("Emote Commander unloaded.");
    }
}
