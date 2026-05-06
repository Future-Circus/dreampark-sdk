using System.Collections.Generic;
using System.Linq;
using Meta.XR.EnvironmentDepth;
using UnityEngine;

public class DepthMask : MonoBehaviour
{
    private EnvironmentDepthManager environmentDepthManager;
    public List<MeshFilter> myMeshFilters = new List<MeshFilter>();
    public float _someOffsetFloatValue = 0.0f;

    void OnEnable()
    {
        // Attempt to (re)bind safely on enable as well.
        TrySetup();
    }

    void Start()
    {
        TrySetup();
    }

    private void TrySetup()
    {
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
        environmentDepthManager.MaskBias = _someOffsetFloatValue;
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

    void OnDisable()
    {
        DisableDepthMasking();
    }

    void OnDestroy()
    {
        DisableDepthMasking();
    }
}