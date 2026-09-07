// MissileIntercept: AI missile self-defence (notch, laser, interceptor). Never overrides AI mode/target/flight.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

public partial class ImprovedAIPlugin
{
    // Missile config: one toggle, everything else fixed. Decisions (range/timing/skill) are automatic.
    static ConfigEntry<bool> cfgInterceptEnabled, cfgStopEvading;
    const float IntCheckInterval = 0.2f, IntCooldown = 3f, IntTimeMargin = 1.0f, IntMinEff = 0.1f;
    // Detect range scales with intercept skill: DetectRangeMin @IntMinSkill -> DetectRangeMax @IntMaxSkill.
    const float DetectRangeMin = 15000f, DetectRangeMax = 30000f;
    const float ConeFallback = 50f;                         // used only if an interceptor weapon has no minAlignment set
    const bool  IRPreferFlares = true; const float IRFlareReserve = 0.1f, IRFlareTime = 2f;
    const bool  LaserEnabled = true; const float LaserMinCharge = 0.5f, LaserRangeFactor = 0.9f;
    const bool  SkillAffects = true; const float IntMinSkill = 1.5f, IntMaxSkill = 6f, ReactMaxSkill = 0.1f, ReactMinSkill = 4f;
    // (Notch viability + jammer capacitor conservation now live in AirCombat.cs — this module just calls NotchViable.)

    // ---- missile runtime ----
    class LaserEngagement { public byte station; public Missile target; }
    static readonly Dictionary<int, float> nextCheck = new Dictionary<int, float>();
    static readonly Dictionary<int, float> lastFiredAtMissile = new Dictionary<int, float>();
    static readonly Dictionary<int, float> firstSeen = new Dictionary<int, float>();   // missile id -> first detection time (skill reaction)
    static readonly Dictionary<int, LaserEngagement> laser = new Dictionary<int, LaserEngagement>();
    static readonly List<Missile> incoming = new List<Missile>();
    static float lastPrune, lastHeartbeat;
    static int hbRaw, hbUpdates, hbWithMissiles, hbShots;

    static AccessTools.FieldRef<MissileWarning, Aircraft> mwAircraft;

    // Bind config + init hooks (called from Awake).
    void MissileConfig()
    {
        cfgInterceptEnabled = Config.Bind("2. Missile Intercept", "Enabled", true,
            "AI defends itself against incoming missiles it can't beat with countermeasures — notching, lasering, or " +
            "firing an interceptor as appropriate. All timing/range/turn-rate/skill decisions are automatic (no tuning).");
        cfgStopEvading = Config.Bind("2. Missile Intercept", "Stop Evading When Intercepting", true,
            "Once an interceptor is inbound to a missile, drop it from the AI's threat list so it stops evading and " +
            "resumes its task (evasion resumes automatically if the interceptor fails). Kept as a toggle for testing.");
        mwAircraft = AccessTools.FieldRefAccess<MissileWarning, Aircraft>("aircraft");
    }

    // Main hook: every physics frame while an AI pilot is in the combat state.
    internal static void OnCombatFixedUpdate(Pilot pilot)
    {
        if (!MasterOn || !cfgInterceptEnabled.Value || pilot == null) return;
        float now = Time.timeSinceLevelLoad;
        hbRaw++;
        Heartbeat(now);

        Aircraft ac = pilot.aircraft;
        if (ac == null || ac.disabled) return;
        if (ac.remoteSim) return;   // only where this AI is actually simulated

        int acid = ac.GetInstanceID();
        if (nextCheck.TryGetValue(acid, out float nc) && now < nc) return;
        nextCheck[acid] = now + Mathf.Max(IntCheckInterval, 0.05f);

        hbUpdates++;
        try { Evaluate(ac, acid, now); }
        catch (Exception ex) { Log.LogWarning("intercept error: " + ex); }
    }

