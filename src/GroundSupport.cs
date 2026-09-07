// GroundSupport: ammo resupply runs + non-combatant (AA/truck) defensive mode. Runs around the combat FSM.

using System;
using System.Collections.Generic;
using UnityEngine;

public partial class ImprovedAIPlugin
{
    // Ammo-resupply tunables (derived where possible; stop distance scales with the rearmer's own Range).
    const float ResupplyStopFrac = 0.6f;      // park this fraction into the rearmer's Range — well inside, so the vanilla in-range auto-rearm fires
    const float ResupplyRefillTimeout = 25f;  // if not actually refilling in this long (parked just out of range), re-approach the supply
    const float ResupplyStuckWindow = 45f;    // give up ONLY if genuinely wedged: not moving (speed<1) for this long while seeking; a moving unit never gives up
    const float ResupplyHomeArrive = 25f;     // "back on station" within this many metres of the original hold spot
    const float ResupplyMinCapacity = 1f;     // a supply must hold at least this much capacity to be worth visiting (skip empty ones)

    // Ammo resupply: dry units road-march to a supply, refill, return. Takes priority over combat. True while owning the unit.
    static bool ResupplyUpdate(GState s, float now, GlobalPosition pos)
    {
        var v = s.v;
        // Support units (rearm/repair/artillery/wreck) run their own vanilla AI or ARE the supply, and strategic launchers
        // (ballistic/cruise/strato — fire from a fixed emplacement) must never be driven off — never divert any of these.
        if (s.hasSpecialAI || s.noOverride || s.stationaryFire) { if (s.resupplyPhase != 0) EndResupply(s); return false; }

        if (s.resupplyPhase == 0)
        {
            // start when: mobile + not player-driven + MAIN weapon empty + a supply with ammo exists. Hold OR non-hold,
            // and even if we're currently combat-owning it (rearming takes priority over fighting).
            if (!gvMobile(v) || gvCommanded(v) || v.IsSlung() || v.NetworkHQ == null) return false;
            if (!MainAmmoEmpty(v)) return false;
            if (!TryFindRearmer(v, pos, out var rearmer)) return false;   // nothing has ammo to share -> carry on
            s.resupplyWasHold = v.GetHoldPosition();
            if (s.resupplyWasHold) s.homePos = pos;    // only hold units return to a saved spot
            s.resupplyPhase = 1; s.resupplyStart = now;
            if (s.owned) ReleaseGround(s, false);      // drop any combat control cleanly first
            // Move via the VANILLA road nav (UnitCommand.SetDestination -> Pathfind on the road network): fastest route,
            // and it also clears the unit's `anchored` flag (a hold unit is anchored at spawn, so SetHoldPosition(false)
            // alone leaves it pinned — that was why hold units wouldn't move). We only suppress the enemy re-route:
            // a hold unit is already immune (CheckObstacles skips holdPosition), a non-hold auto-attacker needs
            // navigateToObjectives=false so it isn't re-pointed at the enemy every 4s. holdPosition is left untouched.
            s.savedNav = gvNavigate(v);
            if (!s.resupplyWasHold) gvNavigate(v) = false;
            if (cfgDiag.Value) Log.LogInfo($"[Ground] RESUPPLY {v.name} ({(s.resupplyWasHold ? "hold" : "mobile")}) main weapons all empty [{MainWeaponsDesc(v)}] -> {rearmer.Unit?.name} @ {FastMath.Distance(pos, rearmer.GetPosition()):0}m");
        }

        // abort: player took command, became immobile/slung, or lost HQ -> hand back to vanilla
        if (gvCommanded(v) || !gvMobile(v) || v.IsSlung() || v.NetworkHQ == null) { EndResupply(s); return false; }

        if (!s.resupplyWasHold) gvNavigate(v) = false;   // keep the enemy re-route suppressed for non-hold units

        if (s.resupplyPhase == 1)   // driving to the supply on roads (re-query each tick so it tracks a MOVING ammo truck)
        {
            if (!TryFindRearmer(v, pos, out var rearmer)) { ResupplyDone(s); return true; }   // NOTHING with ammo anywhere -> only legit give-up
            // don't give up while making progress — only if genuinely wedged (not moving) for a long window
            if (Mathf.Abs(v.speed) > 1f) s.resupplyStart = now;
            else if (now - s.resupplyStart > ResupplyStuckWindow) { if (cfgDiag.Value) Log.LogInfo($"[Ground] RESUPPLY {v.name} stuck seeking -> giving up"); ResupplyDone(s); return true; }
            GlobalPosition rp = rearmer.GetPosition();
            float stop = Mathf.Max(rearmer.Range * ResupplyStopFrac, 20f);
            if (FastMath.InRange(pos, rp, stop))
            { s.resupplyPhase = 2; s.refillStart = now; gvPathfinder(v)?.ClearDestination(); if (cfgDiag.Value) Log.LogInfo($"[Ground] RESUPPLY {v.name} arrived, rearming"); return true; }
            try { v.UnitCommand?.SetDestination(rp, playerCommand: false); } catch { }   // road march to the rearmer
            return true;
        }

        if (s.resupplyPhase == 2)   // rearming: stay stopped and ACTIVELY pull ammo from the in-range rearmer
        {
            gvPathfinder(v)?.ClearDestination();
            // Don't rely only on the vanilla slow-update auto-rearm — trigger it ourselves each tick while in range/stationary.
            try { if (v.NetworkHQ != null && v.NetworkHQ.RearmMissionController.TryGetRearmer(v, out var rer) && rer != null) rer.ProcessRearmRequest(v, out _); } catch { }
            if (MainAmmoFull(v)) { ResupplyDone(s); if (cfgDiag.Value) Log.LogInfo($"[Ground] RESUPPLY {v.name} topped up"); return true; }
            if (!TryFindRearmer(v, pos, out _)) { ResupplyDone(s); return true; }   // every supply is now empty -> stop with what we got
            if (now - s.refillStart > ResupplyRefillTimeout)   // parked but not filling (just out of range / rearmer moved) -> re-approach, keep trying
            { s.resupplyPhase = 1; s.refillStart = now; s.resupplyStart = now; }
            return true;
        }

        // phase 3: returning to the original hold spot on roads (hold units only)
        if (FastMath.InRange(pos, s.homePos, ResupplyHomeArrive))
        { EndResupply(s); if (cfgDiag.Value) Log.LogInfo($"[Ground] RESUPPLY {v.name} back on station"); return true; }
        try { v.UnitCommand?.SetDestination(s.homePos, playerCommand: false); } catch { }   // road march home
        return true;
    }

