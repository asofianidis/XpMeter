using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Configuration;
using HarmonyLib;
using SpiritVale.ModConfig;
using UnityEngine;

namespace NevarisXpMeter;

[BepInPlugin(Guid, Name, Version)]
[BepInDependency(ModConfigPlugin.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "alex.nevaris.xpmeter";
    public const string Name = "XP Meter";
    public const string Version = "1.0.4";

    internal static ManualLogSource Logger = null!;
    internal static MeterSettings Settings = null!;
    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        Settings = new MeterSettings(Config);
        McmIntegration.Register();

        _harmony = new Harmony(Guid);
        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        MeterController.Initialize();

        Info($"{Name} {Version} loaded. {Settings.ToggleHotkey.Value} toggles the meter; "
            + $"{Settings.ResetHotkey.Value} resets the session.");
    }

    public override bool Unload()
    {
        _harmony?.UnpatchSelf();
        ModConfigMenu.Unregister(Guid);
        PlayerWorldGate.Shutdown();
        MeterController.Shutdown();
        return base.Unload();
    }

    internal static void Info(string message) => Logger.LogInfo($"[XpMeter] {message}");
    internal static void Warning(string message) => Logger.LogWarning($"[XpMeter] {message}");
    internal static void Error(string message) => Logger.LogError($"[XpMeter] {message}");
}

internal sealed class MeterSettings
{
    internal const string GroupMonsterName = "Monster name";
    internal const string GroupMonsterType = "Monster type";
    internal const string SortBaseXp = "Base XP";
    internal const string SortJobXp = "Job XP";
    internal const string SortCombinedXp = "Combined XP";
    internal const string SortKills = "Kills";
    internal const string SortRecent = "Most recent";
    internal const string SortName = "Name";
    internal const string RateElapsed = "Elapsed session";
    internal const string RateActive = "Between awards";

    internal readonly ConfigEntry<KeyboardShortcut> ToggleHotkey;
    internal readonly ConfigEntry<KeyboardShortcut> ResetHotkey;
    internal readonly ConfigEntry<bool> StartVisible;
    internal readonly ConfigEntry<bool> ResetOnWorldEntry;
    internal readonly ConfigEntry<float> NewSessionAfter;
    internal readonly ConfigEntry<float> AttributionWindow;
    internal readonly ConfigEntry<string> GroupBy;
    internal readonly ConfigEntry<string> SortBy;
    internal readonly ConfigEntry<int> MaxRows;
    internal readonly ConfigEntry<bool> ShowBaseXp;
    internal readonly ConfigEntry<bool> ShowJobXp;
    internal readonly ConfigEntry<bool> ShowKills;
    internal readonly ConfigEntry<bool> ShowRates;
    internal readonly ConfigEntry<string> RateClock;
    internal readonly ConfigEntry<bool> ShowPercentages;
    internal readonly ConfigEntry<bool> AbbreviateNumbers;
    internal readonly ConfigEntry<bool> ShowUnattributed;
    internal readonly ConfigEntry<string> UnattributedLabel;
    internal readonly ConfigEntry<float> PositionX;
    internal readonly ConfigEntry<float> PositionY;
    internal readonly ConfigEntry<float> UiScale;

    internal MeterSettings(ConfigFile config)
    {
        ToggleHotkey = config.Bind("Hotkeys", "Toggle", new KeyboardShortcut(KeyCode.F10),
            "Show or hide the XP meter.");
        ResetHotkey = config.Bind("Hotkeys", "Reset", new KeyboardShortcut(KeyCode.F11),
            "Reset the current XP session.");

        StartVisible = config.Bind("General", "StartVisible", true,
            "Show the meter when the character enters the world.");
        ResetOnWorldEntry = config.Bind("General", "ResetOnWorldEntry", true,
            "Start a fresh XP session whenever the character enters a world.");
        NewSessionAfter = config.Bind("Session", "NewSessionAfterSeconds", 0f,
            "Idle time before the next XP award starts a new session. Set to 0 to disable.");
        AttributionWindow = config.Bind("Session", "AttributionWindowSeconds", 1.25f,
            "Maximum time between a relevant monster death and its XP update.");

        GroupBy = config.Bind("Rows", "GroupBy", GroupMonsterName,
            "Group awards by localized monster name or distinct monster type ID.");
        SortBy = config.Bind("Rows", "SortBy", SortCombinedXp,
            "Metric used to order source rows.");
        MaxRows = config.Bind("Rows", "MaxRows", 8,
            "Maximum number of XP source rows shown.");
        ShowUnattributed = config.Bind("Rows", "ShowUnattributed", true,
            "Show awards that could not be safely matched to a monster death.");
        UnattributedLabel = config.Bind("Rows", "UnattributedLabel", "Other",
            "Label used for quest, exploration, or otherwise unattributed XP.");

        ShowBaseXp = config.Bind("Display", "ShowBaseXp", true, "Show base-level XP.");
        ShowJobXp = config.Bind("Display", "ShowJobXp", true, "Show job XP.");
        ShowKills = config.Bind("Display", "ShowKills", true,
            "Show the number of attributed monster awards in each row.");
        ShowRates = config.Bind("Display", "ShowRates", true,
            "Show estimated base and job XP per hour.");
        RateClock = config.Bind("Display", "RateClock", RateElapsed,
            "Use total elapsed session time or only the span between the first and latest award.");
        ShowPercentages = config.Bind("Display", "ShowPercentages", false,
            "Show each source's share of the displayed XP type.");
        AbbreviateNumbers = config.Bind("Display", "AbbreviateNumbers", true,
            "Use compact values such as 12.4k in the overlay.");
        PositionX = config.Bind("Display", "PositionX", 22f,
            "Saved horizontal position of the draggable meter.");
        PositionY = config.Bind("Display", "PositionY", -425f,
            "Saved vertical position of the draggable meter.");
        UiScale = config.Bind("Display", "Scale", 1f,
            "Saved meter scale. Drag the bottom-right corner to resize it.");
    }
}
