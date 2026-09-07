// GroundPathfinding: off-road collider-box A* building avoidance + road cornering (steer layer, all ground units).

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

public partial class ImprovedAIPlugin
{
    // Collider-box A* planner: real collider boxes (oriented) -> visibility-graph A* -> pure-pursuit target.
    // U-traps commit to an extended-horizon detour until the corridor opens.
    const float NavLookSpeed = 2.5f;      // extra lookahead metres per m/s of speed
    const float NavLookBase = 22f;        // base lookahead (m)
    const float NavLookMin = 45f;         // never plan shorter than this (need room to steer around)
    const float NavLookMax = 90f;         // never plan farther than this (avoid committing to distant detours)
    const float NavClearance = 1f;        // small gap kept clear of a box beyond the unit's own radius
    const int   NavMaxBoxes = 16;         // cap boxes per plan (nearest-edge first) -> bounded cost
    const float NavMaxBoxSize = 250f;     // skip mesh/AABB-fallback colliders bigger than this on a side (terrain / combined mega-mesh).
                                        // Deliberately roomy: terminal warehouses/halls up to 250m are single buildings and must be avoided (they
                                        // used to draw BLUE/skipped at 100m and units drove straight through them). True terrain is km-scale, so it
                                        // still never passes.
    const float NavMaxTrueBoxSize = 400f; // BoxColliders may be larger (hangars/terminals); blocker volumes live on ExclusionZones, not Statics
    const float NavFlatHeight = 2f;       // colliders flatter than this are GROUND (runway/apron/deck), not walls — drivable, never an obstacle.
                                        // Applied to map-statics hits only, never to listed obstacles (a flattened wreck is still a wreck).
    const float NavMinBoxSize = 2f;       // skip colliders smaller than this on both sides (debris)
    const float NavBlankRange = 55f;      // hide LISTED large obstacles from vanilla within this range (A* owns them)
    const float NavLargeRadius = 8f;      // listed obstacles >= this = "large" (A* owns; vanilla blanked). Smaller = vanilla spacing push.
    const float NavMaxEdge = 240f;        // longest graph edge considered

    // scratch (single-threaded, main-thread only)
    struct NavBox { public Vector3 c; public Vector2 ax, az; public float hx, hz; }   // inflated ORIENTED box: centre, unit XZ axes, half-extents
    static readonly List<NavBox> navBoxes = new List<NavBox>();
    static readonly List<Vector3> navNode = new List<Vector3>();     // graph nodes (0 = start, 1 = goal, rest = box corners)
    static readonly List<int> navPath = new List<int>();
    static readonly Collider[] navOverlap = new Collider[512];       // OverlapBoxNonAlloc buffer (dense city blocks easily exceed 128)

    // U-trap memory per unit (committed detour until the corridor opens or times out).
    struct NavCommit { public float until; }
    static readonly Dictionary<int, NavCommit> navCommit = new Dictionary<int, NavCommit>();

    // Route toward a receding horizon along `dir`; returns the pure-pursuit target. False only in open terrain.
    static bool GroundPlanPath(GroundVehicle v, GlobalPosition pos, Vector3 dir, out GlobalPosition target)
    {
        target = pos;
        if (v == null) return false;
        Vector3 self = v.transform.position;
        float inflate = v.maxRadius + NavClearance;
        float look = Mathf.Clamp(v.maxRadius + Mathf.Abs(v.speed) * NavLookSpeed + NavLookBase, NavLookMin, NavLookMax);
        GatherBoxes(v, self, look, inflate);
        if (navBoxes.Count == 0) return false;                          // open terrain

        // Already INSIDE a box (clipped into a building / scraping a wall): no segment out of it can be clear, so the
        // planner below has nothing to work with — steer straight out through the nearest face instead (escape).
        if (TryBoxEscape(self, out Vector3 escape)) { target = escape.ToGlobalPosition(); return true; }

        Vector3 gdir = Flat(dir); if (gdir == Vector3.zero) return false;
        Vector3 goal = self + gdir * look; goal.y = self.y;
        goal = PushOutOfBoxes(goal);

        int id = v.GetInstanceID();
        float now = Time.timeSinceLevelLoad;
        navCommit.TryGetValue(id, out var cm);

        if (now < cm.until)   // committed to a detour: leave ONLY when the corridor to the goal has opened
        {
            if (SegClear(self, goal)) { cm.until = 0f; navCommit[id] = cm; }
            else { target = DetourTarget(v, self, gdir, look, inflate).ToGlobalPosition(); return true; }
        }

        if (SegClear(self, goal)) { target = goal.ToGlobalPosition(); return true; }   // straight corridor open

        BuildNodes(self, goal);
        if (AStar(out var path) && path.Count >= 2 && path[path.Count - 1] == 1)   // only a path that REACHES the goal
        {
            Vector3 t = navNode[path[1]];
            for (int i = path.Count - 1; i >= 1; i--) { if (SegClear(self, navNode[path[i]])) { t = navNode[path[i]]; break; } }
            target = t.ToGlobalPosition();
            return true;
        }

        // Goal unreachable in-horizon (concave pocket): commit to an extended detour so the opening enters the graph.
        cm.until = now + Mathf.Clamp(4f * look / Mathf.Max(Mathf.Abs(v.speed), 5f), 8f, 40f);
        navCommit[id] = cm;
        target = DetourTarget(v, self, gdir, look, inflate).ToGlobalPosition();
        return true;
    }

