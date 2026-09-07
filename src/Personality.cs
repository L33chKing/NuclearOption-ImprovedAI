// Personality: random per-unit trait lottery (ground/air/naval stat + behaviour traits). Host-side only.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

public partial class ImprovedAIPlugin
{
    internal enum Trait
    {
        Normal,
        // stat (ground + air)
        Ace, Rookie, Tough, Fragile,
        // stat (ground only)
        Juggernaut, Nimble,
        // behavioural (ground only — hook into GroundAI movement)
        Brave, Cautious, LoneWolf, Coward, Tenacious, Reckless, Ambusher, Marksman, Hunter,
        // targeting (ground only — hook into GroundTargetingAI scoring)
        JetEnvious, FlySwatter, TeamPlayer, Vengeful,
    }

    // Full trait sets per unit kind (mission-editor dropdown). The random lottery uses the *Lottery* pools below.
    static readonly Trait[] groundPool =
    {
        Trait.Ace, Trait.Rookie, Trait.Tough, Trait.Fragile, Trait.Juggernaut, Trait.Nimble,
        Trait.Brave, Trait.Cautious, Trait.LoneWolf, Trait.Coward, Trait.Tenacious, Trait.Reckless,
        Trait.Ambusher, Trait.Marksman, Trait.Hunter,
        Trait.JetEnvious, Trait.FlySwatter, Trait.TeamPlayer, Trait.Vengeful,
    };
    static readonly Trait[] airPool = { Trait.Ace, Trait.Rookie, Trait.Tough, Trait.Fragile };
    static readonly Trait[] navalPool = { Trait.Ace, Trait.Rookie, Trait.Tough, Trait.Fragile, Trait.Juggernaut };

    // Random lottery pools (behavioural traits only; stat traits are editor-assign only, never rolled).
    static readonly Trait[] groundLotteryPool =
    {
        Trait.Brave, Trait.Cautious, Trait.LoneWolf, Trait.Coward, Trait.Tenacious, Trait.Reckless,
        Trait.Ambusher, Trait.Marksman,
        Trait.JetEnvious,
    };
    static readonly Trait[] airLotteryPool = new Trait[0];
    static readonly Trait[] navalLotteryPool = new Trait[0];

    // §10 Personalities removed from F1 config (menu de-spam) — always on, fixed at the user's last values.
    const bool PersonGround = true, PersonAir = true, PersonNaval = true;
    const float PersonTraitChance = 0.6f;
    // Kill-switch: trait system off (no rolls, no stats, hooks inert). Skill clamping stays on.
    // To re-enable: set TraitsEnabled = true.
    const bool TraitsEnabled = false;

    static AccessTools.FieldRef<GroundVehicle, float> gvTopOn, gvTopOff, gvAccel;
    static AccessTools.FieldRef<GroundVehicle, float> gvSpring, gvDamping;   // suspension stiffness + damping (elite ground-stick; bound separately so a bad name can't break speed)
    // A unit can carry MULTIPLE traits (editor-assigned units can have several; a random-rolled unit gets 0-1).
    // The list holds only real traits (Trait.Normal is never stored — an empty/absent list means "plain").
    static readonly Dictionary<int, List<Trait>> personality = new Dictionary<int, List<Trait>>();
    static readonly HashSet<int> statApplied = new HashSet<int>();   // health/speed applied once (postfix)
    static bool personBroken;

    void PersonalityConfig()
    {
        // Personalities config removed from F1 — values now the PersonGround/…/PersonTraitChance consts above.
        try
        {
            gvTopOn = AccessTools.FieldRefAccess<GroundVehicle, float>("topSpeedOnroad");
            gvTopOff = AccessTools.FieldRefAccess<GroundVehicle, float>("topSpeedOffroad");
            gvAccel = AccessTools.FieldRefAccess<GroundVehicle, float>("acceleration");
        }
        catch (System.Exception ex) { personBroken = true; Logger.LogError("[Personality] speed field init failed (stat speed traits off): " + ex); }
        try
        {
            gvSpring = AccessTools.FieldRefAccess<GroundVehicle, float>("springRate");
            gvDamping = AccessTools.FieldRefAccess<GroundVehicle, float>("dampingRate");
        }
        catch (System.Exception ex) { gvSpring = null; gvDamping = null; Logger.LogWarning("[Elite] suspension fields unavailable (ground-stick boost off): " + ex.Message); }
    }

    // Trait lookups for the GroundAI / GroundTargeting hooks.
    internal static bool HasTrait(Unit u, Trait t)
    {
        if (!TraitsEnabled) return false;   // kill-switch: all trait hooks inert (see const above)
        if (u == null) return false;
        return personality.TryGetValue(u.GetInstanceID(), out var list) && list != null && list.Contains(t);
    }

