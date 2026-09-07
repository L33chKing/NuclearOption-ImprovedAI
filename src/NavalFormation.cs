// NavalFormation: nearby free warships auto-form a task force (cost-based leader, escort/screen slots). Host-side.

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

public partial class ImprovedAIPlugin
{
    const float FormRadius = 10000f;        // 10 km — join/cluster radius (user spec)
    const float FormElectionInterval = 2f;  // leader/membership election cadence (slots are live per-frame)
    const float FormOrderGrace = 5f;        // after a player order, stay detached at least this long (let it get moving)
    const float FormPlayerGroupWindow = 1.5f;   // player orders within this time + FormPlayerGroupPos = one multi-select group
    const float FormPlayerGroupPos = 2500f;
    // Screen = concentric arcs facing the enemy; bigger screen ships take the inner arcs.
    const float ScreenArcHalfDeg = 35.0f;       // half of the 45deg arc
    const float ScreenRingSpacing = 800f;        // each outer arc is this much further toward the enemy
    const int   ScreenPerRing = 5;              // screen ships per arc before a new (outer) arc is added
    // Weave screening: the slot carrot stays ahead so ships burn excess speed sideways instead of stopping.
    const float ScreenWeaveMaxDeg = 150f;       // max weave angle off the advance axis; >90 lets an OVERRUN ship loop back (loiter when the formation is slow/stopped)
    const float ScreenWeaveGain = 0.08f;        // longitudinal drift -> forward-speed trim (1/s); pulls the ship back on-station
    const float ScreenWeaveLead = 6f;           // carrot distance floor, in ship maxRadius (keeps the destination well ahead -> full throttle)
    const float ScreenCorridorFrac = 0.20f;     // lateral half-width of the weave, as a fraction of the arc slot spacing
    const float EscortCorridorFrac = 0.5f;      // lateral half-width of the escort/carrier weave, as a fraction of the slot gap (modest -> holds formation shape)
    // Leader evasion: jink the rudder while under gun fire; members follow a smoothed centre (undisturbed).
    const float LeaderWeaveAmp = 0.3f;          // steering jink amplitude added on top of ShipAI's own rudder ([-1,1])
    const float LeaderWeavePeriod = 5f;         // weave period (s) — one full port-starboard cycle
    const float LeaderWeaveDetectRange = 6000f; // scan radius for an enemy turret locked onto the leader
    const float LeaderFireHold = 3f;            // keep weaving this long after the last detected lock (anti-stutter)
    const float LeaderFireCheck = 0.5f;         // how often (s) to re-scan whether the leader is under gun fire
    const float LeaderCenterSmoothFire = 0.004f;// navCenter low-pass while weaving (heavy -> filters the sway out of the formation)
    const float LeaderCenterSmoothIdle = 0.15f; // navCenter low-pass otherwise (fast -> centre tracks the leader, ~no lag)

    // Leadership is cost-based and roles read shipType, so modded ships work unnamed (LC excluded).
    static bool IsExcluded(ShipType? t) => t == ShipType.LC;
    static bool IsEligible(Ship s) => !IsExcluded(TypeOf(s));
    static bool IsDestroyer(ShipType? t) => t == ShipType.DDG;
    static bool IsCarrier(ShipType? t) => t == ShipType.CV || t == ShipType.LHA;
    static bool IsScreen(ShipType? t) => t == ShipType.FFL || t == ShipType.PB;   // corvettes + gunboats only

    static AccessTools.FieldRef<ShipAI, Ship> saiShip;
    static AccessTools.FieldRef<ShipAI, bool> saiCommanded;
    static AccessTools.FieldRef<ShipAI, GlobalPosition> saiDestination;
    static AccessTools.FieldRef<ShipAI, float> saiLastSteer;   // ShipAI.lastSteeringUpdate — tells us the frame Steer actually refreshed inputs.steering (so the leader jink never accumulates)

