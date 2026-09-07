// GroundTargeting: faster ground-t turret reaction (scan cadence + elite reassess) + closest-first scoring,
// missile self-defence, multi-weapon fire, salvo, wider skill sliders, RWR anti-spam. Host-side.

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

public partial class ImprovedAIPlugin
{
    // ---- ground-targeting config (fixed: always on / always skill-scaled) ----
    static ConfigEntry<float> cfgGTReassess, cfgGTCloseWeight, cfgGTScan;
    static ConfigEntry<float> cfgRwrInterval;                              // RWR anti-spam (client HUD)
    static readonly Dictionary<int, float> rwrLastPing = new Dictionary<int, float>();

    const float EliteSkill = 4f;   // skill >= this = an "elite" ground unit (above vanilla max 4); unlocks faster turret reassess (A)
    const float GroundInterceptSkill = 2f;    // skill above this -> retask a gun turret onto an incoming direct-threat missile (E)
    const float GroundMultiWeaponSkill = 3f;  // skill above this -> fire ALL ready stations at the current target, not just the selected one (F)

    // ---- Turret private-field access ----
    static AccessTools.FieldRef<Turret, float> gtInterval;         // targetAssessmentInterval
    static AccessTools.FieldRef<Turret, FiringCone[]> gtFiringCones;
    static AccessTools.FieldRef<Turret, bool> gtOnlyDefensive;
    static AccessTools.FieldRef<Turret, float> gtArmorOpt;         // armorTierOptimism
    static AccessTools.FieldRef<Turret, Unit> gtTarget;
    static AccessTools.FieldRef<Turret, WeaponStation> gtCurrentWS;
    static AccessTools.FieldRef<Turret, List<Unit>> gtPotential;   // potentialTargets (threat injection keeps reassessments sticky)
    static AccessTools.FieldRef<Turret, float> gtTimeOnTarget, gtLockTime;
    static AccessTools.FieldRef<Turret, bool> gtManual;            // manual = player-controlled turret -> never retask
    static FieldInfo gtModeField;                                  // targetAcquisitionMode (private nested enum)
    static int gtDatalinkMode = -1;
    // ---- TargetDetector private-field access (detection scan cadence) ----
    static AccessTools.FieldRef<TargetDetector, float> gtCheckInterval, gtAlertInterval;
    // ---- game skill sliders to widen 2 -> 4: per-unit (mission editor) + faction surface/air multipliers (Customize Mission) ----
    static AccessTools.FieldRef<NuclearOption.MissionEditorScripts.VehicleOptions, UnityEngine.UI.Slider> gtSkillSlider;
    static AccessTools.FieldRef<NuclearOption.MissionEditorScripts.AircraftOptions, UnityEngine.UI.Slider> gtAcSkillSlider;
    static AccessTools.FieldRef<NuclearOption.MissionEditorScripts.ShipOptions, UnityEngine.UI.Slider> gtShipSkillSlider;
    static AccessTools.FieldRef<NuclearOption.MissionEditorScripts.FactionSettingsTab, UnityEngine.UI.Slider> gtSurfaceSkillSlider, gtAirSkillSlider;
    static bool gtFieldsBroken;                                    // core (Turret/Detector) reflection failure -> fall back to vanilla
    // ---- salvo (multi-target missile engagement) reflection — optional; failures degrade gracefully ----
    static AccessTools.FieldRef<Turret, bool> gtFireNoAim;         // firesWithoutAiming (VLS-style launchers don't need the aim cone)
    static AccessTools.FieldRef<Turret, object> gtFireControl;     // fireControl (units with one already multi-engage -> excluded)
    // ---- ground missile self-defence state ----
    static readonly Dictionary<int, float> gdNextScan = new Dictionary<int, float>();
    static readonly Dictionary<int, Turret[]> gdTurrets = new Dictionary<int, Turret[]>();