    static void Heartbeat(float now)
    {
        if (!cfgDiag.Value || now - lastHeartbeat < 5f) return;
        lastHeartbeat = now;
        Log.LogInfo($"[ImprovedAI] alive raw={hbRaw} sim={hbUpdates} withInc={hbWithMissiles} shots={hbShots} | cfg intercept={cfgInterceptEnabled.Value} minEff={IntMinEff:0.00} margin={IntTimeMargin:0.00} laser={LaserEnabled} stopEvade={cfgStopEvading.Value}");
        hbRaw = 0; hbUpdates = 0; hbWithMissiles = 0; hbShots = 0;
    }

    static void Evaluate(Aircraft ac, int acid, float now)
    {
        CollectIncoming(ac);
        bool laserBusy = MaintainLaser(ac, acid);   // hold or release an existing laser engagement
        if (incoming.Count == 0) return;
        hbWithMissiles++;
        bool diag = cfgDiag.Value;

        foreach (Missile inc in incoming)
        {
            int mid = inc.GetInstanceID();
            if (laser.TryGetValue(acid, out var le) && le.target == inc) continue; // already lasing this one
            if (lastFiredAtMissile.TryGetValue(mid, out float last) && now - last < IntCooldown) continue;
            if (ac.NetworkHQ != null)
            {
                var ti = ac.NetworkHQ.GetTrackingData(inc.persistentID);
                if (ti != null && ti.missileAttacks > 0) continue; // a friendly interceptor is already inbound
            }

            // AI skill: disabled at/below Min Skill; otherwise a reaction delay that shrinks with skill
            if (SkillAffects)
            {
                float skill = ac.skill;
                if (skill <= IntMinSkill)
                { if (diag) Log.LogInfo($"[Intercept] skill {skill:0.00} <= min {IntMinSkill:0.00} -> no intercept"); continue; }
                if (!firstSeen.TryGetValue(mid, out float seen)) { seen = now; firstSeen[mid] = now; }
                float t = Mathf.InverseLerp(IntMinSkill, IntMaxSkill, skill);
                float react = Mathf.Lerp(ReactMinSkill, ReactMaxSkill, t);
                if (now - seen < react)
                { if (diag) Log.LogInfo($"[Intercept] skill {skill:0.00} reacting {now - seen:0.0}/{react:0.0}s"); continue; }
            }

            Decide(ac, inc, laserBusy, out string reason, out WeaponStation missileStation, out WeaponStation energyStation);
            if (diag) Log.LogInfo($"[Intercept] {ac.definition?.unitName} (skill {ac.skill:0.00}) vs {Name(inc.GetWeaponInfo())} [{Seeker(inc)}] -> {reason}");

            if (energyStation != null && !laserBusy)
            {
                StartLaser(ac, acid, energyStation, inc);
                laserBusy = true; lastFiredAtMissile[mid] = now; hbShots++;
            }
            else if (missileStation != null)
            {
                FireInterceptor(ac, missileStation, inc);
                lastFiredAtMissile[mid] = now; hbShots++;
            }
        }
        PruneState(now);
    }

    // Detect range scales with skill (15km @1.5 -> 30km @6).
    static float DetectRange(float skill) => Mathf.Lerp(DetectRangeMin, DetectRangeMax, Mathf.InverseLerp(IntMinSkill, IntMaxSkill, skill));
    // A weapon's max off-boresight launch angle (falls back to ConeFallback).
    static float LaunchCone(WeaponInfo wi) { float a = wi != null ? wi.targetRequirements.minAlignment : 0f; return a > 0.5f ? a : ConeFallback; }

    static void CollectIncoming(Aircraft ac)
    {
        incoming.Clear();
        float range = DetectRange(ac.skill);
        float maxSqr = range * range;
        Vector3 p = ac.transform.position;

        MissileWarning mws = ac.GetMissileWarningSystem();
        if (mws != null)
            foreach (Missile m in mws.knownMissiles)
                if (Valid(m, ac) && (m.transform.position - p).sqrMagnitude <= maxSqr) incoming.Add(m);

        // Deep scan (faction DB, not just my own warning receiver) — always on.
        if (ac.NetworkHQ != null)
            foreach (var kv in ac.NetworkHQ.trackingDatabase)
            {
                if (!kv.Value.TryGetUnit(out var u) || !(u is Missile m) || !Valid(m, ac)) continue;
                if ((m.transform.position - p).sqrMagnitude > maxSqr || incoming.Contains(m)) continue;
                incoming.Add(m);
            }
    }

