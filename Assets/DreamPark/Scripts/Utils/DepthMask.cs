using System.Collections.Generic;
using System.Linq;
#if !UNITY_IOS || UNITY_EDITOR
using Meta.XR.EnvironmentDepth;
#endif
using UnityEngine;

public class DepthMask : MonoBehaviour
{
#if !UNITY_IOS || UNITY_EDITOR
    private EnvironmentDepthManager environmentDepthManager;
#endif
    public List<MeshFilter> myMeshFilters = new List<MeshFilter>();
    public float _someOffsetFloatValue = 0.0f;

    // Nov 2 2026 (flags-perf-nov2): the RIG's own masks — the DepthMaskCeiling dome's GameObject and
    // HeadTracker/DepthMaskDistance in DreamPark.prefab — are part of DreamPark's custom depth pipeline and
    // register only with the park flag custom_occlusion (DreamPark.DepthMaskCeiling.CustomOcclusion);
    // without it Meta's occlusion stays stock (no mask meshes, so Meta never builds its mask). Masks that
    // content places (portals on a floor, L_DEPTH_MASK) are untouched. Identified by name, not by a new
    // serialized field, so no prefab changes.
    private bool IsRigMask => GetComponent<DreamPark.DepthMaskCeiling>() != null || gameObject.name == "DepthMaskDistance";
    private bool _listening;
    private bool _loggedStock;

    void OnEnable()
    {
        if (IsRigMask && !_listening)
        {
            _listening = true;
            DreamPark.DepthMaskCeiling.CustomOcclusionChanged += OnCustomOcclusionChanged;
        }
        // Attempt to (re)bind safely on enable as well.
        TrySetup();
    }

    private void OnCustomOcclusionChanged()
    {
        if (this == null || !isActiveAndEnabled) return;
        if (DreamPark.DepthMaskCeiling.CustomOcclusion) TrySetup();
        else DisableDepthMasking();
    }

    void Start()
    {
        TrySetup();
    }

    private void TrySetup()
    {
#if UNITY_IOS && !UNITY_EDITOR
        // Meta environment depth is Quest-only and the iOS player is built without
        // the Meta assemblies (Assets/Editor/IosQuestAssemblyFilter.cs). Main-iOS.unity
        // never had an EnvironmentDepthManager, so this always ended here on iOS.
        enabled = false;
#else
        if (IsRigMask && !DreamPark.DepthMaskCeiling.CustomOcclusion)
        {
            // Stock Meta occlusion: stay enabled (a live flag flip registers it), register nothing.
            if (!_loggedStock) { _loggedStock = true; Debug.Log($"[DepthMask] {name}: not registered — stock Meta occlusion (no custom_occlusion flag)"); }
            return;
        }
        if (environmentDepthManager == null)
            environmentDepthManager = FindFirstObjectByType<EnvironmentDepthManager>();

        if (environmentDepthManager == null)
        {
            enabled = false;
            return;
        }

        GetMeshFilters();
        SetDepthMaskMeshFilters();
        ScrubManagerList(); // remove any stale entries that may already be present
#endif
    }

    private void GetMeshFilters()
    {
        myMeshFilters.Clear();
        var subs = GetComponentsInChildren<MeshFilter>(true);
        foreach (var mf in subs)
        {
            if (mf != null && mf.sharedMesh != null) // ensure valid
                myMeshFilters.Add(mf);
        }
    }

#if !UNITY_IOS || UNITY_EDITOR
    private void SetDepthMaskMeshFilters()
    {
        var mgrList = environmentDepthManager.MaskMeshFilters ?? new List<MeshFilter>();

        // add only if not present and valid
        foreach (var mf in myMeshFilters)
        {
            if (mf != null && mf.sharedMesh != null && !mgrList.Contains(mf))
                mgrList.Add(mf);
        }

        environmentDepthManager.MaskMeshFilters = mgrList;
        // Oct 11 2026: MaskBias is GLOBAL and is no longer stamped per object here (the
        // last DepthMask to enable used to win for the whole park). DreamPark.
        // DepthMaskCeiling owns it and writes it every LateUpdate; _someOffsetFloatValue
        // stays serialized but is unused.
    }

    // Call this whenever objects may have been created/destroyed/changed meshes.
    private void ScrubManagerList()
    {
        if (environmentDepthManager?.MaskMeshFilters == null) return;

        environmentDepthManager.MaskMeshFilters =
            environmentDepthManager.MaskMeshFilters
                .Where(mf => mf != null && mf.sharedMesh != null)
                .Distinct()
                .ToList();
    }

    private void DisableDepthMasking()
    {
        if (environmentDepthManager?.MaskMeshFilters == null || myMeshFilters == null) return;

        environmentDepthManager.MaskMeshFilters =
            environmentDepthManager.MaskMeshFilters
                .Where(mf => mf != null && !myMeshFilters.Contains(mf))
                .ToList();
    }
#else
    private void DisableDepthMasking() { }
#endif

    void OnDisable()
    {
        DisableDepthMasking();
        if (_listening) { _listening = false; DreamPark.DepthMaskCeiling.CustomOcclusionChanged -= OnCustomOcclusionChanged; }
    }

    void OnDestroy()
    {
        DisableDepthMasking();
        if (_listening) { _listening = false; DreamPark.DepthMaskCeiling.CustomOcclusionChanged -= OnCustomOcclusionChanged; }
    }
}