    // role: 1 carrier (rear), 2 screen (corvette/gunboat picket), 4 escort (shared front-preferred ring); index = ordinal within role
    class NavalMember { public Ship leader; public int role; public int index; }
    static readonly Dictionary<Ship, NavalMember> navByShip = new Dictionary<Ship, NavalMember>();
    static readonly Dictionary<Ship, int> navLeaderMembers = new Dictionary<Ship, int>();
    static readonly Dictionary<Ship, Vector3> navHeading = new Dictionary<Ship, Vector3>();     // per-leader smoothed heading
    static readonly Dictionary<Ship, Vector3> navScreenDir = new Dictionary<Ship, Vector3>();   // per-leader dir to nearest enemy
    static readonly Dictionary<Ship, int> navScreenWeave = new Dictionary<Ship, int>();         // per-screen-ship current weave side (+1/-1)
    static readonly Dictionary<Ship, float> navLastDriven = new Dictionary<Ship, float>();  // ship -> last time WE drove it (distinguish mission-hold from a member's transient hold)
    static readonly Dictionary<Ship, float> navPlayerCmdTime = new Dictionary<Ship, float>();
    static readonly Dictionary<Ship, GlobalPosition> navPlayerCmdPos = new Dictionary<Ship, GlobalPosition>();
    static readonly HashSet<Ship> knownShips = new HashSet<Ship>();
    static readonly HashSet<Ship> navShipAI = new HashSet<Ship>();   // ships whose ShipAI.Update actually fired = drivable. A leader/member MUST be here (a static/modded airbase-carrier with no ShipAI never enters, so it can't anchor & brick the fleet).
    static readonly Dictionary<Ship, GlobalPosition> navCenter = new Dictionary<Ship, GlobalPosition>();   // per-leader smoothed formation centre (filters the leader's evasive jink so members aren't disturbed)
    static readonly Dictionary<Ship, float> navLeaderFireUntil = new Dictionary<Ship, float>();  // leader -> time until which it's treated as under gun fire (weaving)
    static readonly Dictionary<Ship, float> navLeaderFireChecked = new Dictionary<Ship, float>();// leader -> last under-fire scan time (throttle)
    static readonly List<Unit> navScan = new List<Unit>();
    static readonly List<Unit> navFireScan = new List<Unit>();   // separate buffer for the leader under-fire scan (navScan is used by the election)
    static float navLastElection, navDiagLast;

    void NavalConfig()
    {
        try
        {
            saiShip = AccessTools.FieldRefAccess<ShipAI, Ship>("ship");
            saiCommanded = AccessTools.FieldRefAccess<ShipAI, bool>("commandedDestination");
            saiDestination = AccessTools.FieldRefAccess<ShipAI, GlobalPosition>("destination");
            try { saiLastSteer = AccessTools.FieldRefAccess<ShipAI, float>("lastSteeringUpdate"); } catch { saiLastSteer = null; }   // optional (leader evasion off if missing)
        }
        catch (Exception ex) { saiShip = null; Logger.LogWarning("[Naval] ShipAI internals unavailable (auto formation off): " + ex); }
    }

    static ShipType? TypeOf(Ship s) => (s.definition as ShipDefinition)?.shipType;   // null = not a ShipDefinition / unknown
    static string TypeName(Ship s) => TypeOf(s)?.ToString() ?? "unknown";
    static float CostOf(Ship s) => s.definition != null ? s.definition.value : 0f;

    // Mission-hold ships stay out (a member's transient arrival-hold doesn't count).
    static bool IsHeld(Ship s) => s != null && s.holdPosition && !(navLastDriven.TryGetValue(s, out var t) && Time.timeSinceLevelLoad - t < 3f);

    // Per-frame driver (postfix on ShipAI.Update).
    internal static void NavalUpdate(ShipAI ai)
    {
        if (!MasterOn || saiShip == null || ai == null) return;
        Ship ship; try { ship = saiShip(ai); } catch { return; }
        if (ship == null || ship.disabled || ship.NetworkHQ == null) return;
        if (!IsEligible(ship)) return;

        knownShips.Add(ship);
        navShipAI.Add(ship);   // this ship's ShipAI.Update fired -> it is a real, drivable ShipAI ship (leader/member eligible)

        float now = Time.timeSinceLevelLoad;
        if (now - navLastElection > FormElectionInterval) { navLastElection = now; try { RunElection(now); } catch (Exception ex) { Log?.LogWarning("[Naval] election error: " + ex); } }

        if (IsHeld(ship)) return;   // respect a mission "hold position" order — never drive it into the formation
        if (!navByShip.TryGetValue(ship, out var m) || m == null) return;
        try
        {
            if (m.leader == null) { UpdateLeaderHeading(ship, ai); DetectLeaderFire(ship, now); UpdateLeaderCenter(ship, now); PaceLeader(ship); }   // this ship is a leader
            else if (m.leader != null && !m.leader.disabled) DriveMember(ai, ship, ComputeSlot(m.leader, m, ship, allowFlip: true));
        }
        catch (Exception ex) { Log?.LogWarning("[Naval] drive error: " + ex); }
    }