    // Bind config + init field access (called from Awake).
    void GroundTargetingConfig()
    {
        cfgGTReassess = Config.Bind("8. Ground Targeting", "Reassess Interval (s)", 0.5f, new ConfigDescription("Turret target re-pick interval for ELITE ground units ONLY (skill >= 3, set via the game's own mission skill sliders — which this mod widens to 4). Vanilla is 2s. Normal-skill units (1-2) are left completely vanilla. Lower = faster elite re-targeting.", new AcceptableValueRange<float>(0.1f, 2f)));
        cfgGTCloseWeight = Config.Bind("8. Ground Targeting", "Closest Priority Weight", 1f, new ConfigDescription("How strongly to prefer closer targets among those the weapon can actually hurt. 0 = vanilla opportunity/threat priority only; higher = closest-first (a point-blank target scores up to (1+weight)x). Validity/effectiveness gates are unchanged, so it never picks a target the weapon can't engage.", new AcceptableValueRange<float>(0f, 3f)));
        cfgGTScan = Config.Bind("8. Ground Targeting", "Detection Scan Interval (s)", 0.75f, new ConfigDescription("How often a ground-vehicle's own detector re-scans for new targets. THIS is the main cause of slow acquisition: vanilla is often ~2-3s and a target takes ~2 scans to register, giving the 4-6s reaction. Only ever speeds a detector up, never below its prefab value. Lower = faster spotting, more CPU in big battles (each scan does a spatial query + line-of-sight raycasts).", new AcceptableValueRange<float>(0.1f, 3f)));
        cfgRwrInterval = Config.Bind("11. Radar Warnings", "RWR Ping Min Interval (s)", 1.5f, new ConfigDescription("Minimum seconds between radar-warning pings (audio + visual) from the SAME emitter on your own aircraft. Stops the RWR spam caused by faster AI detection scans WITHOUT slowing detection or targeting (this only throttles the cockpit warning). 0 = vanilla (no throttle).", new AcceptableValueRange<float>(0f, 5f)));

        try
        {
            gtInterval = AccessTools.FieldRefAccess<Turret, float>("targetAssessmentInterval");
            gtFiringCones = AccessTools.FieldRefAccess<Turret, FiringCone[]>("firingCones");
            gtOnlyDefensive = AccessTools.FieldRefAccess<Turret, bool>("onlyDefensive");
            gtArmorOpt = AccessTools.FieldRefAccess<Turret, float>("armorTierOptimism");
            gtTarget = AccessTools.FieldRefAccess<Turret, Unit>("target");
            gtCurrentWS = AccessTools.FieldRefAccess<Turret, WeaponStation>("currentWeaponStation");
            gtPotential = AccessTools.FieldRefAccess<Turret, List<Unit>>("potentialTargets");
            gtTimeOnTarget = AccessTools.FieldRefAccess<Turret, float>("timeOnTarget");
            gtLockTime = AccessTools.FieldRefAccess<Turret, float>("lockTime");
            gtManual = AccessTools.FieldRefAccess<Turret, bool>("manual");
            gtModeField = AccessTools.Field(typeof(Turret), "targetAcquisitionMode");
            if (gtModeField != null && gtModeField.FieldType.IsEnum)
                gtDatalinkMode = Convert.ToInt32(Enum.Parse(gtModeField.FieldType, "datalink"));
            gtCheckInterval = AccessTools.FieldRefAccess<TargetDetector, float>("checkInterval");
            gtAlertInterval = AccessTools.FieldRefAccess<TargetDetector, float>("alertCheckInterval");
        }
        catch (Exception ex) { gtFieldsBroken = true; Logger.LogError("[GroundTargeting] field init failed (feature disabled): " + ex); }

        // Salvo reflection is optional — a failure here only degrades the salvo feature, never core targeting.
        try { gtFireNoAim = AccessTools.FieldRefAccess<Turret, bool>("firesWithoutAiming"); }
        catch (Exception ex) { gtFireNoAim = null; Logger.LogWarning("[GroundTargeting] firesWithoutAiming unavailable (salvo will require firing cones): " + ex.Message); }
        try { gtFireControl = AccessTools.FieldRefAccess<Turret, object>("fireControl"); }
        catch (Exception ex) { gtFireControl = null; Logger.LogWarning("[GroundTargeting] fireControl unavailable (can't detect already-multi-channel units): " + ex.Message); }

        // Slider access is optional — a failure here disables only that slider's extension, not targeting.
        try { gtSkillSlider = AccessTools.FieldRefAccess<NuclearOption.MissionEditorScripts.VehicleOptions, UnityEngine.UI.Slider>("skillSlider"); }
        catch (Exception ex) { gtSkillSlider = null; Logger.LogWarning("[GroundTargeting] per-unit skill-slider field unavailable: " + ex); }
        try { gtAcSkillSlider = AccessTools.FieldRefAccess<NuclearOption.MissionEditorScripts.AircraftOptions, UnityEngine.UI.Slider>("skillSlider"); }
        catch (Exception ex) { gtAcSkillSlider = null; Logger.LogWarning("[GroundTargeting] aircraft skill-slider field unavailable: " + ex); }
        try { gtShipSkillSlider = AccessTools.FieldRefAccess<NuclearOption.MissionEditorScripts.ShipOptions, UnityEngine.UI.Slider>("skillSlider"); }
        catch (Exception ex) { gtShipSkillSlider = null; Logger.LogWarning("[GroundTargeting] ship skill-slider field unavailable: " + ex); }
        try
        {
            gtSurfaceSkillSlider = AccessTools.FieldRefAccess<NuclearOption.MissionEditorScripts.FactionSettingsTab, UnityEngine.UI.Slider>("surfaceSkillMultiplerSlider");
            gtAirSkillSlider = AccessTools.FieldRefAccess<NuclearOption.MissionEditorScripts.FactionSettingsTab, UnityEngine.UI.Slider>("airSkillMultiplerSlider");
        }
        catch (Exception ex) { gtSurfaceSkillSlider = null; gtAirSkillSlider = null; Logger.LogWarning("[GroundTargeting] faction skill-multiplier slider fields unavailable: " + ex); }
    }

    // Faster reassess for elite ground turrets (skill>=4 or Fly-Swatter). Normal units stay vanilla.
    internal static void GroundTargetingOnTurretInit(Turret t)
    {
        if (!MasterOn || gtFieldsBroken || gtInterval == null || t == null) return;
        try
        {
            if (!(t.GetAttachedUnit() is GroundVehicle gv)) return;     // only ground vehicles
            if (gv.remoteSim) return;                                   // only where this AI is actually simulated
            if (gv.skill < EliteSkill && !HasTrait(gv, Trait.FlySwatter)) return;  // elite skill OR Fly-Swatter -> faster reassess (quicker air reaction)
            ref float f = ref gtInterval(t);
            if (cfgGTReassess.Value < f) f = cfgGTReassess.Value;      // only ever speed up
        }
        catch (Exception ex) { Log?.LogWarning("[GroundTargeting] init error: " + ex); }
    }

