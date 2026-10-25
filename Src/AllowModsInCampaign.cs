using HarmonyLib;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

using UnityEngine;

namespace AllowModsInCampaign
{
    /// <summary>
    /// Lets mods load and be used everywhere the game would otherwise hold them back:
    /// <list type="bullet">
    /// <item>parks where the game switches all mods off (build challenges, the custom campaign editor and map),</item>
    /// <item>built-in campaign levels, where asset packs refuse to load,</item>
    /// <item>scenarios, where modded objects start locked behind research.</item>
    /// </list>
    /// </summary>
    public class AMIC : AbstractMod, IModSettings
    {
        public const string VERSION_NUMBER = "261024";
        public const string IDENTIFIER = "AllowModsInCampaign";
        private const string LOG_PREFIX = "AllowModsInCampaign: ";

        private static bool patched;

        public override string getIdentifier() => IDENTIFIER;

        public override string getName() => "Allow Mods In Campaign";

        public override string getDescription() => @"Lets mods and asset packs load everywhere the game would block them: campaign levels, build challenges, and the custom campaign editor. Modded objects also skip scenario research.";

        public override string getVersionNumber() => VERSION_NUMBER;

        public override bool isMultiplayerModeCompatible() => true;

        public override bool isRequiredByAllPlayersInMultiplayerMode() => false;

        /// <summary>Whether the player has this mod switched on in the mod list.</summary>
        public static bool IsActive => Settings.Instance != null && Settings.Instance.isModActive(IDENTIFIER);

        // Patched when the mod is constructed rather than enabled: in parks where the game blocks mods it never
        // enables any mod (this one included), so patches installed on enable would never get the chance to run.
        // They stay for the session, and each one checks IsActive so switching the mod off still turns it off.
        public AMIC()
        {
            if (patched)
                return;

            try
            {
                new Harmony(IDENTIFIER).PatchAll(typeof(AMIC).Assembly);
                patched = true;
                Log("Patched.");
            }
            catch (Exception ex)
            {
                Log("Patching failed: " + ex);
            }
        }

        public override void onEnabled() { }

        public override void onDisabled() { }

        public void onDrawSettingsUI()
        {
            foreach (AMICSettings.Toggle toggle in AMICSettings.All)
                toggle.Value = GUILayout.Toggle(toggle.Value, " " + toggle.Label);

            GUILayout.Space(8f);
            GUILayout.Label("The first three apply the next time a park loads.");
        }

        public void onSettingsOpened() { }

        public void onSettingsClosed()
        {
            PlayerPrefs.Save();
        }

        public static void Log(string message)
        {
            Debug.LogWarning(LOG_PREFIX + message);
        }
    }

    /// <summary>Per-player switches for each override, all on by default, kept in PlayerPrefs.</summary>
    public static class AMICSettings
    {
        public sealed class Toggle
        {
            private readonly string key;
            private bool? value;

            public string Label { get; }

            public Toggle(string key, string label)
            {
                this.key = key;
                Label = label;
            }

            // Cached, since some of these are checked very often (research checks run for every buildable object).
            public bool Value
            {
                get
                {
                    if (value == null)
                        value = PlayerPrefs.GetInt(key, 1) == 1;
                    return value.Value;
                }
                set
                {
                    if (this.value == value)
                        return;

                    this.value = value;
                    PlayerPrefs.SetInt(key, value ? 1 : 0);
                }
            }
        }

        public static readonly Toggle BuildChallenges = new Toggle("AMIC_BuildChallenges", "Allow mods in Build Challenges");
        public static readonly Toggle CustomCampaigns = new Toggle("AMIC_CustomCampaigns", "Allow mods in the custom campaign editor and map");
        public static readonly Toggle CampaignAssetPacks = new Toggle("AMIC_CampaignAssetPacks", "Load asset packs in campaign levels");
        public static readonly Toggle SkipModdedResearch = new Toggle("AMIC_SkipModdedResearch", "Modded objects skip scenario research");

        public static readonly Toggle[] All = { BuildChallenges, CustomCampaigns, CampaignAssetPacks, SkipModdedResearch };
    }

    /// <summary>
    /// The game switches every mod off for some parks via <c>GameController.modsBlocked</c>. That property is too small
    /// to patch reliably (the runtime copies its body into callers), so the places that read it are rewritten to ask
    /// <see cref="AreModsBlocked"/> instead.
    /// </summary>
    [HarmonyPatch]
    public static class ModsBlockedPatch
    {
        private static readonly MethodInfo modsBlockedGetter = typeof(GameController).GetProperty(nameof(GameController.modsBlocked)).GetGetMethod();

