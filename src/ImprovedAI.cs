// ImprovedAI plugin lifecycle + shared config. One partial class split across the src/*.cs modules.

using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

[BepInPlugin(GUID, "Improved AI", "0.9.116")]
public partial class ImprovedAIPlugin : BaseUnityPlugin
{
    public const string GUID = "com.leech.improvedai";
    internal static ManualLogSource Log;

    // ---- shared config (read by both modules) ----
    static ConfigEntry<bool> cfgEnabled, cfgDiag, cfgOverlay, cfgAffectPlayers, cfgNavDebug;
    // Master gate (F1 switch). Sim effects self-gate on server authority at the point they run.
    internal static bool IsHost
    {
        get { try { var n = NuclearOption.Networking.NetworkManagerNuclearOption.i; return n != null && n.Server != null && n.Server.Active; } catch { return false; } }
    }
    internal static bool MasterOn => cfgEnabled != null && cfgEnabled.Value;
    const float SkillCap = 8f;   // §1 "Max Effective Skill" removed from F1 config (menu de-spam) — fixed at user's last value.

    // True when a human-piloted unit must be left stock (mod affects AI only, unless §1 Affect Players).
    internal static bool PlayerProtected(Unit u)
    {
        if (u == null) return false;
        if (cfgAffectPlayers != null && cfgAffectPlayers.Value) return false;   // opt-in: affect players too
        return u is Aircraft ac && ac.Player != null;
    }

    void Awake()
    {
        Log = Logger;
        try
        {
            cfgEnabled = Config.Bind("1. General", "Enabled", true, "Master switch. Every sim-changing effect additionally self-gates on server authority (IsServer / remoteSim / server-only AI components), so this mod is safe to keep loaded on servers that don't run it — only the machine simulating the AI ever applies changes.");
            cfgAffectPlayers = Config.Bind("1. General", "Affect Player-Controlled Units", false, "When ON, the mod's STAT effects (elite boosts, durability, gun reload, personality traits, veterancy) also apply to HUMAN-piloted units — your own and other players' planes. Behavioural AI control (missile intercept, jammer, ground maneuver) never takes over a human. Default OFF = only AI is affected, human flight untouched.");
            cfgOverlay = Config.Bind("1. General", "Show Unit Overlay", false, "When spectating/following a unit, draw its overlay (name, cost, veterancy rank/skill, and current AI status — naval role or ground tactical state).");
            cfgDiag = Config.Bind("1. General", "Diagnostics", false, "Log the heartbeat + per-missile decision trace to BepInEx/LogOutput.log.");
            cfgNavDebug = Config.Bind("1. General", "Debug: Draw Ground Nav", false, "Draw ground-AI debug info in the world. FRONTLINE (all AI-owned units, both factions): line from each unit to its destination, coloured by state (GREEN push, RED retreat, WHITE halt & fire, BLUE support hold, PURPLE support move, GREY pursue). SPECTATED unit additionally gets its avoidance: ORANGE boxes = obstacles our planner routes around, BLUE = size-filtered colliders, GRAY = own footprint, GREEN/CYAN line = steering now (CYAN = committed detour), WHITE = heading.");

            MissileConfig();          // sections 2..6 (Missile Intercept / Notch / IR / Laser / Skill)
            GroundConfig();           // section 7 (Ground Forces — movement)
            GroundTargetingConfig();  // section 8 (Ground Targeting — turret targeting)
            VeterancyConfig();        // section 9 (Ground Veterancy — credits/skill from kills)
            PersonalityConfig();      // section 10 (Personalities — random per-unit traits)
            MissionTraitsConfig();    // section 12 (Editor Traits — author-assigned per-unit traits, stored in mission)
            EliteConfig();            // section 13 (Elite Boost — stat ramp: none at/below skill 4, 2x@6, 4x@8; ground/naval/air)
            AirCombatConfig();        // section 14 (Air Combat — defensive notch decision + jammer power conservation)
            NavalConfig();            // auto naval formation (no F1 config; gated on master Enabled)
            // (Ground squads/armies removed in the per-unit rewrite — no Layer 2/3 config.)

            var h = new Harmony(GUID);
            h.PatchAll(typeof(ImprovedAIPlugin).Assembly);
            int patched = 0; foreach (var _ in h.GetPatchedMethods()) patched++;
            Logger.LogInfo($"Improved AI 0.9.116 loaded — {patched} method(s) patched.");
        }
        catch (Exception ex) { Logger.LogError("Improved AI failed to initialise: " + ex); }
    }
}
