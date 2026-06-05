using System.Collections.Generic;
using UnityEngine;

public class EasyDisableAboveActive : EasyEvent
{
    [ReadOnly] public int disabledCount;
    [HideInInspector] public string[] disabledEventLines = new string[0];
    [ReadOnly] [TextArea(2, 12)] public string disabledSummary = "Nothing disabled yet.";

    public override void OnEvent(object arg0 = null)
    {
        List<string> lines = new List<string>();
        EasyEvent current = aboveEvent;

        while (current != null)
        {
            if (current.isEnabled)
            {
                current.OnEventDisable();
                lines.Add(current.GetType().Name + " OFF \u2714");
            }

            current = current.aboveEvent;
        }

        disabledCount = lines.Count;
        disabledEventLines = lines.ToArray();
        disabledSummary = disabledCount > 0
            ? string.Join("\n", disabledEventLines)
            : "No active EasyEvents above to disable.";

        onEvent?.Invoke(arg0);
    }
}
