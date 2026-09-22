namespace Overrank
{
    internal static class OverrankText
    {
        private static bool _languageResolved;
        private static bool _simplifiedChinese;

        internal static bool IsSimplifiedChinese
        {
            get
            {
                if (_languageResolved)
                {
                    return _simplifiedChinese;
                }

                try
                {
                    _simplifiedChinese = Localization.GetLanguage() == SupportedLanguages.Chinese;

                    // Localization.GetLanguage() falls back to the operating-system
                    // language until SteamPlayerManager is ready. Do not permanently
                    // cache that early fallback because the game's Steam language can
                    // be different from the operating-system language.
                    _languageResolved = SteamPlayerManager.Initialized;
                }
                catch
                {
                    _simplifiedChinese = false;
                }

                return _simplifiedChinese;
            }
        }

        internal static string Get(string english, string simplifiedChinese)
        {
            return IsSimplifiedChinese ? simplifiedChinese : english;
        }
    }
}