    static bool Valid(Missile m, Aircraft ac) => m != null && !m.disabled && m.targetID == ac.persistentID;

    // Pick response per incoming missile: notch (defer to vanilla), laser, or interceptor. Null stations = skip.
    static void Decide(Aircraft ac, Missile inc, bool laserBusy, out string reason, out WeaponStation missileStation, out WeaponStation energyStation)
    {
        missileStation = null; energyStation = null;

        Vector3 rel = inc.transform.position - ac.transform.position;
        float dist = rel.magnitude;
        Vector3 incVel = inc.rb != null ? inc.rb.velocity : Vector3.zero;
        Vector3 acVel = ac.rb != null ? ac.rb.velocity : Vector3.zero;
        float closing = Mathf.Max(Vector3.Dot(-rel.normalized, incVel - acVel), 1f);
        float impactTime = dist / closing;
        float angleOffNose = Vector3.Angle(rel, ac.transform.forward);

        string seeker = Seeker(inc);
        bool isIR = seeker == "IR";
        bool isRadar = seeker.Contains("ARH");                 // ARH, SARH

        // Only IR and radar missiles are worth an interceptor; the rest the AI dodges or ignores.
        if (!isIR && !isRadar) { reason = $"seeker '{seeker}' not intercepted (optical/INS/ARAD/unknown)"; return; }

        // Laser shot available? "frontal" for the laser = within the ENERGY WEAPON's own launch cone (its minAlignment).
        WeaponStation energy = LaserEnabled ? FindReadyEnergyWeapon(ac) : null;
        bool laserFrontal = energy != null && angleOffNose <= LaunchCone(energy.WeaponInfo);
        bool laserAvail = laserFrontal && LaserInRange(energy, dist)
                          && ac.GetPowerSupply() != null && ac.GetPowerSupply().GetCharge() >= LaserMinCharge;

        // Interceptor pre-selected; launch cone widens with skill (1x -> 3x).
        WeaponStation st = FindBestInterceptor(ac, inc);
        float coneSkill = Mathf.Lerp(1f, 3f, Mathf.InverseLerp(IntMinSkill, IntMaxSkill, ac.skill));
        float interceptorCone = (st != null ? LaunchCone(st.WeaponInfo) : ConeFallback) * coneSkill;
        bool withinInterceptorCone = angleOffNose <= interceptorCone;

        // 1) Defence choice: IR flares first; radar notches if viable, else intercept.
        if (isIR && IRPreferFlares)
        {
            float flares = ac.countermeasureManager != null ? ac.countermeasureManager.GetFlareAmmoProportion() : 0f;
            if (flares > IRFlareReserve && impactTime > IRFlareTime) { reason = $"IR: flaring (flares {flares:0.00})"; return; }
        }
        else if (isRadar)
        {
            // Defer to the vanilla notch if AirCombat judges it viable (geometry + jam power); else fall through to intercept.
            if (!laserAvail && NotchViable(ac, angleOffNose, impactTime, out string notchWhy))
            { reason = $"radar: {notchWhy}"; return; }
        }
        // optical(on)/ARAD/unknown -> proceed to intercept

        // 2) Prefer laser for frontal missiles
        if (laserAvail)
        {
            if (laserBusy) { reason = "laser busy on another missile"; return; }
            energyStation = energy;
            reason = $"LASER (frontal {angleOffNose:0}deg, dist {dist:0}m)";
            return;
        }

        // 3) Interceptor missile — effective vs THIS missile type (excludes A2G) AND within its launch cone
        if (st == null) { reason = "no effective interceptor missile"; return; }
        if (!withinInterceptorCone) { reason = $"outside launch cone ({angleOffNose:0}>{interceptorCone:0}deg)"; return; }

        WeaponInfo wi = st.WeaponInfo;
        if (dist > wi.targetRequirements.maxRange) { reason = $"out of range ({dist:0}>{wi.targetRequirements.maxRange:0})"; return; }
        if (dist < wi.targetRequirements.minRange) { reason = $"too close ({dist:0}<{wi.targetRequirements.minRange:0})"; return; }
        float interceptorSpeed = Mathf.Max(wi.GetMaxSpeed(), wi.muzzleVelocity, 1f);
        float meetTime = dist / (interceptorSpeed + Mathf.Max(closing, 0f));
        if (meetTime > impactTime * IntTimeMargin) { reason = $"no time (meet {meetTime:0.0}s vs impact {impactTime:0.0}s)"; return; }

        missileStation = st;
        reason = $"FIRE {Name(wi)} (dist {dist:0}m, impact {impactTime:0.0}s, meet {meetTime:0.0}s)";
    }

