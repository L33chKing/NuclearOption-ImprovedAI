// EliteBoost: skill>4 stat ramp (health/guns/mobility, ground+air+naval) + elite flight/ship stabilizers.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

public partial class ImprovedAIPlugin
{
    // Fixed boost values (no F1 knobs).
    const bool EliteAirThrust = true, EliteAirManeuver = true;
    // Standard stat-boost ramp (planes/ground/naval): NO boost at/below skill 4, 2x at skill 6, 4x at skill 8.
    // EliteFactor = maxMult^((skill-4)/4): f(4)=1, f(6)=2, f(8)=4.
    const float EliteStartSkill = 4f, EliteFullSkill = 8f, EliteFullMult = 4f, EliteTurnMult = 1.5f;
    const float EliteShipDurabilityMult = 2f;   // SHIPS get a gentler durability ramp than the 4x main factor (1x@4, 1.4x@6, 2x@8) — a max carrier at 4x armor was too sturdy; air/ground durability stays at EliteFullMult
    // Elite GROUND planting downforce (see GroundApplyDownforce) — tune these if units still launch / don't grip.
    const float GroundDownforceK = 0.01f;       // planting accel per (factor-1) per (m/s)^2
    const float GroundDownforceMax = 15f;       // cap on planting accel (m/s^2) so it can't bottom out the suspension
    const float GroundDownforceMinSpeed = 3f;   // no planting below this speed (keep stock low-speed feel, no stutter)

    static AccessTools.FieldRef<Gun, float> eGunReload, eGunFireRate, eGunInterval;
    static AccessTools.FieldRef<ShipPropulsion, float> eShipThrust, eShipSteer;
    static AccessTools.FieldRef<ShipPropulsion, Ship> eShipPropShip;   // ShipPropulsion.ship (for the roll/yaw stabilizer)
    const float ShipRollKp = 4f, ShipRollKd = 2.5f;   // active anti-roll: righting gain + roll-rate damping (angular accel)
    const float ShipYawKd = 2.5f;                      // damping on the OSCILLATORY yaw only (hunt), not the steady turn — see EliteStabilizeShip
    const float ShipYawSmooth = 0.08f;                 // low-pass factor for the steady-turn yaw rate (~0.25s @50Hz); higher = passes faster turns undamped
    static readonly Dictionary<Ship, float> eShipYawSmoothed = new Dictionary<Ship, float>();   // per-ship low-passed yaw rate (the "intended" turn)
    static AccessTools.FieldRef<ControlSurface, Aircraft> eCsAircraft;   // ControlSurface.aircraft back-ref
    static AccessTools.FieldRef<ControlSurface, NuclearOption.Jobs.PtrAllocation<NuclearOption.Jobs.ControlSurfaceFields>> eCsJobFields;  // the Burst job copy — pitchRange/rollRange are snapshotted here ONCE at Awake and never refreshed, so this (not the C# field) is the live authority
    static readonly Dictionary<ControlSurface, Vector2> csBaseRange = new Dictionary<ControlSurface, Vector2>();  // ControlSurface -> ORIGINAL (pitchRange, rollRange) from the job (idempotent live scale)
    static AccessTools.FieldRef<Turbojet, float> eTjMaxSpeed;    // private Turbojet.maxSpeed — a HARD thrust cutoff = the top-speed wall
    static AccessTools.FieldRef<DuctedFan, float> eDfMaxThrust;  // private DuctedFan.maxThrust (computed from power at init)
    static AccessTools.FieldRef<Turbofan, AnimationCurve> eTfSpeedThrust;  // private Turbofan.speedThrust — thrust-vs-speed falloff = its (soft) top-speed wall
    static System.Reflection.FieldInfo eNozzleAfterburners, eAfterburnerThrust;  // JetNozzle.afterburners[] + Afterburner.thrust (the AB thrust added on top of engine thrust)
    static AccessTools.FieldRef<JetNozzle, Aircraft> eNozzleAircraft;             // JetNozzle.aircraft back-ref, to key the live thrust scale
    static readonly Dictionary<object, float> abBaseThrust = new Dictionary<object, float>();  // afterburner instance -> ORIGINAL thrust (idempotent live scale)
    static AccessTools.FieldRef<ControlsFilter.FlyByWire, float> eFbwGLimit;      // FlyByWire.gLimitPositive — the commanded-G (turn-rate) cap
    static readonly Dictionary<object, float> fbwBaseGLimit = new Dictionary<object, float>();  // FlyByWire -> ORIGINAL gLimitPositive (idempotent live scale)
    static AccessTools.FieldRef<Airbrake, float> eAirbrakeDrag;                   // Airbrake.dragAmount — decel force scale
    static AccessTools.FieldRef<Airbrake, Aircraft> eAirbrakeAircraft;            // Airbrake.aircraft back-ref
    static readonly Dictionary<object, float> airbrakeBaseDrag = new Dictionary<object, float>();  // Airbrake -> ORIGINAL dragAmount
    static float glocFactor = 1f;   // elite factor of the aircraft the LOCAL player is flying — set just before its GLOC sim runs
    static readonly Dictionary<int, float> eliteApplied = new Dictionary<int, float>();     // unit id -> main factor applied
    static readonly Dictionary<int, float> eliteGunApplied = new Dictionary<int, float>();  // GUN id -> factor applied (per-gun so a late-attaching turret gun / railgun is caught by Gun.Awake, not lost to the unit-level guard)
    static readonly Dictionary<int, float> eliteHpApplied = new Dictionary<int, float>();   // SHIP id -> hitpoint factor applied (ships get elite durability as max-HP, not damage reduction)

