// GroundCommon: shared reflection handles + geometry/threat helpers for the ground modules. No behaviour.

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

public partial class ImprovedAIPlugin
{
    // ---- ground-vehicle / turret / pathfinder private field access (bound once in GroundCommonBind) ----
    static System.Reflection.MethodInfo gvUpdateObstacles;   // GroundVehicle.UpdateObstacles() — rebuild obstacle list
    static AccessTools.FieldRef<GroundVehicle, List<Obstacle>> gvObstacles;   // the built obstacle list (units + buildings)
    static AccessTools.FieldRef<PathfindingAgent, Unit> paUnit;   // PathfindingAgent.unit — reach the vehicle from the GetSteerpoint hook
    static AccessTools.FieldRef<GroundVehicle, PathfindingAgent> gvPathfinder;
    static AccessTools.FieldRef<GroundVehicle, bool> gvNavigate, gvCommanded, gvMobile, gvResetStationary;
    static AccessTools.FieldRef<Turret, Unit> turretTarget;   // what a turret is currently engaging
    // NOTE: JobFields is a Burst unmanaged struct (PtrAllocation<T> contains a raw T*). AccessTools.FieldRefAccess
    // on it builds a DynamicMethod via MonoMod Cecil import, which hard-crashes Unity Mono (native crash in
    // Type.IsValueType, uncatchable by try/catch — see Player.log crash in GroundCommonBind). So we bind it as a
    // plain FieldInfo (no codegen) and operate on a COPY: the copy shares the native ptr, so Ref() mutations
    // still hit the live job memory. Null = feature disabled (same contract as before).
    static System.Reflection.FieldInfo gvJobFieldsInfo;

    // Bind all shared reflection handles. Each failure nulls only its own feature.
    void GroundCommonBind()
    {
        try
        {
            gvPathfinder = AccessTools.FieldRefAccess<GroundVehicle, PathfindingAgent>("pathfinder");
            gvNavigate = AccessTools.FieldRefAccess<GroundVehicle, bool>("navigateToObjectives");
            gvCommanded = AccessTools.FieldRefAccess<GroundVehicle, bool>("commandedDestination");
            gvMobile = AccessTools.FieldRefAccess<GroundVehicle, bool>("mobile");
            gvResetStationary = AccessTools.FieldRefAccess<GroundVehicle, bool>("resetStationary");
            turretTarget = AccessTools.FieldRefAccess<Turret, Unit>("target");
            try { gvJobFieldsInfo = AccessTools.Field(typeof(GroundVehicle), "JobFields"); if (gvJobFieldsInfo == null) throw new MissingFieldException("GroundVehicle", "JobFields"); }
            catch (Exception ex) { gvJobFieldsInfo = null; Logger.LogWarning("Reverse-retreat unavailable (JobFields access failed): " + ex.Message); }
            try { gvUpdateObstacles = AccessTools.Method(typeof(GroundVehicle), "UpdateObstacles"); gvObstacles = AccessTools.FieldRefAccess<GroundVehicle, List<Obstacle>>("obstacles"); }
            catch (Exception ex) { gvUpdateObstacles = null; gvObstacles = null; Logger.LogWarning("Obstacle avoidance unavailable: " + ex.Message); }
            try { paUnit = AccessTools.FieldRefAccess<PathfindingAgent, Unit>("unit"); }
            catch (Exception ex) { paUnit = null; Logger.LogWarning("Steer-layer routing unavailable (PathfindingAgent.unit): " + ex.Message); }
        }
        catch (Exception ex) { gvPathfinder = null; Logger.LogError("Improved AI ground-forces init failed (feature disabled): " + ex); }
    }

    // Safe JobFields read: boxed COPY via FieldInfo.GetValue (no DynamicMethod, never hard-crashes).
    // The struct only holds a native pointer, so mutating via copy.Ref() still writes the live job memory.
    // Returns false when unavailable (feature silently disabled, same as gvJobFields==null before).
    static bool TryGetGroundJob(GroundVehicle v, out NuclearOption.Jobs.PtrAllocation<NuclearOption.Jobs.GroundVehicleFields> job)
    {
        job = default;
        try
        {
            var fi = gvJobFieldsInfo;
            if (fi == null || v == null) return false;
            object o = fi.GetValue(v);
            if (o == null) return false;
            job = (NuclearOption.Jobs.PtrAllocation<NuclearOption.Jobs.GroundVehicleFields>)o;
            return true;
        }
        catch { return false; }
    }

