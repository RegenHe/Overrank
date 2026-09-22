using System;
using System.Reflection;
using HarmonyLib;

namespace Overrank
{
    [HarmonyPatch]
    internal static class ScoreCapturePatch
    {
        internal static Action<ClientCampaignFlowController> LevelFinished;

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ClientCampaignFlowController), "RunLevelOutro");
        }

        private static void Prefix(ClientCampaignFlowController __instance)
        {
            Action<ClientCampaignFlowController> callback = LevelFinished;
            if (callback != null)
            {
                callback(__instance);
            }
        }
    }
}
