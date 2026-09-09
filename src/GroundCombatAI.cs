// GroundCombatAI: per-unit combat FSM (retreat > halt > advance > pursue) + spacing + reverse. Host-side.
// Takeover = silence vanilla road nav (navigateToObjectives=false) and steer via off-road Shortcut points.
// Reverse = direct inputs with no waypoint live (job leaves them alone). Non-combatants live in GroundSupport.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

public partial class ImprovedAIPlugin
{
    // ---- ground config (§7): decision knobs in F1, everything spatial derived per-unit. ----
    static ConfigEntry<bool> cfgGroundReverse, cfgGroundHaltFace;
    static ConfigEntry<float> cfgGroundEngageRadius,
        cfgGroundFallbackRatio, cfgGroundClearTime, cfgGroundCommit;
    // Derived movement consts (fixed; spacing/look/contagion are per-unit dynamic).
    const float GroundTick = 1f;              // combat re-evaluate cadence (must stay < ~4s vanilla re-path)
    const float GroundObstacleRefresh = 0.4f; // rebuild game's obstacle list this often for owned units
    const float GroundStuckTimeout = 120f;    // no progress while closing this long -> road-AI reroute
    const float FormStuckWindow = 60f;        // ordered to move but stationary this long -> road-AI reroute / hold adapt
    const float GroundSepRangeFrac = 0.08f;   // desired spacing = weaponRange * this (3000m gun -> ~240m)
    const float GroundSepMin = 60f, GroundSepMax = 350f;   // clamp so scouts don't stack and long-range units don't over-spread
    const float GroundLookMin = 60f, GroundLookMax = 220f; // steering-point clamp
    const float GroundContagionMin = 300f, GroundContagionMax = 800f;
    const float MaxEngageRange = 6000f;       // no takeover/pursuit beyond this (extreme-range units would crawl
                                              // off-road for 8km+; their turrets still snipe via lock)

    // Shared reflection handles + helpers live in GroundCommon.cs.

    // Bind config (called from Awake).
    void GroundConfig()
    {
            cfgGroundEngageRadius = Config.Bind("7. Ground Forces", "Engage Radius (m)", 3000f, new ConfigDescription("Awareness/pursuit range: once engaged, how far the vehicle keeps considering & pursuing datalinked enemies, and how far one must stay before it disengages. NOTE: the actual engage TRIGGER is now the unit's own weapon max range + line-of-sight (not this fixed radius). Best set >= your ground units' weapon range.", new AcceptableValueRange<float>(200f, 6000f)));
            cfgGroundCommit = Config.Bind("7. Ground Forces", "Action Commit Time (s)", 5f, new ConfigDescription("Once a vehicle starts RETREATING or HALTING it stays in that action at least this long after the trigger clears, to stop rapid stutter when a condition flickers on/off. A higher-priority action (a stronger threat) still interrupts immediately.", new AcceptableValueRange<float>(0f, 10f)));
            cfgGroundFallbackRatio = Config.Bind("7. Ground Forces", "Fallback Strength Ratio", 0.4f, new ConfigDescription("When an enemy is actually shooting at me: if (my threat to that attacker)/(its threat to me) is at or below this, it counts as STRONGER and I bound away from it (reversing while the duel is mutual, driving when outranged). Otherwise I hold and trade fire. Uses the game's own role/type threat model.", new AcceptableValueRange<float>(0f, 1f)));
            cfgGroundClearTime = Config.Bind("7. Ground Forces", "Disengage Delay (s)", 60f, new ConfigDescription("After no datalinked enemy has been within the Engage (awareness) Radius for this long, the vehicle hands control back to the normal road-march AI.", new AcceptableValueRange<float>(1f, 60f)));
            cfgGroundReverse = Config.Bind("7. Ground Forces", "Reverse When Retreating", true, "When falling back, back up in REVERSE while the duel is mutual and the nose is on the threat (keeps front armor / turret on the enemy — pivots first if spun); when outranged it turns and drives regardless. Off = the vehicle always turns and drives away. Reversing needs Turn To Face on, else it engages regardless of nose heading.");
            cfgGroundHaltFace = Config.Bind("7. Ground Forces", "Turn To Face When Halted", true, "While halted to fire, rotate the hull to face the turret's target — and stop the residual spin from whatever turn it was mid-way through when it halted. Off = just stop (still kills the spin).");
            // REMOVED from F1 (now derived per-unit, fixed at last user values): Tactical Tick 1s -> GroundTick, Look Ahead 200 -> GroundLook(), Separation 250/w1 -> GroundSpacing(), Allied Engage 500 -> ContagionRadius(), Obstacle Refresh 0.4 -> GroundObstacleRefresh, Stuck 120 -> GroundStuckTimeout, Morale Radius 1500 (morale system removed in the per-unit rewrite), Avoid Strength (already inert).
            GroundCommonBind();   // shared reflection handles + helpers (GroundCommon.cs)
    }

    // ============================================================================
    // The individual combat state. Exactly ONE value per unit at any time.
    // Support/Defend are set ONLY by GroundSupport.DefensiveUpdate for non-combatants (never by the FSM).
    enum GCombat { None, Advance, Engage, Retreat, Defend, Pursue, Support }

    static string StateName(GCombat s)
    {
        switch (s)
        {
            case GCombat.Advance: return "advance";
            case GCombat.Engage: return "halt";
            case GCombat.Retreat: return "retreat";
            case GCombat.Defend: return "defend";
            case GCombat.Pursue: return "pursue";
            case GCombat.Support: return "support";
            default: return "none";
        }
    }

    class GState
    {
        public GroundVehicle v;
        public bool owned;
        public bool natural;        // engaged naturally (own range+LoS / turret lock / shot at) vs. by allied contagion; only natural propagates
        public bool savedNav;       // navigateToObjectives value before we took over
        public bool hasSpecialAI;   // artillery/rearm/repair/wreck -> excluded from the FULL combat AI (rearm/repair still get defensive mode)
        public bool noOverride;     // artillery / wreck-collector -> leave entirely to vanilla (not even defensive control)
        public bool stationaryFire; // fires an OVER-THE-HORIZON strategic weapon (ballistic/cruise/strato launcher) -> never drive it to resupply
        public bool hasGroundWeapon; // has a weapon effective vs ground (only these units engage ground; AA/SAM excluded)
        public float nextTick;
        public float lastEnemyTime;
        public float reengageAfter;    // brief cooldown after a stuck reroute before we may take over again
        public float lastProgressDist;  // closest we've gotten to the target while advancing (stuck detection)
        public float lastProgressTime;
        public int evadeSign;           // fixed per unit: default side for water-detour re-picks
        public float myWeaponRange;
        public bool hasIssued; public GlobalPosition lastIssued;   // current off-road move order (separation reads these)
        public bool hasLastEnemy; public GlobalPosition lastEnemyPos;
        // ---- the FSM ----
        public GCombat state;       // current maneuver state (None when not owned)
        public float retreatUntil;  // desire: keep retreating until this time (refreshed while outgunned)
        public float haltUntil;     // desire: keep halted until this time (refreshed while I have a shot)
        public Vector3 retreatDir; public float retreatDirUntil;   // committed fallback heading
        public float reverseUntil;  // reverse actuation window (UpdateJobFields postfix)
        public bool reverseBacking; public float reverseCommitUntil; public Vector3 reverseThreatDir; // latched backing sub-mode
        public bool hasHaltFace; public GlobalPosition haltFacePos;   // hull-facing target while halted/pivoting
        public float lastSpacing;   // smoothed spacing
        public Vector3 sepSmooth;   // smoothed separation vector
        public Vector3 sidestepDir; public float sidestepUntil;   // committed halted sidestep
        public float tickPhase;     // per-unit tick stagger 0..1 (breaks simultaneous re-evaluation)
        public float nextObstacle;  // next obstacle-list rebuild time
        // ---- water detour commitment ----
        public Vector3 waterDetourDir;   // committed heading around water (zero = none)
        public Vector3 waterDetourBase;  // tactical intent it was committed for
        public float waterDetourUntil;
        public int waterDetourSign;      // detour side (+1/-1, sticky)
        // ---- barren-halt watchdog (gun can't get on target from this spot) ----
        public float barrenHaltSince;
        public float suppressHaltUntil;
        // ---- ammo resupply (see GroundSupport.cs) ----
        public int resupplyPhase;   // 0 none, 1 seeking, 2 refilling, 3 returning home
        public bool resupplyWasHold;
        public GlobalPosition homePos;
        public float resupplyStart;
        public float refillStart;
        // ---- non-combatant support spot (see GroundSupport.cs) ----
        public bool hasSupSpot; public GlobalPosition supSpot; public float supSpotUntil;
    }