    // ---- generic geometry helpers ----

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.zero; }

    static Vector3 Perp(Vector3 dir, int sign) { return (Vector3.Cross(Vector3.up, dir).normalized) * sign; }

    static bool HasLoS(GlobalPosition a, GlobalPosition b)
    {
        Vector3 pa = a.ToLocalPosition() + Vector3.up * 2f;
        Vector3 pb = b.ToLocalPosition() + Vector3.up * 2f;
        return !Physics.Linecast(pa, pb, (int)PhysicsLayers.StaticsMask);
    }

    // ---- generic threat helpers ----

    static float SafeThreat(UnitDefinition me, UnitDefinition their)
    {
        if (me == null || their == null) return 0f;
        return me.ThreatPosedBy(their.roleIdentity);
    }

    static float MaxWeaponRange(Unit u)
    {
        float r = 0f;
        var stations = u.weaponStations;
        if (stations != null)
            for (int i = 0; i < stations.Count; i++)
            {
                var ws = stations[i];
                if (ws != null && ws.WeaponInfo != null)
                { float m = ws.WeaponInfo.targetRequirements.maxRange; if (m > r) r = m; }
            }
        return r;
    }

    // Max range among weapons that can hit GROUND targets (false for pure AA units).
    static float GroundWeaponRange(Unit u, out bool hasGroundWeapon)
    {
        hasGroundWeapon = false; float r = 0f;
        var stations = u.weaponStations;
        if (stations != null)
            for (int i = 0; i < stations.Count; i++)
            {
                var ws = stations[i];
                if (ws == null || ws.WeaponInfo == null || ws.WeaponInfo.effectiveness.antiSurface <= 0.05f) continue;
                hasGroundWeapon = true;
                float m = ws.WeaponInfo.targetRequirements.maxRange; if (m > r) r = m;
            }
        return r;
    }

    // Is any of the enemy's turrets currently targeting me?
    static bool EnemyTargetsMe(Unit enemy, Unit me)
    {
        var stations = enemy.weaponStations;
        if (stations == null) return false;
        for (int i = 0; i < stations.Count; i++)
        {
            var ws = stations[i]; if (ws == null) continue;
            var turrets = ws.Turrets; if (turrets == null) continue;
            for (int j = 0; j < turrets.Count; j++)
            {
                var t = turrets[j];
                if (t != null && turretTarget(t) == me) return true;
            }
        }
        return false;
    }

    // ---- terrain / water helpers (shared by all ground layers) ----

    // Nearest dry point at/toward `to`. Returns `from` on failure.
    static GlobalPosition DryPoint(GlobalPosition from, GlobalPosition to)
    {
        Vector3 p = to.ToLocalPosition();
        Vector3 b = from.ToLocalPosition();
        for (int i = 0; i < 5; i++)
        {
            if (PathfindingAgent.RaycastTerrain(p, out var hit) && hit.point.y >= Datum.LocalSeaY)
            { p.y = hit.point.y; return p.ToGlobalPosition(); }
            p = FastMath.LerpXZ(p, b, 0.5f);
        }
        return from;
    }

    // Is the whole straight corridor a->b dry land? Samples terrain height along the segment.
    const float WaterSampleStep = 12f;
    static bool CorridorDry(GlobalPosition a, GlobalPosition b)
    {
        Vector3 pa = a.ToLocalPosition(), pb = b.ToLocalPosition();
        int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(pa, pb) / WaterSampleStep));
        for (int i = 1; i <= steps; i++)
        {
            Vector3 p = Vector3.Lerp(pa, pb, i / (float)steps);
            if (!PathfindingAgent.RaycastTerrain(p, out var hit) || hit.point.y < Datum.LocalSeaY) return false;
        }
        return true;
    }
}