    // Widen the game's skill sliders 2 -> 4 (per-unit vehicle / aircraft / ship / faction multipliers).
    internal static void GroundTargetingExtendSkillSlider(NuclearOption.MissionEditorScripts.VehicleOptions vo)
    {
        if (gtSkillSlider == null || vo == null) return;
        try { var s = gtSkillSlider(vo); if (s != null && s.maxValue < 4f) s.maxValue = 4f; }
        catch (Exception ex) { Log?.LogWarning("[GroundTargeting] per-unit skill-slider extend error: " + ex); }
    }

    internal static void GroundTargetingExtendAcSkillSlider(NuclearOption.MissionEditorScripts.AircraftOptions ao)
    {
        if (gtAcSkillSlider == null || ao == null) return;
        try { var s = gtAcSkillSlider(ao); if (s != null && s.maxValue < 4f) s.maxValue = 4f; }
        catch (Exception ex) { Log?.LogWarning("[GroundTargeting] aircraft skill-slider extend error: " + ex); }
    }

    internal static void GroundTargetingExtendShipSkillSlider(NuclearOption.MissionEditorScripts.ShipOptions so)
    {
        if (gtShipSkillSlider == null || so == null) return;
        try { var s = gtShipSkillSlider(so); if (s != null && s.maxValue < 4f) s.maxValue = 4f; }
        catch (Exception ex) { Log?.LogWarning("[GroundTargeting] ship skill-slider extend error: " + ex); }
    }

    internal static void GroundTargetingExtendFactionSkill(NuclearOption.MissionEditorScripts.FactionSettingsTab tab)
    {
        if (tab == null) return;
        try
        {
            if (gtSurfaceSkillSlider != null) { var s = gtSurfaceSkillSlider(tab); if (s != null && s.maxValue < 4f) s.maxValue = 4f; }
            if (gtAirSkillSlider != null) { var a = gtAirSkillSlider(tab); if (a != null && a.maxValue < 4f) a.maxValue = 4f; }
        }
        catch (Exception ex) { Log?.LogWarning("[GroundTargeting] faction skill-slider extend error: " + ex); }
    }

    // Faster detection scans for ground-vehicle detectors (the dominant acquisition-latency fix). Only ever faster.
    internal static void GroundTargetingOnDetectorInit(TargetDetector d)
    {
        if (!MasterOn || gtFieldsBroken || gtCheckInterval == null || d == null) return;
        try
        {
            if (!(d.GetAttachedUnit() is GroundVehicle gv)) return;   // only ground-vehicle detectors
            if (gv.remoteSim) return;                                 // only where this AI is actually simulated
            float scan = cfgGTScan.Value;
            scan /= Mathf.Clamp(gv.skill, 0.33f, 2f);   // low skill -> slower scanning (always skill-scaled)
            ref float ci = ref gtCheckInterval(d);
            if (scan < ci) ci = scan;                                 // only ever faster
            ref float ai = ref gtAlertInterval(d);
            if (ai > 0f && scan < ai) ai = scan;                      // keep the 0 sentinel intact
        }
        catch (Exception ex) { Log?.LogWarning("[GroundTargeting] detector init error: " + ex); }
    }