    // Extended-horizon detour while committed (3x lookahead so the pocket opening enters the graph).
    static Vector3 DetourTarget(GroundVehicle v, Vector3 self, Vector3 gdir, float look, float inflate)
    {
        float ext = look * 3f;
        GatherBoxes(v, self, ext, inflate);
        Vector3 xgoal = self + gdir * ext; xgoal.y = self.y;
        xgoal = PushOutOfBoxes(xgoal);
        BuildNodes(self, xgoal);
        if (AStar(out var path) && path.Count >= 2)
        {
            Vector3 t = navNode[path[1]];
            for (int i = path.Count - 1; i >= 1; i--) { if (SegClear(self, navNode[path[i]])) { t = navNode[path[i]]; break; } }
            return t;
        }
        return xgoal;   // no graph at all (shouldn't happen) — head for the far goal
    }

    // Inside a box (clipped into a collider): steer straight out the nearest face.
    static bool TryBoxEscape(Vector3 p, out Vector3 escape)
    {
        escape = p;
        float bestPen = float.MaxValue; NavBox best = default(NavBox); float blx = 0f, blz = 0f; bool found = false;
        for (int i = 0; i < navBoxes.Count; i++)
        {
            var box = navBoxes[i];
            float lx, lz; ToLocal(box, p, out lx, out lz);
            if (Mathf.Abs(lx) >= box.hx || Mathf.Abs(lz) >= box.hz) continue;
            float pen = Mathf.Min(box.hx - Mathf.Abs(lx), box.hz - Mathf.Abs(lz));
            if (pen < bestPen) { bestPen = pen; best = box; blx = lx; blz = lz; found = true; }
        }
        if (!found) return false;
        if (best.hx - Mathf.Abs(blx) < best.hz - Mathf.Abs(blz)) blx = (blx >= 0f ? 1f : -1f) * (best.hx + 2f);
        else blz = (blz >= 0f ? 1f : -1f) * (best.hz + 2f);
        escape = ToWorld(best, blx, blz);
        return true;
    }

    // ---- oriented-box helpers (all math is 2D on the XZ plane, in the box's own frame) ----

    // Flatten a world axis to XZ (fallback when steeply tilted).
    static Vector2 FlatAxis(Vector3 axis, Vector2 fallback)
    {
        Vector2 a = new Vector2(axis.x, axis.z);
        return a.sqrMagnitude > 0.25f ? a.normalized : fallback;
    }

    // Box-local 2D coords of a world point.
    static void ToLocal(NavBox b, Vector3 p, out float lx, out float lz)
    {
        float dx = p.x - b.c.x, dz = p.z - b.c.z;
        lx = dx * b.ax.x + dz * b.ax.y;
        lz = dx * b.az.x + dz * b.az.y;
    }

    // World position of a box-local 2D point.
    static Vector3 ToWorld(NavBox b, float lx, float lz)
    {
        return new Vector3(b.c.x + b.ax.x * lx + b.az.x * lz, b.c.y, b.c.z + b.ax.y * lx + b.az.y * lz);
    }

