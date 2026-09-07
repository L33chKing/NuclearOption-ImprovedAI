// GroundVeterancy: kill-credit skill ladder for AI units + spectate overlay. Host-side only.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

public partial class ImprovedAIPlugin
{
    // ---- veterancy config (fixed values, no F1 knobs) ----
    const bool VetBuff = true;
    const float VetFirstLevelCost = 2f, VetGrowth = 3f, VetMaxSkill = 8f;

    // ---- reflection ----
    static AccessTools.FieldRef<Unit, Dictionary<PersistentID, float>> vetDamageCredit;
    static AccessTools.FieldRef<Turret, float> vetLockTime, vetTraverse;
    static AccessTools.FieldRef<CameraStateManager, Unit> vetFollowing;
    static bool vetBroken;

    class VetState { public Unit v; public float baseSkill; public float credits; public float appliedSkill; }

    static float GetUnitSkill(Unit u) => u is GroundVehicle gv ? gv.skill : (u is Aircraft ac ? ac.skill : (u is Ship sh ? sh.skill : 1f));
    static void SetUnitSkill(Unit u, float v) { if (u is GroundVehicle gv) gv.skill = v; else if (u is Aircraft ac) ac.skill = v; else if (u is Ship sh) sh.skill = v; }
    static readonly Dictionary<int, VetState> vet = new Dictionary<int, VetState>();
    static readonly Dictionary<int, float> vetTurretSkill = new Dictionary<int, float>();   // turret id -> last skill we baked it at
    static float[] vetThr;   // cumulative own-worths destroyed to REACH each level (index = level); built once at init
    // Rank name per skill band (floor).
    static readonly string[] vetRanks = { "Militia", "Conscript", "Hardened", "Veteran", "Elite", "Heroic", "Legendary", "Mythic", "Immortal", "Eternal", "Divine" };

    void VeterancyConfig()
    {
        // "Show Unit Overlay" now lives in §1 General (cfgOverlay), bound in ImprovedAI.Awake.

        try
        {
            vetDamageCredit = AccessTools.FieldRefAccess<Unit, Dictionary<PersistentID, float>>("damageCredit");
            vetLockTime = AccessTools.FieldRefAccess<Turret, float>("lockTime");
            vetTraverse = AccessTools.FieldRefAccess<Turret, float>("traverseRate");
        }
        catch (Exception ex) { vetBroken = true; Logger.LogError("[Veterancy] core field init failed (feature disabled): " + ex); }
        // overlay-only field: a failure here just disables the overlay, not the leveling
        try { vetFollowing = AccessTools.FieldRefAccess<CameraStateManager, Unit>("followingUnit"); }
        catch (Exception ex) { vetFollowing = null; Logger.LogWarning("[Veterancy] spectate-overlay field unavailable: " + ex); }

        // Build the cumulative XP ladder once (own-worths destroyed to REACH each level).
        int levels = Mathf.Clamp(Mathf.CeilToInt(VetMaxSkill), 2, 20);
        vetThr = new float[levels + 1];
        float first = Mathf.Max(VetFirstLevelCost, 0.01f), grow = Mathf.Max(VetGrowth, 1f), step = first;
        for (int L = 2; L <= levels; L++) { vetThr[L] = vetThr[L - 1] + step; step *= grow; }
    }

    // Postfix on Unit.ReportKilled: distribute kill credit to whoever damaged the dead unit.
    internal static void VetOnKilled(Unit dead)
    {
        if (!MasterOn || vetBroken || vetDamageCredit == null || dead == null) return;
        try { if (!dead.IsServer) return; } catch { }               // kill attribution is server-authoritative
        try
        {
            var dc = vetDamageCredit(dead);
            if (dc == null || dc.Count == 0 || dead.definition == null) return;
            float deadValue = dead.definition.value;
            if (deadValue <= 0f) return;
            float total = 0f; foreach (var d in dc.Values) total += d;
            if (total <= 0f) return;
            var deadHQ = dead.NetworkHQ;
            foreach (var kv in dc)
            {
                if (!UnitRegistry.TryGetUnit(kv.Key, out var attacker) || attacker == null || attacker.disabled) continue;
                if (!(attacker is GroundVehicle || attacker is Aircraft || attacker is Ship)) continue;  // ground + air + naval earn veterancy
                if (PlayerProtected(attacker)) continue;   // human pilots don't earn veterancy unless §1 Affect Players
                if (attacker.NetworkHQ == null || attacker.NetworkHQ == deadHQ) continue;   // enemy kills only (ignore friendly fire)
                float share = kv.Value / total;
                if (share < 0.01f) continue;
                VetAward(attacker, deadValue * share);
            }
        }
        catch (Exception ex) { Log?.LogWarning("[Veterancy] kill error: " + ex); }
    }

