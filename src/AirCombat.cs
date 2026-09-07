// AirCombat: notch-vs-intercept decision + jammer conservation. We decide; vanilla flies the beam.

using System;
using HarmonyLib;
using UnityEngine;

public partial class ImprovedAIPlugin
{
    // ===== Defence (notch decision + jammer conservation), skill-gated =====
    const float NotchTurnEff = 0.7f;       // fraction of the plane's physical max-G turn rate usable for a beam maneuver
    const float NotchJamPowerMin = 0.5f;   // capacitor charge (0-1) below which a jam-reliant notch is judged unviable
    const float JamSmartMinSkill = 2f;     // skill at/above which the AI conserves jammer capacitor
    const float JamWhenTTI = 4f;           // only jam a radar missile within this many seconds; farther out, recharge
    static AccessTools.FieldRef<Countermeasure, Aircraft> jammerAircraft;

    void AirCombatConfig()
    {
        try { jammerAircraft = AccessTools.FieldRefAccess<Countermeasure, Aircraft>("aircraft"); }
        catch (Exception ex) { jammerAircraft = null; Logger.LogWarning("[Defence] Countermeasure.aircraft unavailable (jammer mgmt off): " + ex); }
    }

    // Can this plane beam (notch) a radar missile in time? Needs turn geometry + jammer charge.
    internal static bool NotchViable(Aircraft ac, float angleOffNose, float impactTime, out string why)
    {
        if (ac == null) { why = "no aircraft"; return false; }
        var ps = ac.GetPowerSupply();
        if (ps != null && ps.GetCharge() < NotchJamPowerMin) { why = $"low jam power ({ps.GetCharge():0.00}) -> intercept"; return false; }

        float angleFromBeam = Mathf.Abs(90f - angleOffNose);
        float gLimit = 9f;
        try { gLimit = ac.definition.aircraftParameters.aircraftGLimit; } catch { }
        try { gLimit *= EliteFactor(GetUnitSkill(ac), EliteFullMult); } catch { }   // elite planes pull more G -> notch faster
        float v = Mathf.Max(ac.speed, 30f);
        float turnRateDegS = Mathf.Max(gLimit * 9.81f / v * Mathf.Rad2Deg * NotchTurnEff, 1f);   // physical max-G turn rate, derated
        float timeToBeam = angleFromBeam / turnRateDegS;                                          // no artificial reaction buffer
        if (impactTime < timeToBeam) { why = $"can't notch in time (need {timeToBeam:0.0}s @ {turnRateDegS:0}deg/s, have {impactTime:0.0}s) -> intercept"; return false; }
        why = $"notching (beam {timeToBeam:0.0}s @ {turnRateDegS:0}deg/s < impact {impactTime:0.0}s)";
        return true;
    }

    // Skilled AI holds jammer fire until a radar missile is actually close (saves capacitor).
    internal static bool JammerShouldFire(RadarJammer jammer)
    {
        if (!MasterOn || jammerAircraft == null || jammer == null) return true;
        Aircraft ac; try { ac = jammerAircraft(jammer); } catch { return true; }
        if (ac == null || ac.Player != null || ac.remoteSim) return true;   // only manage host-simulated AI planes
        if (ac.skill < JamSmartMinSkill) return true;                        // low skill = no conservation (dumb but harmless)

        MissileWarning mws = ac.GetMissileWarningSystem();
        if (mws == null) return true;
        Vector3 p = ac.transform.position;
        Vector3 av = ac.rb != null ? ac.rb.velocity : Vector3.zero;
        float nearest = float.MaxValue;
        foreach (var m in mws.knownMissiles)
        {
            if (m == null || m.disabled || m.targetID != ac.persistentID) continue;
            string s = Seeker(m); if (!(s == "ARH" || s.Contains("SARH"))) continue;   // jammer only counters radar missiles
            Vector3 rel = m.transform.position - p; float dist = rel.magnitude;
            Vector3 mv = m.rb != null ? m.rb.velocity : Vector3.zero;
            float closing = Mathf.Max(Vector3.Dot(-rel.normalized, mv - av), 1f);
            nearest = Mathf.Min(nearest, dist / closing);
        }
        return nearest <= JamWhenTTI;   // jam only when a radar missile is within JamWhenTTI s; else block (recharge)
    }
}

// Block a skilled AI plane's radar jammer until a radar missile is close (capacitor conservation).
[HarmonyPatch(typeof(RadarJammer), "Fire")]
public static class RadarJammer_Fire_Patch
{
    static bool Prefix(RadarJammer __instance)
    {
        try { return ImprovedAIPlugin.JammerShouldFire(__instance); }
        catch { return true; }
    }
}
