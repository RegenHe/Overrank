using System;
using System.Reflection;

namespace Overrank
{
    internal static class OverwashedIntegration
    {
        private const string PluginTypeName =
            "Overcooked2DishwasherBot.DishwasherBotPlugin, Overwashed";

        private static Type _pluginType;
        private static PropertyInfo _wasUsedThisRound;
        private static PropertyInfo _runtimeVersion;

        internal static void ReadRoundUsage(out bool used, out string version)
        {
            used = false;
            version = string.Empty;
            Resolve();
            if (_pluginType == null || _wasUsedThisRound == null)
            {
                return;
            }

            try
            {
                object value = _wasUsedThisRound.GetValue(null, null);
                used = value is bool && (bool)value;
                if (used && _runtimeVersion != null)
                {
                    version = _runtimeVersion.GetValue(null, null) as string ?? string.Empty;
                }
            }
            catch
            {
                used = false;
                version = string.Empty;
            }
        }

        private static void Resolve()
        {
            if (_pluginType != null)
            {
                return;
            }
            _pluginType = Type.GetType(PluginTypeName, false);
            if (_pluginType == null)
            {
                return;
            }
            _wasUsedThisRound = _pluginType.GetProperty(
                "WasUsedThisRound",
                BindingFlags.Public | BindingFlags.Static);
            _runtimeVersion = _pluginType.GetProperty(
                "RuntimeVersion",
                BindingFlags.Public | BindingFlags.Static);
        }
    }
}