    static void VetAward(Unit u, float amount)
    {
        int id = u.GetInstanceID();
        if (!vet.TryGetValue(id, out var s))
        { float sk = GetUnitSkill(u); s = new VetState { v = u, baseSkill = sk, appliedSkill = sk }; vet[id] = s; }
        s.credits += amount;

        float own = u.definition != null ? Mathf.Max(u.definition.value, 0.01f) : 1f;   // own cost in $millions
        // Absolute ladder: spawn skill is table XP (head start), earned credits add on top.
        float xp = XPAtSkill(s.baseSkill) + s.credits / own;
        // Cap at Max Skill, but never below spawn skill (no demotion by scoring a kill).
        float cap = Mathf.Max(VetMaxSkill, s.baseSkill);
        float newSkill = Mathf.Clamp(SkillAtXP(xp), s.baseSkill, cap);
        if (Mathf.Abs(newSkill - s.appliedSkill) < 0.01f) return;

        float old = s.appliedSkill;
        SetUnitSkill(u, newSkill);
        s.appliedSkill = newSkill;
        if (VetBuff) { VetRebake(s, newSkill); ApplyElite(u); }   // re-bake turret aim + ramp elite stats as skill climbs
        if (cfgDiag != null && cfgDiag.Value)
            Log?.LogInfo($"[Veterancy] {u.name} skill {old:0.00}->{newSkill:0.00} earned={UnitConverter.ValueReading(s.credits)} own={UnitConverter.ValueReading(own)}");
    }

    // Cumulative own-worths a unit at skill s sits at on the ladder (its spawn head-start XP).
    static float XPAtSkill(float s)
    {
        if (vetThr == null || vetThr.Length < 2) return 0f;
        int max = vetThr.Length - 1;
        if (s < 1f) return (s - 1f) * vetThr[2];          // extrapolate below level 1 (negative)
        s = Mathf.Min(s, max);
        int b = Mathf.Min(Mathf.FloorToInt(s), max - 1);
        return Mathf.Lerp(vetThr[b], vetThr[b + 1], s - b);
    }

    // Inverse: fractional skill reached at a given cumulative XP.
    static float SkillAtXP(float xp)
    {
        if (vetThr == null || vetThr.Length < 2) return 1f;
        int max = vetThr.Length - 1;
        if (xp <= 0f) return vetThr[2] > 0.0001f ? Mathf.Max(0f, 1f + xp / vetThr[2]) : 1f;   // below level 1
        for (int L = 2; L <= max; L++)
            if (xp < vetThr[L])
            {
                float span = vetThr[L] - vetThr[L - 1];
                return (L - 1) + (span > 0.0001f ? (xp - vetThr[L - 1]) / span : 0f);
            }
        return max;   // at/above the top of the ladder
    }

    // Rank name for a unit's current skill.
    static string VetRankName(float skill)
    {
        int b = Mathf.Clamp(Mathf.FloorToInt(skill), 0, vetRanks.Length - 1);
        return vetRanks[b];
    }

