using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace FarmAssistant;

/// <summary>
/// The subset of Generic Mod Config Menu's API used here (signatures copied from GMCM;
/// SMAPI maps this interface onto the real one when GMCM is installed).
/// </summary>
public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);

    void AddSectionTitle(IManifest mod, Func<string> text, Func<string>? tooltip = null);

    void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name,
                       Func<string>? tooltip = null, string? fieldId = null);

    void AddKeybindList(IManifest mod, Func<KeybindList> getValue, Action<KeybindList> setValue, Func<string> name,
                        Func<string>? tooltip = null, string? fieldId = null);
}