    // Closest-first target scoring for ground turrets (reproduces vanilla validity gates + closeness weight).
    internal static bool GroundAssessTargetPriority(Turret t, Unit targetCandidate, ref float priorityThreshold)
    {
        if (!MasterOn || gtFieldsBroken || t == null) return true;
        if (!(t.GetAttachedUnit() is GroundVehicle unit)) return true;  // only ground-vehicle turrets; vanilla for the rest
        if (unit.remoteSim) return true;                                // only where this AI is actually simulated; vanilla elsewhere
        bool jetEnvious = HasTrait(unit, Trait.JetEnvious), flySwatter = HasTrait(unit, Trait.FlySwatter);
        bool ambusher = HasTrait(unit, Trait.Ambusher), teamPlayer = HasTrait(unit, Trait.TeamPlayer), vengeful = HasTrait(unit, Trait.Vengeful);
        // Ground Targeting is always on now (config removed) — the closeness weight always applies to ground turrets.
        try
        {
            var hq = unit.NetworkHQ; if (hq == null) return true;
            TrackingInfo trackingData = hq.GetTrackingData(targetCandidate.persistentID);
            if (trackingData == null || targetCandidate == null || targetCandidate.disabled || targetCandidate.NetworkHQ == null) return false;
            bool candIsAir = targetCandidate is Aircraft;
            Vector3 targetVector = targetCandidate.transform.position - t.transform.position;
            if (!FiringConeChecker.VectorWithinFiringCones(gtFiringCones(t), targetVector, out _))
            {
                // DIAGNOSTIC: a Jet-Envious unit can't even point at this plane (outside its turret's firing cone /
                // elevation envelope) -> it will never engage it, no matter the scoring.
                if (jetEnvious && candIsAir && cfgDiag != null && cfgDiag.Value)
                    Log?.LogInfo($"[JetEnvious] {unit.name}: air target {targetCandidate.name} is OUTSIDE the turret firing cone (can't aim at it) -> skip.");
                return false;
            }
            float magnitude = targetVector.magnitude;
            if (gtOnlyDefensive(t) && (!(targetCandidate is Missile missile) || missile.targetID != unit.persistentID)) return false;

            // Missile self-defence (skill>2): incoming missiles outscore everything for a gun that can reach them.
            if (targetCandidate is Missile gm && gm.targetID == unit.persistentID && unit.skill > GroundInterceptSkill && !HasTrait(unit, Trait.Reckless))
            {
                WeaponStation[] dstations = t.GetWeaponStations();
                for (int i = 0; i < dstations.Length; i++)
                {
                    WeaponStation ws = dstations[i];
                    if (ws == null || ws.WeaponInfo == null || !ws.WeaponInfo.gun) continue;
                    float maxRange = ws.WeaponInfo.targetRequirements.maxRange;
                    if (magnitude > maxRange || magnitude < ws.WeaponInfo.targetRequirements.minRange || ws.Reloading || ws.Ammo <= 0) continue;
                    float mScore = 200000f * Mathf.Clamp01(1f - magnitude / maxRange);
                    if (mScore > priorityThreshold) { gtTarget(t) = targetCandidate; gtCurrentWS(t) = ws; priorityThreshold = mScore; }
                }
                return false;   // candidate handled (even if no gun could engage — other candidates still assessed)
            }

            float armorOpt = gtArmorOpt(t);
            float w = cfgGTCloseWeight.Value;
            bool datalink = GtIsDatalink(t);
            bool targetIsAir = targetCandidate is Aircraft;             // planes & helis
            // Gun takes over while the missile station reloads (skill>3); ATGM resumes once ready.
            WeaponStation curWS = gtCurrentWS(t);
            bool missileStationBusy = curWS != null && curWS.WeaponInfo != null && !curWS.WeaponInfo.gun && (curWS.Reloading || curWS.Ammo <= 0);
            WeaponStation[] stations = t.GetWeaponStations();
            for (int i = 0; i < stations.Length; i++)
            {
                WeaponStation ws = stations[i];
                if (ws == null || ws.WeaponInfo == null) continue;
                float maxRange = ws.WeaponInfo.targetRequirements.maxRange;

                // Jet-Envious: any weapon engages aircraft in range (closest-first, dominating score).
                if (jetEnvious && targetIsAir)
                {
                    if (magnitude > maxRange || ws.Reloading || ws.Ammo <= 0) continue;
                    float airScore = 100000f * Mathf.Clamp01(1f - magnitude / maxRange);
                    if (airScore > priorityThreshold)
                    {
                        gtTarget(t) = targetCandidate; gtCurrentWS(t) = ws; priorityThreshold = airScore;
                        // DIAGNOSTIC: selection succeeded. If the unit still doesn't fire, the blocker is the turret's
                        // aim solution (Turret.FixedUpdate requires timeOnTarget > lockTime, i.e. the barrel on the
                        // ballistic lead within the firing cone) — an unguided weapon usually can't solve that vs a plane.
                        if (cfgDiag != null && cfgDiag.Value)
                            Log?.LogInfo($"[JetEnvious] {unit.name} SELECTED air target {targetCandidate.name} (dist={magnitude:0}m/{maxRange:0}m, {ws.WeaponInfo.weaponName}). Whether it FIRES now depends on the turret getting a valid firing solution.");
                    }
                    continue;
                }

                // Fly-Swatter: extended range + strong preference vs air (still needs a weapon that can hit air).
                float effRange = (flySwatter && targetIsAir) ? maxRange * 1.5f : maxRange;
                float num = effRange / magnitude;
                if (num < 0.7f) continue;
                OpportunityThreat ot = CombatAI.AnalyzeTarget(ws, unit, trackingData, armorOpt, magnitude, (flySwatter && targetIsAir) ? 2f : 1.2f);
                float num2 = ot.opportunity * (1f + ot.threat);
                if (num2 == 0f) continue;
                if (ws.Reloading || ws.Ammo <= 0) num2 *= 0.01f;
                if (num < 1f || magnitude < ws.WeaponInfo.targetRequirements.minRange) num2 *= 0.01f;
                if (targetCandidate.definition.armorTier > ws.WeaponInfo.armorTierEffectiveness) num2 *= 0.2f;
                if (datalink && targetCandidate.speed > 0f && num < 2f) num2 *= 0.6f;
                if (flySwatter && targetIsAir) num2 *= 3f;                                    // strongly prefer air
                if (missileStationBusy && ws.WeaponInfo.gun && unit.skill > GroundMultiWeaponSkill) num2 *= 100f;   // gun takes over during the missile reload
                // Ambusher: hold fire until well inside range. TeamPlayer: focus targets friendlies engage. Vengeful: hunt top damager.
                if (ambusher && magnitude > maxRange * 0.6f) num2 *= 0.01f;
                // TeamPlayer: prefer targets friendlies already engage (up to x3).
                if (teamPlayer && trackingData.attackers > 0) num2 *= 1f + 0.5f * Mathf.Min(trackingData.attackers, 4);
                // Vengeful: hunts the unit that has hurt it the most
                if (vengeful && IsTopDamager(unit, targetCandidate)) num2 *= 3f;
                // closest-first bias (item 6): closer valid targets score higher (up to (1+w)x at point-blank).
                if (w > 0f && maxRange > 1f) num2 *= 1f + w * Mathf.Clamp01(1f - magnitude / maxRange);
                if (num2 > priorityThreshold)
                {
                    gtTarget(t) = targetCandidate;
                    gtCurrentWS(t) = ws;
                    priorityThreshold = num2;
                }
            }
            return false;   // fully handled this candidate
        }
        catch (Exception ex)
        {
            gtFieldsBroken = true;   // stop replacing so target selection keeps working on vanilla
            Log?.LogWarning("[GroundTargeting] scoring error (falling back to vanilla): " + ex);
            return true;
        }
    }