    static readonly Dictionary<int, GState> gStates = new Dictionary<int, GState>();
    static readonly List<Unit> gNearby = new List<Unit>();
    static float gHbLast;

    // Fixed lateral berth for obstacle lists (never speed-scaled; lookahead handles speed).
    const float NavListMargin = 1f;

    // Tactical status string for the spectate overlay.
    internal static string GroundStatus(GroundVehicle gv)
    {
        if (!MasterOn || gv == null) return "";
        bool routing = NavIsRouting(gv);
        string tactical = "";
        if (gStates.TryGetValue(gv.GetInstanceID(), out var s) && s != null)
        {
            if (s.resupplyPhase != 0) tactical = s.resupplyPhase == 3 ? "Returning to position" : (s.resupplyPhase == 2 ? "Rearming" : "Seeking ammo");
            else if (s.owned)
            {
                switch (s.state)
                {
                    case GCombat.Retreat: tactical = "Falling back"; break;
                    case GCombat.Engage:  tactical = "Holding & firing"; break;
                    case GCombat.Advance: tactical = "Pushing"; break;
                    case GCombat.Defend:  tactical = "Holding (support)"; break;
                    case GCombat.Support: tactical = "Moving to support position"; break;
                    case GCombat.Pursue:  tactical = s.hasLastEnemy ? "Pursuing last contact" : "Advancing"; break;
                    default:              tactical = "Engaged"; break;
                }
            }
            else if (gvCommanded != null && gvCommanded(gv)) tactical = "Player orders";
            else if (gv.GetHoldPosition()) tactical = "Holding position (vanilla)";
            else tactical = "Road march (vanilla AI)";
        }
        if (routing) return string.IsNullOrEmpty(tactical) ? "Routing around obstacle" : tactical + "\nRouting around obstacle";
        return tactical;
    }

    // Per-unit combat driver (postfix on GroundVehicle.Update, host-side, staggered ticks).
    internal static void GroundUpdate(GroundVehicle v)
    {
        if (!MasterOn || gvNavigate == null) return;
        if (v == null || v.remoteSim) return;   // only where this AI is actually simulated
        float now = Time.timeSinceLevelLoad;
        int id = v.GetInstanceID();
        gStates.TryGetValue(id, out var s);
        // Stale state (destroyed + ID reused, scene reload): drop and start clean.
        if (s != null && (s.v == null || s.v != v)) { gStates.Remove(id); s = null; }

        if (v.disabled) { if (s != null) { if (s.owned) ReleaseGround(s, false); gStates.Remove(id); } return; }

        if (cfgDiag.Value && now - gHbLast > 5f)
        {
            gHbLast = now; int owned = 0, adv = 0, eng = 0, ret = 0, pur = 0;
            foreach (var st in gStates.Values)
                if (st.owned)
                {
                    owned++;
                    switch (st.state)
                    {
                        case GCombat.Advance: adv++; break;
                        case GCombat.Engage: eng++; break;
                        case GCombat.Retreat: ret++; break;
                        case GCombat.Pursue: pur++; break;
                    }
                }
            Log.LogInfo($"[Ground] tracked={gStates.Count} owned={owned} advance={adv} halt={eng} retreat={ret} pursue={pur} engageR={cfgGroundEngageRadius.Value:0}");
        }

        if (s == null)
        {
            float groundRange = GroundWeaponRange(v, out bool hasGround);
            s = new GState
            {
                v = v,
                evadeSign = (id & 1) == 0 ? 1 : -1,
                myWeaponRange = groundRange,
                hasGroundWeapon = hasGround,
                hasSpecialAI = v.GetComponent<MobileArtilleryAI>() != null || v.GetComponent<RearmVehicleAI>() != null
                             || v.GetComponent<Repairer>() != null || v.GetComponent<WreckCollector>() != null,
                noOverride = v.GetComponent<MobileArtilleryAI>() != null || v.GetComponent<WreckCollector>() != null,
                stationaryFire = HasOverHorizonWeapon(v)
            };
            // Stagger ticks by instance ID so a line never re-evaluates on the same frame (ping-pong source).
            s.tickPhase = ((uint)(id * 2654435761u) % 100) / 100f;
            s.nextTick = now + s.tickPhase * GroundTick;
            gStates[id] = s;
        }
        if (now < s.nextTick) return;
        // Per-unit period spread (0.9-1.1x) keeps the stagger from re-syncing; stays < vanilla 4s re-path.
        s.nextTick = now + Mathf.Max(GroundTick, 0.2f) * (0.9f + 0.2f * s.tickPhase);

        // Ammo resupply first (takes priority over combat).
        if (ResupplyUpdate(s, now, v.GlobalPosition())) return;

        // never touch: immobile / slung / no-HQ / player-commanded / hold-position / artillery / wreck-collector
        if (!gvMobile(v) || s.noOverride || v.IsSlung() || v.NetworkHQ == null || gvCommanded(v) || v.GetHoldPosition())
        { if (s.owned) ReleaseGround(s, false); return; }

        GlobalPosition pos = v.GlobalPosition();

        // Non-combatants get defensive-only control (retreat/shelter, never advance).
        if (!s.hasGroundWeapon || s.hasSpecialAI)
        { DefensiveUpdate(s, now, pos); return; }

        // Trigger range = own ground-weapon max range (capped); unknown (0) falls back to awareness.
        float triggerRange = s.myWeaponRange > 1f ? Mathf.Min(s.myWeaponRange, MaxEngageRange) : cfgGroundEngageRadius.Value;
        // One datalink pass: nearest enemy to pursue + whether I can shoot one (range + LoS).
        bool hasEnemy = ScanEnemies(v, pos, triggerRange, cfgGroundEngageRadius.Value, out GlobalPosition epos, out float dEnemy, out bool canEngage);
        // Live fact: one of my turrets is engaging an enemy ground vehicle (any range).
        bool shootingGround = MyTurretTargetsGroundVehicle(v);
        // Live fact: an enemy is actually engaging me (turret on me, in its range + LoS, can hurt me).
        bool hasThreat = FindActiveThreat(v, pos, Mathf.Max(cfgGroundEngageRadius.Value, 3000f), out Unit threat, out GlobalPosition thpos,
                                          out float incomingSum, out float outgoingBest, out GlobalPosition swarmPos);

        if (!s.owned)
        {
            if (now < s.reengageAfter) return;              // brief cooldown after a stuck reroute
            if (!gvNavigate(v)) return;                     // only auto-attackers (skip scripted-hold)
            bool natural = canEngage || shootingGround;
            // Getting shot at wakes me up even beyond my own reach (and counts as directly engaged).
            bool shotAt = !natural && hasThreat;
            // Allied contagion: a naturally-engaged friendly nearby + my own target. Only natural propagates.
            bool contagion = !natural && !shotAt && hasEnemy && HasNaturalFriendlyNearby(v, pos, ContagionRadius(s));
            if (!natural && !shotAt && !contagion) return;  // no trigger -> stay on the vanilla road-march
            s.savedNav = gvNavigate(v);
            gvNavigate(v) = false;                          // take over: silence vanilla road nav
            gvResetStationary(v) = true;
            s.owned = true; s.natural = natural || shotAt; s.lastEnemyTime = now;
            s.lastProgressDist = float.MaxValue; s.lastProgressTime = now;
            if (cfgDiag.Value) Log.LogInfo($"[Ground] TAKEOVER {v.name} ({(natural ? "natural" : shotAt ? "shot-at" : "allied")}) enemy@{(hasEnemy ? dEnemy : -1f):0}m wr={s.myWeaponRange:0}");
        }
        else if (!s.natural && (canEngage || shootingGround))
        {
            s.natural = true;   // an allied-activated unit that reaches real contact may now itself activate others
        }

        if (hasEnemy) { s.lastEnemyTime = now; s.lastEnemyPos = epos; s.hasLastEnemy = true; }
        else if (s.hasLastEnemy && FastMath.InRange(pos, s.lastEnemyPos, 100f)) s.hasLastEnemy = false;   // arrived at a stale point with nothing live: forget it
        if (shootingGround) s.lastEnemyTime = now;   // keep engaged while firing, even if the datalink drops the target

        // disengage: no ground enemy within the awareness radius for a while -> hand back to the road AI
        if (!hasEnemy && now - s.lastEnemyTime > cfgGroundClearTime.Value)
        { if (cfgDiag.Value) Log.LogInfo($"[Ground] release(clear) {v.name}"); ReleaseGround(s, true); return; }

        // HALT signal: a ground turret can actually fire right now (acquired + barrel clear, incl. vehicles).
        bool haveShot = (hasEnemy || shootingGround) && MyGroundTurretCanFire(v);

        // Stuck detection: only while closing, never while trading fire or threatened (that's the wedge check).
        if (hasEnemy && !hasThreat && !haveShot)
        {
            if (dEnemy < s.lastProgressDist - 15f) { s.lastProgressDist = dEnemy; s.lastProgressTime = now; }
            if (now - s.lastProgressTime > GroundStuckTimeout)
            { if (cfgDiag.Value) Log.LogInfo($"[Ground] release(stuck) {v.name}"); s.reengageAfter = now + 12f; ReleaseGround(s, true); return; }
        }
        else if ((s.hasIssued || s.state == GCombat.Retreat) && !haveShot && Mathf.Abs(v.speed) < 1f && now - s.lastProgressTime > FormStuckWindow)
        {
            // Wedged: live order (or Retreat stop/pivot/reverse), stationary a full minute, no shot. Hand to road AI once.
            if (cfgDiag.Value) Log.LogInfo($"[Ground] release(wedge) {v.name} state={StateName(s.state)} threat={hasThreat}");
            s.reengageAfter = now + 12f; ReleaseGround(s, true); return;
        }
        else { s.lastProgressTime = now; if (hasEnemy && dEnemy < s.lastProgressDist) s.lastProgressDist = dEnemy; }

        GroundManeuver(s, now, hasEnemy, epos, haveShot, hasThreat, threat, thpos, incomingSum, outgoingBest, swarmPos);
    }

