using UnityEngine;

public class EasyVariantPicker : EasyEvent
{
    public GameObject[] variants;
    public override void OnEvent(object arg0 = null) {
        if (variants.Length > 0) {
            //pick a variant, enable, and disable the others
            for (int i = 0; i < variants.Length; i++) {
                variants[i].SetActive(false);
            }
            variants[Random.Range(0, variants.Length)].SetActive(true);
        }
        onEvent?.Invoke(null);
    }
}
