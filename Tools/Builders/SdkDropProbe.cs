// platform-core: proves the SDK editor compiled the current drop (references APIs added in the latest drop).
public static class SdkDropProbe
{
    public static string Run()
    {
        var m = UnityEngine.ScriptableObject.CreateInstance<DreamPark.DreamParkPackageManifest>();
        int n = m.runnerLevels.Count;
        return "OK dwellIdle=" + DreamPark.RunnerDirector.DwellIdleSeconds + " addr=" + DreamPark.WaysToPlay.RunnerIds.LevelAddress("T", "A_Map")
            + " runnerLevels=" + n + " features: RunnerInfoJson=" + (typeof(DreamPark.RunnerDirector).GetMethod("RunnerInfoJson") != null);
    }
}