    // Release to vanilla. resumeMarch re-routes toward the enemy (vanilla no-ops near-identical targets, so reset first).
    static void ReleaseGround(GState s, bool resumeMarch)
    {
        var v = s.v;
        try
        {
            if (s.owned) gvNavigate(v) = s.savedNav;
            if (resumeMarch)
            {
                gvPathfinder(v)?.ClearDestination();
                // A holder goes back to holding (clear any stale AI waypoint, route nothing): handing a hold-flagged
                // unit an enemy destination is what produced "Holding position (vanilla)" units road-marching away.
                if (!v.GetHoldPosition() && NearestGroundEnemyAny(v, out GlobalPosition ep))
                    v.UnitCommand.SetDestination(ep, false);
            }
        }
        catch { }
        s.owned = false;
        s.state = GCombat.None;
        s.retreatUntil = 0f; s.haltUntil = 0f;   // desires die with the takeover (a fresh fight re-earns them)
        s.reverseBacking = false; s.reverseUntil = 0f; s.reverseThreatDir = Vector3.zero;
        s.sepSmooth = Vector3.zero; s.sidestepDir = Vector3.zero;
    }

    // ACTUATION (per-frame job-field writes, postfix on UpdateJobFields).

    // Straight reverse, driven directly every frame while live (slow + nose-on only). No waypoint live, so the job
    // leaves these inputs alone: steering 0, throttle -1, brake 0. Never via reverseTimer (unblind wiggle) or brake.
    internal static void GroundApplyReverse(GroundVehicle v)
    {
        if (!MasterOn || gvJobFieldsInfo == null || cfgGroundReverse == null || !cfgGroundReverse.Value) return;
        if (v == null || v.remoteSim) return;
        if (!gStates.TryGetValue(v.GetInstanceID(), out var s) || !s.owned || s.state != GCombat.Retreat) return;
        if (Time.timeSinceLevelLoad >= s.reverseUntil) return;
        if (v.speed > ReverseStopSpeed) return;   // still carrying forward momentum -> stop first, don't back yet
        try
        {
            if (!TryGetGroundJob(v, out var jf)) return;
            if (jf.IsCreated) { jf.Ref().inputs.steering = 0f; jf.Ref().inputs.throttle = -1f; jf.Ref().inputs.brake = 0f; }
            gvResetStationary(v) = true;
        }
        catch { }
    }

    // Per-frame hull facing: rotate toward the turret target while halted, pivot the nose mid-retreat.
    // Hands off while reversing (reverse owns the frame) and while driving on a waypoint (driver owns inputs).
    // Reverse needs Turn To Face on, else the pivot never aims the nose and backing stays blind.
    internal static void GroundApplyHaltFacing(GroundVehicle v)
    {
        if (!MasterOn || gvJobFieldsInfo == null || v == null || v.remoteSim) return;
        if (!gStates.TryGetValue(v.GetInstanceID(), out var s) || !s.owned) return;
        if (Time.timeSinceLevelLoad < s.reverseUntil) return;   // reverse live: GroundApplyReverse already drove this frame — hands off entirely
        if (s.state == GCombat.Retreat && s.hasIssued) return;  // driving out on a waypoint: driver owns inputs — hands off
        // Facing runs halted to fire (Engage, always — including the spin-stop) and while stopped/pivoting mid-retreat
        // (Retreat + no move order). Anything else: hands off.
        if (s.state != GCombat.Engage && !(s.state == GCombat.Retreat && !s.hasIssued)) return;
        try
        {
            if (!TryGetGroundJob(v, out var jf)) return;
            if (!jf.IsCreated) return;
            float steer = 0f;
            if (cfgGroundHaltFace != null && cfgGroundHaltFace.Value && s.hasHaltFace)
            {
                Vector3 fwd = Flat(v.transform.forward);
                Vector3 to = Flat(s.haltFacePos - v.GlobalPosition());
                if (fwd != Vector3.zero && to != Vector3.zero)
                {
                    float yawErr = Vector3.SignedAngle(fwd, to, Vector3.up);   // + = target is to the right
                    if (Mathf.Abs(yawErr) < 4f) steer = 0f;                    // deadband: don't hunt around zero
                    else steer = Mathf.Clamp(yawErr / 35f, -1f, 1f);           // gentler than before; full lock past ~35deg (no overshoot)
                }
            }
            jf.Ref().inputs.steering = steer;   // 0 = stop the spin / brake straight; nonzero = rotate toward the target
            // Halted/pivoting (no move order): pin the drivetrain. Engage always pins — halting from a reverse
            // (rolling backwards) must stop promptly to fire. Retreat stop/pivot keeps one exception: while still
            // rolling backwards (a lapsed reverse window), braking slammed the hull every second = the wobble, so
            // coast (brake 0) and let the next Retreat tick re-arm reverse or pivot deliberately.
            if (!s.hasIssued)
            {
                jf.Ref().inputs.throttle = 0f;
                jf.Ref().inputs.brake = (s.state == GCombat.Retreat && v.speed < -ReverseStopSpeed) ? 0f : 1f;
            }
            else jf.Ref().inputs.brake = 0f;
            gvResetStationary(v) = true;          // stay dynamic so the steering torque can turn the hull
        }
        catch { }
    }
    // Reverse tunables: enter aligned, hold wider, drive when already facing away, never back with momentum.
    const float ReverseEngageYaw = 30f;   // pivot until the nose is within this of the threat before backing
    const float ReverseHoldYaw = 60f;     // once backing, keep backing until drift exceeds this (no per-tick pivot flip)
    const float ReverseDriveYaw = 120f;   // nose this far off = already pointing at the escape: drive, don't spin 180
    const float ReverseStopSpeed = 2f;    // forward speed above this: brake straight first, don't pivot or reverse yet