    // Gather obstacle boxes: physics OverlapBox (map buildings) + listed large obstacles. Skips terrain, debris, vehicles.
    static void GatherBoxes(GroundVehicle v, Vector3 self, float look, float inflate)
    {
        navBoxes.Clear();
        int n = Physics.OverlapBoxNonAlloc(self, new Vector3(look, 40f, look), navOverlap, Quaternion.identity, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Collide);
        for (int i = 0; i < n; i++)
        {
            var col = navOverlap[i]; if (col == null) continue;
            if (col.GetComponentInParent<GroundVehicle>() != null) continue;    // vehicles/self -> handled via the list
            AddColliderBox(self, col, inflate, look);
        }
        if (gvObstacles != null)
        {
            List<Obstacle> obs = null; try { obs = gvObstacles(v); } catch { }
            if (obs != null)
                for (int i = 0; i < obs.Count; i++)
                {
                    var o = obs[i];
                    if (o.Transform == null || o.Radius < NavLargeRadius) continue;   // small -> vanilla's spacing push
                    var col = o.Transform.GetComponentInChildren<Collider>();
                    if (col != null) AddColliderBox(self, col, inflate, look, true);
                    else AddBox(self, o.Transform.position, new Vector2(1f, 0f), new Vector2(0f, 1f), o.Radius + inflate, o.Radius + inflate, look);   // radius square fallback
                }
        }
    }

    // Debug record of a skipped collider (size filter) for the nav debug draw.
    static bool navGatherDebug;
    static readonly List<NavBox> navSkipped = new List<NavBox>();
    static void NavRecordSkip(Vector3 center, Vector2 ax, Vector2 az, float hx, float hz)
    {
        if (!navGatherDebug || navSkipped.Count >= 64) return;
        navSkipped.Add(new NavBox { c = center, ax = ax, az = az, hx = hx, hz = hz });
    }

    // Add one collider as an inflated oriented box (BoxCollider: true rotation; MeshCollider: oriented mesh-bounds fit).
    static void AddColliderBox(Vector3 self, Collider col, float inflate, float look, bool fromList = false)
    {
        // Flat map statics (runway/apron/bridge deck culled from the raised caps' wider net): drivable ground, not an
        // obstacle. Listed obstacles (wrecks / unit-buildings) always count — a pancaked wreck still blocks the lane.
        if (!fromList && col.bounds.size.y < NavFlatHeight) return;
        if (col is BoxCollider bc)
        {
            Transform t = bc.transform;
            Vector3 sc = t.lossyScale;
            float bx = bc.size.x * 0.5f * Mathf.Abs(sc.x), bz = bc.size.z * 0.5f * Mathf.Abs(sc.z);
            Vector2 ax = FlatAxis(t.right, new Vector2(1f, 0f));
            Vector2 az = FlatAxis(t.forward, new Vector2(0f, 1f));
            if (Mathf.Abs(ax.x * az.y - ax.y * az.x) < 0.5f) { ax = new Vector2(1f, 0f); az = new Vector2(0f, 1f); }   // axes collapsed parallel (steep tilt) -> axis-aligned
            Vector3 center = t.TransformPoint(bc.center);
            if (bx * 2f > NavMaxTrueBoxSize || bz * 2f > NavMaxTrueBoxSize) { NavRecordSkip(center, ax, az, bx, bz); return; }   // mega-collider
            if (bx * 2f < NavMinBoxSize && bz * 2f < NavMinBoxSize) return;   // debris
            AddBox(self, center, ax, az, bx + inflate, bz + inflate, look);
        }
        else if (col is MeshCollider mc && mc.sharedMesh != null)
        {
            Transform t = mc.transform;
            Bounds lb = mc.sharedMesh.bounds;
            Vector2 ax = FlatAxis(t.right, new Vector2(1f, 0f));
            Vector2 az = FlatAxis(t.forward, new Vector2(0f, 1f));
            if (Mathf.Abs(ax.x * az.y - ax.y * az.x) < 0.5f) { ax = new Vector2(1f, 0f); az = new Vector2(0f, 1f); }
            Vector3 c0 = t.TransformPoint(lb.center);
            float hx = 0f, hz = 0f;
            for (int k = 0; k < 8; k++)   // project the local-bounds corners onto the flattened axes -> tight oriented extents
            {
                Vector3 corner = t.TransformPoint(lb.center + Vector3.Scale(lb.extents, new Vector3((k & 1) != 0 ? 1f : -1f, (k & 2) != 0 ? 1f : -1f, (k & 4) != 0 ? 1f : -1f))) - c0;
                float px = Mathf.Abs(corner.x * ax.x + corner.z * ax.y);
                float pz = Mathf.Abs(corner.x * az.x + corner.z * az.y);
                if (px > hx) hx = px;
                if (pz > hz) hz = pz;
            }
            if (hx * 2f > NavMaxBoxSize || hz * 2f > NavMaxBoxSize) { NavRecordSkip(c0, ax, az, hx, hz); return; }
            if (hx * 2f < NavMinBoxSize && hz * 2f < NavMinBoxSize) return;
            AddBox(self, c0, ax, az, hx + inflate, hz + inflate, look);
        }
        else
        {
            Bounds b = col.bounds;
            if (b.size.x > NavMaxBoxSize || b.size.z > NavMaxBoxSize) { NavRecordSkip(b.center, new Vector2(1f, 0f), new Vector2(0f, 1f), b.extents.x, b.extents.z); return; }
            if (b.size.x < NavMinBoxSize && b.size.z < NavMinBoxSize) return;
            AddBox(self, b.center, new Vector2(1f, 0f), new Vector2(0f, 1f), b.extents.x + inflate, b.extents.z + inflate, look);
        }
    }