    // (NotchViable lives in AirCombat.cs.)

    // True air-to-air missiles only (IR/ARH/SARH seeker) — never A2G/bombs/guns. Cached per WeaponInfo.
    static readonly Dictionary<WeaponInfo, bool> airWeaponCache = new Dictionary<WeaponInfo, bool>();
    static bool IsAirInterceptWeapon(WeaponInfo wi)
    {
        if (wi == null) return false;
        if (airWeaponCache.TryGetValue(wi, out bool cached)) return cached;
        bool ok = ComputeAirInterceptWeapon(wi);
        airWeaponCache[wi] = ok;
        return ok;
    }
    static bool ComputeAirInterceptWeapon(WeaponInfo wi)
    {
        if (!wi.missile || wi.bomb || wi.glideBomb || wi.gun || wi.nuclear || wi.cargo || wi.troops || wi.rearmGround || wi.rearmShip || wi.sling) return false;
        var prefab = wi.weaponPrefab;
        if (prefab == null) return false;
        var seeker = prefab.GetComponent<MissileSeeker>();
        if (seeker == null) return false;
        string s; try { s = seeker.GetSeekerType() ?? ""; } catch { return false; }
        return s == "IR" || s == "ARH" || s == "SARH";   // air-intercept guidance only (never Optical/INS/Laser/ARAD = A2G)
    }

    // Best-value interceptor: air rating per cost.
    static WeaponStation FindBestInterceptor(Aircraft ac, Missile inc)
    {
        WeaponStation best = null; float bestScore = 0f;
        foreach (var st in ac.weaponStations)
        {
            if (st == null || st.Cargo) continue;
            WeaponInfo wi = st.WeaponInfo;
            if (!IsAirInterceptWeapon(wi)) continue;             // A2A missile (IR/ARH/SARH) only — never A2G/bomb/optical/INS
            if (st.Ammo <= 0 || !st.Ready()) continue;
            float eff = Mathf.Max(wi.effectiveness.antiAir, wi.effectiveness.antiMissile);
            if (eff < IntMinEff) continue;
            float score = eff / Mathf.Max(wi.costPerRound, 1f);  // value = capability per cost
            if (score > bestScore) { bestScore = score; best = st; }
        }
        return best;
    }

    static void FireInterceptor(Aircraft ac, WeaponStation station, Missile inc)
    {
        station.SetStationTargets(new PersistentID[] { inc.persistentID });
        Vector3 dir = (inc.transform.position - ac.transform.position).normalized;
        station.LaunchMount(ac, inc, ac.GlobalPosition() + dir * 50000f);
    }

    // ---- laser (energy weapon) held on target, no flicker ----
    static WeaponStation FindReadyEnergyWeapon(Aircraft ac)
    {
        foreach (var st in ac.weaponStations)
            if (st != null && st.WeaponInfo != null && st.WeaponInfo.energy && st.Ammo > 0 && st.Ready()) return st;
        return null;
    }

    static bool LaserInRange(WeaponStation st, float dist)
    {
        var tr = st.WeaponInfo.targetRequirements;
        return dist <= tr.maxRange * LaserRangeFactor && dist >= tr.minRange;
    }

    static void StartLaser(Aircraft ac, int acid, WeaponStation station, Missile inc)
    {
        station.SetStationTargets(new PersistentID[] { inc.persistentID });
        ac.SetFiringState(station.Number, true);
        laser[acid] = new LaserEngagement { station = station.Number, target = inc };
    }