    // Ground missile self-defence scan (throttled): retask the best gun turret onto the most urgent incoming missile.
    internal static void GroundDefenseUpdate(GroundVehicle gv)
    {
        if (!MasterOn || gtFieldsBroken || gv == null || gv.remoteSim || gv.disabled || PlayerProtected(gv)) return;
        if (gv.skill <= GroundInterceptSkill) return;
        int id = gv.GetInstanceID();
        float now = Time.timeSinceLevelLoad;
        if (gdNextScan.TryGetValue(id, out var nxt) && now < nxt) return;
        gdNextScan[id] = now + Mathf.Max(cfgGTReassess.Value * 0.5f, 0.1f);
        try
        {
            var hq = gv.NetworkHQ; if (hq == null) return;
            Missile threat = null; float bestTTI = float.MaxValue;
            foreach (var kv in hq.trackingDatabase)
            {
                if (!kv.Value.TryGetUnit(out var tu) || !(tu is Missile m) || m.disabled || m.targetID != gv.persistentID) continue;
                Vector3 rel = m.transform.position - gv.transform.position;
                Vector3 vv = m.rb != null ? m.rb.velocity : Vector3.zero;
                float closing = Mathf.Max(Vector3.Dot(-rel.normalized, vv), 1f);
                float tti = rel.magnitude / closing;
                if (tti < bestTTI) { bestTTI = tti; threat = m; }
            }
            if (threat == null) return;
            if (HasTrait(gv, Trait.Reckless)) return;                 // bloodthirsty: never flinches off its target

            if (!gdTurrets.TryGetValue(id, out var turrets) || turrets == null) { turrets = gv.GetComponentsInChildren<Turret>(true); gdTurrets[id] = turrets; }
            bool cautious = HasTrait(gv, Trait.Cautious);             // self-preserving: pre-aims before the missile is in range
            Vector3 tpos = threat.transform.position;
            for (int k = 0; k < turrets.Length; k++)
            {
                var t = turrets[k]; if (t == null) continue;
                if (gtManual != null && gtManual(t)) continue;        // player-controlled turret
                if (gtTarget(t) == threat) { InjectThreat(t, threat); return; }   // already on it — just keep it sticky
                float dist = Vector3.Distance(t.transform.position, tpos);
                var stations = t.GetWeaponStations();
                for (int i = 0; i < stations.Length; i++)
                {
                    var ws = stations[i];
                    if (ws == null || ws.WeaponInfo == null || !ws.WeaponInfo.gun) continue;
                    float maxR = ws.WeaponInfo.targetRequirements.maxRange;
                    if (dist > maxR * (cautious ? 1.5f : 1f) || dist < ws.WeaponInfo.targetRequirements.minRange) continue;
                    if (!FiringConeChecker.VectorWithinFiringCones(gtFiringCones(t), tpos - t.transform.position, out _)) continue;
                    if (ws.Ammo <= 0) continue;                       // a reload in progress is fine — the missile may arrive after it finishes
                    gtTarget(t) = threat; gtCurrentWS(t) = ws;
                    Span<PersistentID> span = stackalloc PersistentID[1]; span[0] = threat.persistentID;
                    gv.RpcSetStationTargets(ws.Number, span);
                    InjectThreat(t, threat);
                    if (cfgDiag != null && cfgDiag.Value)
                        Log?.LogInfo($"[GroundDefense] {gv.name}: {ws.WeaponInfo.weaponName} -> incoming {threat.name} (TTI {bestTTI:0.0}s, {dist:0}m/{maxR:0}m)");
                    return;
                }
            }
        }
        catch (Exception ex) { Log?.LogWarning("[GroundDefense] " + ex); }
    }

    // Keep the threat in the turret's candidate list so reassessments re-pick it (sticky).
    static void InjectThreat(Turret t, Missile threat)
    {
        if (gtPotential == null) return;
        try { var list = gtPotential(t); if (list != null && !list.Contains(threat)) list.Add(threat); } catch { }
    }