    // Add an inflated oriented box (range-culled; keeps the nearest NavMaxBoxes by edge distance).
    static void AddBox(Vector3 self, Vector3 center, Vector2 ax, Vector2 az, float hx, float hz, float look)
    {
        center.y = self.y;
        var box = new NavBox { c = center, ax = ax, az = az, hx = hx, hz = hz };
        float lx, lz; ToLocal(box, self, out lx, out lz);
        float dx = Mathf.Max(Mathf.Abs(lx) - hx, 0f);       // distance from unit to the nearest box edge
        float dz = Mathf.Max(Mathf.Abs(lz) - hz, 0f);
        float myEdge = dx * dx + dz * dz;
        if (myEdge > look * look) return;                                  // whole box is beyond the plan range
        if (navBoxes.Count < NavMaxBoxes) { navBoxes.Add(box); return; }
        int far = 0; float farD = -1f;
        for (int k = 0; k < navBoxes.Count; k++)                           // replace the farthest-by-edge kept box if nearer
        {
            float kx, kz; ToLocal(navBoxes[k], self, out kx, out kz);
            float ex = Mathf.Max(Mathf.Abs(kx) - navBoxes[k].hx, 0f), ez = Mathf.Max(Mathf.Abs(kz) - navBoxes[k].hz, 0f);
            float dk = ex * ex + ez * ez;
            if (dk > farD) { farD = dk; far = k; }
        }
        if (myEdge < farD) navBoxes[far] = box;
    }

    // Nudge a point out of any box it landed in (nearest face), so the goal is reachable open space.
    static Vector3 PushOutOfBoxes(Vector3 p)
    {
        for (int iter = 0; iter < 4; iter++)
        {
            bool moved = false;
            for (int i = 0; i < navBoxes.Count; i++)
            {
                var box = navBoxes[i];
                float lx, lz; ToLocal(box, p, out lx, out lz);
                if (Mathf.Abs(lx) < box.hx && Mathf.Abs(lz) < box.hz)
                {
                    if (box.hx - Mathf.Abs(lx) < box.hz - Mathf.Abs(lz)) lx = (lx >= 0f ? 1f : -1f) * (box.hx + 0.5f);
                    else lz = (lz >= 0f ? 1f : -1f) * (box.hz + 0.5f);
                    p = ToWorld(box, lx, lz);
                    moved = true;
                }
            }
            if (!moved) break;
        }
        return p;
    }

    // Graph nodes = start, goal, box corners (dropping corners buried inside another box).
    static void BuildNodes(Vector3 start, Vector3 goal)
    {
        navNode.Clear();
        navNode.Add(start);   // 0
        navNode.Add(goal);    // 1
        for (int i = 0; i < navBoxes.Count; i++)
        {
            var box = navBoxes[i]; float ex = box.hx + 0.3f, ez = box.hz + 0.3f;
            AddCorner(box, ex, ez, i); AddCorner(box, ex, -ez, i);
            AddCorner(box, -ex, ez, i); AddCorner(box, -ex, -ez, i);
        }
    }

    static void AddCorner(NavBox box, float ox, float oz, int except)
    {
        Vector3 p = ToWorld(box, ox, oz);
        if (!InsideAnyBox(p, except)) navNode.Add(p);
    }