    // Finished rearming: holders head back to their spot, others release to the road-march.
    static void ResupplyDone(GState s)
    {
        if (s.resupplyWasHold) { s.resupplyPhase = 3; }
        else EndResupply(s);
    }

    // Hand the unit back to vanilla.
    static void EndResupply(GState s)
    {
        var v = s.v;
        try
        {
            gvNavigate(v) = s.savedNav;
            gvPathfinder(v)?.ClearDestination();
            if (!s.resupplyWasHold && s.savedNav && NearestGroundEnemyAny(v, out GlobalPosition ep))
                v.UnitCommand?.SetDestination(ep, playerCommand: false);   // resume the push immediately
        }
        catch { }
        s.resupplyPhase = 0;
        s.hasIssued = false;
        s.state = GCombat.None;
    }

    // Main weapons = missile/armour-defeating stations (MGs excluded); fallback = most lethal station.
    const float MainWeaponArmorMin = 3f;
    static readonly List<WeaponStation> mainWeapons = new List<WeaponStation>();
    static void CollectMainWeapons(GroundVehicle v)
    {
        mainWeapons.Clear();
        var stations = v.weaponStations;
        if (stations == null) return;
        for (int i = 0; i < stations.Count; i++)
        {
            var ws = stations[i]; var info = ws?.WeaponInfo;
            if (ws == null || ws.Cargo || info == null || ws.FullAmmo <= 0) continue;
            if (info.missile || info.armorTierEffectiveness > MainWeaponArmorMin) mainWeapons.Add(ws);
        }
        if (mainWeapons.Count > 0) return;
        WeaponStation best = null; float bestD = -1f;   // fallback: most-lethal single station
        for (int i = 0; i < stations.Count; i++)
        {
            var ws = stations[i]; var info = ws?.WeaponInfo;
            if (ws == null || ws.Cargo || info == null || ws.FullAmmo <= 0) continue;
            float d = info.pierceDamage + info.blastDamage;
            if (d > bestD) { bestD = d; best = ws; }
        }
        if (best != null) mainWeapons.Add(best);
    }