    // Rebuild the game's obstacle list for owned units (faster than the vanilla cadence). Self-throttled.
    internal static void GroundRefreshObstacles(GroundVehicle v)
    {
        if (!MasterOn || gvUpdateObstacles == null) return;
        if (v == null || v.remoteSim) return;
        if (!gStates.TryGetValue(v.GetInstanceID(), out var s) || !s.owned) return;
        float now = Time.timeSinceLevelLoad;
        if (now < s.nextObstacle) return;
        s.nextObstacle = now + GroundObstacleRefresh;
        try { gvUpdateObstacles.Invoke(v, null); } catch { }
    }

    // Curate the obstacle list: drop moving traffic we won't collide with, undo the game's radius padding.
    internal static void GroundPruneObstacles(GroundVehicle v)
    {
        if (!MasterOn || gvObstacles == null || v == null || v.remoteSim) return;
        List<Obstacle> obs;
        try { obs = gvObstacles(v); } catch { return; }
        if (obs == null || obs.Count == 0) return;
        Vector3 self = v.transform.position;
        Vector3 vel = v.rb != null ? v.rb.velocity : Vector3.zero; vel.y = 0f;
        float mySpeed = vel.magnitude;
        Vector3 myDir = mySpeed > 0.5f ? vel / mySpeed : Vector3.zero;
        for (int i = obs.Count - 1; i >= 0; i--)
        {
            var o = obs[i];
            if (o.Transform == null) continue;
            var gv = o.Transform.GetComponent<GroundVehicle>();
            if (gv != null && !gv.disabled && Mathf.Abs(gv.speed) > 1f && !MovingCollisionCourse(v, gv, self, myDir, mySpeed))
            { obs.RemoveAt(i); continue; }   // moving traffic we will NOT collide with -> ignore (no swerving)
            var u = o.Transform.GetComponent<Unit>();
            float actual = u != null ? u.maxRadius : o.Radius;             // undo the game's maxRadius*1.4 pad for units/wrecks
            obs[i] = new Obstacle(o.Transform, actual + NavListMargin, o.Top);
        }
    }

    // True only for same-direction traffic ahead that we're catching, inside our corridor (rear-end risk).
    static bool MovingCollisionCourse(GroundVehicle v, GroundVehicle o, Vector3 self, Vector3 myDir, float mySpeed)
    {
        if (myDir == Vector3.zero || mySpeed < 3f) return false;                  // we're (nearly) stopped — nothing to rear-end
        Vector3 toO = o.transform.position - self; toO.y = 0f;
        float dist = toO.magnitude;
        if (dist < 1f) return false;
        if (Vector3.Dot(toO / dist, myDir) < 0.5f) return false;                  // not ahead of us (>60deg off our travel direction)
        Vector3 oVel = o.rb != null ? o.rb.velocity : Vector3.zero; oVel.y = 0f;
        if (Vector3.Dot(oVel, myDir) < 0.3f) return false;                        // not moving our way (oncoming/crossing)
        if (mySpeed - Mathf.Abs(o.speed) < 1f) return false;                      // not closing on it — no rear-end risk
        float lateral = Mathf.Abs(toO.x * myDir.z - toO.z * myDir.x);             // 2D cross = offset from our path line
        if (lateral > v.maxRadius + o.maxRadius + NavListMargin) return false;    // beside our corridor, not in it
        return true;
    }

    // Desired spacing: weapon reach scaled by local density (packed tightens to 0.45x, never below min).
    static float GroundSpacing(GState s, GlobalPosition pos)
    {
        float full = Mathf.Clamp(s.myWeaponRange * GroundSepRangeFrac, GroundSepMin, GroundSepMax);
        var hq = s.v != null ? s.v.NetworkHQ : null;
        if (hq == null) { s.lastSpacing = full; return full; }
        // Count friendlies inside the FULL spacing (fixed aperture — not the shrinking value, or the count and the
        // radius chase each other and oscillate).
        int friends = 0;
        BattlefieldGrid.GetUnitsInRangeNonAlloc(pos, full, gNearby);
        for (int i = 0; i < gNearby.Count; i++)
        {
            var u = gNearby[i];
            if (u == null || u == s.v || u.disabled || !(u is GroundVehicle) || u.NetworkHQ != hq) continue;
            friends++;
        }
        // Alone (0): full spacing. Thinning: ease open toward full; packed: tighten, but never below 0.45x —
        // collapsing big battles to the minimum is what blobbed large engagements.
        float target = full * Mathf.Clamp(1f - friends / 12f, 0.45f, 1f);
        target = Mathf.Clamp(target, GroundSepMin, full);
        float cur = s.lastSpacing > 1f ? s.lastSpacing : target;
        s.lastSpacing = Mathf.Lerp(cur, target, 0.35f);
        return s.lastSpacing;
    }
    // Steering-point distance: scales with closing distance (far = straight legs, near = tight).
    static float GroundLook(float distToGoal)
    {
        if (distToGoal <= 1f) return GroundLookMin;
        return Mathf.Clamp(distToGoal * 0.25f, GroundLookMin, GroundLookMax);
    }
    // Allied activation radius (~half own weapon range; needs its own target anyway).
    static float ContagionRadius(GState s)
    {
        return Mathf.Clamp(s.myWeaponRange > 1f ? s.myWeaponRange * 0.5f : 500f, GroundContagionMin, GroundContagionMax);
    }