    static bool InsideAnyBox(Vector3 p, int except)
    {
        for (int i = 0; i < navBoxes.Count; i++)
        {
            if (i == except) continue;
            float lx, lz; ToLocal(navBoxes[i], p, out lx, out lz);
            if (Mathf.Abs(lx) < navBoxes[i].hx && Mathf.Abs(lz) < navBoxes[i].hz) return true;
        }
        return false;
    }

    // Flat segment vs every box (2D slab test in each box's local frame).
    static bool SegClear(Vector3 a, Vector3 b)
    {
        for (int i = 0; i < navBoxes.Count; i++)
            if (SegBoxT(a, b, navBoxes[i], out _)) return false;
        return true;
    }

    // Segment vs box slab test in the box's local frame (t = entry parameter on hit).
    static bool SegBoxT(Vector3 a, Vector3 b, NavBox box, out float t)
    {
        const float eps = 0.4f;
        float minx = -box.hx + eps, maxx = box.hx - eps;
        float minz = -box.hz + eps, maxz = box.hz - eps;
        t = 0f;
        if (maxx <= minx || maxz <= minz) return false;
        float ax, az, bx, bz;
        ToLocal(box, a, out ax, out az); ToLocal(box, b, out bx, out bz);
        float dx = bx - ax, dz = bz - az, tmin = 0f, tmax = 1f;
        if (Mathf.Abs(dx) < 1e-6f) { if (ax < minx || ax > maxx) return false; }
        else { float t1 = (minx - ax) / dx, t2 = (maxx - ax) / dx; if (t1 > t2) { float s = t1; t1 = t2; t2 = s; } tmin = Mathf.Max(tmin, t1); tmax = Mathf.Min(tmax, t2); if (tmin > tmax) return false; }
        if (Mathf.Abs(dz) < 1e-6f) { if (az < minz || az > maxz) return false; }
        else { float t1 = (minz - az) / dz, t2 = (maxz - az) / dz; if (t1 > t2) { float s = t1; t1 = t2; t2 = s; } tmin = Mathf.Max(tmin, t1); tmax = Mathf.Min(tmax, t2); if (tmin > tmax) return false; }
        t = tmin;
        return true;
    }

    // A* over the visibility graph (goal unreachable -> best-effort path to the closest-to-goal node).
    static bool AStar(out List<int> path)
    {
        path = navPath; path.Clear();
        int n = navNode.Count;
        if (n < 2) return false;
        var g = new float[n]; var f = new float[n]; var came = new int[n]; var open = new bool[n]; var closed = new bool[n];
        for (int i = 0; i < n; i++) { g[i] = float.MaxValue; f[i] = float.MaxValue; came[i] = -1; }
        g[0] = 0f; f[0] = Vector3.Distance(navNode[0], navNode[1]); open[0] = true;
        int bestNode = 0; float bestH = Vector3.Distance(navNode[0], navNode[1]);   // closest-to-goal settled node so far
        while (true)
        {
            int cur = -1; float bestf = float.MaxValue;
            for (int i = 0; i < n; i++) if (open[i] && f[i] < bestf) { bestf = f[i]; cur = i; }
            if (cur < 0) { if (bestNode == 0) return false; Reconstruct(bestNode, came, path); return true; }   // goal unreachable -> best effort
            if (cur == 1) { Reconstruct(1, came, path); return true; }
            open[cur] = false; closed[cur] = true;
            float h = Vector3.Distance(navNode[cur], navNode[1]);
            if (h < bestH) { bestH = h; bestNode = cur; }                          // track the most-progress reachable node
            Vector3 pc = navNode[cur];
            for (int j = 0; j < n; j++)
            {
                if (j == cur || closed[j]) continue;
                float d = Vector3.Distance(pc, navNode[j]);
                if (d > NavMaxEdge) continue;
                if (!SegClear(pc, navNode[j])) continue;
                float tentative = g[cur] + d;
                if (tentative < g[j]) { came[j] = cur; g[j] = tentative; f[j] = tentative + Vector3.Distance(navNode[j], navNode[1]); open[j] = true; }
            }
        }
    }

    static void Reconstruct(int end, int[] came, List<int> path)
    {
        int c = end; while (c != -1) { path.Add(c); c = came[c]; } path.Reverse();
    }

    // Steer-layer hook: building avoidance + cornering for EVERY navigating ground unit (GetSteerpoint is patchable managed code).
    const float NavSteerInterval = 0.35f;   // per-unit building-route replan cadence (cached between)
    struct NavSteer { public float next; public Vector3 dir; public bool active; }
    static readonly Dictionary<int, NavSteer> navSteerCache = new Dictionary<int, NavSteer>();

