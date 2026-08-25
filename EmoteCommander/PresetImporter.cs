using System;
using System.Collections.Generic;
using System.Linq;

namespace EmoteCommander;

/// <summary>
/// One preset found embedded in a mod's Penumbra description.
/// </summary>
/// <param name="Preset">The decoded preset. Already sanitised by ShareCode.</param>
/// <param name="SourceModDirectory">The mod whose description carried the code.</param>
/// <param name="SourceModName">That mod's real name, from Penumbra.</param>
/// <param name="Problem">
/// Why this cannot be added as-is, or null. Presets with a problem are still
/// returned so the UI can explain rather than silently dropping them - a
/// command that quietly fails to appear is worse than one that says why.
/// </param>
public sealed record DiscoveredPreset(
    Preset Preset,
    string SourceModDirectory,
    string SourceModName,
    string? Problem)
{
    public bool CanAdd => Problem is null;
}

/// <summary>
/// Finds share codes embedded in installed mods' descriptions.
///
/// This is what makes a mod configure itself: an author pastes a code into the
/// mod's Penumbra description, and anyone who installs the mod is offered the
/// commands rather than having to build them by hand. The description is used
/// because Penumbra owns that field, so it survives .pmp packing and install
/// with certainty, and it can be added to mods that already shipped.
///
/// Nothing here registers anything. It proposes; the user accepts.
/// </summary>
public sealed class PresetImporter
{
    private readonly PenumbraBridge _penumbra;
    private readonly Config _config;
    private readonly EmoteCatalogue _emotes;

    public PresetImporter(PenumbraBridge penumbra, Config config, EmoteCatalogue emotes)
    {
        _penumbra = penumbra;
        _config = config;
        _emotes = emotes;
    }

    /// <summary>
    /// Every preset offered by an installed mod's description.
    ///
    /// Never throws: this runs across the whole mod list, and one mod with a
    /// mangled code must not stop the rest being found.
    /// </summary>
    public IReadOnlyList<DiscoveredPreset> Scan()
    {
        var found = new List<DiscoveredPreset>();
        if (!_penumbra.Available)
            return found;

        foreach (var (modDir, modName) in _penumbra.ModList())
        {
            var description = _penumbra.ModDescription(modDir);
            if (description.Length == 0)
                continue;

            foreach (var code in ShareCode.ExtractFromText(description))
            {
                IReadOnlyList<Preset> presets;
                try
                {
                    presets = ShareCode.Decode(code);
                }
                catch (FormatException ex)
                {
                    found.Add(new DiscoveredPreset(
                        new Preset { ModName = modName, ModDirectory = modDir },
                        modDir, modName,
                        $"the code in this mod's description could not be read: {ex.Message}"));
                    continue;
                }

                foreach (var preset in presets)
                    found.Add(Describe(preset, modDir, modName));
            }
        }
        return found;
    }

    private DiscoveredPreset Describe(Preset preset, string sourceDir, string sourceName)
    {
        // A code found in mod A that points at mod B is not necessarily
        // malicious - a pack may configure a companion mod - but the user
        // should be told, since the name shown comes from the code itself.
        if (!string.Equals(preset.ModDirectory, sourceDir, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(preset.ModDirectory))
                return new DiscoveredPreset(preset, sourceDir, sourceName,
                    "the code does not say which mod it applies to.");

            if (!_penumbra.ModList().TryGetValue(preset.ModDirectory, out var otherName))
                return new DiscoveredPreset(preset, sourceDir, sourceName,
                    $"it applies to '{preset.ModName}', which is not installed.");

            preset.ModName = otherName;   // trust Penumbra, not the code
        }
        else
        {
            preset.ModName = sourceName;
        }

        var emote = _emotes.ByRowId(preset.EmoteRowId);
        if (emote is null)
            return new DiscoveredPreset(preset, sourceDir, sourceName,
                "its emote is not one this game version can perform.");

        // Belt and braces: the catalogue already excludes these, so a
        // pose-family emote here means the code was built elsewhere.
        if (EmoteResolver.IsPoseFamily(emote.TimelineKey))
            return new DiscoveredPreset(preset, sourceDir, sourceName,
                $"{emote.TextCommand} is a pose emote, which cannot be driven by a command.");

        try
        {
            Preset.Normalise(preset.Command);
        }
        catch (ArgumentException)
        {
            return new DiscoveredPreset(preset, sourceDir, sourceName,
                "it has no usable command name.");
        }

        // Already have this exact command bound to this exact mod? Then it is
        // not news, and offering it again every scan would be noise.
        var existing = _config.ByCommand(preset.Command);
        if (existing is not null
            && string.Equals(existing.ModDirectory, preset.ModDirectory,
                             StringComparison.OrdinalIgnoreCase))
        {
            return new DiscoveredPreset(preset, sourceDir, sourceName,
                "already added.");
        }

        if (existing is not null)
            return new DiscoveredPreset(preset, sourceDir, sourceName,
                $"{existing.SlashCommand} is already used by '{existing.ModName}'. "
                + "Rename it before adding.");

        return new DiscoveredPreset(preset, sourceDir, sourceName, null);
    }
}