    // THE FSM: Retreat (outgunned) > Engage (firing solution) > Advance (push) > Pursue (last known).
    static void GroundManeuver(GState s, float now, bool hasTarget, GlobalPosition tpos,
                               bool haveShot, bool hasThreat, Unit threat, GlobalPosition thpos,
                               float incomingSum, float outgoingBest, GlobalPosition swarmPos)
    {
        var v = s.v;
        GlobalPosition pos = v.GlobalPosition();
        // "front" = toward the fight; the frontline forms perpendicular to this and faces the enemy
        Vector3 front = hasThreat ? Flat(thpos - pos) : (hasTarget ? Flat(tpos - pos) : Flat(v.transform.forward));
        if (front == Vector3.zero) front = Flat(v.transform.forward);

        // 1) TRIGGERS (min-commit desires; higher priority still interrupts immediately).
        // Outmatched = my best punch vs their combined observer weight (1v1 = the duel, Nv1 = routs pre first shot).
        bool strongerThreat = incomingSum > 0.001f
            && outgoingBest / Mathf.Max(incomingSum, 0.01f) <= cfgGroundFallbackRatio.Value;

        float commit = cfgGroundCommit.Value;
        if (strongerThreat) s.retreatUntil = now + commit;
        // Halt only with a real firing solution (being shot at without one just makes a stationary target).
        if (haveShot) s.haltUntil = now + commit;
        // Point-blank without a shot (dead-zone weapon, blocked barrels, dead turret): STOP instead of ramming.
        else if (hasTarget && !haveShot && FastMath.Distance(pos, tpos) < 150f) s.haltUntil = now + commit;
        bool retreating = now < s.retreatUntil;
        bool halting = now < s.haltUntil && now >= s.suppressHaltUntil;

        // Barren-halt watchdog: stationary but never on-target = gun can't solve from here; move to change geometry.
        if (halting)
        {
            if (MyGroundTurretOnTarget(v)) s.barrenHaltSince = 0f;
            else
            {
                if (s.barrenHaltSince == 0f) s.barrenHaltSince = now;
                else if (now - s.barrenHaltSince > 8f)
                {
                    s.barrenHaltSince = 0f; s.haltUntil = 0f; s.suppressHaltUntil = now + 10f;
                    halting = false;
                    if (cfgDiag.Value) Log.LogInfo($"[Ground] {v.name} halt suppressed 10s — gun cannot get on target from this spot");
                }
            }
        }
        else s.barrenHaltSince = 0f;

        // 2) SELECT highest-priority live desire, else positional default.
        GCombat next;
        if (retreating) next = GCombat.Retreat;
        else if (halting) next = GCombat.Engage;
        else if (hasTarget) next = GCombat.Advance;
        else next = GCombat.Pursue;

        // 3) TRANSITION once, then 4) TICK the state.
        if (next != s.state)
        {
            if (next == GCombat.Retreat)
            {
                // Enter(Retreat): clear Engage actuation (its brake would pin the retreat).
                s.haltUntil = 0f; s.hasHaltFace = false;
                s.retreatDir = Vector3.zero;   // re-pick the committed heading this tick
                s.reverseBacking = false; s.reverseThreatDir = Vector3.zero; s.reverseUntil = 0f;
            }
            if (next != GCombat.Retreat) { s.reverseBacking = false; s.reverseUntil = 0f; }
            if (cfgDiag.Value) Log.LogInfo($"[Ground] {v.name} -> {StateName(next)}{(next == GCombat.Retreat && strongerThreat && threat != null ? " vs " + threat.name : "")}");
            s.state = next;
        }

        // 4) TICK the current state.
        Vector3 desired;
        switch (s.state)
        {
            case GCombat.Retreat:
            {
                // Stop -> pivot -> reverse while the duel is mutual and in reach; drive out when outranged,
                // nose-away, or reverse off. Backing latches across aimer/LoS flicker and broken LoS in cover.
                bool reverseOn = cfgGroundReverse != null && cfgGroundReverse.Value;
                bool canFace = cfgGroundHaltFace != null && cfgGroundHaltFace.Value;
                float duelR = s.myWeaponRange > 1f ? s.myWeaponRange : 800f;
                // Fallback threat for range: live shooter first, else nearest observer (pre-shot rout).
                bool haveFallback = false; float fallbackDist = float.MaxValue;
                if (hasThreat) { fallbackDist = FastMath.Distance(pos, thpos); haveFallback = true; }
                else if (incomingSum > 0.001f) { fallbackDist = FastMath.Distance(pos, swarmPos); haveFallback = true; }
                bool inRangeNow = haveFallback && fallbackDist < duelR;
                bool wasBacking = s.reverseBacking && now < s.reverseCommitUntil;
                bool wantReverse = reverseOn && canFace && (inRangeNow || wasBacking);
                if (wantReverse)
                {
                    // Threat dir: live aimer, else stored dir (rides through flicker), else current swarm.
                    Vector3 threatDir = Vector3.zero; GlobalPosition threatPos = pos;
                    if (hasThreat) { threatDir = Flat(thpos - pos); threatPos = thpos; }
                    else if (s.reverseThreatDir != Vector3.zero) { threatDir = Flat(s.reverseThreatDir); threatPos = pos + threatDir * 100f; }
                    else if (incomingSum > 0.001f) { threatDir = Flat(swarmPos - pos); threatPos = swarmPos; }
                    if (threatDir == Vector3.zero) threatDir = Flat(v.transform.forward);
                    float yawErr = Vector3.zero == threatDir ? 0f
                        : Mathf.Abs(Vector3.SignedAngle(Flat(v.transform.forward), threatDir, Vector3.up));
                    // Nose already at the escape: drive out forward instead of spinning 180 to back.
                    if (yawErr <= ReverseDriveYaw)
                    {
                        float holdYaw = wasBacking ? ReverseHoldYaw : ReverseEngageYaw;
                        s.reverseBacking = true;
                        s.reverseCommitUntil = now + Mathf.Max(cfgGroundCommit.Value, 2f);
                        s.reverseThreatDir = threatDir;
                        // 1) STOP: bleed forward momentum (straight brake, no steering yet).
                        if (v.speed > ReverseStopSpeed)
                        {
                            gvPathfinder(v)?.ClearDestination(); s.hasIssued = false;
                            s.hasHaltFace = false;
                            s.reverseUntil = 0f;
                            return;
                        }
                        // 2) PIVOT: aim the nose first (never back blind).
                        if (yawErr > holdYaw)
                        {
                            gvPathfinder(v)?.ClearDestination(); s.hasIssued = false;
                            s.hasHaltFace = true; s.haltFacePos = threatPos;
                            s.reverseUntil = 0f;
                            return;
                        }
                        // 3) REVERSE: slow + aimed + dry behind.
                        Vector3 backDir = Flat(-v.transform.forward);
                        float backLook = GroundLook(120f);
                        if (backDir == Vector3.zero || CorridorDry(pos, pos + backDir * backLook))
                        {
                            gvPathfinder(v)?.ClearDestination(); s.hasIssued = false;
                            s.hasHaltFace = false;
                            s.reverseUntil = now + Mathf.Max(GroundTick, 0.2f) + 0.35f;
                            return;
                        }
                        // Water/cliff behind: fall through to drive out.
                    }
                    else s.reverseBacking = false;
                }
                else s.reverseBacking = false;
                // Drive out rearward on a committed heading (away from shooter, else nearest observer). No halt-facing:
                // the waypoint driver owns inputs while issued.
                s.hasHaltFace = false;
                s.reverseUntil = 0f;
                Vector3 away = Vector3.zero;
                if (hasThreat) away = Flat(pos - thpos);
                else if (incomingSum > 0.001f) away = Flat(pos - swarmPos);
                if (away == Vector3.zero) away = front;
                float rlook = GroundLook(200f);
                if (s.retreatDir == Vector3.zero || now >= s.retreatDirUntil
                    || Vector3.Dot(s.retreatDir, away) < 0.5f
                    || !CorridorDry(pos, pos + s.retreatDir * rlook))
                { s.retreatDir = away; s.retreatDirUntil = now + Mathf.Max(cfgGroundCommit.Value, 2f); }
                desired = s.retreatDir;
                break;
            }
            case GCombat.Engage:
            {
                // Crowded halt: sidestep to firing spacing first (turrets aim independently); full halt with elbow room.
                if (CrowdSidestep(s, pos, front, out Vector3 sidestep))
                {
                    IssueMove(s, pos, sidestep, pos + sidestep * 60f);
                    if (MyTurretTargetPos(v, out var sface)) { s.hasHaltFace = true; s.haltFacePos = sface; }
                    else if (hasTarget) { s.hasHaltFace = true; s.haltFacePos = tpos; }
                    else s.hasHaltFace = false;
                    return;
                }
                // Halt and fire (a blocking friendly means haveShot is false -> advance instead).
                gvPathfinder(v)?.ClearDestination();
                s.hasIssued = false;
                // Face the hull at what the turret is shooting (also kills residual spin).
                if (MyTurretTargetPos(v, out var facePos)) { s.hasHaltFace = true; s.haltFacePos = facePos; }
                else if (hasTarget) { s.hasHaltFace = true; s.haltFacePos = tpos; }
                else s.hasHaltFace = false;
                return;   // halted: no move order, no separation
            }
            case GCombat.Advance:
            {
                // Push at the nearest known enemy; separation below fans the line out.
                desired = Flat(tpos - pos);
                break;
            }
            default:   // GCombat.Pursue (and None safety: drift forward)
            {
                desired = s.hasLastEnemy ? Flat(s.lastEnemyPos - pos) : Flat(v.transform.forward);
                break;
            }
        }

        if (desired == Vector3.zero) desired = front;
        if (Mathf.Abs(v.speed) > 1f) s.lastProgressTime = now;   // moving under orders: wedge timer resets here
        desired = ApplySeparation(s, pos, desired, front);
        IssueMove(s, pos, desired);
    }

