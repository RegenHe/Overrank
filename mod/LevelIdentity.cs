using System;
using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Overrank
{
    internal sealed class LevelIdentity
    {
        internal string Uid;
        internal int DlcId;
        internal int LevelId;
        internal string SceneName;
        internal string DisplayName;

        internal static LevelIdentity Read(GameSession session)
        {
            SceneDirectoryData.PerPlayerCountDirectoryEntry variant = session.LevelSettings == null
                ? null
                : session.LevelSettings.SceneDirectoryVarientEntry;
            Scene scene = SceneManager.GetActiveScene();
            string sceneName = variant == null || string.IsNullOrEmpty(variant.SceneName)
                ? scene.name
                : variant.SceneName;

            int levelId = SafeLevelId();
            string label = FindDirectoryLabel(session, levelId);
            string uid;
            string customDisplay;
            if (TryFindOc2DiyIdentity(sceneName, null, out uid, out customDisplay))
            {
                return new LevelIdentity
                {
                    Uid = uid,
                    DlcId = session.DLC,
                    LevelId = levelId,
                    SceneName = sceneName,
                    DisplayName = customDisplay
                };
            }

            bool official = levelId >= 0 && session.DLC != 15;
            string levelConfigName = variant == null || variant.LevelConfig == null
                ? string.Empty
                : variant.LevelConfig.name;
            string seed = official
                // Player-count variants of the same official level must share one
                // level UID; player_count is a separate leaderboard dimension.
                ? "official|" + session.DLC + "|" + levelId
                : "custom|" + scene.path + "|" + scene.name + "|" + sceneName + "|" + levelConfigName + "|" + label;
            return new LevelIdentity
            {
                Uid = (official ? "official-" : "custom-") + Hash(seed, 32),
                DlcId = session.DLC,
                LevelId = levelId,
                SceneName = sceneName,
                DisplayName = string.IsNullOrEmpty(label) ? sceneName : label
            };
        }

        private static int SafeLevelId()
        {
            try
            {
                return GameUtils.GetLevelID();
            }
            catch
            {
                return -1;
            }
        }

        private static string FindDirectoryLabel(GameSession session, int levelId)
        {
            try
            {
                SceneDirectoryData directory = session.Progress.GetSceneDirectory();
                if (directory != null && directory.Scenes != null && levelId >= 0 && levelId < directory.Scenes.Length)
                {
                    return LocalizeLabel(CleanLabel(directory.Scenes[levelId].Label));
                }
            }
            catch
            {
            }
            return string.Empty;
        }

        internal static string ResolveDisplayName(string levelUid, string fallback)
        {
            string resolvedUid;
            string displayName;
            if (!string.IsNullOrEmpty(levelUid)
                && levelUid.StartsWith("oc2diy-", StringComparison.Ordinal)
                && TryFindOc2DiyIdentity(null, levelUid, out resolvedUid, out displayName))
            {
                return displayName;
            }
            return LocalizeLabel(fallback);
        }

        private static bool TryFindOc2DiyIdentity(
            string sceneName,
            string expectedUid,
            out string uid,
            out string displayName)
        {
            uid = null;
            displayName = null;
            Type manager = Type.GetType("OC2DIYLevel.DIYLevelAssetBundleManager, OC2DIYLevel", false);
            if (manager == null)
            {
                return false;
            }

            FieldInfo setsField = manager.GetField("levelSetInfos", BindingFlags.Public | BindingFlags.Static);
            IEnumerable sets = setsField == null ? null : setsField.GetValue(null) as IEnumerable;
            if (sets == null)
            {
                return false;
            }

            foreach (object pair in sets)
            {
                object set = ReadMember(pair, "Value");
                if (set == null)
                {
                    continue;
                }
                IEnumerable levels = ReadMember(set, "levelInfos") as IEnumerable;
                if (levels == null)
                {
                    continue;
                }
                foreach (object level in levels)
                {
                    string candidateScene = ReadMember(level, "sceneName") as string;
                    string setUid = ReadMember(set, "uid") as string;
                    if (string.IsNullOrEmpty(setUid))
                    {
                        continue;
                    }
                    string candidateUid = "oc2diy-" + Hash(
                        setUid.ToLowerInvariant() + "|" + (candidateScene ?? string.Empty).ToLowerInvariant(),
                        32);
                    bool sceneMatches = !string.IsNullOrEmpty(sceneName)
                        && string.Equals(candidateScene, sceneName, StringComparison.OrdinalIgnoreCase);
                    bool uidMatches = !string.IsNullOrEmpty(expectedUid)
                        && string.Equals(candidateUid, expectedUid, StringComparison.Ordinal);
                    if (!sceneMatches && !uidMatches)
                    {
                        continue;
                    }

                    string setName = LocalizedCustomName(
                        ReadMember(set, "levelSetName") as string,
                        ReadMember(set, "levelSetNameZH") as string);
                    string levelName = LocalizedCustomName(
                        ReadMember(level, "levelName") as string,
                        ReadMember(level, "levelNameZH") as string);
                    uid = candidateUid;
                    displayName = JoinDisplayName(setName, levelName, sceneName);
                    return true;
                }
            }
            return false;
        }

        private static string LocalizedCustomName(string english, string chinese)
        {
            try
            {
                if (Localization.GetLanguage() == SupportedLanguages.Chinese && !string.IsNullOrEmpty(chinese))
                {
                    return chinese;
                }
            }
            catch
            {
            }
            return english;
        }

        private static string LocalizeLabel(string label)
        {
            if (string.IsNullOrEmpty(label) || !label.StartsWith("Text.", StringComparison.Ordinal))
            {
                return label;
            }
            try
            {
                string localized = Localization.Get(label);
                if (!string.IsNullOrEmpty(localized) && !localized.StartsWith("MT[", StringComparison.Ordinal))
                {
                    return localized;
                }
            }
            catch
            {
            }
            return label;
        }

        private static object ReadMember(object instance, string name)
        {
            if (instance == null)
            {
                return null;
            }
            Type type = instance.GetType();
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null)
            {
                return property.GetValue(instance, null);
            }
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field == null ? null : field.GetValue(instance);
        }

        private static string JoinDisplayName(string setName, string levelName, string fallback)
        {
            if (!string.IsNullOrEmpty(setName) && !string.IsNullOrEmpty(levelName))
            {
                return setName + " / " + levelName;
            }
            return string.IsNullOrEmpty(levelName) ? fallback : levelName;
        }

        private static string CleanLabel(string label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return string.Empty;
            }
            return label.Trim().Trim('"');
        }

        internal static string Hash(string value, int characters)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                StringBuilder builder = new StringBuilder(bytes.Length * 2);
                for (int index = 0; index < bytes.Length; index++)
                {
                    builder.Append(bytes[index].ToString("x2"));
                }
                return builder.ToString(0, Math.Min(characters, builder.Length));
            }
        }
    }
}