    void EliteConfig()
    {
        // Elite Boost config removed from F1 — values now the EliteAirThrust/…/EliteTurnMult consts above.
        try
        {
            eGunReload = AccessTools.FieldRefAccess<Gun, float>("reloadTime");
            eGunFireRate = AccessTools.FieldRefAccess<Gun, float>("fireRate");
            eGunInterval = AccessTools.FieldRefAccess<Gun, float>("fireInterval");
        }
        catch (Exception ex) { eGunReload = null; Logger.LogWarning("[Elite] gun fields unavailable (reload/firerate boost off): " + ex); }
        try
        {
            eShipThrust = AccessTools.FieldRefAccess<ShipPropulsion, float>("thrust");
            eShipSteer = AccessTools.FieldRefAccess<ShipPropulsion, float>("steeringThrust");
            eShipPropShip = AccessTools.FieldRefAccess<ShipPropulsion, Ship>("ship");
        }
        catch (Exception ex) { eShipThrust = null; Logger.LogWarning("[Elite] ship propulsion fields unavailable (naval mobility boost off): " + ex); }
        try
        {
            eCsAircraft = AccessTools.FieldRefAccess<ControlSurface, Aircraft>("aircraft");
            eCsJobFields = AccessTools.FieldRefAccess<ControlSurface, NuclearOption.Jobs.PtrAllocation<NuclearOption.Jobs.ControlSurfaceFields>>("JobFields");
        }
        catch (Exception ex) { eCsAircraft = null; eCsJobFields = null; Logger.LogWarning("[Elite] control-surface fields unavailable (aircraft turn boost off): " + ex); }
        try { eTjMaxSpeed = AccessTools.FieldRefAccess<Turbojet, float>("maxSpeed"); }
        catch (Exception ex) { eTjMaxSpeed = null; Logger.LogWarning("[Elite] Turbojet.maxSpeed unavailable (jet top-speed cap won't be lifted): " + ex); }
        try { eDfMaxThrust = AccessTools.FieldRefAccess<DuctedFan, float>("maxThrust"); }
        catch (Exception ex) { eDfMaxThrust = null; Logger.LogWarning("[Elite] DuctedFan.maxThrust unavailable (ducted-fan boost off): " + ex); }
        try { eTfSpeedThrust = AccessTools.FieldRefAccess<Turbofan, AnimationCurve>("speedThrust"); }
        catch (Exception ex) { eTfSpeedThrust = null; Logger.LogWarning("[Elite] Turbofan.speedThrust unavailable (turbofan top-speed cap won't be lifted): " + ex); }
        try
        {
            eNozzleAfterburners = AccessTools.Field(typeof(JetNozzle), "afterburners");
            var abType = typeof(JetNozzle).GetNestedType("Afterburner", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            eAfterburnerThrust = abType != null ? AccessTools.Field(abType, "thrust") : null;
        }
        catch (Exception ex) { eNozzleAfterburners = null; eAfterburnerThrust = null; Logger.LogWarning("[Elite] JetNozzle afterburner fields unavailable (afterburner thrust not scaled): " + ex); }
        try { eNozzleAircraft = AccessTools.FieldRefAccess<JetNozzle, Aircraft>("aircraft"); }
        catch (Exception ex) { eNozzleAircraft = null; Logger.LogWarning("[Elite] JetNozzle.aircraft unavailable (live thrust scaling off): " + ex); }
        try { eFbwGLimit = AccessTools.FieldRefAccess<ControlsFilter.FlyByWire, float>("gLimitPositive"); }
        catch (Exception ex) { eFbwGLimit = null; Logger.LogWarning("[Elite] FlyByWire.gLimitPositive unavailable (turn/G boost off): " + ex); }
        try
        {
            eAirbrakeDrag = AccessTools.FieldRefAccess<Airbrake, float>("dragAmount");
            eAirbrakeAircraft = AccessTools.FieldRefAccess<Airbrake, Aircraft>("aircraft");
        }
        catch (Exception ex) { eAirbrakeDrag = null; Logger.LogWarning("[Elite] Airbrake fields unavailable (airbrake boost off): " + ex); }
    }

    // Geometric ramp: 1x at Start Skill -> maxMult at Full Skill.
    static float EliteFactor(float skill, float maxMult)
    {
        float start = EliteStartSkill, full = Mathf.Max(EliteFullSkill, start + 0.01f);
        float t = Mathf.Clamp01((skill - start) / (full - start));
        return Mathf.Pow(maxMult, t);
    }

    // Apply the boost for a unit's current skill (idempotent delta-ratio, composes as skill climbs).
    internal static void ApplyElite(Unit u)
    {
        if (!MasterOn) return;
        if (u == null) return;
        bool ground = u is GroundVehicle, air = u is Aircraft, naval = u is Ship;
        if (!ground && !air && !naval) return;
        if (air && PlayerProtected(u)) return;                         // human-piloted aircraft left stock (unless §1 Affect Players)
        try { if (!u.IsServer) return; } catch { }                     // host-authoritative only

        int id = u.GetInstanceID();
        float skill = GetUnitSkill(u);
        try
        {
            // ---- main factor: health, weapons, mobility ----
            float target = EliteFactor(skill, EliteFullMult);
            float prev = eliteApplied.TryGetValue(id, out var p) ? p : 1f;
            if (Mathf.Abs(target - prev) >= 0.01f)
            {
                float ratio = target / Mathf.Max(prev, 0.01f);

                // Durability is now handled LIVE by the UnitPart.ApplyDamage patch (divide incoming damage by the factor).
                // The old spawn-time ScaleHealth silently did nothing for AIRCRAFT — their parts attach after InitializeUnit,
                // so GetAllParts() found none (why elite planes stayed as fragile as skill 1). Damage-reduction is timing-proof.

                // Guns — incl. RAILGUNS/cannons (all are Gun) — are scaled PER-GUN & idempotently (EliteApplyGun) rather
                // than by the unit ratio, so a turret gun that ATTACHES LATE (after this spawn-time scan, and thus missed
                // by the unit-level guard that never re-runs without veterancy) is still boosted by the Gun.Awake hook.
                if (eGunReload != null)
                {
                    int gunN = 0;
                    foreach (var g in u.GetComponentsInChildren<Gun>(true)) if (EliteApplyGun(g, u)) gunN++;
                    if (gunN == 0 && cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[Elite] {u.name}: no Gun components found to speed up (missile/other weapon?).");
                }

                if (ground)
                {
                    ScaleSpeed((GroundVehicle)u, ratio, ratio);                       // top speed + accel (+ Burst job copy)
                    ScaleGroundStick((GroundVehicle)u, ratio);                       // mass+spring+damping+grip all x factor: same suspension feel, heavier, more grip (handles the extra speed)
                }
                else if (naval && eShipThrust != null)
                {
                    foreach (var sp in u.GetComponentsInChildren<ShipPropulsion>(true))
                    {
                        if (sp == null) continue;
                        try { eShipThrust(sp) *= ratio; if (eShipSteer != null) eShipSteer(sp) *= ratio; } catch { }   // faster + turns quicker
                    }
                }
                else if (air && EliteAirThrust)
                {
                    // Thrust ×factor only — drag is left at STOCK. Top speed still rises (∝ sqrt(thrust) ≈ ×2 at skill 8)
                    // and acceleration ×factor, but the plane keeps its normal drag so it can still BLEED speed — cutting
                    // drag (the old 1/factor) left elite planes unable to decelerate for dogfights / bomb runs. dragRatio=1.
                    ScaleAircraftThrust((Aircraft)u, ratio, 1f, ratio);
                }

                eliteApplied[id] = target;
                if (cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[Elite] {u.name} skill {skill:0.00} -> boost x{target:0.00} (weapons/mobility)");
            }

            // ---- GROUND + SHIP durability: MAX-HITPOINTS (not damage reduction) ----
            // Ground & ships take durability as bigger part hitPoints (stacks multiplicatively with Personality Tough/
            // Juggernaut). Ground keeps the full factor (same tankiness as before, just via HP); ships use a gentler factor
            // (a max carrier at 4x was too sturdy). Own tracking so it composes as skill climbs; only lock in the applied
            // value once parts actually exist (retry next call if they weren't ready), so late part-registration isn't skipped.
            if (ground || naval)
            {
                float hpTarget = EliteFactor(skill, naval ? EliteShipDurabilityMult : EliteFullMult);
                float hpPrev = eliteHpApplied.TryGetValue(id, out var hp) ? hp : 1f;
                if (Mathf.Abs(hpTarget - hpPrev) >= 0.01f)
                {
                    int n = ScaleHealth(u, hpTarget / Mathf.Max(hpPrev, 0.01f));
                    if (n > 0)
                    {
                        eliteHpApplied[id] = hpTarget;
                        if (cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[Elite] {u.name} hitpoints -> x{hpTarget:0.00} on {n} parts");
                    }
                }
            }

            // aircraft turn authority (control-surface pitch/roll range) is scaled LIVE — see EliteScaleControlSurface
            // (the range is snapshotted once into the Burst job at Awake, so spawn-time field scaling was a no-op).
        }
        catch (Exception ex) { Log?.LogWarning("[Elite] apply error: " + ex); }
    }

    // Scale every thrust-producing engine on an aircraft (jet caps lifted, props powered, drag untouched).
    static void ScaleAircraftThrust(Aircraft ac, float thrustRatio, float dragRatio, float speedRatio)
    {
        if (ac == null) return;
        // NOTE: jet ENGINE thrust (+ afterburner) is scaled LIVE in JetNozzle.Thrust (EliteScaleNozzle) — immune to the
        // engine/nozzle attaching after InitializeUnit, which is almost certainly why the old spawn-time field scaling
        // did nothing. Here we only handle the parts that field-scaling CAN own at spawn: the speed caps, drag, and the
        // non-jet (prop/turboshaft) power that doesn't go through a JetNozzle.
        foreach (var tj in ac.GetComponentsInChildren<Turbojet>(true))
            if (tj != null) { try { if (eTjMaxSpeed != null) eTjMaxSpeed(tj) *= speedRatio; } catch { } }   // lift the hard maxSpeed thrust-cutoff
        foreach (var tf in ac.GetComponentsInChildren<Turbofan>(true))
            if (tf != null) { try { if (eTfSpeedThrust != null) { var c = eTfSpeedThrust(tf); if (c != null) eTfSpeedThrust(tf) = StretchCurveTime(c, speedRatio); } } catch { } }   // stretch thrust-vs-speed falloff
        foreach (var te in ac.GetComponentsInChildren<TurbineEngine>(true))
            if (te != null) { try { te.maxPower *= thrustRatio; } catch { } }   // turboprop/turboshaft (no JetNozzle) → power drives prop/rotor thrust
        if (eDfMaxThrust != null)
            foreach (var df in ac.GetComponentsInChildren<DuctedFan>(true))
                if (df != null) { try { eDfMaxThrust(df) *= thrustRatio; } catch { } }
        // Drag: top speed ∝ sqrt(thrust/drag). Cut per-part parasitic drag so the extra thrust turns into speed.
        if (dragRatio > 0f && !Mathf.Approximately(dragRatio, 1f))
            foreach (var ap in ac.GetComponentsInChildren<AeroPart>(true))
                if (ap != null) { try { ap.dragArea *= dragRatio; } catch { } }
    }

    // Live jet-thrust scaling at the point of application (immune to late-attaching engines). Skill-gated.
    internal static void EliteScaleNozzle(JetNozzle nz, ref float thrustAmount)
    {
        if (!MasterOn || nz == null || eNozzleAircraft == null) return;
        Aircraft ac = eNozzleAircraft(nz);
        if (ac == null || ac.remoteSim || PlayerProtected(ac)) return;   // host-simulated AI only; human-piloted plane left stock (unless §1 Affect Players)
        float f = EliteFactor(GetUnitSkill(ac), EliteFullMult);
        if (f <= 1.0001f) return;
        thrustAmount *= f;                                            // dry engine thrust
        if (eNozzleAfterburners == null || eAfterburnerThrust == null) return;
        if (!(eNozzleAfterburners.GetValue(nz) is Array abs)) return;
        foreach (var ab in abs)
        {
            if (ab == null) continue;
            if (!abBaseThrust.TryGetValue(ab, out float b)) { b = (float)eAfterburnerThrust.GetValue(ab); abBaseThrust[ab] = b; }
            eAfterburnerThrust.SetValue(ab, b * f);                   // afterburner thrust = base × factor (idempotent — no per-frame compounding)
        }
    }

    // Stretch a thrust-vs-speed curve along the speed axis (lifts the soft top-speed wall). Returns a new curve.
    static AnimationCurve StretchCurveTime(AnimationCurve src, float k)
    {
        if (src == null || k <= 0f || Mathf.Approximately(k, 1f)) return src;
        Keyframe[] ks = src.keys;
        for (int i = 0; i < ks.Length; i++) { ks[i].time *= k; ks[i].inTangent /= k; ks[i].outTangent /= k; }
        return new AnimationCurve(ks) { preWrapMode = src.preWrapMode, postWrapMode = src.postWrapMode };
    }

    // Elite aircraft damage reduction (covers late-attaching sub-damageables). Ground/ships use max-HP instead.
    internal static float EliteDamageInv(object dmg)
    {
        if (!MasterOn) return 1f;
        Unit u = DamageableUnit(dmg);
        // Only AIRCRAFT get elite durability as DAMAGE REDUCTION (timing-proof; covers pilot/engine sub-damageables that
        // attach late). GROUND + SHIPS take their elite durability as MAX-HITPOINTS on the parts instead (see ApplyElite),
        // so they are excluded here — no damage reduction for them.
        if (u == null || !(u is Aircraft ac)) return 1f;
        if (PlayerProtected(ac)) return 1f;                          // don't buff a human-piloted plane's durability (unless §1 Affect Players)
        try { if (!u.IsServer) return 1f; } catch { }                // damage is server-authoritative — never alter client-local numbers
        float f = EliteFactor(GetUnitSkill(u), EliteFullMult);
        return f > 1.0001f ? 1f / f : 1f;
    }

    static Unit DamageableUnit(object dmg)
    {
        if (dmg is UnitPart up) return up.parentUnit;      // most reliable (survives reparenting)
        if (dmg is Pilot pl) return pl.aircraft;
        if (dmg is Component c) return c.GetComponentInParent<Unit>();   // Turbofan, rotors, pods, etc.
        return null;
    }

    // Live fly-by-wire G-limit scaling (tighter elite turns). Idempotent via cached base.
    internal static void EliteScaleGLimit(ControlsFilter.FlyByWire fbw, Aircraft ac)
    {
        if (!MasterOn || fbw == null || ac == null || eFbwGLimit == null) return;
        float f = PlayerProtected(ac) ? 1f : EliteFactor(GetUnitSkill(ac), EliteFullMult);   // protected human pilot -> factor 1 = stock G limit
        if (!fbwBaseGLimit.TryGetValue(fbw, out float b)) { b = eFbwGLimit(fbw); fbwBaseGLimit[fbw] = b; }
        float target = b * (f > 1.0001f ? f : 1f);
        if (Mathf.Abs(eFbwGLimit(fbw) - target) > 0.001f) eFbwGLimit(fbw) = target;
    }

    // Live airbrake scaling (keeps accel/decel symmetric for boosted planes).
    internal static void EliteScaleAirbrake(Airbrake ab)
    {
        if (!MasterOn || ab == null || eAirbrakeDrag == null || eAirbrakeAircraft == null) return;
        Aircraft ac = eAirbrakeAircraft(ab);
        if (ac == null) return;
        float f = PlayerProtected(ac) ? 1f : EliteFactor(GetUnitSkill(ac), EliteFullMult);   // protected human pilot -> factor 1 = stock airbrake
        if (!airbrakeBaseDrag.TryGetValue(ab, out float b)) { b = eAirbrakeDrag(ab); airbrakeBaseDrag[ab] = b; }
        float target = b * (f > 1.0001f ? f : 1f);
        if (Mathf.Abs(eAirbrakeDrag(ab) - target) > 1e-6f) eAirbrakeDrag(ab) = target;
    }

    // Live control-surface authority scaling in the Burst job (spawn-time scaling was a no-op).
    internal static void EliteScaleControlSurface(ControlSurface cs)
    {
        if (!MasterOn || !EliteAirManeuver || cs == null || eCsAircraft == null || eCsJobFields == null) return;
        Aircraft ac; try { ac = eCsAircraft(cs); } catch { return; }
        if (ac == null || PlayerProtected(ac)) return;
        float f = EliteFactor(GetUnitSkill(ac), EliteTurnMult);
        try
        {
            ref var jf = ref eCsJobFields(cs);
            if (!jf.IsCreated) return;
            ref var r = ref jf.Ref();
            if (!csBaseRange.TryGetValue(cs, out var b)) { b = new Vector2(r.pitchRange, r.rollRange); csBaseRange[cs] = b; }
            float scale = f > 1.0001f ? f : 1f;
            float tp = b.x * scale, tr = b.y * scale;
            if (Mathf.Abs(r.pitchRange - tp) > 1e-5f) r.pitchRange = tp;
            if (Mathf.Abs(r.rollRange - tr) > 1e-5f) r.rollRange = tr;
        }
        catch { }
    }

    // Ship roll/yaw stabilizer: counters heel and P-only steering hunt on boosted turns (turn itself preserved).
    internal static void EliteStabilizeShip(ShipPropulsion sp)
    {
        if (!MasterOn || sp == null || eShipPropShip == null) return;
        Ship ship = eShipPropShip(sp);
        if (ship == null || ship.disabled || ship.rb == null) return;
        try { if (!ship.LocalSim) return; } catch { }
        float f = EliteFactor(GetUnitSkill(ship), EliteFullMult);
        if (f <= 1.0001f) { eShipYawSmoothed.Remove(ship); return; }

        var rb = ship.rb; var t = ship.transform;
        Vector3 axis = Vector3.Cross(t.up, Vector3.up);               // axis that rotates the deck back to level
        float roll = Vector3.Dot(axis, t.forward);                    // roll component only (~sin of heel; +/- by side)
        float rollRate = Vector3.Dot(rb.angularVelocity, t.forward);
        float yawRate = Vector3.Dot(rb.angularVelocity, t.up);        // heading turn rate
        float gain = f - 1f;                                          // more boost -> more stabilization

        // low-pass = the steady/intended turn; damp only the residual (the hunt), so the commanded turn is NOT opposed.
        float smooth = eShipYawSmoothed.TryGetValue(ship, out var ys) ? Mathf.Lerp(ys, yawRate, ShipYawSmooth) : yawRate;
        eShipYawSmoothed[ship] = smooth;
        float yawHunt = yawRate - smooth;

        Vector3 torque = t.forward * (roll * ShipRollKp - rollRate * ShipRollKd)   // right the deck + damp roll
                       + t.up * (-yawHunt * ShipYawKd);                            // damp only the yaw OSCILLATION
        rb.AddTorque(torque * gain, ForceMode.Acceleration);
    }

    // Speed-squared planting downforce for boosted ground units (grip at speed, stock feel when slow). Elite-only.
    internal static void GroundApplyDownforce(GroundVehicle v)
    {
        if (!MasterOn || v == null || v.remoteSim || v.rb == null) return;
        float f = EliteFactor(GetUnitSkill(v), EliteFullMult);
        if (f <= 1.0001f) return;                                         // non-elite (skill <= 4) -> no downforce
        if (Vector3.Dot(v.transform.up, Vector3.up) < 0.5f) return;       // tilted/inverted -> don't press it down (never fight a self-right)
        float sp = Mathf.Abs(v.speed);
        if (sp < GroundDownforceMinSpeed) return;
        float accel = Mathf.Min(GroundDownforceK * (f - 1f) * sp * sp, GroundDownforceMax);   // (f-1) = skill ramp 0..3
        try { v.rb.AddForce(-Vector3.up * accel, ForceMode.Acceleration); } catch { }
    }

    // Reinforce wing/tail joints for higher elite G (they snap on joint breakForce, not damage).
    internal static void EliteReinforceJoints(AeroPart part)
    {
        if (!MasterOn || part == null) return;
        Unit u = part.parentUnit;
        if (!(u is Aircraft ac)) return;
        if (PlayerProtected(ac)) return;                            // human-piloted plane's joints left stock (unless §1 Affect Players)
        float f = EliteFactor(GetUnitSkill(ac), EliteFullMult);
        if (f <= 1.0001f) return;
        var pjs = part.Joints;
        if (pjs == null) return;
        foreach (var pj in pjs)
        {
            if (pj == null) continue;
            try
            {
                pj.breakForce *= f; pj.breakTorque *= f;                       // base (survives the on-damage recompute)
                if (pj.joint != null) { pj.joint.breakForce *= f; pj.joint.breakTorque *= f; }   // live joint
            }
            catch { }
        }
    }

    // Pilot blackout tolerance: capture the flown aircraft's factor pre GLOC sim...
    internal static void EliteSetGlocFactor(Pilot pilot)
    {
        if (!MasterOn) { glocFactor = 1f; return; }
        glocFactor = (pilot != null && pilot.aircraft != null && !PlayerProtected(pilot.aircraft))
            ? Mathf.Max(EliteFactor(GetUnitSkill(pilot.aircraft), EliteFullMult), 1f) : 1f;   // human-piloted -> 1 unless §1 Affect Players
    }

    // ...then scale the G the sim sees, so elite pilots tolerate factor× more G.
    internal static void EliteScaleGloc(ref float gForce)
    {
        if (glocFactor > 1.0001f) gForce /= glocFactor;
    }

    // Structural G-damage tolerance: factor× G headroom before TakeGForceDamage bites. False skips the damage.
    internal static bool EliteGForceTolerance(Pilot pilot, ref float sqrGForces)
    {
        if (!MasterOn || pilot == null || pilot.aircraft == null) return true;
        if (PlayerProtected(pilot.aircraft)) return true;           // human-piloted plane left stock (unless §1 Affect Players)
        float f = EliteFactor(GetUnitSkill(pilot.aircraft), EliteFullMult);
        if (f <= 1.0001f) return true;
        float eff = sqrGForces / (f * f);
        if (eff <= 400f) return false;      // effective G below the 20G damage threshold -> no structural G-damage
        sqrGForces = eff;
        return true;
    }

    // Scale one gun to its owner's elite factor (per-gun tracking catches late-attaching turret guns).
    internal static bool EliteApplyGun(Gun g, Unit u)
    {
        if (!MasterOn) return true;
        if (g == null || eGunReload == null) return false;
        if (u == null) u = g.attachedUnit;
        if (u == null) return true;                                   // gun present but no owner yet — Gun.Awake will retry
        try { if (PlayerProtected(u)) return true; } catch { }        // human's guns left stock (unless §1 Affect Players)
        try { if (!u.IsServer) return true; } catch { }
        int gid = g.GetInstanceID();
        float target = EliteFactor(GetUnitSkill(u), EliteFullMult);
        float prev = eliteGunApplied.TryGetValue(gid, out var p) ? p : 1f;
        if (Mathf.Abs(target - prev) < 0.01f) return true;
        float ratio = target / Mathf.Max(prev, 0.01f);
        try
        {
            float rt0 = eGunReload(g);
            eGunReload(g) /= ratio;                                   // faster reload (the reliable lever)
            if (eGunFireRate != null) eGunFireRate(g) *= ratio;      // higher rate of fire
            if (eGunInterval != null) eGunInterval(g) /= ratio;      // shorter interval between rounds
            eliteGunApplied[gid] = target;
            if (cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[Elite] {u.name} gun reloadTime {rt0:0.00}->{eGunReload(g):0.00}s (x{target:0.00})");
        }
        catch { }
        return true;
    }
}

// Gun boost hook (catches late-attaching turret guns the spawn scan misses).
[HarmonyPatch(typeof(Gun), "Awake")]
public static class Gun_Awake_ElitePatch
{
    static void Postfix(Gun __instance)
    {
        try { ImprovedAIPlugin.EliteApplyGun(__instance, null); }
        catch { }
    }
}

// Spawn-time elite application (mission-set skill / Ace / faction multiplier).
[HarmonyPatch(typeof(Unit), "InitializeUnit")]
public static class Unit_InitializeUnit_ElitePatch
{
    static void Postfix(Unit __instance)
    {
        try { ImprovedAIPlugin.ApplyElite(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("elite init patch error: " + ex); }
    }
}

// Live jet-thrust hook.
[HarmonyPatch(typeof(JetNozzle), "Thrust")]
public static class JetNozzle_Thrust_ElitePatch
{
    static void Prefix(JetNozzle __instance, ref float thrustAmount)
    {
        try { ImprovedAIPlugin.EliteScaleNozzle(__instance, ref thrustAmount); }
        catch { }
    }
}

// Universal damage-reduction hook (covers every IDamageable: airframe/pilot/engine/rotor/pod).
[HarmonyPatch]
public static class IDamageable_TakeDamage_ElitePatch
{
    static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        System.Type[] types;
        try { types = typeof(UnitPart).Assembly.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types; }   // game assembly can have unloadable types
        var sig = new[] { typeof(float), typeof(float), typeof(float), typeof(float), typeof(float), typeof(PersistentID) };
        var seen = new HashSet<System.Reflection.MethodBase>();
        foreach (var t in types)
        {
            if (t == null || t.IsInterface || t.IsAbstract || !typeof(IDamageable).IsAssignableFrom(t)) continue;
            System.Reflection.MethodInfo m = null;
            try { m = t.GetMethod("TakeDamage", sig); } catch { }
            if (m == null || m.IsAbstract || !seen.Add(m)) continue;
            // The ref-param prefix binds by NAME — only patch methods with the expected lowercase names (skips e.g.
            // SubmunitionDispenser's "ImpactDamage"). Covers airframe UnitPart, Pilot, Turbofan engine, rotors, pods.
            var ps = m.GetParameters();
            if (ps.Length == 6 && ps[0].Name == "pierceDamage" && ps[1].Name == "blastDamage" && ps[3].Name == "fireDamage" && ps[4].Name == "impactDamage")
                yield return m;
        }
    }

    static void Prefix(object __instance, ref float pierceDamage, ref float blastDamage, ref float fireDamage, ref float impactDamage)
    {
        try
        {
            float inv = ImprovedAIPlugin.EliteDamageInv(__instance);
            if (inv >= 0.9999f) return;
            pierceDamage *= inv; blastDamage *= inv; fireDamage *= inv; impactDamage *= inv;
        }
        catch { }
    }
}

// Live G-limit hook.
[HarmonyPatch(typeof(ControlsFilter.FlyByWire), "Filter")]
public static class FlyByWire_Filter_ElitePatch
{
    static void Prefix(ControlsFilter.FlyByWire __instance, Aircraft aircraft)
    {
        try { ImprovedAIPlugin.EliteScaleGLimit(__instance, aircraft); }
        catch { }
    }
}

// Live airbrake hook.
[HarmonyPatch(typeof(Airbrake), "FixedUpdate")]
public static class Airbrake_FixedUpdate_ElitePatch
{
    static void Prefix(Airbrake __instance)
    {
        try { ImprovedAIPlugin.EliteScaleAirbrake(__instance); }
        catch { }
    }
}

// Live control-surface hook.
[HarmonyPatch(typeof(ControlSurface), "UpdateJobFields")]
public static class ControlSurface_UpdateJobFields_ElitePatch
{
    static void Postfix(ControlSurface __instance)
    {
        try { ImprovedAIPlugin.EliteScaleControlSurface(__instance); }
        catch { }
    }
}

// Ship stabilizer hook.
[HarmonyPatch(typeof(ShipPropulsion), "FixedUpdate")]
public static class ShipPropulsion_FixedUpdate_ElitePatch
{
    static void Postfix(ShipPropulsion __instance)
    {
        try { ImprovedAIPlugin.EliteStabilizeShip(__instance); }
        catch { }
    }
}

// Structural joint-reinforcement hook.
[HarmonyPatch(typeof(AeroPart), "CreateJoints")]
public static class AeroPart_CreateJoints_ElitePatch
{
    static void Postfix(AeroPart __instance)
    {
        try { ImprovedAIPlugin.EliteReinforceJoints(__instance); }
        catch { }
    }
}

// Pilot blackout hooks (capture factor pre sim, scale the G the sim sees).
[HarmonyPatch(typeof(PilotPlayerState), "FixedUpdateState")]
public static class PilotPlayerState_FixedUpdateState_ElitePatch
{
    static void Prefix(Pilot pilot)
    {
        try { ImprovedAIPlugin.EliteSetGlocFactor(pilot); }
        catch { }
    }
}

// (Scaled in EliteScaleGloc above.)
[HarmonyPatch(typeof(GLOC), "SimulateGLOC")]
public static class GLOC_SimulateGLOC_ElitePatch
{
    static void Prefix(ref float gForce)
    {
        try { ImprovedAIPlugin.EliteScaleGloc(ref gForce); }
        catch { }
    }
}

// Structural G-damage tolerance hook.
[HarmonyPatch(typeof(Pilot), "TakeGForceDamage")]
public static class Pilot_TakeGForceDamage_ElitePatch
{
    static bool Prefix(Pilot __instance, ref float sqrGForces)
    {
        try { return ImprovedAIPlugin.EliteGForceTolerance(__instance, ref sqrGForces); }
        catch { return true; }
    }
}