    // Re-bake turret aim timings for the new skill (skill is baked at init, not read live).
    static void VetRebake(VetState s, float newSkill)
    {
        if (vetLockTime == null || newSkill <= 0f) return;
        var u = s.v;
        var stations = u.weaponStations;
        if (stations != null)
            for (int i = 0; i < stations.Count; i++)
            {
                var ws = stations[i]; if (ws == null) continue;
                var turrets = ws.Turrets; if (turrets == null) continue;
                for (int j = 0; j < turrets.Count; j++)
                {
                    var t = turrets[j]; if (t == null) continue;
                    int tid = t.GetInstanceID();
                    float last = vetTurretSkill.TryGetValue(tid, out var lv) ? lv : s.baseSkill;
                    if (last <= 0f) last = 0.1f;
                    try
                    {
                        vetLockTime(t) *= last / newSkill;      // lockTime = orig/skill  -> orig/newSkill
                        vetTraverse(t) *= newSkill / last;      // traverseRate = orig*skill -> orig*newSkill
                    }
                    catch { }
                    vetTurretSkill[tid] = newSkill;
                    // grant the elite faster-reassess perk live once a unit levels to skill >= EliteSkill (4)
                    if (newSkill >= EliteSkill && gtInterval != null && cfgGTReassess != null)
                    {
                        try { ref float f = ref gtInterval(t); if (cfgGTReassess.Value < f) f = cfgGTReassess.Value; } catch { }
                    }
                }
            }
    }

    // Spectate overlay: followed unit's rank, skill, earnings + current AI status.
    static void VetDrawOverlay()
    {
        if (!MasterOn || cfgOverlay == null || !cfgOverlay.Value || vetFollowing == null) return;
        CameraStateManager cam = SceneSingleton<CameraStateManager>.i;
        if (cam == null) return;
        Unit u = vetFollowing(cam);
        if (u == null || u.disabled || !(u is GroundVehicle || u is Aircraft || u is Ship)) return;   // ground + air + naval

        // show for ANY followed ground/air unit, even before its first kill: use the ledger if present, else live skill
        float credits = 0f, skill = GetUnitSkill(u);
        if (vet.TryGetValue(u.GetInstanceID(), out var s)) { credits = s.credits; skill = s.appliedSkill; }
        string rank = VetRankName(skill);
        float ownCost = u.definition != null ? u.definition.value : 0f;   // in $millions
        string trait = TraitsDisplay(u);
        string traitLine = string.IsNullOrEmpty(trait) ? "" : $"   [{trait}]";
        // credits & cost are already in $millions (UnitDefinition.value) — format with the game's own money reader
        string text = $"{u.name}  ({UnitConverter.ValueReading(ownCost)}){traitLine}\n{rank}   Skill {skill:0.0}\nEarned {UnitConverter.ValueReading(credits)}";

        // current AI activity: naval formation role, or ground tactical state (each module owns its own status string)
        string status = "";
        if (u is Ship shp) status = NavalStatus(shp);
        else if (u is GroundVehicle gvu) status = GroundStatus(gvu);
        if (!string.IsNullOrEmpty(status)) text += $"\n{status}";

        // place the label near the unit on screen if we can project it, else fall back to top-center.
        // Height grows with the actual line count (status can add tactical + routing lines).
        int lines = 1; for (int i = 0; i < text.Length; i++) if (text[i] == '\n') lines++;
        float w = 300f, h = 12f + 20f * lines, x = (Screen.width - w) * 0.5f, y = 12f;
        Camera c = Camera.main;
        if (c != null)
        {
            Vector3 sp = c.WorldToScreenPoint(u.transform.position + Vector3.up * (u.maxRadius + 3f));
            if (sp.z > 0f) { x = Mathf.Clamp(sp.x - w * 0.5f, 4f, Screen.width - w - 4f); y = Mathf.Clamp(Screen.height - sp.y - h, 4f, Screen.height - h - 4f); }
        }
        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.Box(new Rect(x, y, w, h), GUIContent.none);
        GUI.color = new Color(1f, 0.92f, 0.55f, 1f);
        GUI.Label(new Rect(x + 8f, y + 6f, w - 16f, h - 12f), text);
        GUI.color = prev;
    }

    // Unity OnGUI on the plugin MonoBehaviour (host only).
    void OnGUI()
    {
        try { VetDrawOverlay(); }
        catch (Exception ex) { Log?.LogWarning("[Veterancy] overlay error: " + ex); }
    }
}

// Credit a unit's killers when it dies (server-authoritative).
[HarmonyPatch(typeof(Unit), "ReportKilled")]
public static class Unit_ReportKilled_Patch
{
    static void Postfix(Unit __instance)
    {
        try { ImprovedAIPlugin.VetOnKilled(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("report-killed patch error: " + ex); }
    }
}
