// MissionTraits: mission-author pinned traits, stored in the mission JSON (modless-safe: vanilla ignores unknown props).

using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuclearOption.MissionEditorScripts;
using NuclearOption.MissionEditorScripts.MultiSelect;
using NuclearOption.SavedMission;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public partial class ImprovedAIPlugin
{
    const string TraitsJsonKey = "improvedAiTraits";      // array of trait names we stamp onto each saved unit
    const string LegacyJsonKey = "improvedAiTrait";       // old single-string form (read for back-compat)

    // §12 Editor Traits "Enabled" removed from F1 config (menu de-spam) — the feature is always on.

    // Assignment maps keyed by UniqueName (stable across editor reloads; instances are not).
    static readonly Dictionary<string, List<Trait>> assignedByName = new Dictionary<string, List<Trait>>();   // spawn (PersonalityRoll)
    static readonly Dictionary<string, List<Trait>> assignedEditor = new Dictionary<string, List<Trait>>();   // editing session (dropdown + save inject)

    static readonly HashSet<SavedUnit> mtRenameHooked = new HashSet<SavedUnit>();   // units whose OnRenamed we track
    static AccessTools.FieldRef<UnitPanelOptions, MultiSelect<SavedUnit>> mtTargets;
    static AccessTools.FieldRef<AircraftOptions, Slider> mtAcSkillSlider;   // aircraft-panel placement anchor
    static AccessTools.FieldRef<ShipOptions, Slider> mtShipSkillSlider;     // ship-panel placement anchor
    static TMP_Dropdown mtTemplate;                                        // cached dropdown to clone
    static readonly Dictionary<UnitPanelOptions, TMP_Dropdown> mtPanelDropdown = new Dictionary<UnitPanelOptions, TMP_Dropdown>();
    static bool mtUiBroken;

    void MissionTraitsConfig()
    {
        try { mtTargets = AccessTools.FieldRefAccess<UnitPanelOptions, MultiSelect<SavedUnit>>("targets"); }
        catch (Exception ex) { mtUiBroken = true; Logger.LogWarning("[MissionTraits] 'targets' field unavailable (dropdown disabled, storage still works): " + ex); }
        try { mtAcSkillSlider = AccessTools.FieldRefAccess<AircraftOptions, Slider>("skillSlider"); }
        catch (Exception ex) { mtAcSkillSlider = null; Logger.LogWarning("[MissionTraits] aircraft skillSlider anchor unavailable: " + ex); }
        try { mtShipSkillSlider = AccessTools.FieldRefAccess<ShipOptions, Slider>("skillSlider"); }
        catch (Exception ex) { mtShipSkillSlider = null; Logger.LogWarning("[MissionTraits] ship skillSlider anchor unavailable: " + ex); }
    }

    // APPLY: pinned traits for this unit ([Normal] = force plain). False = roll random.
    internal static bool TryGetAssignedTraits(Unit u, out List<Trait> list)
    {
        list = null;
        if (u == null || assignedByName.Count == 0) return false;
        string key = UnitSavedName(u);
        if (string.IsNullOrEmpty(key)) return false;
        return assignedByName.TryGetValue(key, out list) && list != null && list.Count > 0;
    }

    static string UnitSavedName(Unit u)
    {
        try { if (u.SavedUnit != null && !string.IsNullOrEmpty(u.SavedUnit.UniqueName)) return u.SavedUnit.UniqueName; } catch { }
        try { if (!string.IsNullOrEmpty(u.NetworkUniqueName)) return u.NetworkUniqueName; } catch { }
        try { return u.UniqueName; } catch { return null; }
    }

    // SAVE: stamp assignments into the mission JSON the game just produced.
    internal static void MissionTraitsInject(Mission m, ref string json)
    {
        if (m == null || string.IsNullOrEmpty(json)) return;
        if (assignedEditor.Count == 0) { if (cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[MissionTraits] save '{m.Name}': no assigned traits to stamp."); return; }

        try
        {
            var root = JObject.Parse(json);
            int n = InjectArray(root["vehicles"] as JArray) + InjectArray(root["aircraft"] as JArray) + InjectArray(root["ships"] as JArray);
            if (n > 0) json = root.ToString(Formatting.Indented);
            if (cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[MissionTraits] stamped traits on {n} unit(s) in saved mission '{m.Name}' (assignedEditor={assignedEditor.Count}).");
        }
        catch (Exception ex) { Log?.LogWarning("[MissionTraits] save inject failed (mission saved WITHOUT trait data): " + ex); }
    }

    // Stamp each JSON element whose UniqueName has an editor assignment.
    static int InjectArray(JArray arr)
    {
        if (arr == null) return 0;
        int n = 0;
        foreach (var el in arr.OfType<JObject>())
        {
            var un = (string)el["UniqueName"];
            if (un == null || !assignedEditor.TryGetValue(un, out var list) || list == null || list.Count == 0) continue;
            el.Remove(LegacyJsonKey);                                     // drop any stale single-value form
            el[TraitsJsonKey] = new JArray(list.Select(t => t.ToString()));
            n++;
        }
        return n;
    }

    // LOAD: read our property back from the raw JSON (the game discards unknown members).
    internal static void MissionTraitsParse(string json, Mission m)
    {
        assignedByName.Clear();                                           // each full mission load replaces the state
        assignedEditor.Clear();
        mtRenameHooked.Clear();                                           // SavedUnit instances are being replaced
        if (string.IsNullOrEmpty(json) || m == null) return;

        try
        {
            var root = JObject.Parse(json);
            var nameTraits = new Dictionary<string, List<Trait>>();
            CollectFromArray(root["vehicles"] as JArray, nameTraits);
            CollectFromArray(root["aircraft"] as JArray, nameTraits);
            CollectFromArray(root["ships"] as JArray, nameTraits);
            if (nameTraits.Count == 0) return;

            foreach (var kv in nameTraits) { assignedByName[kv.Key] = kv.Value; assignedEditor[kv.Key] = new List<Trait>(kv.Value); }
            if (cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[MissionTraits] loaded assigned traits for {assignedByName.Count} unit(s) from mission '{m.Name}'.");
        }
        catch (Exception ex) { Log?.LogWarning("[MissionTraits] load parse failed (assigned traits ignored, random rolls apply): " + ex); }
    }

    static void CollectFromArray(JArray arr, Dictionary<string, List<Trait>> into)
    {
        if (arr == null) return;
        foreach (var el in arr.OfType<JObject>())
        {
            var un = (string)el["UniqueName"];
            if (string.IsNullOrEmpty(un)) continue;
            var list = ReadTraitTokens(el[TraitsJsonKey]);               // new array form
            if (list.Count == 0)                                          // legacy single-string fallback
            {
                var single = (string)el[LegacyJsonKey];
                if (!string.IsNullOrEmpty(single) && Enum.TryParse<Trait>(single, out var one)) list.Add(one);
            }
            if (list.Count > 0) into[un] = list;
        }
    }

    static List<Trait> ReadTraitTokens(JToken tok)
    {
        var list = new List<Trait>();
        if (tok is JArray a)
            foreach (var t in a)
            {
                var s = (string)t;
                if (!string.IsNullOrEmpty(s) && Enum.TryParse<Trait>(s, out var tr) && !list.Contains(tr)) list.Add(tr);
            }
        return list;
    }

    // Migrate assignment keys on unit rename (assignments are UniqueName-keyed).
    static void HookRenames(MultiSelect<SavedUnit> targets)
    {
        foreach (var su in targets.Targets)
        {
            if (su == null || mtRenameHooked.Contains(su)) continue;
            su.OnRenamed += OnUnitRenamed;
            mtRenameHooked.Add(su);
        }
    }

    static void OnUnitRenamed(NuclearOption.SavedMission.ISaveableReference reference, string oldName, string newName)
    {
        if (string.IsNullOrEmpty(oldName) || oldName == newName) return;
        if (assignedEditor.TryGetValue(oldName, out var l)) { assignedEditor.Remove(oldName); if (l != null && l.Count > 0) assignedEditor[newName] = l; }
        if (assignedByName.TryGetValue(oldName, out var l2)) { assignedByName.Remove(oldName); if (l2 != null && l2.Count > 0) assignedByName[newName] = l2; }
        if (cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[MissionTraits] migrated assignment '{oldName}' -> '{newName}'.");
    }

    // EDITOR helpers.
    static List<Trait> EditorList(SavedUnit su) => (su != null && su.UniqueName != null && assignedEditor.TryGetValue(su.UniqueName, out var l) && l != null) ? l : null;

    static void EditorSet(SavedUnit su, List<Trait> l)
    {
        if (su == null || su.UniqueName == null) return;
        if (l == null || l.Count == 0) assignedEditor.Remove(su.UniqueName);
        else assignedEditor[su.UniqueName] = l;
    }

    // Copy/paste or duplicate: carry the source unit's traits onto the new unit.
    internal static void MissionTraitsCopyPaste(SavedUnit copyFrom, SavedUnit pasteTo)
    {
        if (copyFrom == null || pasteTo == null || copyFrom.UniqueName == null || pasteTo.UniqueName == null) return;
        if (copyFrom.UniqueName == pasteTo.UniqueName) return;         // pasting a unit onto itself — nothing to carry
        List<Trait> src = EditorList(copyFrom);
        if (src != null && src.Count > 0) assignedEditor[pasteTo.UniqueName] = new List<Trait>(src);
        else assignedEditor.Remove(pasteTo.UniqueName);               // source has no traits -> destination becomes plain too
        if (cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[MissionTraits] copy/paste traits '{copyFrom.UniqueName}' -> '{pasteTo.UniqueName}' ({(src == null ? 0 : src.Count)}).");
    }

    static bool AllTargetsHave(MultiSelect<SavedUnit> targets, Trait t)
    {
        if (targets.Targets.Count == 0) return false;
        foreach (var su in targets.Targets) { var l = EditorList(su); if (l == null || !l.Contains(t)) return false; }
        return true;
    }

    static bool AllTargetsPlain(MultiSelect<SavedUnit> targets)          // exactly {Normal}
    {
        if (targets.Targets.Count == 0) return false;
        foreach (var su in targets.Targets) { var l = EditorList(su); if (l == null || l.Count != 1 || l[0] != Trait.Normal) return false; }
        return true;
    }

    static void ToggleTraitOnTargets(MultiSelect<SavedUnit> targets, Trait t)
    {
        bool allHave = AllTargetsHave(targets, t);
        foreach (var su in targets.Targets)
        {
            var l = new List<Trait>(EditorList(su) ?? Enumerable.Empty<Trait>());
            l.Remove(Trait.Normal);                                       // picking a real trait clears the "plain" marker
            if (allHave) l.Remove(t);
            else if (!l.Contains(t)) l.Add(t);
            EditorSet(su, l);
        }
    }

    static void TogglePlainOnTargets(MultiSelect<SavedUnit> targets)
    {
        bool allPlain = AllTargetsPlain(targets);
        foreach (var su in targets.Targets)
            EditorSet(su, allPlain ? new List<Trait>() : new List<Trait> { Trait.Normal });   // toggle plain <-> random
    }

    // Collapsed-dropdown summary text for the current selection.
    static string TargetsSummary(MultiSelect<SavedUnit> targets)
    {
        if (targets.Targets.Count == 0) return "Traits";
        // uniform check
        List<Trait> first = EditorList(targets.Targets[0]);
        for (int i = 1; i < targets.Targets.Count; i++)
            if (!SameList(first, EditorList(targets.Targets[i]))) return "Traits: (mixed)";
        if (first == null || first.Count == 0) return "Random";                       // title label already says "Traits"
        if (first.Count == 1 && first[0] == Trait.Normal) return "None (plain)";
        return string.Join(", ", first.Where(t => t != Trait.Normal));
    }

    static bool SameList(List<Trait> a, List<Trait> b)
    {
        int ca = a?.Count ?? 0, cb = b?.Count ?? 0;
        if (ca != cb) return false;
        if (ca == 0) return true;
        foreach (var t in a) if (!b.Contains(t)) return false;
        return true;
    }

    // ------------------------------------------------------------------ EDITOR UI ---
    internal static void MissionTraitsSetupVehicle(VehicleOptions vo)
    {
        Slider anchor = null;
        try { if (gtSkillSlider != null) anchor = gtSkillSlider(vo); } catch { }
        EnsureTraitDropdown(vo, groundPool, anchor);
    }

    internal static void MissionTraitsSetupAircraft(AircraftOptions ao)
    {
        Slider anchor = null;
        try { if (mtAcSkillSlider != null) anchor = mtAcSkillSlider(ao); } catch { }
        EnsureTraitDropdown(ao, airPool, anchor);
    }

    internal static void MissionTraitsSetupShip(ShipOptions so)
    {
        Slider anchor = null;
        try { if (mtShipSkillSlider != null) anchor = mtShipSkillSlider(so); } catch { }
        EnsureTraitDropdown(so, navalPool, anchor);
    }

    // `pool` = traits offered for this unit kind. Captured per panel type.
    static void EnsureTraitDropdown(UnitPanelOptions panel, Trait[] pool, Slider anchor)
    {
        if (mtUiBroken || mtTargets == null || panel == null) return;
        try
        {
            var targets = mtTargets(panel);
            if (targets == null) { if (cfgDiag != null && cfgDiag.Value) Log?.LogWarning("[MissionTraits] panel had null targets."); return; }
            HookRenames(targets);                                          // keep assignments attached across a rename

            if (!mtPanelDropdown.TryGetValue(panel, out var dd) || dd == null)
            {
                dd = CreateTraitDropdown(panel, anchor);
                if (dd == null) return;
                mtPanelDropdown[panel] = dd;
                dd.onValueChanged.AddListener(idx => OnTraitToggled(panel, pool, idx));
            }
            RefreshTraitDropdown(panel, pool);
        }
        catch (Exception ex) { Log?.LogWarning("[MissionTraits] dropdown setup error (non-fatal): " + ex); }
    }

    // Populate options + summary caption directly (TMP's refresh path NRE'd on a fresh clone).
    static void RefreshTraitDropdown(UnitPanelOptions panel, Trait[] pool)
    {
        if (!mtPanelDropdown.TryGetValue(panel, out var dd) || dd == null) return;
        var targets = mtTargets(panel);
        if (targets == null) return;

        var opts = new List<TMP_Dropdown.OptionData> { new TMP_Dropdown.OptionData(Mark(AllTargetsPlain(targets)) + "None (force plain)") };
        foreach (var t in pool) opts.Add(new TMP_Dropdown.OptionData(Mark(AllTargetsHave(targets, t)) + t.ToString()));

        try { dd.options = opts; } catch (Exception ex) { Log?.LogWarning("[MissionTraits] set options failed: " + ex); }
        try { dd.SetValueWithoutNotify(-1); } catch { }                   // best-effort; ignore if TMP dislikes -1 here
        try { if (dd.captionText != null) dd.captionText.text = TargetsSummary(targets); } catch { }
    }

    static string Mark(bool on) => on ? "[x]  " : "[  ]  ";           // ASCII marker (the game font lacks a check glyph)

    static void OnTraitToggled(UnitPanelOptions panel, Trait[] pool, int idx)
    {
        try
        {
            var targets = mtTargets(panel);
            if (targets == null || idx < 0) return;
            if (idx == 0) TogglePlainOnTargets(targets);
            else
            {
                int ti = idx - 1;
                if (ti >= 0 && ti < pool.Length) ToggleTraitOnTargets(targets, pool[ti]);
            }
            RefreshTraitDropdown(panel, pool);                          // rebuild ticks + summary
            if (cfgDiag != null && cfgDiag.Value) Log?.LogInfo($"[MissionTraits] editor toggled option {idx} on {targets.Targets.Count} unit(s) -> {TargetsSummary(targets)}");
        }
        catch (Exception ex) { Log?.LogWarning("[MissionTraits] trait toggle error: " + ex); }
    }

    // Clone an in-game dropdown into the unit panel, next to the skill-slider group.
    static TMP_Dropdown CreateTraitDropdown(UnitPanelOptions panel, Slider anchor)
    {
        var tmpl = GetDropdownTemplate(panel);
        if (tmpl == null) { Log?.LogWarning("[MissionTraits] no TMP_Dropdown template found to clone (dropdown not created)."); return null; }

        // Find the top-level group box (direct child of the panel root) that contains the skill slider, so we can
        // insert our dropdown at the same layout level as the other rows (Faction / AI Skill / Hold Position).
        Transform group = null, parent = panel.transform;
        if (anchor != null)
        {
            Transform t = anchor.transform;
            while (t != null && t.parent != null && t.parent != panel.transform) t = t.parent;
            if (t != null && t.parent == panel.transform) { group = t; parent = panel.transform; }
            else parent = anchor.transform.parent != null ? anchor.transform.parent : panel.transform;
        }

        // Avoid duplicates if a stale one lingers.
        var existing = parent.Find("ImprovedAITraitDropdown");
        if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);

        var go = UnityEngine.Object.Instantiate(tmpl.gameObject, parent);
        go.name = "ImprovedAITraitDropdown";
        go.transform.localScale = Vector3.one;
        go.SetActive(true);
        var dd = go.GetComponent<TMP_Dropdown>();
        if (dd == null) { UnityEngine.Object.Destroy(go); return null; }
        dd.onValueChanged.RemoveAllListeners();

        var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        if (le.minHeight < 28f) le.minHeight = 34f;                       // don't collapse under a vertical layout group

        // The clone kept the TEMPLATE dropdown's anchor/pivot/width, so a VerticalLayoutGroup positions it off to the
        // side (observed anchoredPos.x ~361). Copy the sibling group's horizontal RectTransform setup so the layout
        // treats it exactly like the other rows (Skill Panel / Faction).
        var rt = go.transform as RectTransform;
        var grt = group as RectTransform;
        if (rt != null && grt != null)
        {
            rt.anchorMin = grt.anchorMin;
            rt.anchorMax = grt.anchorMax;
            rt.pivot = grt.pivot;
            var sd = rt.sizeDelta; sd.x = grt.sizeDelta.x; rt.sizeDelta = sd;   // match width, keep our height
            var ap = rt.anchoredPosition; ap.x = grt.anchoredPosition.x; rt.anchoredPosition = ap;
        }

        // Add a "Traits" title above the dropdown, matching the panel's row headers. Clone a header label from the
        // skill group so it inherits the game's font/size/colour; parent it, retitle, and match the group's width.
        Transform title = CreateTraitTitle(parent, group);

        // Order the rows: [skill group] -> [Traits title] -> [Traits dropdown].
        if (group != null)
        {
            int idx = group.GetSiblingIndex() + 1;
            if (title != null) { title.SetSiblingIndex(idx); idx++; }
            go.transform.SetSiblingIndex(idx);
        }

        if (cfgDiag != null && cfgDiag.Value)
        {
            var vlg = parent.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
            string lg = vlg != null ? $"Vertical(ctrlW={vlg.childControlWidth},expandW={vlg.childForceExpandWidth},pad={vlg.padding.left}/{vlg.padding.right})" : "other/none";
            string grpChildren = group != null ? string.Join(", ", Enumerable.Range(0, group.childCount).Select(i => group.GetChild(i).name)) : "n/a";
            string myInfo = rt != null ? $"clone sizeDelta={rt.sizeDelta} anchPos={rt.anchoredPosition} rectSize={rt.rect.size}" : "clone rt=null";
            Log?.LogInfo($"[MissionTraits] created trait dropdown+title under '{parent.name}'. layout={lg} title={(title != null)} groupChildren=[{grpChildren}] {myInfo}");
        }
        return dd;
    }

    // Clone a header label from the skill group and retitle it "Traits".
    static Transform CreateTraitTitle(Transform parent, Transform group)
    {
        try
        {
            var existing = parent.Find("ImprovedAITraitTitle");
            if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);
            if (group == null) return null;
            var srcLabel = group.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
            if (srcLabel == null) return null;

            var labelGo = UnityEngine.Object.Instantiate(srcLabel.gameObject, parent);
            labelGo.name = "ImprovedAITraitTitle";
            labelGo.transform.localScale = Vector3.one;
            labelGo.SetActive(true);
            var lt = labelGo.GetComponent<TMPro.TextMeshProUGUI>();
            if (lt != null) lt.text = "Traits";
            // match the group's horizontal RectTransform so the vertical layout places it full-width like siblings
            var lrt = labelGo.transform as RectTransform;
            var grt = group as RectTransform;
            if (lrt != null && grt != null)
            {
                lrt.anchorMin = grt.anchorMin; lrt.anchorMax = grt.anchorMax; lrt.pivot = grt.pivot;
                var sd = lrt.sizeDelta; sd.x = grt.sizeDelta.x; lrt.sizeDelta = sd;
                var ap = lrt.anchoredPosition; ap.x = grt.anchoredPosition.x; lrt.anchoredPosition = ap;
            }
            return labelGo.transform;
        }
        catch (Exception ex) { Log?.LogWarning("[MissionTraits] title label create error: " + ex); return null; }
    }

    static TMP_Dropdown GetDropdownTemplate(UnitPanelOptions panel)
    {
        if (mtTemplate != null) return mtTemplate;
        try
        {
            // Prefer a dropdown already in this editor window (correctly styled + active), e.g. the Faction dropdown.
            var root = panel.transform.root;
            mtTemplate = root.GetComponentsInChildren<TMP_Dropdown>(true).FirstOrDefault(d => d != null && d.template != null && d.gameObject != null);
            if (mtTemplate == null)
                mtTemplate = Resources.FindObjectsOfTypeAll<TMP_Dropdown>().FirstOrDefault(d => d != null && d.template != null);
        }
        catch (Exception ex) { Log?.LogWarning("[MissionTraits] template search error: " + ex); }
        return mtTemplate;
    }
}

// SAVE: stamp assigned traits into the mission JSON the game just produced.
[HarmonyPatch(typeof(NuclearOption.SavedMission.NewtonsoftHelper), "ToJson")]
public static class NewtonsoftHelper_ToJson_TraitPatch
{
    static void Postfix(object obj, ref string __result)
    {
        try { if (obj is Mission m) ImprovedAIPlugin.MissionTraitsInject(m, ref __result); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("[MissionTraits] ToJson postfix error: " + ex); }
    }
}

// LOAD: recover our property from the raw JSON into the assignment maps.
[HarmonyPatch(typeof(NuclearOption.SavedMission.MissionSaveLoad), "TryReadJson")]
public static class MissionSaveLoad_TryReadJson_TraitPatch
{
    static void Postfix(string json, Mission mission, bool __result)
    {
        try { if (__result && mission != null) ImprovedAIPlugin.MissionTraitsParse(json, mission); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("[MissionTraits] TryReadJson postfix error: " + ex); }
    }
}

// COPY/PASTE/DUPLICATE: carry editor-assigned traits onto the new unit.
[HarmonyPatch(typeof(NuclearOption.MissionEditorScripts.UnitCopyPaste), "CopyPaste")]
public static class UnitCopyPaste_CopyPaste_TraitPatch
{
    static void Postfix(SavedUnit copyFrom, SavedUnit pasteTo)
    {
        try { ImprovedAIPlugin.MissionTraitsCopyPaste(copyFrom, pasteTo); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("[MissionTraits] copy-paste patch error: " + ex); }
    }
}

// EDITOR UI: trait dropdown on the unit panels (SetupInner + refresh on every selection change).
[HarmonyPatch(typeof(NuclearOption.MissionEditorScripts.VehicleOptions), "SetupInner")]
public static class VehicleOptions_SetupInner_TraitPatch
{
    static void Postfix(NuclearOption.MissionEditorScripts.VehicleOptions __instance)
    {
        try { ImprovedAIPlugin.MissionTraitsSetupVehicle(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("[MissionTraits] vehicle panel patch error: " + ex); }
    }
}

// EDITOR UI: aircraft panel.
[HarmonyPatch(typeof(NuclearOption.MissionEditorScripts.AircraftOptions), "SetupInner")]
public static class AircraftOptions_SetupInner_TraitPatch
{
    static void Postfix(NuclearOption.MissionEditorScripts.AircraftOptions __instance)
    {
        try { ImprovedAIPlugin.MissionTraitsSetupAircraft(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("[MissionTraits] aircraft panel patch error: " + ex); }
    }
}

// (Vehicle/aircraft/ship panel + selection-change refreshes share the pattern above; ship panel below.)
[HarmonyPatch(typeof(NuclearOption.MissionEditorScripts.VehicleOptions), "OnTargetsChanged")]
public static class VehicleOptions_OnTargetsChanged_TraitPatch
{
    static void Postfix(NuclearOption.MissionEditorScripts.VehicleOptions __instance)
    {
        try { ImprovedAIPlugin.MissionTraitsSetupVehicle(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("[MissionTraits] vehicle targets-changed patch error: " + ex); }
    }
}

[HarmonyPatch(typeof(NuclearOption.MissionEditorScripts.AircraftOptions), "OnTargetsChanged")]
public static class AircraftOptions_OnTargetsChanged_TraitPatch
{
    static void Postfix(NuclearOption.MissionEditorScripts.AircraftOptions __instance)
    {
        try { ImprovedAIPlugin.MissionTraitsSetupAircraft(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("[MissionTraits] aircraft targets-changed patch error: " + ex); }
    }
}

// EDITOR UI: ship panel.
[HarmonyPatch(typeof(NuclearOption.MissionEditorScripts.ShipOptions), "SetupInner")]
public static class ShipOptions_SetupInner_TraitPatch
{
    static void Postfix(NuclearOption.MissionEditorScripts.ShipOptions __instance)
    {
        try { ImprovedAIPlugin.MissionTraitsSetupShip(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("[MissionTraits] ship panel patch error: " + ex); }
    }
}

[HarmonyPatch(typeof(NuclearOption.MissionEditorScripts.ShipOptions), "OnTargetsChanged")]
public static class ShipOptions_OnTargetsChanged_TraitPatch
{
    static void Postfix(NuclearOption.MissionEditorScripts.ShipOptions __instance)
    {
        try { ImprovedAIPlugin.MissionTraitsSetupShip(__instance); }
        catch (Exception ex) { ImprovedAIPlugin.Log?.LogWarning("[MissionTraits] ship targets-changed patch error: " + ex); }
    }
}