    // Hold the laser on target until it dies/leaves the envelope (no flicker). True while engaged.
    static bool MaintainLaser(Aircraft ac, int acid)
    {
        if (!laser.TryGetValue(acid, out var le)) return false;
        bool keep = le.target != null && !le.target.disabled && le.target.targetID == ac.persistentID;
        WeaponStation st = keep ? StationByNumber(ac, le.station) : null;
        if (keep && (st == null || st.WeaponInfo == null || !st.WeaponInfo.energy)) keep = false;
        if (keep)
        {
            Vector3 rel = le.target.transform.position - ac.transform.position;
            float dist = rel.magnitude;
            float ang = Vector3.Angle(rel, ac.transform.forward);
            var tr = st.WeaponInfo.targetRequirements;
            var ps = ac.GetPowerSupply();
            // hysteresis: keep firing while roughly on-aim, in range, and any meaningful charge remains
            keep = ang <= LaunchCone(st.WeaponInfo) * 1.2f
                   && dist <= tr.maxRange * LaserRangeFactor && dist >= tr.minRange
                   && (ps == null || ps.GetCharge() > 0.05f);
        }
        if (keep) { ac.SetFiringState(le.station, true); return true; }   // idempotent — no flicker
        ac.SetFiringState(le.station, false);
        laser.Remove(acid);
        return false;
    }

    static WeaponStation StationByNumber(Aircraft ac, byte number)
    {
        foreach (var st in ac.weaponStations) if (st != null && st.Number == number) return st;
        return null;
    }

    static string Seeker(Missile m) { try { return m.GetSeekerType() ?? ""; } catch { return ""; } }
    static string Name(WeaponInfo wi) => wi == null ? "?" : (!string.IsNullOrEmpty(wi.shortName) ? wi.shortName : (wi.weaponName ?? "?"));

    static void PruneState(float now)
    {
        if (now - lastPrune < 10f) return;
        lastPrune = now;
        var stale = new List<int>();
        foreach (var kv in lastFiredAtMissile) if (now - kv.Value > 20f) stale.Add(kv.Key);
        foreach (var k in stale) lastFiredAtMissile.Remove(k);
        stale.Clear();
        foreach (var kv in firstSeen) if (now - kv.Value > 20f) stale.Add(kv.Key);
        foreach (var k in stale) firstSeen.Remove(k);
        stale.Clear();
        foreach (var kv in nextCheck) if (now - kv.Value > 60f) stale.Add(kv.Key);
        foreach (var k in stale) nextCheck.Remove(k);
    }

    // Stop treating a missile as a threat once a friendly interceptor is inbound to it.
    internal static void OnMissileWarningUpdated(MissileWarning mw)
    {
        if (!MasterOn || cfgStopEvading == null || !cfgStopEvading.Value || mw == null || mwAircraft == null) return;
        Aircraft ac;
        try { ac = mwAircraft(mw); } catch { return; }
        if (ac == null || ac.NetworkHQ == null) return;
        if (GameManager.IsLocalAircraft(ac)) return;   // never blind the human player's warning
        var km = mw.knownMissiles;
        for (int i = km.Count - 1; i >= 0; i--)
        {
            Missile m = km[i];
            if (m == null) continue;
            var ti = ac.NetworkHQ.GetTrackingData(m.persistentID);
            if (ti != null && ti.missileAttacks > 0) km.RemoveAt(i);   // interceptor inbound -> ignore it
        }
    }
}

// Combat-state driver hook.
[HarmonyPatch(typeof(AIPilotCombatModes), "FixedUpdateState")]
public static class AIPilotCombatModes_FixedUpdateState_Patch
{
    static void Postfix(Pilot pilot) => ImprovedAIPlugin.OnCombatFixedUpdate(pilot);
}

[HarmonyPatch(typeof(MissileWarning), "Update")]
public static class MissileWarning_Update_Patch
{
    static void Postfix(MissileWarning __instance) => ImprovedAIPlugin.OnMissileWarningUpdated(__instance);
}
