using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

namespace DSPRE
{
    /// <summary>
    /// Optional per-ROM file that replaces the hardcoded ARM9/overlay offsets in RomInfo.
    /// Meant for ROM hacks (e.g. decomp builds) whose code and tables no longer sit where the retail game has them.
    ///
    /// The file is looked for, in order, at:
    ///   1. [contents folder]\dspre_offsets.json
    ///   2. [ROM path without extension].dspre_offsets.json (next to the ROM the contents folder came from)
    ///
    /// It is a JSON object whose keys are RomInfo property names (e.g. "headerTableOffset") or one of the extra
    /// keys listed in <see cref="extraKeys"/>. Values are numbers, hex strings ("0xE71C0") or arrays of those.
    /// Keys starting with '_' are ignored, so they can hold comments. An optional "gameCode" key (e.g. "CPUE")
    /// makes DSPRE ignore the file when it is opened alongside a different game.
    /// </summary>
    public static class RomOffsetOverrides
    {
        public const string fileName = "dspre_offsets.json";
        private const string gameCodeKey = "gameCode";

        /// <summary>Offsets DSPRE hardcodes outside RomInfo's properties.</summary>
        public static readonly string[] extraKeys = {
            "monIconPalTablePointerOffset",   // ARM9 offset of the pointer to the icon palette table
            "machineMoveOffset",              // ARM9 TM/HM move table
            "eggMoveTableOffset",             // ov5 egg move table
            "griseousOrbScriptIdOffset",      // Plat ov9: item script ID patched by the Griseous Orb fix
        };

        public static string loadedPath { get; private set; }
        public static bool isActive => values.Count > 0;

        private static Dictionary<string, JsonElement> values = new Dictionary<string, JsonElement>();

        public static void Load(string workDir, string romID)
        {
            values = new Dictionary<string, JsonElement>();
            loadedPath = null;

            string path = FindFile(workDir);
            if (path == null)
            {
                return;
            }

            List<string> problems = new List<string>();
            Dictionary<string, JsonElement> parsed = new Dictionary<string, JsonElement>();
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
                {
                    foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                    {
                        parsed[prop.Name] = prop.Value.Clone();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Could not read offset overrides file {path}: {ex.Message}");
                MessageBox.Show($"Could not read the offset overrides file:\n{path}\n\n{ex.Message}\n\nThe ROM will be opened with DSPRE's built-in offsets.",
                    "Offset overrides ignored", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (parsed.TryGetValue(gameCodeKey, out JsonElement gameCode))
            {
                if (gameCode.ValueKind != JsonValueKind.String || !string.Equals(gameCode.GetString(), romID, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warn($"Offset overrides file {path} is for game code {gameCode}, not {romID}; ignoring it.");
                    MessageBox.Show($"The offset overrides file\n{path}\nis for game code {gameCode}, but this ROM is {romID}.\n\nIt will be ignored.",
                        "Offset overrides ignored", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                parsed.Remove(gameCodeKey);
            }

            foreach (KeyValuePair<string, JsonElement> kv in parsed)
            {
                if (kv.Key.StartsWith("_"))
                {
                    continue;
                }

                PropertyInfo prop = GetRomInfoProperty(kv.Key);
                if (prop == null && !extraKeys.Contains(kv.Key))
                {
                    problems.Add($"unknown key \"{kv.Key}\"");
                    continue;
                }

                if (prop != null && !TryConvert(kv.Value, prop.PropertyType, out _))
                {
                    problems.Add($"\"{kv.Key}\" has a value DSPRE can't use as {prop.PropertyType.Name}");
                    continue;
                }

                if (prop == null && !TryConvert(kv.Value, typeof(uint), out _))
                {
                    problems.Add($"\"{kv.Key}\" is not a number");
                    continue;
                }

                values[kv.Key] = kv.Value;
            }

            loadedPath = path;
            AppLogger.Info($"Loaded {values.Count} offset override(s) from {path}");

            if (problems.Count > 0)
            {
                string list = string.Join("\n", problems.Select(p => "- " + p));
                AppLogger.Warn($"Offset overrides file {path} has entries that were skipped:\n{list}");
                MessageBox.Show($"Some entries in the offset overrides file were skipped:\n{path}\n\n{list}",
                    "Offset overrides", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Writes every overridden RomInfo property. RomInfo calls this after each of its offset setters,
        /// since many of them only run when an editor is first opened and would undo earlier overrides.
        /// </summary>
        public static void Apply()
        {
            foreach (KeyValuePair<string, JsonElement> kv in values)
            {
                PropertyInfo prop = GetRomInfoProperty(kv.Key);
                if (prop != null && TryConvert(kv.Value, prop.PropertyType, out object converted))
                {
                    prop.SetValue(null, converted);
                }
            }
        }

        public static bool TryGet(string key, out uint value)
        {
            value = 0;
            if (!values.TryGetValue(key, out JsonElement element) || !TryConvert(element, typeof(uint), out object converted))
            {
                return false;
            }
            value = (uint)converted;
            return true;
        }

        /// <summary>Returns the override for <paramref name="key"/>, or <paramref name="fallback"/> when there is none.</summary>
        public static uint Get(string key, uint fallback)
        {
            return TryGet(key, out uint value) ? value : fallback;
        }

        private static string FindFile(string workDir)
        {
            string contentsDir = workDir.TrimEnd('\\', '/');
            string inContents = Path.Combine(contentsDir, fileName);
            if (File.Exists(inContents))
            {
                return inContents;
            }

            if (contentsDir.EndsWith(RomInfo.folderSuffix))
            {
                string besideRom = contentsDir.Substring(0, contentsDir.Length - RomInfo.folderSuffix.Length) + "." + fileName;
                if (File.Exists(besideRom))
                {
                    return besideRom;
                }
            }

            return null;
        }

        private static PropertyInfo GetRomInfoProperty(string name)
        {
            PropertyInfo prop = typeof(RomInfo).GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            return prop != null && prop.CanWrite ? prop : null;
        }

        private static bool TryConvert(JsonElement element, Type type, out object result)
        {
            result = null;

            if (type.IsArray)
            {
                if (element.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                Type elementType = type.GetElementType();
                JsonElement[] items = element.EnumerateArray().ToArray();
                Array array = Array.CreateInstance(elementType, items.Length);
                for (int i = 0; i < items.Length; i++)
                {
                    if (!TryConvert(items[i], elementType, out object item))
                    {
                        return false;
                    }
                    array.SetValue(item, i);
                }
                result = array;
                return true;
            }

            if (type == typeof(bool))
            {
                if (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False)
                {
                    return false;
                }
                result = element.GetBoolean();
                return true;
            }

            long number;
            if (element.ValueKind == JsonValueKind.Number)
            {
                if (!element.TryGetInt64(out number))
                {
                    return false;
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                string text = element.GetString().Trim();
                bool parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? long.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number)
                    : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
                if (!parsed)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            try
            {
                result = Convert.ChangeType(number, type, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is OverflowException)
            {
                return false;
            }
        }
    }
}