    internal static void AdjustSteerpoint(PathfindingAgent pa, ref SteeringInfo? result)
    {
        if (result == null) return;
        SteeringInfo si = SoftenCorner(result.Value);                    // 2) cornering (all units)

        if (MasterOn && paUnit != null)                                 // 1) building avoidance (all ground units)
        {
            Unit u = null; try { u = paUnit(pa); } catch { }
            if (u is GroundVehicle gv && !gv.remoteSim)
            {
                Vector3 baseDir = si.steerVector; baseDir.y = 0f;
                if (baseDir.sqrMagnitude > 1e-4f)
                {
                    Vector3 adj = GroundSteerAvoid(gv, Flat(baseDir));
                    if (adj != Vector3.zero)
                    {
                        // Report the TRUE sharpness of the avoidance turn to the Burst speed governor. Its target speed
                        // is Clamp(80/max((angle-10)*0.1,0.1), 30, topSpeed) — driven by nextWaypointAngle — and the
                        // road's own waypoint angle can be near-zero while our reroute demands a hard turn, which is
                        // exactly how units carried full speed into a building despite the green line showing the
                        // detour. With the real angle reported, the governor brakes early enough to make the turn
                        // (and any steer >60deg off-heading floors it to 30km/h via its own dot-product rule).
                        float avoidAngle = Vector3.Angle(Flat(gv.transform.forward), adj);
                        si = new SteeringInfo(adj * si.steerVector.magnitude, Mathf.Max(si.nextWaypointAngle, avoidAngle));
                    }
                }
            }
        }
        result = si;
    }

    // Throttled per-unit building route (corner-hugging steer dir, or zero = no change). Hysteresis kills wiggle.
    static Vector3 GroundSteerAvoid(GroundVehicle gv, Vector3 steerDir)
    {
        if (gvObstacles == null) return Vector3.zero;
        int id = gv.GetInstanceID();
        float now = Time.timeSinceLevelLoad;
        if (!navSteerCache.TryGetValue(id, out var c) || now >= c.next)
        {
            c.next = now + NavSteerInterval;
            GlobalPosition pos = gv.GlobalPosition();
            // A* is the SOLE avoider (vanilla's large-obstacle avoidance is blanked). When the goal is unreachable it
            // commits to an extended-horizon detour until the corridor opens (U-trap fix). Zero only in open terrain.
            Vector3 d = GroundPlanPath(gv, pos, steerDir, out GlobalPosition target) ? Flat(target - pos) : Vector3.zero;
            // "active" only when we actually bent the path meaningfully (so an open corridor reads as no-change) —
            // with HYSTERESIS: a borderline bend must not flip between vanilla steer and our detour every 0.35s
            // recompute (visible left/right wiggle). Activate at a >~10deg bend, stay active until it shrinks under ~6deg.
            c.dir = d; c.active = d != Vector3.zero && Vector3.Dot(d, steerDir) < (c.active ? 0.995f : 0.985f);
            navSteerCache[id] = c;
        }
        return c.active ? c.dir : Vector3.zero;
    }

    // Is this unit currently routing around a building? (spectate overlay)
    internal static bool NavIsRouting(GroundVehicle gv) => gv != null && navSteerCache.TryGetValue(gv.GetInstanceID(), out var c) && c.active;