    // Multi-weapon fire (skill>3): settled turrets also fire every other ready station at the same target.
    internal static void GroundMultiWeaponFire(Turret t)
    {
        if (!MasterOn || gtFieldsBroken || t == null) return;
        try
        {
            if (!(t.GetAttachedUnit() is GroundVehicle gv) || gv.remoteSim || gv.disabled || PlayerProtected(gv)) return;
            if (gv.skill <= GroundMultiWeaponSkill) return;
            Unit target = gtTarget(t);
            if (target == null || target.disabled || target is Missile) return;
            if (gtTimeOnTarget(t) <= gtLockTime(t)) return;           // turret hasn't settled on the target yet
            var hq = gv.NetworkHQ; if (hq == null) return;
            TrackingInfo tracking = hq.GetTrackingData(target.persistentID);
            if (tracking == null) return;
            WeaponStation cur = gtCurrentWS(t);
            float dist = Vector3.Distance(t.transform.position, target.transform.position);
            foreach (var ws in t.GetWeaponStations())
            {
                if (ws == null || ws == cur || ws.WeaponInfo == null) continue;
                if (ws.Reloading || ws.Ammo <= 0 || !ws.Ready()) continue;
                if (dist > ws.WeaponInfo.targetRequirements.maxRange || dist < ws.WeaponInfo.targetRequirements.minRange) continue;
                OpportunityThreat ot = CombatAI.AnalyzeTarget(ws, gv, tracking, gtArmorOpt(t), dist, 1.2f);
                if (ot.opportunity <= 0f) continue;
                ws.Fire(gv, target);
            }
        }
        catch (Exception ex) { Log?.LogWarning("[GroundMultiWeapon] " + ex); }
    }

    // Missile salvo (skill-scaled simultaneous engagements, fraction of magazine). Fire-and-forget seekers only.
    struct SalvoInfo { public Turret turret; public WeaponStation ws; public int fullAmmo; }
    struct SalvoShot { public PersistentID target; public float expiry; }
    static readonly Dictionary<int, SalvoInfo> salvoUnit = new Dictionary<int, SalvoInfo>();
    static readonly Dictionary<int, List<SalvoShot>> salvoShots = new Dictionary<int, List<SalvoShot>>();
    static readonly Dictionary<int, float> salvoNext = new Dictionary<int, float>();
    static readonly Dictionary<WeaponInfo, string> seekerCache = new Dictionary<WeaponInfo, string>();

    // Seeker type off the weapon prefab (cached per WeaponInfo).
    static string WeaponSeekerType(WeaponInfo wi)
    {
        if (wi == null) return "";
        if (seekerCache.TryGetValue(wi, out var s)) return s;
        s = "";
        try { var sk = wi.weaponPrefab != null ? wi.weaponPrefab.GetComponent<MissileSeeker>() : null; if (sk != null) s = sk.GetSeekerType() ?? ""; } catch { }
        seekerCache[wi] = s;
        return s;
    }

    // The one missile station this unit may salvo with (null ws = ineligible). Computed once per unit.
    static SalvoInfo GetSalvoInfo(GroundVehicle gv)
    {
        int id = gv.GetInstanceID();
        if (salvoUnit.TryGetValue(id, out var info)) return info;
        info = new SalvoInfo();
        try
        {
            Turret mt = null; int eligibleTurrets = 0;
            foreach (var t in gv.GetComponentsInChildren<Turret>(true))
            {
                if (t == null) continue;
                bool turretEligible = false;
                foreach (var ws in t.GetWeaponStations())
                {
                    if (ws == null || ws.WeaponInfo == null || !ws.WeaponInfo.missile || ws.WeaponInfo.overHorizon) continue;   // no guns, no ballistic/stratolance
                    string sk = WeaponSeekerType(ws.WeaponInfo);
                    if (sk != "ARH" && sk != "IR" && sk != "Optical") continue;                     // fire-and-forget only (no SARH/Laser/ARAD/INS beam-riders)
                    bool hasFC = false; try { hasFC = gtFireControl != null && gtFireControl(t) != null; } catch { }
                    if (hasFC) continue;                                                             // FireControl radar = already multi-channel
                    if (!turretEligible) { turretEligible = true; mt = t; eligibleTurrets++; }
                    if (info.ws == null) info.ws = ws;
                }
            }
            if (eligibleTurrets == 1 && info.ws != null && info.ws.FullAmmo > 1) info.turret = mt;   // >1 missile turret = already multi-target
            else info.ws = null;
            if (info.ws != null) info.fullAmmo = info.ws.FullAmmo;
        }
        catch { info.ws = null; }
        salvoUnit[id] = info;
        return info;
    }