        // Blocked parks are re-blocked per setting, by why the game blocked them. Any other reason a future update adds
        // is allowed.
        public static bool AreModsBlocked(GameController gameController)
        {
            if (!gameController.modsBlocked)
                return false;

            if (!AMIC.IsActive)
                return true;

            if ((gameController.isInCustomCampaignEditor || gameController.isInCustomCampaignMap) && !AMICSettings.CustomCampaigns.Value)
                return true;

            Park park = gameController.park;
            bool isBuildChallenge = park != null && park.scenario != null && park.scenario.GetType().Name == "BuildChallengeScenario";
            return isBuildChallenge && !AMICSettings.BuildChallenges.Value;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type modEntry = typeof(ModManager).GetNestedType("ModEntry", all);

            yield return modEntry.GetMethod("setActive", all);
            yield return modEntry.GetMethod("enableMod", all);
            yield return typeof(ModManager).GetMethod("triggerEnable", all);
            yield return typeof(GameController).GetMethod("startNewPark", all);

            // Park loading is a coroutine, so its body lives in a compiler-generated class.
            foreach (Type nested in typeof(GameController).GetNestedTypes(all).Where(type => type.Name.Contains("loadGameCoroutine")))
                yield return nested.GetMethod("MoveNext", all);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            MethodInfo replacement = typeof(ModsBlockedPatch).GetMethod(nameof(AreModsBlocked));
            int replaced = 0;

            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && Equals(instruction.operand, modsBlockedGetter))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    replaced++;
                }
                yield return instruction;
            }

            if (replaced == 0)
                AMIC.Log($"Didn't find the mod block check in {original.DeclaringType?.Name}.{original.Name}; the game may have changed.");
        }
    }

    /// <summary>
    /// Asset packs skip loading in built-in campaign levels by asking <c>CampaignProgress.isBuiltInCampaignScenario</c>.
    /// While an asset pack is loading, that question gets "no"; everywhere else (campaign progress, achievements) the
    /// game gets its real answer.
    /// </summary>
    [HarmonyPatch]
    public static class AssetPackLoadPatch
    {
        private static bool loadingAssetPack;

        private static MethodBase TargetMethod()
        {
            Type assetPackMod = typeof(GameController).Assembly.GetType("Parkitect.Mods.AssetPacks.AssetPackMod");
            return assetPackMod.GetMethod("onEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static void Prefix()
        {
            loadingAssetPack = true;
        }

        private static Exception Finalizer(Exception __exception)
        {
            loadingAssetPack = false;
            return __exception;
        }

        [HarmonyPatch(typeof(CampaignProgress), nameof(CampaignProgress.isBuiltInCampaignScenario))]
        public static class CampaignCheckPatch
        {
            private static void Postfix(ref bool __result)
            {
                if (__result && loadingAssetPack && AMIC.IsActive && AMICSettings.CampaignAssetPacks.Value)
                    __result = false;
            }
        }
    }

    /// <summary>
    /// In scenarios, the game gives modded objects a research rule that starts locked. Treat modded objects (only those)
    /// as unlocked, and keep them out of the research list.
    /// </summary>
    public static class ModdedResearchPatch
    {
        private static bool IsModded(string referenceName)
        {
            AssetManager assetManager = ScriptableSingleton<AssetManager>.Instance;
            SerializedMonoBehaviour prefab = assetManager != null ? assetManager.getPrefab(referenceName) : null;
            return prefab != null && assetManager.getModForSerializedMonoBehaviour(prefab) != null;
        }

        [HarmonyPatch(typeof(ResearchController), nameof(ResearchController.isUnlocked))]
        public static class UnlockedPatch
        {
            private static void Postfix(string referenceName, ref bool __result)
            {
                if (!__result && AMIC.IsActive && AMICSettings.SkipModdedResearch.Value && IsModded(referenceName))
                    __result = true;
            }
        }

        [HarmonyPatch(typeof(ResearchController), nameof(ResearchController.isResearchable))]
        public static class ResearchablePatch
        {
            private static void Postfix(string referenceName, ref bool __result)
            {
                if (__result && AMIC.IsActive && AMICSettings.SkipModdedResearch.Value && IsModded(referenceName))
                    __result = false;
            }
        }
    }
}