    // Debug visualization ("Debug: Draw Ground Nav" toggle): obstacle boxes + steer line for the spectated unit.
    static Material navGlMat;
    static void EnsureGlMat()
    {
        if (navGlMat != null) return;
        var sh = Shader.Find("Hidden/Internal-Colored"); if (sh == null) return;
        navGlMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
        navGlMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        navGlMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        navGlMat.SetInt("_Cull", 0);
        navGlMat.SetInt("_ZWrite", 0);
        navGlMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);   // draw over terrain so it's always visible
    }

    // Spectated unit's obstacle boxes (orange = routed, blue = size-skipped) + steer line (green/cyan).
    static int navVizFrame = -1;
    internal static void NavDebugRender()
    {
        if (!MasterOn || cfgNavDebug == null || !cfgNavDebug.Value || vetFollowing == null) return;
        var cam = SceneSingleton<CameraStateManager>.i; if (cam == null) return;
        Unit u; try { u = vetFollowing(cam); } catch { return; }
        if (!(u is GroundVehicle gv) || gv.disabled) return;
        EnsureGlMat(); if (navGlMat == null) return;

        Vector3 self = gv.transform.position;
        if (navVizFrame != Time.frameCount)   // gather once per frame (OnRenderObject fires per camera)
        {
            float inflate = gv.maxRadius + NavClearance;
            float look = Mathf.Clamp(gv.maxRadius + Mathf.Abs(gv.speed) * NavLookSpeed + NavLookBase, NavLookMin, NavLookMax);
            navSkipped.Clear(); navGatherDebug = true;
            GatherBoxes(gv, self, look, inflate);
            navGatherDebug = false;
            navVizFrame = Time.frameCount;
        }

        Vector3 lift = Vector3.up * 0.5f;
        FlatDisc(self, gv.maxRadius, new Color(0.6f, 0.6f, 0.6f, 0.15f), new Color(0.6f, 0.6f, 0.6f), lift);   // our own footprint
        for (int i = 0; i < navBoxes.Count; i++)
            DrawBox(navBoxes[i], new Color(1f, 0.3f, 0.15f), lift);                     // obstacle boxes (real collider bounds + our size)
        for (int i = 0; i < navSkipped.Count; i++)
        {
            var sb = navSkipped[i]; sb.c.y = self.y;                                    // ground-align for readability
            DrawBox(sb, new Color(0.25f, 0.5f, 1f), lift);                              // skipped by size filter (debug)
        }
        if (navSteerCache.TryGetValue(gv.GetInstanceID(), out var c) && c.active && c.dir != Vector3.zero)
        {
            bool contouring = navCommit.TryGetValue(gv.GetInstanceID(), out var cm) && Time.timeSinceLevelLoad < cm.until;
            Color sc = contouring ? Color.cyan : Color.green;
            ThickLine(self + lift, self + lift + c.dir.normalized * 45f, sc, 1.5f);     // where the planner is steering it now
        }
        ThickLine(self + lift, self + lift + Flat(gv.transform.forward) * 18f, Color.white, 1f);   // current heading
    }

    // Debug draw helpers (camera-facing quads/discs; GL.LINES is 1px in Unity).
    static void ThickLine(Vector3 a, Vector3 b, Color col, float width = -1f)
    {
        var cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : a + Vector3.up * 200f;
        Vector3 mid = (a + b) * 0.5f;
        if (width <= 0f) width = Mathf.Max(0.6f, Vector3.Distance(camPos, mid) * 0.004f);   // ~constant screen width
        Vector3 dir = b - a; float len = dir.magnitude; if (len < 1e-3f) return; dir /= len;
        Vector3 side = Vector3.Cross(dir, (camPos - mid).normalized);
        if (side.sqrMagnitude < 1e-4f) return;   // camera looking straight down the line
        side = side.normalized * (width * 0.5f);
        navGlMat.SetPass(0);
        GL.Begin(GL.QUADS);
        GL.Color(col);
        GL.Vertex(a - side); GL.Vertex(a + side); GL.Vertex(b + side); GL.Vertex(b - side);
        GL.End();
    }

    // Filled ground-parallel disc + outline (destination/position markers).
    static void FlatDisc(Vector3 center, float r, Color fill, Color edge, Vector3 lift)
    {
        const int seg = 20;
        Vector3 c = center + lift;
        navGlMat.SetPass(0);
        if (fill.a > 0.01f)
        {
            GL.Begin(GL.TRIANGLES);
            GL.Color(fill);
            for (int i = 0; i < seg; i++)
            {
                float a0 = (2f * Mathf.PI / seg) * i, a1 = (2f * Mathf.PI / seg) * (i + 1);
                GL.Vertex(c);
                GL.Vertex(c + new Vector3(Mathf.Cos(a0) * r, 0f, Mathf.Sin(a0) * r));
                GL.Vertex(c + new Vector3(Mathf.Cos(a1) * r, 0f, Mathf.Sin(a1) * r));
            }
            GL.End();
        }
        GL.Begin(GL.LINES);
        GL.Color(edge);
        Vector3 prev = c + new Vector3(r, 0f, 0f);
        for (int i = 1; i <= seg; i++)
        {
            float a = (2f * Mathf.PI / seg) * i;
            Vector3 p = c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            GL.Vertex(prev); GL.Vertex(p); prev = p;
        }
        GL.End();
    }

    static void DrawBox(NavBox box, Color col, Vector3 lift)
    {
        Vector3 p0 = ToWorld(box, box.hx, box.hz) + lift, p1 = ToWorld(box, box.hx, -box.hz) + lift;
        Vector3 p2 = ToWorld(box, -box.hx, -box.hz) + lift, p3 = ToWorld(box, -box.hx, box.hz) + lift;
        ThickLine(p0, p1, col); ThickLine(p1, p2, col); ThickLine(p2, p3, col); ThickLine(p3, p0, col);
    }

    // Frontline debug layer: every owned unit's destination line + ring, coloured by state.
    internal static void FrontlineDebugRender()
    {
        EnsureGlMat(); if (navGlMat == null) return;
        Vector3 lift = Vector3.up * 1.2f;
        foreach (var kv in gStates)
        {
            var s = kv.Value;
            if (s == null || !s.owned || s.v == null || s.v.disabled) continue;
            Vector3 p = s.v.transform.position;
            Color state = StateDebugColor(s.state);
            if (s.hasIssued)
            {
                Vector3 d = s.lastIssued.ToLocalPosition();
                ThickLine(p + lift, d + lift, state);                                    // where it's trying to go
                FlatDisc(d, 4.5f, new Color(state.r, state.g, state.b, 0.5f), state, lift);   // its destination
            }
        }
    }

    static Color StateDebugColor(GCombat state)
    {
        switch (state)
        {
            case GCombat.Advance: return new Color(0.2f, 0.9f, 0.3f);
            case GCombat.Retreat: return new Color(1f, 0.25f, 0.2f);
            case GCombat.Defend:  return new Color(0.2f, 0.7f, 1f);
            case GCombat.Engage:  return Color.white;
            case GCombat.Support: return new Color(0.7f, 0.4f, 1f);
            default:              return new Color(0.6f, 0.6f, 0.6f);   // pursue / none
        }
    }

    void OnRenderObject()
    {
        try
        {
            NavDebugRender();
            if (MasterOn && cfgNavDebug != null && cfgNavDebug.Value) FrontlineDebugRender();
        }
        catch { }
    }

    // Hide near large obstacles from vanilla's Burst avoidance (A* owns them instead). Small ones keep vanilla spacing.
    internal static void GroundBlankLargeObstacles(GroundVehicle v)
    {
        if (!MasterOn || gvJobFields == null || v == null || v.remoteSim) return;
        try
        {
            ref var jf = ref gvJobFields(v);
            if (!jf.IsCreated) return;
            var arr = jf.Ref().ObstaclesArray;
            Vector3 self = v.transform.position; float rng2 = NavBlankRange * NavBlankRange;
            int len = arr.Length;
            for (int i = 0; i < len; i++)
            {
                var op = arr[i];
                if (op.Radius < NavLargeRadius) continue;                            // small -> leave vanilla's spacing push
                Vector3 d = op.Position - self; d.y = 0f;
                if (d.sqrMagnitude <= rng2) { op.Radius = 0f; arr[i] = op; }         // close large -> A* owns it; far ones stay with vanilla (never un-avoided)
            }
        }
        catch { }
    }

    // Cornering: compress the reported turn angle so the Burst speed governor carries speed through turns.
    const float CornerAngleScale = 0.5f;   // lower = carry more speed through turns (raise toward 1 if units overshoot)

    internal static SteeringInfo SoftenCorner(SteeringInfo si)
    {
        float ang = si.nextWaypointAngle;
        if (ang <= 10f) return si;
        return new SteeringInfo(si.steerVector, 10f + (ang - 10f) * CornerAngleScale);
    }
}

// Steer-layer hook: building avoidance + cornering for every navigating ground unit.
[HarmonyPatch(typeof(PathfindingAgent), "GetSteerpoint")]
public static class PathfindingAgent_GetSteerpoint_Patch
{
    static void Postfix(PathfindingAgent __instance, ref SteeringInfo? __result)
    {
        try { ImprovedAIPlugin.AdjustSteerpoint(__instance, ref __result); }
        catch { }
    }
}

// Large-obstacle blanking hook (A* is the sole large avoider).
[HarmonyPatch(typeof(GroundVehicle), "UpdateJobFields_Obstacles")]
public static class GroundVehicle_UpdateJobFields_Obstacles_Patch
{
    static void Postfix(GroundVehicle __instance)
    {
        try { ImprovedAIPlugin.GroundBlankLargeObstacles(__instance); }
        catch { }
    }
}