    // Salvo driver (throttled): launch at additional distinct, unsaturated, weapon-valid targets.
    internal static void GroundSalvoUpdate(GroundVehicle gv)
    {
        if (!MasterOn || gtFieldsBroken || gv == null || gv.remoteSim || gv.disabled || PlayerProtected(gv)) return;
        if (gv.skill <= 1f) return;                                     // skill <= 1 -> vanilla single-target
        int id = gv.GetInstanceID();
        float now = Time.timeSinceLevelLoad;
        if (salvoNext.TryGetValue(id, out var nx) && now < nx) return;
        salvoNext[id] = now + Mathf.Max(cfgGTReassess.Value * 0.5f, 0.1f);
        try
        {
            var info = GetSalvoInfo(gv);
            if (info.ws == null || info.turret == null) return;
            float frac = Mathf.Min(gv.skill * 0.125f, 0.5f);            // user spec: 2 -> 25%, 3 -> 37.5%, 4+ -> 50%
            int maxTargets = Mathf.FloorToInt(info.fullAmmo * frac);
            if (maxTargets < 2) return;                                 // allowance of 1 = effectively vanilla
            if (!salvoShots.TryGetValue(id, out var shots)) { shots = new List<SalvoShot>(); salvoShots[id] = shots; }
            for (int i = shots.Count - 1; i >= 0; i--) if (now >= shots[i].expiry) shots.RemoveAt(i);
            int engaged = 1 + shots.Count;                              // the turret's own current engagement + our extra launches
            if (engaged >= maxTargets) return;
            var hq = gv.NetworkHQ; if (hq == null) return;
            var wi = info.ws.WeaponInfo;
            bool noAim = false; try { noAim = gtFireNoAim != null && gtFireNoAim(info.turret); } catch { }
            Unit best = null; float bestDist = float.MaxValue;
            foreach (var kv in hq.trackingDatabase)
            {
                var ti = kv.Value;
                if (ti == null || !ti.TryGetUnit(out var u2) || u2 == null || u2.disabled || u2.NetworkHQ == hq) continue;
                if (ti.missileAttacks > wi.CalcAttacksNeeded(u2)) continue;                          // already saturated (vanilla overkill guard)
                bool dup = false; for (int i = 0; i < shots.Count; i++) if (shots[i].target == u2.persistentID) { dup = true; break; }
                if (dup) continue;                                                                   // never two of OUR missiles on one target
                Vector3 tvec = u2.transform.position - info.turret.transform.position;
                float dist = tvec.magnitude;
                if (dist > wi.targetRequirements.maxRange || dist < wi.targetRequirements.minRange) continue;
                if (!noAim && !FiringConeChecker.VectorWithinFiringCones(gtFiringCones(info.turret), tvec, out _)) continue;
                if (wi.targetRequirements.lineOfSight && Physics.Linecast(info.turret.transform.position, u2.transform.position, PhysicsLayers.StaticsMask)) continue;
                if (CombatAI.AnalyzeTarget(info.ws, gv, ti, gtArmorOpt(info.turret), dist, 1.2f).opportunity <= 0f) continue;   // weapon can't hurt it
                if (dist < bestDist) { bestDist = dist; best = u2; }
            }
            if (best == null) return;
            if (info.ws.Ammo <= 0 || info.ws.Reloading || !info.ws.Ready()) return;
            info.ws.Fire(gv, best);                                     // MissileLauncher fireInterval rate-limits itself
            float flight = bestDist / Mathf.Max(wi.GetMaxSpeed(), 1f) * 1.5f + 3f;   // generous in-flight estimate for the ledger
            shots.Add(new SalvoShot { target = best.persistentID, expiry = now + flight });
            if (cfgDiag != null && cfgDiag.Value)
                Log?.LogInfo($"[Salvo] {gv.name} -> {best.name} ({engaged}/{maxTargets} simultaneous, {bestDist:0}m)");
        }
        catch (Exception ex) { Log?.LogWarning("[Salvo] " + ex); }
    }

    // Jet-Envious potshot assist: fire anyway after 2x lockTime of aggregate aim (unguided guns rarely solve vs planes).
    struct JeAim { public int targetId; public float time; }
    static readonly Dictionary<int, JeAim> jeAim = new Dictionary<int, JeAim>();
    internal static void GroundJetEnviousFire(Turret t)
    {
        if (!MasterOn || gtFieldsBroken || t == null) return;
        try
        {
            if (!(t.GetAttachedUnit() is GroundVehicle gv) || gv.remoteSim || gv.disabled || PlayerProtected(gv)) return;
            if (!HasTrait(gv, Trait.JetEnvious)) return;
            int id = t.GetInstanceID();
            Unit target = gtTarget(t);
            if (!(target is Aircraft) || target.disabled) { jeAim.Remove(id); return; }
            WeaponStation ws = gtCurrentWS(t);
            if (ws == null || ws.WeaponInfo == null || ws.Reloading || ws.Ammo <= 0 || !ws.Ready()) return;
            float dist = Vector3.Distance(t.transform.position, target.transform.position);
            if (dist > ws.WeaponInfo.targetRequirements.maxRange || dist < ws.WeaponInfo.targetRequirements.minRange) return;
            jeAim.TryGetValue(id, out var a);
            if (a.targetId != target.GetInstanceID()) { a.targetId = target.GetInstanceID(); a.time = 0f; }
            a.time += Time.deltaTime;
            if (a.time > gtLockTime(t) * 2f) { ws.Fire(gv, target); a.time = 0f; }
            jeAim[id] = a;
        }
        catch { }
    }

    // RWR anti-spam: rate-limit warning pings per emitter (HUD-only, detection/targeting untouched).
    internal static bool RwrShouldPing(Unit emitter)
    {
        if (!MasterOn || cfgRwrInterval == null || cfgRwrInterval.Value <= 0f) return true;   // off -> vanilla behaviour
        int id = emitter != null ? emitter.GetInstanceID() : 0;
        float now = Time.timeSinceLevelLoad;
        if (rwrLastPing.TryGetValue(id, out var last) && now - last < cfgRwrInterval.Value) return false;
        rwrLastPing[id] = now;
        return true;
    }