    // Leader/membership election (throttled; slots themselves are live per-frame).
    static void RunElection(float now)
    {
        knownShips.RemoveWhere(s => s == null || s.disabled);
        navShipAI.RemoveWhere(s => s == null || s.disabled);
        navByShip.Clear(); navLeaderMembers.Clear();

        // Include every host-simulated ship near a known one (a ship's ShipAI.Update may not have fired if it's idle),
        // so high-cost carriers are always leader candidates.
        var seeds = new List<Ship>(knownShips);
        bool diag = cfgDiag != null && cfgDiag.Value && now - navDiagLast > 5f;
        var diagSeen = diag ? new HashSet<Ship>() : null;
        foreach (var s in seeds)
        {
            if (s == null || s.disabled) continue;
            navScan.Clear();
            BattlefieldGrid.GetUnitsInRangeNonAlloc(s.GlobalPosition(), FormRadius, navScan);
            foreach (var u in navScan)
                if (u is Ship sh && !sh.disabled)
                {
                    try { if (sh.IsServer) knownShips.Add(sh); } catch { knownShips.Add(sh); }
                    if (diag && diagSeen.Add(sh)) Log?.LogInfo($"[Naval] near {sh.name} type={TypeName(sh)} cost={CostOf(sh):0.0} eligible={IsEligible(sh)} hqSame={(sh.NetworkHQ == s.NetworkHQ)}");
                }
        }
        if (diag) navDiagLast = now;

        var byFaction = new Dictionary<FactionHQ, List<Ship>>();
        foreach (var s in knownShips)
        {
            if (s == null || s.disabled || s.NetworkHQ == null || !IsEligible(s) || IsHeld(s)) continue;   // skip mission-hold ships
            if (!byFaction.TryGetValue(s.NetworkHQ, out var list)) { list = new List<Ship>(); byFaction[s.NetworkHQ] = list; }
            list.Add(s);
        }

        foreach (var kv in byFaction)
        {
            var ships = kv.Value;
            var parent = new Dictionary<Ship, Ship>();
            foreach (var s in ships) parent[s] = s;
            Ship Find(Ship a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
            for (int i = 0; i < ships.Count; i++)
                for (int j = i + 1; j < ships.Count; j++)
                    if (FastMath.Distance(ships[i].GlobalPosition(), ships[j].GlobalPosition()) <= FormRadius)
                        parent[Find(ships[i])] = Find(ships[j]);
            var clusters = new Dictionary<Ship, List<Ship>>();
            foreach (var s in ships) { var r = Find(s); if (!clusters.TryGetValue(r, out var c)) { c = new List<Ship>(); clusters[r] = c; } c.Add(s); }
            foreach (var cl in clusters.Values) BuildFormation(cl, now, diag);
        }
    }

    static void BuildFormation(List<Ship> cluster, float now, bool diag)
    {
        if (cluster.Count == 0) return;

    // Leader = highest-cost ShipAI-driven ship (static/modded ships without ShipAI can't anchor a fleet).
        Ship leader = null;
        foreach (var s in cluster) { if (!navShipAI.Contains(s)) continue; if (leader == null || Outranks(s, leader)) leader = s; }
        if (leader == null)
        {
            if (diag) Log?.LogInfo($"[Naval] cluster n={cluster.Count} skipped — no ShipAI-driven ship (static/modded only?): " + DescribeCluster(cluster));
            return;
        }

        var members = new List<Ship>();
        foreach (var s in cluster)
        {
            if (s == leader) continue;
            if (!navShipAI.Contains(s)) continue;   // not drivable by us — leave to vanilla
            if (IsIndependentlyCommanded(s, leader, now)) continue;
            members.Add(s);
        }
        navByShip[leader] = new NavalMember { leader = null };
        navLeaderMembers[leader] = members.Count;
        if (members.Count == 0) return;   // solo drivable ship (everything else static/detached) — nothing to form around it

        // nearest enemy surface direction for screening (recomputed each election; changes slowly)
        if (TryGetNearestEnemyShipDir(leader, out var ed)) navScreenDir[leader] = ed; else navScreenDir.Remove(leader);

        // Split: carriers -> rear, corvettes/gunboats -> screen, everything else -> a SHARED front-preferred escort ring.
        var carriers = new List<Ship>(); var screens = new List<Ship>(); var escorts = new List<Ship>();
        foreach (var s in members)
        {
            ShipType? t = TypeOf(s);
            if (IsScreen(t)) screens.Add(s);
            else if (IsCarrier(t)) carriers.Add(s);
            else escorts.Add(s);
        }
        // Escort slots fill front-first: destroyers get first pick, then by cost — so with NO destroyers the frigates
        // take the front, and EXTRA destroyers spill onto the flanks/rear. (role 4 = escort ring, 1 = carrier rear, 2 = screen)
        escorts.Sort((a, b) => { bool da = IsDestroyer(TypeOf(a)), db = IsDestroyer(TypeOf(b)); if (da != db) return db.CompareTo(da); return CostOf(b).CompareTo(CostOf(a)); });
        carriers.Sort((a, b) => CostOf(b).CompareTo(CostOf(a)));
        // bigger screen ships first -> inner arcs; small gunboats end up on the outer arcs
        screens.Sort((a, b) => { int c = b.maxRadius.CompareTo(a.maxRadius); return c != 0 ? c : CostOf(b).CompareTo(CostOf(a)); });
        for (int i = 0; i < escorts.Count; i++) navByShip[escorts[i]] = new NavalMember { leader = leader, role = 4, index = i };
        for (int i = 0; i < carriers.Count; i++) navByShip[carriers[i]] = new NavalMember { leader = leader, role = 1, index = i };
        for (int i = 0; i < screens.Count; i++) navByShip[screens[i]] = new NavalMember { leader = leader, role = 2, index = i };

        if (diag)
            Log?.LogInfo($"[Naval] cluster n={cluster.Count} leader={leader.name} {TypeOf(leader)} cost={CostOf(leader):0.0} members={members.Count} | cand: " + DescribeCluster(cluster));
    }

    // Cluster diagnostic dump (name/type/cost/ShipAI presence per ship).
    static string DescribeCluster(List<Ship> cluster)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in cluster) sb.Append($" {s.name}({TypeOf(s)},{CostOf(s):0.0},ai={(navShipAI.Contains(s) ? "T" : "F")})");
        return sb.ToString();
    }

    static bool Outranks(Ship a, Ship b)
    {
        float ca = CostOf(a), cb = CostOf(b);
        if (Mathf.Abs(ca - cb) > 0.001f) return ca > cb;
        int ma = navLeaderMembers.TryGetValue(a, out var x) ? x : -1;
        int mb = navLeaderMembers.TryGetValue(b, out var y) ? y : -1;
        if (ma != mb) return ma > mb;
        return a.GetInstanceID() < b.GetInstanceID();
    }

    // Orient the formation by the leader's destination (pre-rotates the formation into the turn).
    static void UpdateLeaderHeading(Ship leader, ShipAI ai)
    {
        Vector3 fwd = Vector3.zero;
        try
        {
            Vector3 toDest = saiDestination(ai) - leader.GlobalPosition(); toDest.y = 0f;
            float minGo = leader.maxRadius * 2f;
            if (toDest.sqrMagnitude > minGo * minGo) fwd = toDest;   // leader has a real destination to steer for
        }
        catch { }
        if (fwd.sqrMagnitude < 0.01f) fwd = leader.rb != null && leader.speed > 3f ? leader.rb.velocity : leader.transform.forward;
        fwd.y = 0f; if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward; fwd.Normalize();
        Vector3 prev = navHeading.TryGetValue(leader, out var ph) ? ph : fwd;
        navHeading[leader] = Vector3.Slerp(prev, fwd, 0.1f).normalized;   // smooth (anti-jitter)
    }

    // Driven slot = on-station point + weave (excess speed goes sideways, never into braking).
    static GlobalPosition ComputeSlot(Ship leader, NavalMember m, Ship ship, bool allowFlip)
    {
        GlobalPosition station = ComputeStation(leader, m, ship, out float amp);
        return WeaveCarrot(leader, ship, station, amp, allowFlip);
    }

    // On-station point per role (screen arc / carrier rear fan / escort ring). Outputs the weave corridor half-width.
    static GlobalPosition ComputeStation(Ship leader, NavalMember m, Ship ship, out float amp)
    {
        if (m.role == 2) return ScreenArcStation(leader, ship, m.index, out amp);

        Vector3 heading = navHeading.TryGetValue(leader, out var h) ? h : LeaderFwd(leader);
        Vector3 right = Vector3.Cross(Vector3.up, heading).normalized;
        float baseGap = Mathf.Max(leader.maxRadius * 2.5f, 250f);
        Vector3 off = m.role == 1
            ? SlotFan(heading, right, -1, m.index, baseGap * 1.2f)   // non-leader carrier: rear
            : EscortRingSlot(heading, right, m.index, baseGap);      // escort: front-preferred ring (fills front first)
        amp = Mathf.Max(baseGap * EscortCorridorFrac, ship.maxRadius * 3f);   // modest corridor: escort holds its slot while weaving
        return LeaderCenter(leader) + off;
    }

    // Smoothed formation centre (filters the leader's evasive sway out of member slots).
    static GlobalPosition LeaderCenter(Ship leader) => navCenter.TryGetValue(leader, out var c) ? c : leader.GlobalPosition();

    static void UpdateLeaderCenter(Ship leader, float now)
    {
        GlobalPosition lp = leader.GlobalPosition();
        bool weaving = navLeaderFireUntil.TryGetValue(leader, out var until) && now < until;
        float k = weaving ? LeaderCenterSmoothFire : LeaderCenterSmoothIdle;
        GlobalPosition prev = navCenter.TryGetValue(leader, out var c) ? c : lp;
        navCenter[leader] = prev + (lp - prev) * k;
    }

    // Throttled scan: is an enemy turret aiming at the leader? Holds the weave briefly against flicker.
    static void DetectLeaderFire(Ship leader, float now)
    {
        if (saiLastSteer == null || turretTarget == null) return;   // leader evasion unavailable
        if (navLeaderFireChecked.TryGetValue(leader, out var last) && now - last < LeaderFireCheck) return;
        navLeaderFireChecked[leader] = now;
        navFireScan.Clear();
        BattlefieldGrid.GetUnitsInRangeNonAlloc(leader.GlobalPosition(), LeaderWeaveDetectRange, navFireScan);
        var hq = leader.NetworkHQ;
        foreach (var u in navFireScan)
        {
            if (u == null || u.disabled || u.NetworkHQ == null || u.NetworkHQ == hq) continue;
            if (EnemyTargetsMe(u, leader)) { navLeaderFireUntil[leader] = now + LeaderFireHold; return; }
        }
    }

    // Leader evasion: oscillating rudder jink on top of ShipAI steering, only while under fire (nav untouched).
    internal static void NavalLeaderSteerWeave(ShipAI ai)
    {
        if (!MasterOn || saiShip == null || saiLastSteer == null || ai == null) return;
        Ship ship; try { ship = saiShip(ai); } catch { return; }
        if (ship == null || ship.disabled) return;
        if (!navByShip.TryGetValue(ship, out var m) || m == null || m.leader != null) return;   // formation leaders only
        if (!(navLeaderMembers.TryGetValue(ship, out var mc) && mc > 0)) return;                 // that actually lead a formation
        float now = Time.timeSinceLevelLoad;
        if (!(navLeaderFireUntil.TryGetValue(ship, out var until) && now < until)) return;        // only while under gun fire
        if (Mathf.Abs(saiLastSteer(ai) - now) > 1e-3f) return;   // only the frame Steer refreshed steering -> no accumulation
        var inp = ship.GetInputs(); if (inp == null) return;
        float jink = LeaderWeaveAmp * Mathf.Sin(2f * Mathf.PI * now / LeaderWeavePeriod);
        inp.steering = Mathf.Clamp(inp.steering + jink, -1f, 1f);
    }

    static Vector3 LeaderFwd(Ship leader) { Vector3 f = leader.transform.forward; f.y = 0f; return f.sqrMagnitude < 0.01f ? Vector3.forward : f.normalized; }

    static Vector3 SlotFan(Vector3 heading, Vector3 right, int dir, int index, float gap)
    {
        int row = index / 3 + 1;
        int col = index % 3;
        float lateral = (col == 0 ? 0f : (col == 1 ? 1f : -1f)) * gap;
        float along = dir * gap * (1f + row * 0.6f);
        return heading * along + right * lateral;
    }

    // Shared escort slots in preference order (front first). Destroyers pick first, then by cost.
    static readonly Vector2[] EscortRing =
    {
        new Vector2(0f, 1.6f),      // front centre
        new Vector2(1.0f, 1.4f),    // front right
        new Vector2(-1.0f, 1.4f),   // front left
        new Vector2(1.4f, 0.4f),    // starboard beam
        new Vector2(-1.4f, 0.4f),   // port beam
        new Vector2(2.0f, 1.8f),    // wide front right
        new Vector2(-2.0f, 1.8f),   // wide front left
        new Vector2(1.6f, -0.6f),   // quarter right (rear)
        new Vector2(-1.6f, -0.6f),  // quarter left (rear)
    };

    static Vector3 EscortRingSlot(Vector3 heading, Vector3 right, int index, float gap)
    {
        int n = EscortRing.Length;
        int tier = index / n, i = index % n;
        Vector2 b = EscortRing[i];
        float rr = b.x * (1f + tier * 0.6f);   // outer tiers widen
        float ff = b.y - tier * 0.8f;          // and sit further back
        return heading * (ff * gap) + right * (rr * gap);
    }

    // Screening arcs: concentric arcs facing the enemy, inner arcs first, odd arcs staggered.
    static GlobalPosition ScreenArcStation(Ship leader, Ship ship, int index, out float amp)
    {
        Vector3 a = navHeading.TryGetValue(leader, out var h) ? h : LeaderFwd(leader);
        a.y = 0f; if (a.sqrMagnitude < 0.01f) a = Vector3.forward; a.Normalize();
        Vector3 screenDir = navScreenDir.TryGetValue(leader, out var sd) ? sd : a;
        screenDir.y = 0f; if (screenDir.sqrMagnitude < 0.01f) screenDir = a; screenDir.Normalize();

        float lr = leader.maxRadius;
        float baseR = Mathf.Max(lr * 10f, 2000f);
        int ring = index / ScreenPerRing, slot = index % ScreenPerRing;
        float radius = baseR + ring * ScreenRingSpacing;
        float step = ScreenPerRing > 1 ? (2f * ScreenArcHalfDeg / (ScreenPerRing - 1)) : 0f;   // angular gap between slots
        float angle = -ScreenArcHalfDeg + slot * step + ((ring & 1) == 1 ? step * 0.5f : 0f);   // stagger odd arcs

        Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * screenDir;       // this ship's radial (within the arc)
        // Corridor: half-width the ship is allowed to weave, kept below the slot spacing so neighbours never overlap.
        float arcSpacing = Mathf.Max(step * Mathf.Deg2Rad * radius, ship.maxRadius * 4f);
        amp = Mathf.Max(arcSpacing * ScreenCorridorFrac, ship.maxRadius * 3f);
        return LeaderCenter(leader) + dir * radius;                              // the ship's on-station point in the arc
    }

    // Weave carrot around a moving station: forward progress matches leader pace, excess goes sideways.
    static GlobalPosition WeaveCarrot(Ship leader, Ship ship, GlobalPosition station, float amp, bool allowFlip)
    {
        Vector3 a = navHeading.TryGetValue(leader, out var h) ? h : LeaderFwd(leader);
        a.y = 0f; if (a.sqrMagnitude < 0.01f) a = Vector3.forward; a.Normalize();
        Vector3 t = Vector3.Cross(Vector3.up, a).normalized;

        // Where the ship sits relative to its station, in the advance frame (a = along advance, t = lateral).
        Vector3 rel = ship.GlobalPosition() - station; rel.y = 0f;
        float sAlong = Vector3.Dot(rel, a);
        float sLat = Vector3.Dot(rel, t);

        // Bounce inside the +/-amp corridor (hysteresis: keep weaving the same way until we reach an edge).
        int sign = navScreenWeave.TryGetValue(ship, out var sg) && sg != 0 ? sg : 1;
        if (sLat > amp) sign = -1; else if (sLat < -amp) sign = 1;
        if (allowFlip) navScreenWeave[ship] = sign;

        // Weave angle so FORWARD progress matches the (moving) station: forwardSpeed = vShip*cos(t) = leader speed, trimmed
        // by longitudinal drift to self-centre. Excess speed goes sideways -> fast ship snakes hard, slow one runs straight.
        float vForm = leader.speed;
        float vShip = Mathf.Max(ship.speed, 1f);
        float desiredFwd = vForm - ScreenWeaveGain * sAlong;                    // ahead of station -> aim slower/behind; behind -> faster
        float minCos = Mathf.Cos(ScreenWeaveMaxDeg * Mathf.Deg2Rad);
        float cosT = Mathf.Clamp(desiredFwd / vShip, minCos, 1f);
        float sinT = Mathf.Sqrt(Mathf.Max(0f, 1f - cosT * cosT));
        Vector3 travel = (a * cosT + t * (sign * sinT)).normalized;            // desired heading

        float lead = Mathf.Max(amp * 2f, ship.maxRadius * ScreenWeaveLead, 400f);
        return station + travel * lead;                                        // carrot ahead of the ship -> throttle stays up
    }

    // Drive a member to its live slot via vanilla ShipAI (deadbanded re-issue).
    static void DriveMember(ShipAI ai, Ship ship, GlobalPosition slot)
    {
        navLastDriven[ship] = Time.timeSinceLevelLoad;   // mark as an active member (so its transient hold isn't read as a mission-hold)
        try { saiCommanded(ai) = true; } catch { }     // keep ChooseTarget suppressed even when parked at the slot
        ship.holdPosition = false;

        GlobalPosition cur; try { cur = saiDestination(ai); } catch { cur = ship.GlobalPosition(); }
        float deadband = Mathf.Max(ship.maxRadius * 2f, 200f);
        if (FastMath.Distance(cur, slot) > deadband && ship.UnitCommand != null)
            ship.UnitCommand.SetDestination(slot, playerCommand: false);
    }

    // Leader throttle pacing: slow toward the slowest lagging member, never freeze.
    static void PaceLeader(Ship leader)
    {
        float maxLag = 0f;
        foreach (var kv in navByShip)
        {
            var m = kv.Value; if (m == null || m.leader != leader || m.role == 2) continue;   // screen ships patrol; don't pace on them
            float lag = FastMath.Distance(kv.Key.GlobalPosition(), ComputeStation(leader, m, kv.Key, out _));   // lag vs STATION, not the weaving carrot
            if (lag > maxLag) maxLag = lag;
        }
        float slack = Mathf.Max(leader.maxRadius * 6f, 1200f);
        if (maxLag <= slack) return;
        var inp = leader.GetInputs(); if (inp == null) return;
        float f = Mathf.Lerp(1f, 0.4f, Mathf.Clamp01((maxLag - slack) / (slack * 4f)));   // floor 0.4 -> no deadlock
        if (inp.throttle > f) inp.throttle = f;
    }

    static bool TryGetNearestEnemyShipDir(Ship from, out Vector3 dir)
    {
        dir = Vector3.forward;
        var hq = from.NetworkHQ; if (hq == null) return false;
        float best = float.MaxValue; GlobalPosition bestPos = default; bool found = false;
        foreach (var kv in hq.trackingDatabase)
        {
            if (!kv.Value.TryGetUnit(out var u) || u.disabled || u.NetworkHQ == null || u.NetworkHQ == hq) continue;
            if (u.radarAlt > 10f || u.speed > 100f) continue;
            float d = FastMath.Distance(from.GlobalPosition(), kv.Value.GetPosition());
            if (d < best) { best = d; bestPos = kv.Value.GetPosition(); found = true; }
        }
        if (!found) return false;
        Vector3 v = bestPos - from.GlobalPosition(); v.y = 0f;
        if (v.sqrMagnitude < 1f) return false;
        dir = v.normalized; return true;
    }

    // A player-ordered member stays detached until stopped (multi-select groups with the leader still join).
    static bool IsIndependentlyCommanded(Ship s, Ship leader, float now)
    {
        if (!navPlayerCmdTime.TryGetValue(s, out var t)) return false;   // never player-ordered
        if (navPlayerCmdTime.TryGetValue(leader, out var lt) && Mathf.Abs(t - lt) < FormPlayerGroupWindow
            && navPlayerCmdPos.TryGetValue(s, out var sp) && navPlayerCmdPos.TryGetValue(leader, out var lp)
            && FastMath.Distance(sp, lp) < FormPlayerGroupPos)
            return false;   // same order as the leader -> part of the group
        if (now - t < FormOrderGrace) return true;   // grace: let it start moving
        if (s.speed > 2f) return true;               // still travelling to its order
        navPlayerCmdTime.Remove(s);                  // stopped -> order done, rejoin
        return false;
    }

    // Formation status for the spectate overlay.
    internal static string NavalStatus(Ship s)
    {
        if (s == null || !navByShip.TryGetValue(s, out var m) || m == null) return "";
        if (m.leader == null)
        {
            int n = navLeaderMembers.TryGetValue(s, out var c) ? c : 0;
            if (n <= 0) return "";
            bool weaving = navLeaderFireUntil.TryGetValue(s, out var until) && Time.timeSinceLevelLoad < until;
            return weaving ? $"Formation Leader ({n}) — Evading" : $"Formation Leader ({n})";
        }
        if (m.leader.disabled) return "";
        if (m.role == 2) return "Screening";   // always patrolling, never "in position"
        if (FastMath.Distance(s.GlobalPosition(), ComputeStation(m.leader, m, s, out _)) > Mathf.Max(s.maxRadius * 4f, 800f))
            return "Moving to formation";
        return "Escorting";
    }

    // Called on player move orders to a ship (detach / re-elect immediately).
    internal static void NavalOnPlayerCommand(Ship ship, GlobalPosition pos)
    {
        if (ship == null) return;
        navPlayerCmdTime[ship] = Time.timeSinceLevelLoad;
        navPlayerCmdPos[ship] = pos;
        navLastElection = 0f;   // force an immediate re-election so the ship detaches at once (don't fight the order)
    }
}

// Per-frame formation driver (ShipAI is server-only, so host only).
[HarmonyPatch(typeof(ShipAI), "Update")]
public static class ShipAI_Update_NavalPatch
{
    static void Postfix(ShipAI __instance)
    {
        try { ImprovedAIPlugin.NavalUpdate(__instance); }
        catch { }
    }
}

// Leader evasion hook (jinks rudder post-Steer, navigation untouched).
[HarmonyPatch(typeof(ShipAI), "Steer")]
public static class ShipAI_Steer_NavalLeaderWeavePatch
{
    static void Postfix(ShipAI __instance)
    {
        try { ImprovedAIPlugin.NavalLeaderSteerWeave(__instance); }
        catch { }
    }
}

// Player-order hook (player orders detach a member; mission/own orders ignored).
[HarmonyPatch(typeof(UnitCommand), "ServerSetDestination")]
public static class UnitCommand_ServerSetDestination_NavalPatch
{
    static void Postfix(UnitCommand __instance, GlobalPosition waypoint, object player)
    {
        try
        {
            if (player == null) return;
            var ship = __instance.GetComponent<Ship>();
            if (ship != null) ImprovedAIPlugin.NavalOnPlayerCommand(ship, waypoint);
        }
        catch { }
    }
}
