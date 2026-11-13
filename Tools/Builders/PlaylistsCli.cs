// Playlists for agents, in a RUNNING editor (no window, no modal). Each entry returns one JSON document.
//   Tools/agent/u.sh run_script --file Tools/Builders/PlaylistsCli.cs --entry PlaylistsCli.Check
//   Tools/agent/u.sh run_script --file Tools/Builders/PlaylistsCli.cs --entry PlaylistsCli.Convert
//   Tools/agent/u.sh run_script --file Tools/Builders/PlaylistsCli.cs --entry PlaylistsCli.GenerateStart
//   Tools/agent/u.sh run_script --file Tools/Builders/PlaylistsCli.cs --entry PlaylistsCli.GenerateStatus
// Options (optional) in Temp/playlists-cli.json:
//   { "contentId": "MyGame", "count": 10, "remix": "<playlistId>", "variant": "boss-rush", "accept": false }
// "accept": true saves every proposal that passes the validator AND the fit matrix into
// Assets/Content/{id}/.dreampark-playlists.json (never one that fails). With the editor closed, use the
// batchmode command in Docs/PLAYLIST-STUDIO.md (DreamPark.Editor.PlaylistStudio.PlaylistStudioHeadless.Run).
using System.IO;
using System.Linq;
using Defective.JSON;
using DreamPark;
using DreamPark.Editor.PlaylistStudio;
using UnityEngine;

public static class PlaylistsCli
{
    static JSONObject Args()
    {
        string path = Path.Combine(Application.dataPath, "..", "Temp", "playlists-cli.json");
        return File.Exists(path) ? new JSONObject(File.ReadAllText(path)) : JSONObject.emptyObject;
    }

    static string Title(JSONObject a) => a.GetField("contentId")?.stringValue ?? PlaylistStudioHeadless.Titles().FirstOrDefault();

    public static string Check()
    {
        var all = new JSONObject(JSONObject.Type.Array);
        foreach (string id in PlaylistStudioHeadless.Titles()) all.Add(PlaylistStudioHeadless.Check(id, out _));
        return PlaylistStudioJson.Pretty(all);
    }

    public static string Convert()
    {
        JSONObject a = Args();
        bool accept = a.GetField("accept")?.boolValue ?? false;
        return PlaylistStudioJson.Pretty(PlaylistStudioHeadless.Convert(Title(a), accept));
    }

    static string StatusPath(string id) => Path.Combine(PlaylistStudioHeadless.ReportDir, id + ".generate.json");

    /// Starts a generation (it takes minutes) and returns at once; poll GenerateStatus.
    public static string GenerateStart()
    {
        JSONObject a = Args();
        string id = Title(a);
        int count = a.GetField("count")?.intValue ?? 10;
        string remix = a.GetField("remix")?.stringValue, variant = a.GetField("variant")?.stringValue;
        bool accept = a.GetField("accept")?.boolValue ?? false;
        PlaylistStudioSession s = PlaylistStudioSession.FromProject(id);
        PackagePlaylist baseP = string.IsNullOrEmpty(remix) ? null : s.data.playlists.FirstOrDefault(p => p.id == remix);
        Directory.CreateDirectory(PlaylistStudioHeadless.ReportDir);
        File.WriteAllText(StatusPath(id), "{\"status\":\"running\"}");
        PlaylistStudioApi.Send(id, PlaylistStudioApi.Body(s, Mathf.Clamp(count, 1, 15), baseP, variant), r =>
        {
            JSONObject report;
            if (!r.success) report = new JSONObject($"{{\"status\":\"failed\",\"ok\":false}}");
            else
            {
                var made = s.TakeResponse(r, baseP != null ? "remix" : "generate");
                if (accept) { foreach (var p in made.Where(p => p.CanSave)) s.Accept(p); if (s.dirty) s.Save(); }
                report = PlaylistStudioHeadless.ProposalsReport(s, baseP != null ? "remix" : "generate", made, accept);
                report.AddField("status", "done");
            }
            if (!r.success) report.AddField("error", (r.code ?? "") + " " + r.error);
            File.WriteAllText(StatusPath(id), PlaylistStudioJson.Pretty(report));
        });
        return "{\"status\":\"started\",\"titleId\":\"" + id + "\",\"poll\":\"PlaylistsCli.GenerateStatus\"}";
    }

    public static string GenerateStatus()
    {
        string id = Title(Args());
        return File.Exists(StatusPath(id)) ? File.ReadAllText(StatusPath(id)) : "{\"status\":\"none\"}";
    }
}