    // Has this candidate dealt the most damage to us? (Vengeful.)
    static bool IsTopDamager(Unit unit, Unit candidate)
    {
        if (vetDamageCredit == null) return false;
        try
        {
            var dc = vetDamageCredit(unit); if (dc == null || dc.Count == 0) return false;
            PersistentID best = default(PersistentID); float bv = 0f;
            foreach (var kv in dc) if (kv.Value > bv) { bv = kv.Value; best = kv.Key; }
            return bv > 0f && best == candidate.persistentID;
        }
        catch { return false; }
    }

    static bool GtIsDatalink(Turret t)
    {
        if (gtModeField == null || gtDatalinkMode < 0) return false;
        try { return Convert.ToInt32(gtModeField.GetValue(t)) == gtDatalinkMode; } catch { return false; }
    }
}

// Turret reassess-speed hook.
[HarmonyPatch(typeof(Turret), "Turret_OnInitialize")]
public static class Turret_OnInitialize_Patch
{
    static void Prefix(Turret __instance)
    {
        try { ImprovedAIPlugin.GroundTargetingOnTurretInit(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("turret init patch error: " + ex); }
    }
}

// Closest-first scoring hook (ground turrets only; vanilla elsewhere).
[HarmonyPatch(typeof(Turret), "AssessTargetPriority")]
public static class Turret_AssessTargetPriority_Patch
{
    static bool Prefix(Turret __instance, Unit targetCandidate, ref float priorityThreshold)
    {
        try { return ImprovedAIPlugin.GroundAssessTargetPriority(__instance, targetCandidate, ref priorityThreshold); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("assess-priority patch error: " + ex); return true; }
    }
}

// Detector scan-speed hook.
[HarmonyPatch(typeof(TargetDetector), "TargetDetector_OnInitialize")]
public static class TargetDetector_OnInitialize_Patch
{
    static void Prefix(TargetDetector __instance)
    {
        try { ImprovedAIPlugin.GroundTargetingOnDetectorInit(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("detector init patch error: " + ex); }
    }
}

// Skill-slider hooks (per-unit vehicle/aircraft/ship + faction multipliers, 2 -> 4).
[HarmonyPatch(typeof(NuclearOption.MissionEditorScripts.VehicleOptions), "SetupInner")]
public static class VehicleOptions_SetupInner_Patch
{
    static void Prefix(NuclearOption.MissionEditorScripts.VehicleOptions __instance)
    {
        try { ImprovedAIPlugin.GroundTargetingExtendSkillSlider(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("skill-slider patch error: " + ex); }
    }
}

// (Aircraft/ship/faction variants of the slider hook above.)
[HarmonyPatch(typeof(NuclearOption.MissionEditorScripts.AircraftOptions), "SetupInner")]
public static class AircraftOptions_SetupInner_SkillPatch
{
    static void Prefix(NuclearOption.MissionEditorScripts.AircraftOptions __instance)
    {
        try { ImprovedAIPlugin.GroundTargetingExtendAcSkillSlider(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("aircraft skill-slider patch error: " + ex); }
    }
}

[HarmonyPatch(typeof(NuclearOption.MissionEditorScripts.ShipOptions), "SetupInner")]
public static class ShipOptions_SetupInner_SkillPatch
{
    static void Prefix(NuclearOption.MissionEditorScripts.ShipOptions __instance)
    {
        try { ImprovedAIPlugin.GroundTargetingExtendShipSkillSlider(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("ship skill-slider patch error: " + ex); }
    }
}

// (Faction multiplier variant.)
[HarmonyPatch(typeof(NuclearOption.MissionEditorScripts.FactionSettingsTab), "Start")]
public static class FactionSettingsTab_Start_Patch
{
    static void Prefix(NuclearOption.MissionEditorScripts.FactionSettingsTab __instance)
    {
        try { ImprovedAIPlugin.GroundTargetingExtendFactionSkill(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("faction skill-slider patch error: " + ex); }
    }
}

// RWR anti-spam hook (HUD-only).
[HarmonyPatch(typeof(RadarWarning), "RadarWarning_OnRadarWarning")]
public static class RadarWarning_OnRadarWarning_Patch
{
    static bool Prefix(Aircraft.OnRadarWarning source)
    {
        try { return ImprovedAIPlugin.RwrShouldPing(source.emitter); }
        catch { return true; }
    }
}

// Ground self-defence + salvo driver hook.
[HarmonyPatch(typeof(GroundVehicle), "Update")]
public static class GroundVehicle_Update_DefensePatch
{
    static void Postfix(GroundVehicle __instance)
    {
        try { ImprovedAIPlugin.GroundDefenseUpdate(__instance); }
        catch { }
        try { ImprovedAIPlugin.GroundSalvoUpdate(__instance); }
        catch { }
    }
}

// Multi-weapon + Jet-Envious fire hook.
[HarmonyPatch(typeof(Turret), "FixedUpdate")]
public static class Turret_FixedUpdate_MultiWeaponPatch
{
    static void Postfix(Turret __instance)
    {
        try { ImprovedAIPlugin.GroundMultiWeaponFire(__instance); }
        catch { }
        try { ImprovedAIPlugin.GroundJetEnviousFire(__instance); }
        catch { }
    }
}