    // Out of ammo = every main weapon empty.
    static bool MainAmmoEmpty(GroundVehicle v)
    {
        CollectMainWeapons(v);
        if (mainWeapons.Count == 0) return false;   // no ammo-based weapon at all -> nothing to resupply
        for (int i = 0; i < mainWeapons.Count; i++) if (mainWeapons[i].GetAmmoTotal() > 0) return false;
        return true;
    }
    // Topped up = every main weapon full.
    static bool MainAmmoFull(GroundVehicle v)
    {
        CollectMainWeapons(v);
        if (mainWeapons.Count == 0) return true;
        for (int i = 0; i < mainWeapons.Count; i++) if (mainWeapons[i].GetAmmoTotal() < mainWeapons[i].FullAmmo) return false;
        return true;
    }

    // True for over-the-horizon strategic weapons (never drive these to a rearmer).
    static bool HasOverHorizonWeapon(GroundVehicle v)
    {
        var stations = v.weaponStations;
        if (stations == null) return false;
        for (int i = 0; i < stations.Count; i++)
        {
            var info = stations[i]?.WeaponInfo;
            if (info != null && info.overHorizon) return true;
        }
        return false;
    }

    // Diagnostics: main weapons + ammo levels.
    static string MainWeaponsDesc(GroundVehicle v)
    {
        CollectMainWeapons(v);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < mainWeapons.Count; i++)
        { var ws = mainWeapons[i]; if (i > 0) sb.Append(", "); sb.Append($"{ws.WeaponInfo?.weaponName}({ws.GetAmmoTotal()}/{ws.FullAmmo})"); }
        return sb.ToString();
    }

    // Nearest friendly supply with ammo left (trucks/bunkers, no ships).
    static bool TryFindRearmer(GroundVehicle v, GlobalPosition pos, out Rearmer rearmer)
    {
        rearmer = null;
        var hq = v.NetworkHQ; if (hq == null) return false;
        try { return hq.RearmMissionController.TryGetNearestRearmer(pos, out rearmer, false, ResupplyMinCapacity) && rearmer != null && rearmer.Unit != null && !rearmer.Unit.disabled; }
        catch { return false; }
    }

    // Non-combatant defence: flee when engaged, shelter near engaged friendlies, never advance.
    static void DefensiveUpdate(GState s, float now, GlobalPosition pos)
    {
        var v = s.v;
        // Hands off, same as the combat FSM: immobile / slung / no-HQ / player-commanded / hold-position units are
        // NEVER taken over. Without this a mission holder (or a player-driven truck) got hijacked to a support spot
        // and, on release, routed at the nearest enemy while still flagged hold — i.e. "Holding position (vanilla)"
        // while road-marching at the enemy and fighting every player order.
        if (!gvMobile(v) || v.IsSlung() || v.NetworkHQ == null || gvCommanded(v) || v.GetHoldPosition())
        { if (s.owned) ReleaseGround(s, false); return; }
        bool threatened = FindActiveThreat(v, pos, Mathf.Max(cfgGroundEngageRadius.Value, 3000f), out Unit threat, out GlobalPosition thpos,
                                           out _, out _, out _);
        float contagionR = ContagionRadius(s);
        bool friendlyEngaged = !threatened && HasOwnedFriendlyNearby(v, pos, contagionR);

        if (!threatened && !friendlyEngaged)
        {
            if (s.owned && now - s.lastEnemyTime > cfgGroundClearTime.Value)
            { if (cfgDiag.Value) Log.LogInfo($"[Ground] release(def-clear) {v.name}"); ReleaseGround(s, true); }
            return;
        }
        s.lastEnemyTime = now;

        if (!s.owned)
        {
            if (!gvNavigate(v)) return;              // scripted-hold path -> leave it alone
            s.savedNav = gvNavigate(v);
            gvNavigate(v) = false;                   // take over the road nav
            gvResetStationary(v) = true;
            s.owned = true; s.natural = false;       // never propagates engagement to combat units
            if (cfgDiag.Value) Log.LogInfo($"[Ground] TAKEOVER(def) {v.name} threat={threatened} allyEngaged={friendlyEngaged}");
        }

        if (threatened)
        {
            // flee away from the threat (no reverse — an unarmed unit just runs; it has nothing to keep its nose on).
            // Committed heading like the combat retreat: re-picking the scored fallback every tick jinked trucks
            // in circles instead of actually leaving.
            Vector3 away = Flat(pos - thpos);
            if (away == Vector3.zero) away = Flat(v.transform.forward);
            if (s.retreatDir == Vector3.zero || now >= s.retreatDirUntil || Vector3.Dot(s.retreatDir, away) < 0.5f)
            { s.retreatDir = ChooseFallback(pos, thpos); s.retreatDirUntil = now + 5f; }
            if (s.retreatDir == Vector3.zero) s.retreatDir = away;
            if (s.state != GCombat.Retreat && cfgDiag.Value) Log.LogInfo($"[Ground] {v.name} -> retreat(def) vs {threat.name}");
            s.state = GCombat.Retreat;
            IssueMove(s, pos, s.retreatDir);
        }
        else
        {
            // A friendly is engaged nearby: take up an actual SUPPORT position — pulled back from the enemy, off the
            // road axis — instead of freezing on the asphalt where the road-march left us (the old "halt in the middle
            // of the road" behaviour). Recomputed at most every 10s so the spot doesn't jitter with the friendlies.
            if (EngagedFriendlyAnchor(v, pos, ContagionRadius(s), out GlobalPosition anchor))
            {
                GlobalPosition spot = SupportSpot(s, v, pos, anchor, now);
                if (!FastMath.InRange(pos, spot, 35f))
                {
                    IssueMove(s, pos, Flat(spot - pos), spot);
                    if (s.state != GCombat.Support && cfgDiag.Value) Log.LogInfo($"[Ground] {v.name} -> support position");
                    s.state = GCombat.Support;
                    return;
                }
            }
            // at the spot (or no engaged friendly to anchor on): hold
            gvPathfinder(v)?.ClearDestination();
            s.hasIssued = false;
            if (s.state != GCombat.Defend && cfgDiag.Value) Log.LogInfo($"[Ground] {v.name} -> halt(def)");
            s.state = GCombat.Defend;
        }
    }

    // Centroid of nearby engaged friendly ground vehicles (the fight to support). False when none.
    static bool EngagedFriendlyAnchor(GroundVehicle v, GlobalPosition pos, float radius, out GlobalPosition anchor)
    {
        anchor = pos;
        var hq = v.NetworkHQ;
        Vector3 sum = Vector3.zero; int n = 0;
        BattlefieldGrid.GetUnitsInRangeNonAlloc(pos, radius, gNearby);
        for (int i = 0; i < gNearby.Count; i++)
        {
            var u = gNearby[i];
            if (u == null || u == v || u.disabled || !(u is GroundVehicle) || u.NetworkHQ != hq) continue;
            if (!gStates.TryGetValue(u.GetInstanceID(), out var st) || !st.owned || !(st.natural || st.hasLastEnemy)) continue;
            sum += u.GlobalPosition().ToLocalPosition(); n++;
        }
        if (n == 0) return false;
        anchor = (sum / n).ToGlobalPosition();
        return true;
    }

    // Support spot: behind the engaged group, off the road axis. Cached 10s.
    static GlobalPosition SupportSpot(GState s, GroundVehicle v, GlobalPosition pos, GlobalPosition anchor, float now)
    {
        if (s.hasSupSpot && now < s.supSpotUntil) return s.supSpot;
        Vector3 back = NearestGroundEnemyAny(v, out GlobalPosition ep) ? Flat(anchor - ep) : Flat(anchor - pos);
        if (back == Vector3.zero) back = Flat(pos - anchor);
        if (back == Vector3.zero) back = Flat(v.transform.forward);
        GlobalPosition spot = anchor + back * 100f + Perp(back, s.evadeSign) * 45f;
        if (!CorridorDry(pos, spot)) spot = anchor + back * 40f;   // water behind: settle closer to the group
        s.supSpot = spot; s.hasSupSpot = true; s.supSpotUntil = now + 10f;
        return spot;
    }
}
