using HarmonyLib;

namespace NevarisXpMeter;

// The client death callback provides the monster identity. XP updates do not
// contain a source, so the controller correlates this event within a short,
// configurable window and leaves unmatched awards explicitly unattributed.
[HarmonyPatch(typeof(MonsterController), "OnDeath_C")]
internal static class MonsterDeathPatch
{
    private static void Postfix(MonsterController __instance, Damage dmg)
    {
        try
        {
            MeterController.NoteMonsterDeath(__instance, dmg);
        }
        catch (Exception ex)
        {
            Plugin.Warning($"Could not inspect a monster death: {ex.Message}");
        }
    }
}