    // Issue an off-road Shortcut order: `exact` = go to that point, else a steering point `look` ahead.
    const float WaterLookMax = 500f;   // corridor scan cap (re-checked every tick as we close)
    const float OrderCommitAngle = 25f;   // re-issue only on a genuinely new heading (deadband kills wobble feedback)
    static void IssueMove(GState s, GlobalPosition pos, Vector3 dir, GlobalPosition? exact = null)
    {
        dir = Flat(dir);
        if (dir == Vector3.zero) return;
        float look = exact.HasValue ? Mathf.Max(FastMath.Distance(pos, exact.Value), 10f) : GroundLook(FastMath.Distance(pos, s.hasLastEnemy ? s.lastEnemyPos : pos + dir * 200f));
        float now = Time.timeSinceLevelLoad;

        // WATER: validate the whole corridor, not just the endpoint (a dry far shore hides a lake between).
        // Blocked -> commit to a one-side detour until the direct corridor is dry again (no re-pick oscillation).
        float waterLook = Mathf.Min(look, WaterLookMax);
        if (!CorridorDry(pos, pos + dir * waterLook) || (s.waterDetourDir != Vector3.zero && now < s.waterDetourUntil))
        {
            dir = WaterDetour(s, now, pos, dir, look);
            if (dir == Vector3.zero)
            {
                // Hemmed in by water: hold the shore and let the stuck-timeout hand to road AI once (bridges).
                if (s.hasIssued && !CorridorDry(pos, s.lastIssued)) { gvPathfinder(s.v)?.ClearDestination(); s.hasIssued = false; }
                return;
            }
        }

        // Building routing lives in the GetSteerpoint hook; here we only place the tactical destination.
        GlobalPosition dry = DryPoint(pos, pos + dir * look);
        if (dry.Equals(pos)) return;   // no dry endpoint: keep the prior order this tick
        // ORDER COMMITMENT (deadband): same heading + far from goal = keep the order (re-issuing every tick's
        // wobble flickered destinations and fed back into neighbours' separation). New heading (>25°), arrival
        // (<18m), or dead stop re-issues immediately; a stopped unit always re-issues (never freeze).
        if (s.hasIssued && Mathf.Abs(s.v.speed) > 1f && FastMath.OutOfRange(pos, s.lastIssued, 18f))
        {
            Vector3 cur = Flat(s.lastIssued - pos);
            if ((cur != Vector3.zero && Vector3.Angle(cur, dir) < OrderCommitAngle)
                || FastMath.InRange(dry, s.lastIssued, 12f)) return;
        }
        var pf = gvPathfinder(s.v);
        if (pf == null) return;
        pf.Shortcut(dry);
        gvResetStationary(s.v) = true;   // keep it out of the 5s-idle kinematic freeze
        s.lastIssued = dry; s.hasIssued = true;
    }

    // ---- sensing helpers ----

    // Nearest datalinked enemy at any range (march target, support back-vector). Skips long-unseen ghosts.
    static bool NearestGroundEnemyAny(GroundVehicle v, out GlobalPosition epos)
    {
        epos = v.GlobalPosition();
        var hq = v.NetworkHQ; if (hq == null) return false;
        float now = Time.timeSinceLevelLoad;
        GlobalPosition from = v.GlobalPosition(); float bestSq = float.MaxValue; bool found = false;
        foreach (var kv in hq.trackingDatabase)
        {
            var ti = kv.Value;
            if (!ti.TryGetUnit(out var u) || u == null || u.disabled) continue;
            if (now - ti.lastSpottedTime > GhostContactTimeout) continue;
            if (!(u is GroundVehicle || u is Building)) continue;
            if (u.NetworkHQ == null || u.NetworkHQ == hq) continue;
            GlobalPosition p = ti.GetPosition();
            float dsq = FastMath.SquareDistance(p, from);
            if (dsq < bestSq) { bestSq = dsq; epos = p; found = true; }
        }
        return found;
    }

    // Long-unseen contacts are ignored for maneuver (movement never chases ghosts; targeting scans fresher).
    const float GhostContactTimeout = 120f;

    // One datalink pass: nearest ground enemy to pursue + whether any is in my range + LoS (engage trigger).
    static bool ScanEnemies(GroundVehicle v, GlobalPosition pos, float weaponRange, float awareness,
        out GlobalPosition nearestPos, out float nearestDist, out bool canEngage)
    {
        nearestPos = pos; nearestDist = float.MaxValue; canEngage = false;
        Unit nearest = null;
        var hq = v.NetworkHQ; if (hq == null) return false;
        float now = Time.timeSinceLevelLoad;
        weaponRange = Mathf.Min(weaponRange, MaxEngageRange);
        float eff = Mathf.Max(awareness, weaponRange);
        float bestSq = eff * eff, wrSq = weaponRange * weaponRange, nearSq = float.MaxValue;
        foreach (var kv in hq.trackingDatabase)
        {
            var ti = kv.Value;
            if (!ti.TryGetUnit(out var u) || u == null || u.disabled) continue;
            if (now - ti.lastSpottedTime > GhostContactTimeout) continue;   // long-unseen ghost, not a maneuver target
            if (u is not GroundVehicle) continue;   // buildings never trigger engagement: only mobile ground threats
            if (u.NetworkHQ == null || u.NetworkHQ == hq) continue;
            GlobalPosition p = ti.GetPosition();
            float dsq = FastMath.SquareDistance(p, pos);
            if (dsq <= bestSq && dsq < nearSq) { nearSq = dsq; nearest = u; nearestPos = p; }
            if (!canEngage && weaponRange > 1f && dsq <= wrSq && HasLoS(pos, p)) canEngage = true;
        }
        if (nearest == null) return false;
        nearestDist = Mathf.Sqrt(nearSq);
        return true;
    }

    // Nearest live aimer (turret on me, in its range + LoS) + swarm tally (all observers in range + LoS).
    // Outputs: incomingSum (their combined weight), outgoingBest (my best punch), swarmPos (nearest observer).
    static bool FindActiveThreat(GroundVehicle v, GlobalPosition pos, float scanR, out Unit threat, out GlobalPosition tpos,
                                 out float incomingSum, out float outgoingBest, out GlobalPosition swarmPos)
    {
        threat = null; tpos = pos;
        incomingSum = 0f; outgoingBest = 0f; swarmPos = pos;
        var hq = v.NetworkHQ; if (hq == null || turretTarget == null) return false;
        float scanSq = scanR * scanR, bestSq = float.MaxValue, swarmSq = float.MaxValue;
        foreach (var kv in hq.trackingDatabase)
        {
            var ti = kv.Value;
            if (!ti.TryGetUnit(out var u) || u == null || u.disabled) continue;
            if (u is not GroundVehicle) continue;   // only a ground vehicle counts as an engaging threat; buildings excluded
            if (u.NetworkHQ == null || u.NetworkHQ == hq) continue;
            GlobalPosition p = u.GlobalPosition();
            float dsq = FastMath.SquareDistance(p, pos);
            if (dsq > scanSq) continue;
            float incoming = SafeThreat(v.definition, u.definition);
            if (incoming <= 0.001f) continue;       // its weapons can't hurt my type (e.g. anti-air) -> neither threat nor counted
            // turret-target scan last: it walks the enemy's stations/turrets (most expensive gate)
            float er = MaxWeaponRange(u); if (er < 1f) er = Mathf.Sqrt(dsq);
            if (dsq > er * er) continue;             // out of its range -> can't shoot me
            if (!HasLoS(pos, p)) continue;           // no line of sight -> can't shoot me
            incomingSum += incoming;                                          // observer counted...
            float outgoing = SafeThreat(u.definition, v.definition);
            if (outgoing > outgoingBest) outgoingBest = outgoing;
            if (dsq < swarmSq) { swarmSq = dsq; swarmPos = p; }
            if (!EnemyTargetsMe(u, v)) continue;     // ...but only an aimer is an engager (LoS alone is not enough)
            if (dsq >= bestSq) continue;
            bestSq = dsq; threat = u; tpos = p;
        }
        return threat != null;
    }

    // Can a ground turret actually fire right now? (acquired target + barrel clear of vehicles; terrain ignored.)
    static bool MyGroundTurretCanFire(GroundVehicle v)
    {
        if (turretTarget == null) return false;
        var stations = v.weaponStations;
        if (stations == null) return false;
        var myHQ = v.NetworkHQ;
        for (int i = 0; i < stations.Count; i++)
        {
            var ws = stations[i];
            if (ws == null || ws.WeaponInfo == null || ws.WeaponInfo.effectiveness.antiSurface <= 0.05f) continue;
            var turrets = ws.Turrets;
            if (turrets == null) continue;
            float maxRange = ws.WeaponInfo.targetRequirements.maxRange;
            for (int j = 0; j < turrets.Count; j++)
            {
                var t = turrets[j];
                if (t == null) continue;
                var tgt = turretTarget(t);
                // Acquired = in-cone/in-range/LoS vetted (don't require IsOnTarget: it needs a steady hull = deadlock).
                if (tgt == null || tgt.disabled || tgt is not GroundVehicle || tgt.NetworkHQ == myHQ) continue;  // ground vehicles only, not buildings
                // Reject only if ANOTHER UNIT blocks the line (terrain false-positives near the target's feet).
                Vector3 muzzle = t.transform.position + Vector3.up * 0.5f;
                Vector3 center = tgt.transform.position + Vector3.up * 1.5f;   // aim center-mass, not the ground origin
                float targetRange = Vector3.Distance(muzzle, center);
                if (maxRange > 1f && targetRange > maxRange) continue;
                if (Physics.Linecast(muzzle, center, out var hit, ~(int)PhysicsLayers.ExclusionZonesMask))
                {
                    var blocker = hit.collider.GetComponentInParent<Unit>();
                    if (blocker != null && blocker != v && blocker != tgt && !blocker.disabled)
                        continue;                                             // a vehicle blocks this shot -> try another turret
                }
                return true;                                                 // clear (nothing, terrain-only, or the target itself)
            }
        }
        return false;
    }