    internal static List<Trait> GetTraits(Unit u)
    {
        if (u != null && personality.TryGetValue(u.GetInstanceID(), out var list) && list != null) return list;
        return null;
    }

    // Comma-joined trait names for the overlay ("" = plain unit).
    internal static string TraitsDisplay(Unit u)
    {
        var list = GetTraits(u);
        return (list == null || list.Count == 0) ? "" : string.Join(",", list);
    }

    // Init prefix: roll/store the trait + apply SKILL traits (before the weapon-timing bake).
    internal static void PersonalityRoll(Unit u)
    {
        if (!MasterOn || u == null) return;
        if (!TraitsEnabled) return;   // kill-switch: no rolls, no assignments stored (reversible — see const above)
        bool ground = u is GroundVehicle, air = u is Aircraft, naval = u is Ship;
        if (!ground && !air && !naval) return;                         // ground + air + naval
        if (air && PlayerProtected(u)) return;                        // human-piloted aircraft left stock (unless §1 Affect Players)
        try { if (!u.IsServer) return; } catch { }                    // host-authoritative only
        int id = u.GetInstanceID();
        if (personality.ContainsKey(id)) return;

        // An explicit mission-editor assignment (section 12) OVERRIDES the random roll and may pin SEVERAL traits.
        // A unit the author left unassigned still rolls a single random trait, per the section-10 settings.
        List<Trait> traits;
        if (TryGetAssignedTraits(u, out var assigned) && assigned.Count > 0)
        {
            // Assigned list may contain Trait.Normal alone (= "force plain"); strip Normal and de-dup to the real set.
            traits = new List<Trait>();
            foreach (var at in assigned) if (at != Trait.Normal && !traits.Contains(at)) traits.Add(at);
            if (cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[Personality] {u.name} -> [{string.Join(",", traits)}] (assigned in mission)");
        }
        else
        {
            if (ground && !PersonGround) return;
            if (air && !PersonAir) return;
            if (naval && !PersonNaval) return;
            traits = new List<Trait>();
            var pool = ground ? groundLotteryPool : air ? airLotteryPool : navalLotteryPool;   // behavioural-only lottery
            if (pool.Length > 0 && UnityEngine.Random.value < PersonTraitChance)
                traits.Add(pool[UnityEngine.Random.Range(0, pool.Length)]);
            if (traits.Count > 0 && cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[Personality] {u.name} -> {traits[0]}");
        }

        personality[id] = traits;
        foreach (var t in traits)
        {
            if (t == Trait.Ace) ScaleSkill(u, 1.5f);
            else if (t == Trait.Rookie) ScaleSkill(u, 0.6f);
        }
    }

    // Init postfix: apply HEALTH + SPEED (part registration resets hitPoints mid-init, so not in the prefix).
    internal static void PersonalityApplyStats(Unit u)
    {
        if (!MasterOn || u == null) return;
        if (!TraitsEnabled) return;   // kill-switch: no health/speed scaling (reversible — see const above)
        // Presence in `personality` (random OR assigned) is the signal to apply — don't re-gate on the random toggle,
        // or an editor-assigned trait wouldn't get its health/speed while the random system is off.
        int id = u.GetInstanceID();
        if (!personality.TryGetValue(id, out var list) || list == null || list.Count == 0 || statApplied.Contains(id)) return;
        statApplied.Add(id);
        bool ground = u is GroundVehicle;
        // Stat traits STACK (e.g. Tough + Nimble scales both health and speed; Tough + Juggernaut = x1.5 x2 health).
        foreach (var t in list)
        {
            switch (t)
            {
                case Trait.Tough: ScaleHealth(u, 1.5f); break;
                case Trait.Fragile: ScaleHealth(u, 0.6f); break;
                case Trait.Juggernaut: ScaleHealth(u, 2f); break;                        // 2x health, NO speed penalty (slow units jam convoys)
                case Trait.Nimble: if (ground) ScaleSpeed((GroundVehicle)u, 1.4f, 2f); break; // +40% top speed (on & off road) + 2x acceleration
                // Ace/Rookie handled in the prefix; behavioural traits read live by GroundAI
            }
        }
    }

    static int ScaleHealth(Unit u, float m)
    {
        var parts = u.GetAllParts();
        if (parts == null || parts.Count == 0)
        { if (cfgDiag != null && cfgDiag.Value) Log?.LogWarning($"[Personality] {u.name} health x{m}: NO PARTS"); return 0; }
        int n = 0;
        for (int i = 0; i < parts.Count; i++) if (parts[i] != null) { parts[i].hitPoints *= m; n++; }
        if (cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[Personality] {u.name} health x{m} on {n} parts");
        return n;
    }

    // Scales top speeds + acceleration (C# fields AND the Burst job's one-time copy).
    static void ScaleSpeed(GroundVehicle v, float speedMul, float accelMul)
    {
        if (gvTopOn == null || personBroken)
        { if (cfgDiag != null && cfgDiag.Value) Log?.LogWarning($"[Personality] {v.name} speed UNAVAILABLE (broken={personBroken})"); return; }
        try
        {
            float on0 = gvTopOn(v);
            // 1) the C# instance fields (source for GetTopSpeed + the one-time GetOrCreateJobField copy)
            gvTopOn(v) *= speedMul; gvTopOff(v) *= speedMul; gvAccel(v) *= accelMul;
            // 2) CRITICAL: GetOrCreateJobField copies topSpeed/accel into the Burst job struct ONCE and UpdateJobFields
            //    never refreshes them — so if the job is already created, patch its copy too or the change is ignored.
            if (gvJobFields != null)
            {
                ref var jf = ref gvJobFields(v);
                if (jf.IsCreated) { jf.Ref().topSpeedOnroad *= speedMul; jf.Ref().topSpeedOffroad *= speedMul; jf.Ref().acceleration *= accelMul; }
            }
            if (cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[Personality] {v.name} speed x{speedMul} accel x{accelMul} (topOnroad {on0:0.0}->{gvTopOn(v):0.0})");
        }
        catch (Exception ex) { Log?.LogWarning("[Personality] speed scale error: " + ex); }
    }

    // Ground-stick: scale mass+spring+damping together (same suspension feel, more grip for extra speed).
    static void ScaleGroundStick(GroundVehicle v, float mul)
    {
        try
        {
            if (v.rb != null) v.rb.mass *= mul;                                   // weight (job snapshots mass from rb)
            if (gvSpring != null) gvSpring(v) *= mul;
            if (gvDamping != null) gvDamping(v) *= mul;
            if (gvJobFields != null)
            {
                ref var jf = ref gvJobFields(v);
                if (jf.IsCreated)
                {
                    jf.Ref().mass *= mul;
                    jf.Ref().springRate *= mul;
                    jf.Ref().dampingRate *= mul;
                }
            }
            if (cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[Elite] {v.name} ground-stick x{mul:0.00} (mass/spring/damping; rb.mass -> {(v.rb != null ? v.rb.mass : 0f):0})");
        }
        catch (Exception ex) { Log?.LogWarning("[Elite] ground-stick scale error: " + ex); }
    }

    // Hard-cap final skill after all multipliers stack (runs pre weapon-timing bake). Only lowers, never raises.
    internal static void ClampSkill(Unit u)
    {
        if (!MasterOn || u == null) return;
        if (!(u is GroundVehicle || u is Aircraft || u is Ship)) return;
        if (PlayerProtected(u)) return;                               // human-piloted aircraft left stock (unless §1 Affect Players)
        try { if (!u.IsServer) return; } catch { }
        float s = GetUnitSkill(u);
        if (s > SkillCap)
        {
            SetUnitSkill(u, SkillCap);
            if (cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[Skill] {u.name} capped {s:0.0} -> {SkillCap:0.0}");
        }
    }

    // Skill multiplier (applied pre weapon bake).
    static void ScaleSkill(Unit u, float m)
    {
        float before = GetUnitSkill(u);
        if (u is GroundVehicle gv) gv.skill *= m;
        else if (u is Aircraft ac) ac.skill *= m;
        else if (u is Ship sh) sh.skill *= m;
        if (cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[Personality] {u.name} skill x{m} ({before:0.00}->{GetUnitSkill(u):0.00})");
    }
}

// Roll traits at unit init: skill in the prefix (pre weapon bake), health/speed in the postfix.
[HarmonyPatch(typeof(Unit), "InitializeUnit")]
public static class Unit_InitializeUnit_Patch
{
    static void Prefix(Unit __instance)
    {
        try { ImprovedAIPlugin.PersonalityRoll(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("personality roll error: " + ex); }
        try { ImprovedAIPlugin.ClampSkill(__instance); }   // cap final skill before the weapon-timing bake reads it
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("skill clamp error: " + ex); }
    }
    static void Postfix(Unit __instance)
    {
        try { ImprovedAIPlugin.PersonalityApplyStats(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("personality stats error: " + ex); }
    }
}
