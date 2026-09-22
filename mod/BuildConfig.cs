using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Overrank
{
    internal static class BuildConfig
    {
        private const string ResourceName = "Overrank.BuildEnvironment";
        private static readonly Dictionary<string, string> Values = LoadValues();

        internal static string DefaultServerUrl
        {
            get { return Get("OVERRANK_SERVER_URL", "http://127.0.0.1:3005"); }
        }

        internal static string DefaultApiKey
        {
            get { return Get("OVERRANK_API_KEY", string.Empty); }
        }

        private static string Get(string key, string fallback)
        {
            string value;
            return Values.TryGetValue(key, out value) && !string.IsNullOrEmpty(value) ? value : fallback;
        }

        private static Dictionary<string, string> LoadValues()
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
                if (stream == null)
                {
                    return values;
                }
                using (stream)
                using (StreamReader reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        int separator = line.IndexOf('=');
                        if (separator <= 0)
                        {
                            continue;
                        }
                        values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
                    }
                }
            }
            catch
            {
            }
            return values;
        }
    }
}