    // Does any of my anti-surface turrets currently have an enemy GROUND VEHICLE locked? True regardless of
    // range / awareness radius / datalink — the direct "I am shooting a ground unit" fact used to flag me engaged.
    static bool MyTurretTargetsGroundVehicle(GroundVehicle v)
    {
        if (turretTarget == null) return false;
        var stations = v.weaponStations;
        if (stations == null) return false;
        var myHQ = v.NetworkHQ;
        for (int i = 0; i < stations.Count; i++)
        {
            var ws = stations[i];
            if (ws == null || ws.WeaponInfo == null || ws.WeaponInfo.effectiveness.antiSurface <= 0.05f) continue;
            var turrets = ws.Turrets;
            if (turrets == null) continue;
            for (int j = 0; j < turrets.Count; j++)
            {
                var t = turrets[j];
                if (t == null) continue;
                var tgt = turretTarget(t);
                if (tgt != null && !tgt.disabled && tgt is GroundVehicle && tgt.NetworkHQ != myHQ) return true;
            }
        }
        return false;
    }

    // Is any ground turret barrel-converged on target? (barren-halt watchdog: never-true = unshootable spot.)
    static bool MyGroundTurretOnTarget(GroundVehicle v)
    {
        if (turretTarget == null) return false;
        var stations = v.weaponStations;
        if (stations == null) return false;
        var myHQ = v.NetworkHQ;
        for (int i = 0; i < stations.Count; i++)
        {
            var ws = stations[i];
            if (ws == null || ws.WeaponInfo == null || ws.WeaponInfo.effectiveness.antiSurface <= 0.05f) continue;
            var turrets = ws.Turrets;
            if (turrets == null) continue;
            for (int j = 0; j < turrets.Count; j++)
            {
                var t = turrets[j];
                if (t == null) continue;
                var tgt = turretTarget(t);
                if (tgt == null || tgt.disabled || tgt is not GroundVehicle || tgt.NetworkHQ == myHQ) continue;
                try { if (t.IsOnTarget()) return true; } catch { }
            }
        }
        return false;
    }

