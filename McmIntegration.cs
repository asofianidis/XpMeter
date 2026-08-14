using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP.Configuration;
using SpiritVale.ModConfig;

namespace NevarisXpMeter;

internal static class McmIntegration
{
    internal static void Register()
    {
        ModPage page = ModConfigMenu.Register(
            Plugin.Guid,
            Plugin.Name,
            "Track base and job XP by monster without leaving the game.",
            Plugin.Version);

        page.AddHeader("GENERAL", "Changes save immediately.");
        page.BindToggle(Plugin.Settings.StartVisible, "Show on world entry",
            "Show the meter when your character enters the world.");
        page.BindToggle(Plugin.Settings.ResetOnWorldEntry, "Reset on world entry",
            "Start a fresh session on world entry. Disable this to keep totals across map changes.");

        page.AddHeader("SESSION");
        page.BindFloatSlider(Plugin.Settings.NewSessionAfter, "New session delay",
            0f, 600f, 5f, "0s",
            "Start a new session when XP resumes after this many idle seconds. 0 disables it.");
        page.BindFloatSlider(Plugin.Settings.AttributionWindow, "Monster match window",
            0.25f, 5f, 0.25f, "0.00s",
            "Maximum time between a relevant monster death and its XP update. Increase only if awards appear under Other.");

        page.AddHeader("ROWS");
        page.BindChoice(Plugin.Settings.GroupBy, "Group XP by",
            new[] { MeterSettings.GroupMonsterName, MeterSettings.GroupMonsterType },
            "Monster name combines matching localized names. Monster type keeps distinct game IDs separate.");
        page.BindChoice(Plugin.Settings.SortBy, "Sort rows by",
            new[]
            {
                MeterSettings.SortCombinedXp,
                MeterSettings.SortBaseXp,
                MeterSettings.SortJobXp,
                MeterSettings.SortKills,
                MeterSettings.SortRecent,
                MeterSettings.SortName
            }, "Choose how XP source rows are ordered.");
        page.BindIntegerSlider(Plugin.Settings.MaxRows, "Maximum rows",
            1, 20, 1, "Maximum number of XP sources shown in the meter.");
        page.BindToggle(Plugin.Settings.ShowUnattributed, "Show Other row",
            "Show quest, exploration, and awards that could not be safely matched to a monster.");
        page.BindText(Plugin.Settings.UnattributedLabel, "Other row label",
            "Name used for unattributed XP. Empty values fall back to Other.");

        page.AddHeader("COLUMNS");
        page.BindToggle(Plugin.Settings.ShowBaseXp, "Show base XP",
            "Show base-level XP totals and row values.");
        page.BindToggle(Plugin.Settings.ShowJobXp, "Show job XP",
            "Show job XP totals and row values.");
        page.BindToggle(Plugin.Settings.ShowKills, "Show kills",
            "Show the number of monster awards attributed to each row.");
        page.BindToggle(Plugin.Settings.ShowPercentages, "Show XP share",
            "Show each source's percentage of the currently displayed XP total.");

        page.AddHeader("RATE AND FORMAT");
        page.BindToggle(Plugin.Settings.ShowRates, "Show XP per hour",
            "Show estimated base and job XP earned per hour.");
        page.BindChoice(Plugin.Settings.RateClock, "XP/hour clock",
            new[] { MeterSettings.RateElapsed, MeterSettings.RateActive },
            "Elapsed session includes downtime. Between awards stops the clock at the latest award.");
        page.BindToggle(Plugin.Settings.AbbreviateNumbers, "Compact large numbers",
            "Display 12.4k instead of 12,400 in the overlay.");

        AddHotkeyControls(page);

        page.AddHeader("DISPLAY");
        page.BindFloatSlider(Plugin.Settings.UiScale, "Meter scale",
            0.55f, 1.25f, 0.05f, "0.00x",
            "Resize the meter while preserving its aspect ratio.",
            _ => MeterController.ApplyConfiguredScale());

        page.AddHeader("ACTIONS");
        page.AddButton("ResetSession", "Reset XP session",
            MeterController.ResetSession, "Clear all recorded XP and source rows now.");
        page.AddButton("ResetLayout", "Reset meter layout",
            MeterController.ResetLayout, "Restore the default position and size.");

        Plugin.Info("Registered the XP Meter page with Mod Configuration Menu.");
    }

    private static void AddHotkeyControls(ModPage page)
    {
        MethodInfo bindHotkey = typeof(ModPage).GetMethod(
            "BindHotkey",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            new[]
            {
                typeof(ConfigEntry<KeyboardShortcut>),
                typeof(string),
                typeof(string),
                typeof(Action<KeyboardShortcut>)
            },
            null);

        if (bindHotkey is null)
        {
            page.AddHeader("HOTKEYS",
                $"Shortcuts remain active ({Plugin.Settings.ToggleHotkey.Value} toggle, "
                + $"{Plugin.Settings.ResetHotkey.Value} reset), but this MCM version cannot edit chords.");
            Plugin.Warning("The installed MCM does not expose multi-key hotkey controls; "
                + "all other XP Meter settings were registered.");
            return;
        }

        page.AddHeader("HOTKEYS", "Hold the desired keys, press the main key last, then release.");
        bindHotkey.Invoke(page, new object[]
        {
            Plugin.Settings.ToggleHotkey,
            "Toggle meter",
            "Show or hide the XP meter.",
            null
        });
        bindHotkey.Invoke(page, new object[]
        {
            Plugin.Settings.ResetHotkey,
            "Reset session",
            "Clear all XP from the current session.",
            null
        });
    }
}
