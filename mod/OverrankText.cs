namespace Overrank
{
    internal static class OverrankText
    {
        private static bool? _simplifiedChinese;

        internal static bool IsSimplifiedChinese
        {
            get
            {
                if (!_simplifiedChinese.HasValue)
                {
                    try
                    {
                        _simplifiedChinese = Localization.GetLanguage() == SupportedLanguages.Chinese;
                    }
                    catch
                    {
                        _simplifiedChinese = false;
                    }
                }
                return _simplifiedChinese.Value;
            }
        }

        internal static string Get(string english, string simplifiedChinese)
        {
            return IsSimplifiedChinese ? simplifiedChinese : english;
        }
    }
}
