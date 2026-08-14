using System.Reflection;
using HarmonyLib;

namespace NevarisXpMeter;

// FishNet's generated TargetRpc logic receives the authoritative character XP
// totals on the owning client. Comparing consecutive totals records both base
// and job gains without reading floating combat text or estimating rewards.
[HarmonyPatch]
internal static class XpCapturePatch
{
    private static MethodBase TargetMethod()
    {
        MethodInfo method = AccessTools.GetDeclaredMethods(typeof(PlayerSave))
            .FirstOrDefault(candidate =>
            {
                ParameterInfo[] parameters = candidate.GetParameters();
                return candidate.Name.StartsWith("RpcLogic___ExpCoinsChanged_T___", StringComparison.Ordinal)
                    && parameters.Length == 6
                    && parameters[1].ParameterType == typeof(int)
                    && parameters[2].ParameterType == typeof(int)
                    && parameters[3].ParameterType == typeof(int)
                    && parameters[4].ParameterType == typeof(int)
                    && parameters[5].ParameterType == typeof(long);
            });

        if (method is null)
            throw new MissingMethodException(typeof(PlayerSave).FullName,
                "generated ExpCoinsChanged TargetRpc logic");

        Plugin.Info($"XP capture target resolved as {typeof(PlayerSave).FullName}.{method.Name}.");
        return method;
    }

    private static void Postfix(PlayerSave __instance, object[] __args)
    {
        try
        {
            if (__args is null || __args.Length < 5)
                return;

            MeterController.RecordTotals(
                Convert.ToInt32(__args[1]),
                Convert.ToInt32(__args[2]),
                Convert.ToInt32(__args[3]),
                Convert.ToInt32(__args[4]));
        }
        catch (Exception ex)
        {
            Plugin.Warning($"Could not record an XP update: {ex.Message}");
        }
    }
}