    // Position of what my turrets aim at (ground-capable first: face the ground fight, not AA targets).
    static bool MyTurretTargetPos(GroundVehicle v, out GlobalPosition pos)
    {
        pos = default;
        if (turretTarget == null) return false;
        var stations = v.weaponStations;
        if (stations == null) return false;
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < stations.Count; i++)
            {
                var ws = stations[i]; if (ws == null) continue;
                bool groundCapable = ws.WeaponInfo != null && ws.WeaponInfo.effectiveness.antiSurface > 0.05f;
                if ((pass == 0) != groundCapable) continue;
                var turrets = ws.Turrets; if (turrets == null) continue;
                for (int j = 0; j < turrets.Count; j++)
                {
                    var t = turrets[j]; if (t == null) continue;
                    var tgt = turretTarget(t);
                    if (tgt != null && !tgt.disabled) { pos = tgt.GlobalPosition(); return true; }
                }
            }
        return false;
    }

    // Naturally-engaged friendly nearby (contagion activation; only natural propagates, no chaining).
    static bool HasNaturalFriendlyNearby(GroundVehicle v, GlobalPosition pos, float radius)
    {
        var hq = v.NetworkHQ;
        BattlefieldGrid.GetUnitsInRangeNonAlloc(pos, radius, gNearby);
        for (int i = 0; i < gNearby.Count; i++)
        {
            var u = gNearby[i];
            if (u == null || u == v || u.disabled || !(u is GroundVehicle) || u.NetworkHQ != hq) continue;
            if (gStates.TryGetValue(u.GetInstanceID(), out var st) && st.owned && st.natural
                && FastMath.InRange(pos, u.GlobalPosition(), radius))
                return true;
        }
        return false;
    }

    // Any combat-owned friendly nearby (looser: includes pre-contact advancers; AA anchoring).
    static bool HasOwnedFriendlyNearby(GroundVehicle v, GlobalPosition pos, float radius)
    {
        var hq = v.NetworkHQ;
        BattlefieldGrid.GetUnitsInRangeNonAlloc(pos, radius, gNearby);
        for (int i = 0; i < gNearby.Count; i++)
        {
            var u = gNearby[i];
            if (u == null || u == v || u.disabled || !(u is GroundVehicle) || u.NetworkHQ != hq) continue;
            if (gStates.TryGetValue(u.GetInstanceID(), out var st) && st.owned
                && FastMath.InRange(pos, u.GlobalPosition(), radius))
                return true;
        }
        return false;
    }

    // Best retreat heading: ring of candidates scored to break LoS first, then lower ground (dry + reachable only).
    static readonly float[] FallbackAngles = { 0f, -35f, 35f, -70f, 70f };
    static readonly float[] DetourAngles = { 30f, 60f, 90f, 120f, 150f };

    static Vector3 ChooseFallback(GlobalPosition pos, GlobalPosition epos)
    {
        Vector3 away = Flat(pos - epos);
        if (away == Vector3.zero) away = Flat(-pos.AsVector3());
        float look = GroundLook(FastMath.Distance(pos, epos));
        Vector3 best = away; float bestScore = float.NegativeInfinity;
        for (int i = 0; i < FallbackAngles.Length; i++)
        {
            Vector3 dir = Flat(Quaternion.AngleAxis(FallbackAngles[i], Vector3.up) * away);
            if (dir == Vector3.zero) continue;
            GlobalPosition g = pos + dir * look;
            if (!PathfindingAgent.RaycastTerrain(g.ToLocalPosition(), out var hit) || hit.point.y < Datum.LocalSeaY) continue;
            if (!CorridorDry(pos, g)) continue;   // don't reverse THROUGH a water body on the way (endpoint dry isn't enough)
            GlobalPosition gp = hit.point.ToGlobalPosition();
            float score = (HasLoS(gp, epos) ? 0f : 1000f) - hit.point.y; // prefer breaking LoS, then lower ground
            if (score > bestScore) { bestScore = score; best = dir; }
        }
        return best;
    }

    // Frontline spacing: position-anchored push + small destination anticipation, low-passed, forward-clamped.
    static Vector3 ApplySeparation(GState s, GlobalPosition pos, Vector3 desired, Vector3 front)
    {
        var v = s.v;
        desired = Flat(desired);
        if (desired == Vector3.zero) return desired;
        float R = GroundSpacing(s, pos);
        var hq = v.NetworkHQ;
        float look = GroundLook(R);
        Vector3 myPos = pos.ToLocalPosition(); myPos.y = 0f;
        GlobalPosition myDest = pos + desired * look;              // where I intend to go (anticipation only)
        Vector3 right = Flat(Vector3.Cross(Vector3.up, front));    // lateral (row) axis
        if (right == Vector3.zero) right = Flat(Vector3.Cross(Vector3.up, desired));
        Vector3 fwdAxis = Flat(front) != Vector3.zero ? Flat(front) : desired;
        Vector3 sep = Vector3.zero;
        BattlefieldGrid.GetUnitsInRangeNonAlloc(pos, R + look, gNearby);
        for (int i = 0; i < gNearby.Count; i++)
        {
            var u = gNearby[i];
            if (u == null || u == v || u.disabled || !(u is GroundVehicle) || u.NetworkHQ != hq) continue;
            // Core: current positions (continuous, no feedback loop).
            Vector3 fpos = u.GlobalPosition().ToLocalPosition(); fpos.y = 0f;
            Vector3 raw = myPos - fpos;
            float dist = raw.magnitude;
            Vector3 push = Vector3.zero;
            if (dist < 0.1f)
            {
                // Exactly stacked: split by ID order (lower goes +side, higher -side).
                Vector3 axis = right != Vector3.zero ? right : Flat(v.transform.forward);
                if (axis == Vector3.zero) axis = desired;
                push = axis * (v.GetInstanceID() < u.GetInstanceID() ? 1f : -1f);
            }
            else if (dist < R)
            {
                push = (raw / dist) * (1f - dist / R);
            }
            // Anticipation (small weight): lets a rear unit pass a stopped friend.
            Vector3 d = myDest - FriendlyDest(u); d.y = 0f;
            float distD = d.magnitude;
            if (distD >= 0.1f && distD < R)
                push += (d / distD) * ((1f - distD / R) * 0.35f);
            else if (distD < 0.1f)
                push += right * (v.GetInstanceID() < u.GetInstanceID() ? 0.35f : -0.35f);
            if (push == Vector3.zero) continue;
            if (right == Vector3.zero) { sep += push; continue; }
            float lat = Vector3.Dot(push, right);
            float fwd = Vector3.Dot(push, fwdAxis);                // longitudinal damped 0.6x (unstack columns, keep the line fanned)
            sep += right * lat + fwdAxis * (fwd * 0.6f);
        }
        // Fixed side lean breaks mirror symmetry; cap + low-pass kill tick-to-tick flips.
        if (right != Vector3.zero) sep += right * (s.evadeSign * 0.12f);
        // Cap the shove, low-pass it, then clamp to MaxSepDeflect off the advance (bend, never flip).
        if (sep.sqrMagnitude > 2.25f) sep = sep.normalized * 1.5f;
        s.sepSmooth = Vector3.Lerp(s.sepSmooth, sep, 0.35f);
        if (s.sepSmooth.sqrMagnitude < 0.0025f) { s.sepSmooth = Vector3.zero; return desired; }
        Vector3 combined = desired + s.sepSmooth * SepWeight;
        combined.y = 0f;
        if (combined.sqrMagnitude < 1e-6f) return desired;
        float ang = Vector3.Angle(desired, combined);
        if (ang > MaxSepDeflect)
        {
            float side = Mathf.Sign(Vector3.SignedAngle(desired, combined, Vector3.up));
            if (side == 0f) side = s.evadeSign;
            combined = Quaternion.AngleAxis(MaxSepDeflect * side, Vector3.up) * desired;
        }
        Vector3 outDir = Flat(combined);
        return outDir == Vector3.zero ? desired : outDir;
    }
    const float SepWeight = 1f;       // crowd-shove weight (radius adapts, no tuning)
    const float MaxSepDeflect = 60f;  // max bend off the advance (forward progress guaranteed)

    // Halted-line breathing room: committed 2D sidestep with hysteresis (enter 0.25, hold 0.15, 3s commit).
    static bool CrowdSidestep(GState s, GlobalPosition pos, Vector3 front, out Vector3 step)
    {
        step = Vector3.zero;
        float now = Time.timeSinceLevelLoad;
        float R = Mathf.Max(GroundSpacing(s, pos) * 0.6f, 30f);
        Vector3 right = Flat(Vector3.Cross(Vector3.up, front));
        if (right == Vector3.zero) return false;
        Vector3 fwdAxis = Flat(front);
        var hq = s.v.NetworkHQ;
        Vector3 sep = Vector3.zero;
        BattlefieldGrid.GetUnitsInRangeNonAlloc(pos, R, gNearby);
        for (int i = 0; i < gNearby.Count; i++)
        {
            var u = gNearby[i];
            if (u == null || u == s.v || u.disabled || !(u is GroundVehicle) || u.NetworkHQ != hq) continue;
            Vector3 raw = pos.ToLocalPosition() - u.GlobalPosition().ToLocalPosition(); raw.y = 0f;
            float dist = raw.magnitude;
            if (dist >= R) continue;
            Vector3 push = dist < 0.1f
                ? right * (s.v.GetInstanceID() < u.GetInstanceID() ? 1f : -1f)
                : (raw / dist) * (1f - dist / R);
            sep += right * Vector3.Dot(push, right) + fwdAxis * (Vector3.Dot(push, fwdAxis) * 0.6f);
        }
        float mag = sep.magnitude;
        bool committed = s.sidestepDir != Vector3.zero && now < s.sidestepUntil;
        float thresh = committed ? 0.15f : 0.25f;
        if (mag < thresh)
        {
            if (!committed) s.sidestepDir = Vector3.zero;
            if (mag < 0.15f) s.sidestepDir = Vector3.zero;   // crowd gone = stop (else finish the committed step)
            else if (committed) { step = Flat(s.sidestepDir); return step != Vector3.zero; }
            else return false;
            return false;
        }
        Vector3 want = Flat(sep);
        if (want == Vector3.zero) return false;
        if (committed && Vector3.Dot(s.sidestepDir, want) > 0.3f) { step = Flat(s.sidestepDir); return true; }
        s.sidestepDir = want; s.sidestepUntil = now + 3f;
        step = want;
        return true;
    }

    // Friendly's intended destination (live move order if owned, else position).
    static GlobalPosition FriendlyDest(Unit u)
    {
        if (gStates.TryGetValue(u.GetInstanceID(), out var st) && st.owned && st.hasIssued) return st.lastIssued;
        return u.GlobalPosition();
    }

    // Committed one-side detour around blocking water (previous side first; intent flips invalidate it).
    static Vector3 WaterDetour(GState s, float now, GlobalPosition pos, Vector3 dir, float look)
    {
        if (s.waterDetourDir != Vector3.zero && Vector3.Dot(s.waterDetourBase, dir) < 0.3f)
        { s.waterDetourDir = Vector3.zero; s.waterDetourUntil = 0f; }
        if (s.waterDetourDir != Vector3.zero && now < s.waterDetourUntil && CorridorDry(pos, pos + s.waterDetourDir * look))
            return s.waterDetourDir;
        if (CorridorDry(pos, pos + dir * look)) { s.waterDetourDir = Vector3.zero; return dir; }   // direct route open again
        int sign = s.waterDetourSign != 0 ? s.waterDetourSign : s.evadeSign;
        for (int i = 0; i < DetourAngles.Length; i++)
            for (int k = 0; k < 2; k++)
            {
                int side = k == 0 ? sign : -sign;
                Vector3 cand = Flat(Quaternion.AngleAxis(DetourAngles[i] * side, Vector3.up) * dir);
                if (cand == Vector3.zero || !CorridorDry(pos, pos + cand * look)) continue;
                s.waterDetourDir = cand;
                s.waterDetourBase = dir;
                s.waterDetourSign = side;
                s.waterDetourUntil = now + Mathf.Clamp(look / Mathf.Max(Mathf.Abs(s.v.speed), 5f), 4f, 15f);
                return cand;
            }
        return Vector3.zero;
    }
}

// Per-unit combat driver hook.
[HarmonyPatch(typeof(GroundVehicle), "Update")]
public static class GroundVehicle_Update_Patch
{
    static void Postfix(GroundVehicle __instance)
    {
        try { ImprovedAIPlugin.GroundUpdate(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("ground update error: " + ex); }
    }
}

// Reverse / halt-facing / obstacle-refresh actuation hook (main-thread prepare before the physics job).
[HarmonyPatch(typeof(GroundVehicle), "UpdateJobFields")]
public static class GroundVehicle_UpdateJobFields_Patch
{
    static void Postfix(GroundVehicle __instance)
    {
        try { ImprovedAIPlugin.GroundApplyReverse(__instance); ImprovedAIPlugin.GroundApplyHaltFacing(__instance); ImprovedAIPlugin.GroundRefreshObstacles(__instance); ImprovedAIPlugin.GroundApplyDownforce(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("ground reverse error: " + ex); }
    }
}

// Obstacle-pruning hook (drops non-collision-course traffic so units stop swerving at everything).
[HarmonyPatch(typeof(GroundVehicle), "UpdateObstacles")]
public static class GroundVehicle_UpdateObstacles_Patch
{
    static void Postfix(GroundVehicle __instance)
    {
        try { ImprovedAIPlugin.GroundPruneObstacles(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("ground prune-obstacles error: " + ex); }
    }
}
